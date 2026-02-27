using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Microsoft.Win32.SafeHandles;

using SharedFileJournal.Internal;

namespace SharedFileJournal;

/// <summary>
/// A high-performance concurrent inter-process append journal backed by a single shared file.
/// </summary>
/// <remarks>
/// <para>
/// The journal file contains a small metadata header in the first 4096 bytes, followed by
/// sequential record data. The metadata region is memory-mapped for atomic offset reservation
/// via <see cref="Interlocked.Add(ref long, long)"/> on a shared pointer. Record writes go
/// directly to the reserved offset using <see cref="RandomAccess"/>.
/// </para>
/// <para>
/// <b>Atomic reservation strategy:</b> The first 4096 bytes of the journal file are mapped
/// into memory with <see cref="MemoryMappedFile.CreateFromFile(SafeFileHandle, string?, long, MemoryMappedFileAccess, HandleInheritability, bool)"/>.
/// A raw pointer to the <c>NextWriteOffset</c> field (at a cache-line-aligned offset) is used
/// with <see cref="Interlocked.Add(ref long, long)"/> for lock-free atomic fetch-add semantics.
/// This works across processes because the memory-mapped region is backed by the same physical
/// pages, and <c>Interlocked</c> operations compile to hardware atomics (e.g. <c>lock xadd</c>
/// on x86-64) that are coherent across all processes sharing the mapping. Requires 8-byte
/// alignment of the target field, which is guaranteed by placing it at offset 64.
/// </para>
/// <para>
/// <b>Platform assumptions:</b> Works on Windows, Linux, and macOS with .NET 10+. Relies on
/// <see cref="MemoryMappedFile.CreateFromFile(string, FileMode)"/> (no named shared memory)
/// and <see cref="RandomAccess"/> for portable I/O.
/// </para>
/// <para>
/// <b>Disposal contract:</b> Callers must ensure that no concurrent operations
/// (<see cref="Append"/>, <see cref="ReadAll"/>) are in flight
/// when <see cref="Dispose"/> is called. This is a deliberate design choice to avoid
/// adding synchronization overhead to the lock-free write path.
/// </para>
/// </remarks>
public sealed unsafe class SharedJournal : IDisposable
{
    private readonly SafeFileHandle _fileHandle;
    private readonly MemoryMappedFile _metaMap;
    private readonly MemoryMappedViewAccessor _metaView;
    private readonly MetadataHeader* _meta;
    private int _disposed;

    /// <summary>
    /// Opens or creates a journal at the specified file path.
    /// </summary>
    /// <param name="path">
    /// Path to the journal file. The file contains both the metadata header and record data.
    /// </param>
    /// <param name="options">Optional configuration. Uses defaults if <c>null</c>.</param>
    public SharedJournal(string path, SharedJournalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        var fileOptions = (options ?? SharedJournalOptions.Default).FlushMode == FlushMode.None
            ? FileOptions.None
            : FileOptions.WriteThrough;

        _fileHandle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, fileOptions);
        try
        {
            if (RandomAccess.GetLength(_fileHandle) < JournalFormat.MetadataFileSize)
                RandomAccess.SetLength(_fileHandle, JournalFormat.MetadataFileSize);

            _metaMap = MemoryMappedFile.CreateFromFile(_fileHandle, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
            _metaView = _metaMap.CreateViewAccessor(0, JournalFormat.MetadataFileSize, MemoryMappedFileAccess.ReadWrite);

            byte* rawPtr = null;
            _metaView.SafeMemoryMappedViewHandle.AcquirePointer(ref rawPtr);
            rawPtr += _metaView.PointerOffset;
            _meta = (MetadataHeader*)rawPtr;

            InitializeOrValidateMetadata(_meta);
        }
        catch
        {
            _metaView?.Dispose();
            _metaMap?.Dispose();
            _fileHandle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Appends a record with the given payload to the journal.
    /// </summary>
    /// <param name="payload">The record payload. May be empty.</param>
    /// <param name="flushMode">
    /// Per-record flush override. When set to <see cref="FlushMode.WriteThrough"/>,
    /// the data file is flushed to disk after this write regardless of the journal-level setting.
    /// </param>
    /// <returns>Information about the appended record.</returns>
    [SkipLocalsInit]
    public JournalAppendResult Append(ReadOnlySpan<byte> payload, FlushMode flushMode = FlushMode.None)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, int.MaxValue - JournalFormat.RecordHeaderSize - JournalFormat.RecordAlignment, nameof(payload));

        var dataLength = JournalFormat.RecordHeaderSize + payload.Length;
        var alignedLength = JournalFormat.AlignRecordSize(dataLength);
        var offset = ReserveSpace(alignedLength);

        byte[]? rented = null;
        var span = dataLength <= 2048 ? stackalloc byte[dataLength] : (rented = ArrayPool<byte>.Shared.Rent(dataLength));
        try
        {
            JournalFormat.WriteRecord(span, payload);
            RandomAccess.Write(_fileHandle, span[..dataLength], offset);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }

        if (flushMode == FlushMode.WriteThrough)
            RandomAccess.FlushToDisk(_fileHandle);

        return new JournalAppendResult(offset, alignedLength);
    }

    /// <summary>
    /// Reads all valid records from the journal sequentially.
    /// Stops at the first invalid or incomplete record.
    /// </summary>
    public IEnumerable<JournalRecord> ReadAll()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return ReadRecords();
    }

    /// <summary>
    /// Flushes all buffered data to the underlying storage device.
    /// </summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        RandomAccess.FlushToDisk(_fileHandle);
    }

    /// <summary>
    /// Compacts the journal at the specified path, reclaiming space from gaps left by
    /// crashed writers and removing corrupted or incomplete records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Creates a temporary journal, copies all valid records from the source (skipping gaps
    /// and corrupted regions), then atomically replaces the original file. The source file
    /// is untouched until the swap, making this operation crash-safe.
    /// </para>
    /// <para>
    /// <b>Exclusive access required:</b> No other processes or instances may have the journal
    /// file open when this method is called. On Windows, open file handles prevent the file
    /// replacement. On Linux, stale handles would continue referencing the old (replaced) file.
    /// </para>
    /// </remarks>
    /// <param name="path">Path to the journal file to compact.</param>
    public static void Compact(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var tempPath = path + ".compact";
        File.Delete(tempPath);

        using (var source = new SharedJournal(path))
        using (var temp = new SharedJournal(tempPath))
        {
            foreach (var record in source.ReadAll())
                temp.Append(record.Payload.Span);
            temp.Flush();
        }

        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Releases all resources used by this journal instance.
    /// </summary>
    /// <remarks>
    /// Callers must ensure that no concurrent <see cref="Append"/> or <see cref="ReadAll"/>
    /// operations are in flight when this method is called.
    /// Accessing the journal from another thread during or after disposal leads to
    /// undefined behavior (use-after-free of memory-mapped pointers).
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _metaView.SafeMemoryMappedViewHandle.ReleasePointer();
        _metaView.Dispose();
        _metaMap.Dispose();
        _fileHandle.Dispose();
    }

    private static void InitializeOrValidateMetadata(MetadataHeader* meta)
    {
        if (Interlocked.CompareExchange(ref meta->Magic, JournalFormat.MetadataMagic, 0) == 0)
        {
            // Won the initialization race — write NextWriteOffset before Version
            // so followers spinning on Version cannot proceed with an uninitialized tail
            Volatile.Write(ref meta->NextWriteOffset, (long)JournalFormat.DataStartOffset);
            Volatile.Write(ref meta->Version, JournalFormat.MetadataVersion);
        }
        else
        {
            // Another process initialized it — spin until version is written
            var deadline = Environment.TickCount64 + 5000;
            SpinWait spin = default;
            while (Volatile.Read(ref meta->Version) == 0)
            {
                spin.SpinOnce();
                if (Environment.TickCount64 > deadline)
                {
                    // The initializer may have crashed after setting Magic but before
                    // writing Version. If Magic is valid, complete the initialization.
                    if (Volatile.Read(ref meta->Magic) != JournalFormat.MetadataMagic)
                        throw new InvalidOperationException("Invalid journal metadata file: wrong magic.");

                    Interlocked.CompareExchange(ref meta->NextWriteOffset, JournalFormat.DataStartOffset, 0);
                    Volatile.Write(ref meta->Version, JournalFormat.MetadataVersion);
                    return;
                }
            }

            if (Volatile.Read(ref meta->Magic) != JournalFormat.MetadataMagic)
                throw new InvalidOperationException("Invalid journal metadata file: wrong magic.");

            var version = Volatile.Read(ref meta->Version);
            if (version != JournalFormat.MetadataVersion)
                throw new InvalidOperationException($"Unsupported journal version: {version}");
        }
    }

    private long ReserveSpace(int totalLength) =>
        Interlocked.Add(ref _meta->NextWriteOffset, totalLength) - totalLength;

    private long GetNextWriteOffset() =>
        Volatile.Read(ref _meta->NextWriteOffset);

    private IEnumerable<JournalRecord> ReadRecords()
    {
        var tail = GetNextWriteOffset();
        long offset = JournalFormat.DataStartOffset;
        var headerBuf = new byte[JournalFormat.RecordHeaderSize];

        while (offset + JournalFormat.MinRecordSize <= tail)
        {
            if (RandomAccess.Read(_fileHandle, headerBuf, offset) < JournalFormat.RecordHeaderSize)
                yield break;

            var header = MemoryMarshal.Read<RecordHeader>(headerBuf);
            if (!header.IsValid())
            {
                offset = ScanForNextMagic(offset + 1, tail);
                continue;
            }

            var totalRecordLength = JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + header.PayloadLength);
            if (offset + totalRecordLength > tail)
                yield break;

            var payloadBuf = new byte[header.PayloadLength];
            if (header.PayloadLength > 0 &&
                RandomAccess.Read(_fileHandle, payloadBuf, offset + JournalFormat.RecordHeaderSize) < header.PayloadLength)
            {
                offset = ScanForNextMagic(offset + 1, tail);
                continue;
            }

            if (JournalFormat.ComputeChecksum(payloadBuf) != header.Checksum)
            {
                offset = ScanForNextMagic(offset + 1, tail);
                continue;
            }

            yield return new JournalRecord(offset, payloadBuf);
            offset += totalRecordLength;
        }
    }

    /// <summary>
    /// Scans forward from <paramref name="startOffset"/> looking for the next
    /// <see cref="JournalFormat.RecordHeaderMagic"/> byte pattern at an aligned offset.
    /// Returns <paramref name="endOffset"/> if none found.
    /// </summary>
    [SkipLocalsInit]
    private long ScanForNextMagic(long startOffset, long endOffset)
    {
        Span<byte> chunk = stackalloc byte[4096];
        var offset = JournalFormat.AlignUp(startOffset);

        while (offset + JournalFormat.MinRecordSize <= endOffset)
        {
            var toRead = (int)Math.Min(chunk.Length, endOffset - offset);
            var bytesRead = RandomAccess.Read(_fileHandle, chunk[..toRead], offset);
            if (bytesRead < sizeof(uint))
                break;

            for (var i = 0; i <= bytesRead - sizeof(uint); i += JournalFormat.RecordAlignment)
            {
                if (MemoryMarshal.Read<uint>(chunk[i..]) == JournalFormat.RecordHeaderMagic)
                    return offset + i;
            }

            // Advance to the next unchecked alignment boundary
            var lastCheckedOffset = bytesRead - (bytesRead % JournalFormat.RecordAlignment);
            offset += Math.Max(lastCheckedOffset, JournalFormat.RecordAlignment);
        }

        return endOffset;
    }
}
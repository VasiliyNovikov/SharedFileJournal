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
/// into memory with <see cref="MemoryMappedFile.CreateFromFile(string, FileMode, string?, long, MemoryMappedFileAccess)"/>.
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
/// (<see cref="Append"/>, <see cref="ReadAll"/>, <see cref="Recover"/>) are in flight
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
        options ??= new SharedJournalOptions();

        EnsureJournalFile(path);

        var fileOptions = options.FlushMode == FlushMode.WriteThrough
            ? FileOptions.WriteThrough
            : FileOptions.None;
        _fileHandle = File.OpenHandle(
            path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite, fileOptions);

        // Open a FileStream with ReadWrite sharing for the MMF — CreateFromFile(string, ...)
        // opens the file internally without FileShare.ReadWrite, which conflicts with
        // our already-open data handle on Windows.
        FileStream? metaStream = null;
        MemoryMappedFile? metaMap = null;
        MemoryMappedViewAccessor? metaView = null;
        try
        {
            metaStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            metaMap = MemoryMappedFile.CreateFromFile(metaStream, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
            metaView = metaMap.CreateViewAccessor(0, JournalFormat.MetadataFileSize, MemoryMappedFileAccess.ReadWrite);

            byte* rawPtr = null;
            metaView.SafeMemoryMappedViewHandle.AcquirePointer(ref rawPtr);
            rawPtr += metaView.PointerOffset;
            _meta = (MetadataHeader*)rawPtr;
            InitializeOrValidateMetadata(_meta);

            _metaMap = metaMap;
            _metaView = metaView;
        }
        catch
        {
            metaView?.Dispose();
            metaMap?.Dispose();
            // metaStream is disposed by metaMap (leaveOpen: false), only dispose if metaMap wasn't created
            if (metaMap is null)
                metaStream?.Dispose();
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

        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            payload.Length, int.MaxValue - JournalFormat.RecordHeaderSize - JournalFormat.RecordAlignment, nameof(payload));

        var dataLength = JournalFormat.RecordHeaderSize + payload.Length;
        var alignedLength = JournalFormat.AlignRecordSize(dataLength);
        var offset = ReserveSpace(alignedLength);

        byte[]? rented = null;
        var span = dataLength <= 2048
            ? stackalloc byte[dataLength]
            : (rented = ArrayPool<byte>.Shared.Rent(dataLength));
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
            FlushDataFile();

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
    /// Scans the journal from the beginning, validates each record, and updates the
    /// metadata tail to point past the last valid record.
    /// </summary>
    /// <remarks>
    /// This method must not be called while other writers are concurrently appending.
    /// Doing so may cause newly appended records to be orphaned.
    /// </remarks>
    [SkipLocalsInit]
    public JournalRecoveryResult Recover()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var fileLength = RandomAccess.GetLength(_fileHandle);
        var currentTail = GetNextWriteOffset();
        var scanEnd = Math.Max(fileLength, currentTail);

        long offset = JournalFormat.DataStartOffset;
        var validCount = 0;
        Span<byte> headerBuf = stackalloc byte[JournalFormat.RecordHeaderSize];

        while (offset + JournalFormat.MinRecordSize <= scanEnd)
        {
            if (RandomAccess.Read(_fileHandle, headerBuf, offset) < JournalFormat.RecordHeaderSize)
                break;

            var header = MemoryMarshal.Read<RecordHeader>(headerBuf);
            if (!header.IsValid())
                break;

            var totalRecordLength = JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + header.PayloadLength);
            if (offset + totalRecordLength > scanEnd)
                break;

            var payloadBuf = new byte[header.PayloadLength];
            if (header.PayloadLength > 0 &&
                RandomAccess.Read(_fileHandle, payloadBuf, offset + JournalFormat.RecordHeaderSize) < header.PayloadLength)
                break;

            if (JournalFormat.ComputeChecksum(payloadBuf) != header.Checksum)
                break;

            offset += totalRecordLength;
            validCount++;
        }

        if (offset != currentTail)
        {
            var previous = CompareAndSetNextWriteOffset(currentTail, offset);
            if (previous != currentTail)
                throw new InvalidOperationException(
                    $"Recovery failed: concurrent modification detected. Expected tail {currentTail}, actual {previous}.");
        }

        return new JournalRecoveryResult(offset, validCount);
    }

    /// <summary>
    /// Releases all resources used by this journal instance.
    /// </summary>
    /// <remarks>
    /// Callers must ensure that no concurrent <see cref="Append"/>, <see cref="ReadAll"/>,
    /// or <see cref="Recover"/> operations are in flight when this method is called.
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

    private void FlushDataFile()
    {
        RandomAccess.FlushToDisk(_fileHandle);
    }

    private static void EnsureJournalFile(string path)
    {
        using var fs = new FileStream(
            path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (fs.Length < JournalFormat.MetadataFileSize)
            fs.SetLength(JournalFormat.MetadataFileSize);
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
        }
    }

    private long ReserveSpace(int totalLength) =>
        Interlocked.Add(ref _meta->NextWriteOffset, totalLength) - totalLength;

    private long GetNextWriteOffset() =>
        Volatile.Read(ref _meta->NextWriteOffset);

    private long CompareAndSetNextWriteOffset(long expected, long value) =>
        Interlocked.CompareExchange(ref _meta->NextWriteOffset, value, expected);

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
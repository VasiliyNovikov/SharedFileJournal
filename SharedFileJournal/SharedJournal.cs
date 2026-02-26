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
/// <b>Platform assumptions:</b> Works on Windows and Linux with .NET 8+. Relies on
/// <see cref="MemoryMappedFile.CreateFromFile(string, FileMode)"/> (no named shared memory)
/// and <see cref="RandomAccess"/> for portable I/O.
/// </para>
/// </remarks>
public sealed unsafe class SharedJournal : IDisposable
{
    private readonly string _filePath;
    private readonly SharedJournalOptions _options;
    private readonly SafeFileHandle _fileHandle;
    private readonly MemoryMappedFile _metaMap;
    private readonly MemoryMappedViewAccessor _metaView;
    private readonly MetadataHeader* _meta;
    private int _disposed;

    private SharedJournal(
        string filePath,
        SharedJournalOptions options,
        SafeFileHandle fileHandle,
        MemoryMappedFile metaMap,
        MemoryMappedViewAccessor metaView,
        MetadataHeader* meta)
    {
        _filePath = filePath;
        _options = options;
        _fileHandle = fileHandle;
        _metaMap = metaMap;
        _metaView = metaView;
        _meta = meta;
    }

    /// <summary>
    /// Opens or creates a journal at the specified file path.
    /// </summary>
    /// <param name="path">
    /// Path to the journal file. The file contains both the metadata header and record data.
    /// </param>
    /// <param name="options">Optional configuration. Uses defaults if <c>null</c>.</param>
    /// <returns>A <see cref="SharedJournal"/> instance ready for concurrent use.</returns>
    public static SharedJournal Open(string path, SharedJournalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new SharedJournalOptions();

        EnsureJournalFile(path);

        var fileOptions = options.FlushMode == FlushMode.WriteThrough
            ? FileOptions.WriteThrough
            : FileOptions.None;
        var fileHandle = File.OpenHandle(
            path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite, fileOptions);

        MemoryMappedFile? metaMap = null;
        MemoryMappedViewAccessor? metaView = null;
        try
        {
            metaMap = MemoryMappedFile.CreateFromFile(
                path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
            metaView = metaMap.CreateViewAccessor(
                0, JournalFormat.MetadataFileSize, MemoryMappedFileAccess.ReadWrite);

            byte* rawPtr = null;
            metaView.SafeMemoryMappedViewHandle.AcquirePointer(ref rawPtr);
            rawPtr += metaView.PointerOffset;
            var meta = (MetadataHeader*)rawPtr;
            InitializeOrValidateMetadata(meta);

            return new SharedJournal(path, options, fileHandle, metaMap, metaView, meta);
        }
        catch
        {
            metaView?.Dispose();
            metaMap?.Dispose();
            fileHandle.Dispose();
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
            payload.Length, int.MaxValue - JournalFormat.MinRecordSize, nameof(payload));

        var totalLength = JournalFormat.RecordHeaderSize + payload.Length;
        var offset = ReserveSpace(totalLength);

        byte[]? rented = null;
        var span = totalLength <= 2048
            ? stackalloc byte[totalLength]
            : (rented = ArrayPool<byte>.Shared.Rent(totalLength));
        try
        {
            JournalFormat.WriteRecord(span, payload);
            RandomAccess.Write(_fileHandle, span[..totalLength], offset);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }

        if (flushMode == FlushMode.WriteThrough)
            FlushDataFile();

        return new JournalAppendResult(offset, totalLength);
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
    /// <param name="truncate">
    /// If <c>true</c>, physically truncates the data file to the valid end offset.
    /// </param>
    [SkipLocalsInit]
    public JournalRecoveryResult Recover(bool truncate = false)
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

            var totalRecordLength = JournalFormat.RecordHeaderSize + header.PayloadLength;
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
            CompareAndSetNextWriteOffset(currentTail, offset);

        var wasTruncated = false;
        if (truncate && offset < fileLength)
        {
            using var fs = new FileStream(
                _filePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            fs.SetLength(offset);
            wasTruncated = true;
        }

        return new JournalRecoveryResult(offset, validCount, wasTruncated);
    }

    /// <inheritdoc/>
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

    private static unsafe void InitializeOrValidateMetadata(MetadataHeader* meta)
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
                    throw new TimeoutException("Timed out waiting for journal metadata initialization.");
            }

            if (Volatile.Read(ref meta->Magic) != JournalFormat.MetadataMagic)
                throw new InvalidOperationException("Invalid journal metadata file: wrong magic.");
        }
    }

    private long ReserveSpace(int totalLength) =>
        Interlocked.Add(ref _meta->NextWriteOffset, totalLength) - totalLength;

    private long GetNextWriteOffset() =>
        Volatile.Read(ref _meta->NextWriteOffset);

    private void CompareAndSetNextWriteOffset(long expected, long value) =>
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

            var totalRecordLength = JournalFormat.RecordHeaderSize + header.PayloadLength;
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
    /// <see cref="JournalFormat.RecordHeaderMagic"/> byte pattern.
    /// Returns <paramref name="endOffset"/> if none found.
    /// </summary>
    [SkipLocalsInit]
    private long ScanForNextMagic(long startOffset, long endOffset)
    {
        Span<byte> chunk = stackalloc byte[4096];
        var offset = startOffset;

        while (offset + JournalFormat.MinRecordSize <= endOffset)
        {
            var toRead = (int)Math.Min(chunk.Length, endOffset - offset);
            var bytesRead = RandomAccess.Read(_fileHandle, chunk[..toRead], offset);
            if (bytesRead < sizeof(uint))
                break;

            for (var i = 0; i <= bytesRead - sizeof(uint); i++)
            {
                if (MemoryMarshal.Read<uint>(chunk[i..]) == JournalFormat.RecordHeaderMagic)
                    return offset + i;
            }

            // Overlap by 3 bytes so magic spanning chunk boundaries isn't missed
            offset += bytesRead - (sizeof(uint) - 1);
        }

        return endOffset;
    }
}
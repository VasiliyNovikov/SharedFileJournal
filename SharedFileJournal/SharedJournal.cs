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
/// <b>Companion lock file:</b> Each journal instance acquires a shared lock on a sidecar
/// file (<c>&lt;path&gt;.lock</c>) for the duration of its lifetime. The
/// <see cref="Compact"/> method acquires this lock exclusively, ensuring no journal
/// instances can be open during compaction and that the lock is held through the file
/// replacement.
/// </para>
/// <para>
/// <b>Disposal contract:</b> Callers must ensure that no concurrent operations
/// (<see cref="Append"/>, <see cref="ReadAll"/>) are in flight
/// when <see cref="Dispose"/> is called. This is a deliberate design choice to avoid
/// adding synchronization overhead to the lock-free write path.
/// </para>
/// </remarks>
public sealed class SharedJournal : IDisposable
{
    private readonly JournalLockFile? _lock;
    private readonly SafeFileHandle _fileHandle;
    private readonly MemoryMappedView<MetadataHeader> _metaView;
    private readonly int _maxPayloadLength;
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

        var opts = options ?? SharedJournalOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(opts.MaxPayloadLength, 1, nameof(SharedJournalOptions.MaxPayloadLength));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(opts.MaxPayloadLength, int.MaxValue - JournalFormat.RecordHeaderSize - JournalFormat.RecordAlignment, nameof(SharedJournalOptions.MaxPayloadLength));
        _maxPayloadLength = opts.MaxPayloadLength;

        try
        {
            if (opts.AcquireLockFile)
                _lock = new JournalLockFile(path, FileShare.ReadWrite);

            var fileOptions = opts.FlushMode == FlushMode.None
                ? FileOptions.None
                : FileOptions.WriteThrough;

            _fileHandle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, opts.FileShare, fileOptions);

            if (RandomAccess.GetLength(_fileHandle) < JournalFormat.MetadataFileSize)
                RandomAccess.SetLength(_fileHandle, JournalFormat.MetadataFileSize);

            _metaView = new MemoryMappedView<MetadataHeader>(_fileHandle, 0);
            InitializeOrValidateMetadata(ref _metaView.Value);
        }
        catch
        {
            _metaView?.Dispose();
            _fileHandle?.Dispose();
            _lock?.Dispose();
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
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, _maxPayloadLength, nameof(payload));

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
    /// Reads all valid records from the journal sequentially, skipping corrupted or
    /// incomplete regions and continuing to recover subsequent records.
    /// </summary>
    /// <remarks>
    /// When corruption is encountered, the reader scans forward for the next valid record
    /// header and may write skip markers to the journal file so that future reads can
    /// efficiently bypass the corrupted region.
    /// </remarks>
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
    /// and corrupted regions), then atomically replaces the original file. The read pass may
    /// write skip markers to the source file when corruption is encountered (see
    /// <see cref="ReadAll"/>), but the record data is not otherwise modified until the swap.
    /// </para>
    /// <para>
    /// <b>Lock file protocol:</b> Acquires an exclusive lock on a companion lock file
    /// (<c>&lt;path&gt;.lock</c>) before opening the source journal. Normal
    /// <see cref="SharedJournal"/> instances hold this lock in shared mode, so the exclusive
    /// acquisition blocks until all existing instances are closed and prevents new ones from
    /// opening. The lock is held through the entire operation including the
    /// <see cref="File.Move(string, string, bool)"/> that replaces the journal file,
    /// eliminating the race window between closing the source and the rename.
    /// </para>
    /// </remarks>
    /// <param name="path">Path to the journal file to compact.</param>
    /// <param name="options">
    /// Optional configuration. If the journal was written with a non-default
    /// <see cref="SharedJournalOptions.MaxPayloadLength"/>, the same value should be passed here;
    /// otherwise records exceeding the default limit will be silently treated as corrupted and
    /// dropped during the read pass. Uses defaults if <c>null</c>.
    /// </param>
    public static void Compact(string path, SharedJournalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        var tempPath = path + ".compact";
        var maxPayloadLength = (options ?? SharedJournalOptions.Default).MaxPayloadLength;

        // Exclusive lock — held through entire operation including File.Move
        using var journalLock = new JournalLockFile(path, FileShare.None);

        File.Delete(tempPath);

        var sourceOptions = new SharedJournalOptions
            { FileShare = FileShare.None, AcquireLockFile = false, MaxPayloadLength = maxPayloadLength };
        var tempOptions = new SharedJournalOptions
            { AcquireLockFile = false, MaxPayloadLength = maxPayloadLength };

        using (var source = new SharedJournal(path, sourceOptions))
        using (var temp = new SharedJournal(tempPath, tempOptions))
        {
            foreach (var record in source.ReadAll())
                temp.Append(record.Payload.Span);
            temp.Flush();
        }

        File.Move(tempPath, path, overwrite: true);
        // journalLock disposed here — after the rename
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

        _metaView.Dispose();
        _fileHandle.Dispose();
        _lock?.Dispose();
    }

    private static void InitializeOrValidateMetadata(ref MetadataHeader meta)
    {
        if (Interlocked.CompareExchange(ref meta.Magic, JournalFormat.MetadataMagic, 0) == 0)
        {
            // Won the initialization race — write NextWriteOffset before Version
            // so followers spinning on Version cannot proceed with an uninitialized tail
            Volatile.Write(ref meta.NextWriteOffset, (long)JournalFormat.DataStartOffset);
            Volatile.Write(ref meta.Version, JournalFormat.MetadataVersion);
        }
        else
        {
            // Another process initialized it — spin until version is written
            var deadline = Environment.TickCount64 + 5000;
            SpinWait spin = default;
            while (Volatile.Read(ref meta.Version) == 0)
            {
                spin.SpinOnce();
                if (Environment.TickCount64 > deadline)
                {
                    // The initializer may have crashed after setting Magic but before
                    // writing Version. If Magic is valid, complete the initialization.
                    if (Volatile.Read(ref meta.Magic) != JournalFormat.MetadataMagic)
                        throw new InvalidOperationException("Invalid journal metadata file: wrong magic.");

                    Interlocked.CompareExchange(ref meta.NextWriteOffset, JournalFormat.DataStartOffset, 0);
                    ValidateNextWriteOffset(Volatile.Read(ref meta.NextWriteOffset));
                    Volatile.Write(ref meta.Version, JournalFormat.MetadataVersion);
                    return;
                }
            }

            if (Volatile.Read(ref meta.Magic) != JournalFormat.MetadataMagic)
                throw new InvalidOperationException("Invalid journal metadata file: wrong magic.");

            var version = Volatile.Read(ref meta.Version);
            if (version != JournalFormat.MetadataVersion)
                throw new InvalidOperationException($"Unsupported journal version: {version}");

            ValidateNextWriteOffset(Volatile.Read(ref meta.NextWriteOffset));
        }
    }

    private static void ValidateNextWriteOffset(long nextWriteOffset)
    {
        if (nextWriteOffset < JournalFormat.DataStartOffset || nextWriteOffset % JournalFormat.RecordAlignment != 0)
            throw new InvalidOperationException(
                $"Corrupted journal metadata: NextWriteOffset ({nextWriteOffset}) is invalid. " +
                $"Must be >= {JournalFormat.DataStartOffset} and aligned to {JournalFormat.RecordAlignment}.");
    }

    private long ReserveSpace(int totalLength) =>
        Interlocked.Add(ref _metaView.Value.NextWriteOffset, totalLength) - totalLength;

    private long GetNextWriteOffset() =>
        Volatile.Read(ref _metaView.Value.NextWriteOffset);

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
            var originalSlotValue = RecordHeader.MagicAndPayloadLength(ref header);

            if (header.IsSkip())
            {
                var skipLength = JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + header.PayloadLength);
                if (offset + skipLength <= tail)
                {
                    offset += skipLength;
                    continue;
                }
                // Corrupted skip marker — fall through to scan
            }

            if (!header.IsValid() || header.PayloadLength > _maxPayloadLength)
            {
                SkipCorruptedRegion(ref offset, tail, originalSlotValue);
                continue;
            }

            var totalRecordLength = JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + header.PayloadLength);
            if (offset + totalRecordLength > tail)
                yield break;

            var payloadBuf = new byte[header.PayloadLength];
            if (header.PayloadLength > 0 &&
                RandomAccess.Read(_fileHandle, payloadBuf, offset + JournalFormat.RecordHeaderSize) < header.PayloadLength)
            {
                SkipCorruptedRegion(ref offset, tail, originalSlotValue);
                continue;
            }

            if (JournalFormat.ComputeChecksum(payloadBuf) != header.Checksum)
            {
                SkipCorruptedRegion(ref offset, tail, originalSlotValue);
                continue;
            }

            yield return new JournalRecord(offset, payloadBuf);
            offset += totalRecordLength;
        }
    }

    private void SkipCorruptedRegion(ref long offset, long tail, long originalSlotValue)
    {
        var maxGap = int.MaxValue - JournalFormat.RecordAlignment;
        var scanLimit = (long)Math.Min((ulong)tail, (ulong)offset + (ulong)maxGap);
        var nextOffset = ScanForNextMagic(offset + 1, scanLimit);
        if (nextOffset > offset && nextOffset < tail)
        {
            // Only write a skip marker when the observed magic is not a recognized
            // record or skip magic. If magic == RecordHeaderMagic, a concurrent writer
            // may have written the header (magic + payload length) but not yet completed
            // the checksum and payload — the CAS would match the partially-written header
            // and overwrite it with a skip marker, permanently losing the in-flight record.
            // For unrecognized magic (zeros, garbage), no writer can be in-flight at this
            // offset, so the skip marker is safe. If a writer starts after we captured
            // originalSlotValue, it will write RecordHeaderMagic, making the CAS fail.
            var observedMagic = (uint)(originalSlotValue & 0xFFFFFFFF);
            if (observedMagic != JournalFormat.RecordHeaderMagic && observedMagic != JournalFormat.SkipHeaderMagic)
                TryWriteSkipMarker(offset, nextOffset, originalSlotValue);
        }
        offset = nextOffset;
    }

    /// <summary>
    /// Attempts to atomically write a skip marker at <paramref name="gapStart"/> covering
    /// the gap up to <paramref name="gapEnd"/>. Uses an 8-byte CAS (magic + PayloadLength)
    /// via a temporary memory-mapped view to avoid overwriting a concurrent writer's record.
    /// The caller must ensure the gap fits in a single skip marker (at most ~2 GB).
    /// </summary>
    private void TryWriteSkipMarker(long gapStart, long gapEnd, long originalSlotValue)
    {
        if (gapStart + JournalFormat.RecordHeaderSize > RandomAccess.GetLength(_fileHandle))
            return;

        using var view = new MemoryMappedView<RecordHeader>(_fileHandle, gapStart);
        var skipHeader = RecordHeader.CreateSkip((int)(gapEnd - gapStart) - JournalFormat.RecordHeaderSize);
        Interlocked.CompareExchange(ref RecordHeader.MagicAndPayloadLength(ref view.Value), RecordHeader.MagicAndPayloadLength(ref skipHeader), originalSlotValue);
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
                var magic = MemoryMarshal.Read<uint>(chunk[i..]);
                if (magic == JournalFormat.RecordHeaderMagic || magic == JournalFormat.SkipHeaderMagic)
                    return offset + i;
            }

            // Advance past the last checked alignment boundary
            var lastChecked = (bytesRead - sizeof(uint)) / JournalFormat.RecordAlignment * JournalFormat.RecordAlignment;
            offset += lastChecked + JournalFormat.RecordAlignment;
        }

        return endOffset;
    }
}
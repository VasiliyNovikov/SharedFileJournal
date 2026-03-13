using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;

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
    private readonly int _readAheadSize;
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
        ArgumentOutOfRangeException.ThrowIfLessThan(opts.ReadAheadSize, 1, nameof(SharedJournalOptions.ReadAheadSize));
        _readAheadSize = opts.ReadAheadSize;

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

    #region Append / AppendAsync

    /// <summary>
    /// Validates, reserves space, allocates a buffer, and serializes the record.
    /// The caller owns the returned buffer and must return it to <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    private (byte[] Buffer, int DataLength, long Offset, int AlignedLength) PrepareAppend(ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, _maxPayloadLength, nameof(payload));

        var dataLength = JournalFormat.RecordHeaderSize + payload.Length;
        var alignedLength = JournalFormat.AlignRecordSize(dataLength);
        var offset = Interlocked.Add(ref _metaView.Value.NextWriteOffset, alignedLength) - alignedLength;

        var buffer = ArrayPool<byte>.Shared.Rent(dataLength);
        JournalFormat.WriteRecord(buffer, payload);

        return (buffer, dataLength, offset, alignedLength);
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
    public JournalAppendResult Append(ReadOnlySpan<byte> payload, FlushMode flushMode = FlushMode.None)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var (buffer, dataLength, offset, alignedLength) = PrepareAppend(payload);
        try
        {
            RandomAccess.Write(_fileHandle, buffer.AsSpan(0, dataLength), offset);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (flushMode == FlushMode.WriteThrough)
            RandomAccess.FlushToDisk(_fileHandle);

        return new JournalAppendResult(offset, alignedLength);
    }

    /// <summary>
    /// Appends a record with the given payload to the journal.
    /// </summary>
    /// <param name="payload">The record payload. May be empty.</param>
    /// <param name="flushMode">
    /// Per-record flush override. When set to <see cref="FlushMode.WriteThrough"/>,
    /// the data file is flushed to disk after this write regardless of the journal-level setting.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Information about the appended record.</returns>
    public ValueTask<JournalAppendResult> AppendAsync(ReadOnlySpan<byte> payload, FlushMode flushMode = FlushMode.None, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var (buffer, dataLength, offset, alignedLength) = PrepareAppend(payload);
        return WriteAndCompleteAppendAsync(buffer, dataLength, offset, alignedLength, flushMode, cancellationToken);
    }

    private async ValueTask<JournalAppendResult> WriteAndCompleteAppendAsync(byte[] buffer, int dataLength, long offset, int alignedLength, FlushMode flushMode, CancellationToken cancellationToken)
    {
        try
        {
            await RandomAccess.WriteAsync(_fileHandle, buffer.AsMemory(0, dataLength), offset, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (flushMode == FlushMode.WriteThrough)
            RandomAccess.FlushToDisk(_fileHandle);

        return new JournalAppendResult(offset, alignedLength);
    }

    #endregion

    #region ReadAll / ReadAllAsync

    /// <summary>
    /// Reads all valid records from the journal sequentially, skipping corrupted or
    /// incomplete regions and continuing to recover subsequent records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Payload lifetime:</b> Each <see cref="JournalRecord.Payload"/> is backed by a
    /// pooled buffer that is reused across iterations. The payload is only valid until the
    /// next call to <c>MoveNext</c> on the enumerator (or until enumeration ends). Callers
    /// that need to retain payload data must copy it before advancing, for example via
    /// <see cref="ReadOnlyMemory{T}.ToArray"/>.
    /// </para>
    /// <para>
    /// When corruption is encountered, the reader scans forward for the next valid record
    /// header and may write skip markers to the journal file so that future reads can
    /// efficiently bypass the corrupted region.
    /// </para>
    /// </remarks>
    public IEnumerable<JournalRecord> ReadAll()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return CreateReadSequence();
    }

    /// <summary>
    /// Asynchronously reads all valid records from the journal sequentially, skipping corrupted or
    /// incomplete regions and continuing to recover subsequent records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Payload lifetime:</b> Each <see cref="JournalRecord.Payload"/> is backed by a
    /// pooled buffer that is reused across iterations. The payload is only valid until the
    /// next call to <c>MoveNextAsync</c> on the enumerator (or until enumeration ends). Callers
    /// that need to retain payload data must copy it before advancing, for example via
    /// <see cref="ReadOnlyMemory{T}.ToArray"/>.
    /// </para>
    /// <para>
    /// When corruption is encountered, the reader scans forward for the next valid record
    /// header and may write skip markers to the journal file so that future reads can
    /// efficiently bypass the corrupted region.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public IAsyncEnumerable<JournalRecord> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return CreateReadSequence(cancellationToken: cancellationToken);
    }

    #endregion

    #region Flush / FlushAsync

    /// <summary>
    /// Flushes all buffered data to the underlying storage device.
    /// </summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        RandomAccess.FlushToDisk(_fileHandle);
    }

    /// <summary>
    /// Asynchronously flushes all buffered data to the underlying storage device.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        RandomAccess.FlushToDisk(_fileHandle);
        return ValueTask.CompletedTask;
    }

    #endregion

    private JournalRecordSequence CreateReadSequence(bool allowSkipMarkerWrites = true, CancellationToken cancellationToken = default) =>
        new(_fileHandle, _maxPayloadLength, _readAheadSize, () => Volatile.Read(ref _metaView.Value.NextWriteOffset), allowSkipMarkerWrites, cancellationToken);

    #region Compact / CompactAsync

    /// <summary>
    /// Compacts the journal at the specified path, reclaiming space from gaps left by
    /// crashed writers and removing corrupted or incomplete records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Creates a temporary journal, copies all valid records from the source (skipping gaps
    /// and corrupted regions), then atomically replaces the original file. The read pass uses
    /// the same validation and recovery rules as <see cref="ReadAll"/>, but does not write
    /// skip markers back to the source file.
    /// </para>
    /// <para>
    /// <b>Lock file protocol:</b> Attempts to acquire an exclusive lock on a companion lock file
    /// (<c>&lt;path&gt;.lock</c>) before opening the source journal. Normal
    /// <see cref="SharedJournal"/> instances hold this lock in shared mode, so the exclusive
    /// acquisition fails immediately with <see cref="IOException"/> if any instance is open.
    /// When acquired, the lock prevents new instances from opening and is held through the
    /// entire operation including the
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

        var sourceOptions = new SharedJournalOptions { FileShare = FileShare.None, AcquireLockFile = false, MaxPayloadLength = maxPayloadLength };
        var tempOptions = new SharedJournalOptions { AcquireLockFile = false, MaxPayloadLength = maxPayloadLength };

        using (var source = new SharedJournal(path, sourceOptions))
        using (var temp = new SharedJournal(tempPath, tempOptions))
        {
            foreach (var record in source.CreateReadSequence(allowSkipMarkerWrites: false))
                temp.Append(record.Payload.Span);
            temp.Flush();
        }

        File.Move(tempPath, path, overwrite: true);
        // journalLock disposed here — after the rename
    }

    /// <summary>
    /// Asynchronously compacts the journal at the specified path, reclaiming space from gaps left by
    /// crashed writers and removing corrupted or incomplete records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Creates a temporary journal, copies all valid records from the source (skipping gaps
    /// and corrupted regions), then atomically replaces the original file. The read pass uses
    /// the same validation and recovery rules as <see cref="ReadAllAsync"/>, but does not write
    /// skip markers back to the source file.
    /// </para>
    /// <para>
    /// <b>Lock file protocol:</b> Attempts to acquire an exclusive lock on a companion lock file
    /// (<c>&lt;path&gt;.lock</c>) before opening the source journal. Normal
    /// <see cref="SharedJournal"/> instances hold this lock in shared mode, so the exclusive
    /// acquisition fails immediately with <see cref="IOException"/> if any instance is open.
    /// When acquired, the lock prevents new instances from opening and is held through the
    /// entire operation including the
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
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public static async ValueTask CompactAsync(string path, SharedJournalOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();

        var tempPath = path + ".compact";
        var maxPayloadLength = (options ?? SharedJournalOptions.Default).MaxPayloadLength;

        // Exclusive lock — held through entire operation including File.Move
        using var journalLock = new JournalLockFile(path, FileShare.None);
        var moved = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(tempPath);

            var sourceOptions = new SharedJournalOptions { FileShare = FileShare.None, AcquireLockFile = false, MaxPayloadLength = maxPayloadLength };
            var tempOptions = new SharedJournalOptions { AcquireLockFile = false, MaxPayloadLength = maxPayloadLength };

            using (var source = new SharedJournal(path, sourceOptions))
            using (var temp = new SharedJournal(tempPath, tempOptions))
            {
                await foreach (var record in source.CreateReadSequence(allowSkipMarkerWrites: false, cancellationToken: cancellationToken))
                    await temp.AppendAsync(record.Payload.Span, cancellationToken: cancellationToken);
                await temp.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, path, overwrite: true);
            moved = true;
        }
        finally
        {
            if (!moved)
                File.Delete(tempPath);
        }
    }

    #endregion

    #region Lifecycle

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

    #endregion

    #region Initialization

    private static void InitializeOrValidateMetadata(ref MetadataHeader meta)
    {
        if (Interlocked.CompareExchange(ref meta.Magic, JournalFormat.MetadataMagic, 0) == 0)
        {
            // Won the initialization race — CAS NextWriteOffset so a concurrent
            // completer cannot overwrite an already-advanced value
            Interlocked.CompareExchange(ref meta.NextWriteOffset, JournalFormat.DataStartOffset, 0);
            ValidateNextWriteOffset(Volatile.Read(ref meta.NextWriteOffset));
            Volatile.Write(ref meta.Version, JournalFormat.MetadataVersion);
        }
        else
        {
            if (Volatile.Read(ref meta.Magic) != JournalFormat.MetadataMagic)
                throw new InvalidOperationException("Invalid journal metadata file: wrong magic.");

            var version = Volatile.Read(ref meta.Version);
            if (version == 0)
            {
                // Version not yet visible — the initializer is either still running
                // or crashed. Safely complete initialization: CAS guards
                // NextWriteOffset against overwriting a value already advanced by appends.
                Interlocked.CompareExchange(ref meta.NextWriteOffset, JournalFormat.DataStartOffset, 0);
                ValidateNextWriteOffset(Volatile.Read(ref meta.NextWriteOffset));
                Volatile.Write(ref meta.Version, JournalFormat.MetadataVersion);
            }
            else
            {
                if (version != JournalFormat.MetadataVersion)
                    throw new InvalidOperationException($"Unsupported journal version: {version}");

                ValidateNextWriteOffset(Volatile.Read(ref meta.NextWriteOffset));
            }
        }
    }

    private static void ValidateNextWriteOffset(long nextWriteOffset)
    {
        if (nextWriteOffset < JournalFormat.DataStartOffset || nextWriteOffset % JournalFormat.RecordAlignment != 0)
            throw new InvalidOperationException($"Corrupted journal metadata: NextWriteOffset ({nextWriteOffset}) is invalid. " +
                                                $"Must be >= {JournalFormat.DataStartOffset} and aligned to {JournalFormat.RecordAlignment}.");
    }

    #endregion
}

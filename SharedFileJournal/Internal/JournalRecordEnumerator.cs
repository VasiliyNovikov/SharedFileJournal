using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Win32.SafeHandles;

namespace SharedFileJournal.Internal;

/// <summary>
/// Produces fresh journal record enumerators for each sync or async enumeration pass.
/// </summary>
internal sealed class JournalRecordSequence(SafeFileHandle fileHandle, int maxPayloadLength, int readAheadSize, Func<long> getTail, bool allowSkipMarkerWrites = true, CancellationToken defaultCancellationToken = default)
    : IEnumerable<JournalRecord>, IAsyncEnumerable<JournalRecord>
{
    private long _tail = long.MinValue;

    private long GetOrCreateTail()
    {
        var tail = Volatile.Read(ref _tail);
        if (tail != long.MinValue)
            return tail;

        tail = getTail();
        var publishedTail = Interlocked.CompareExchange(ref _tail, tail, long.MinValue);
        return publishedTail == long.MinValue ? tail : publishedTail;
    }

    public IEnumerator<JournalRecord> GetEnumerator() => new JournalRecordEnumerator(fileHandle, maxPayloadLength, readAheadSize, GetOrCreateTail(), allowSkipMarkerWrites: allowSkipMarkerWrites);
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IAsyncEnumerator<JournalRecord> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new JournalRecordEnumerator(fileHandle, maxPayloadLength, readAheadSize, GetOrCreateTail(),
                                    allowSkipMarkerWrites,
                                    cancellationToken == CancellationToken.None ? defaultCancellationToken : cancellationToken);
}

/// <summary>
/// Enumerates journal records both synchronously and asynchronously, sharing
/// validation, corruption recovery, and skip-marker logic between the two paths.
/// </summary>
internal sealed class JournalRecordEnumerator : IEnumerator<JournalRecord>, IAsyncEnumerator<JournalRecord>
{
    private readonly SafeFileHandle _fileHandle;
    private readonly int _maxPayloadLength;
    private readonly FileReadBuffer _readBuf;
    private readonly long _tail;
    private readonly bool _allowSkipMarkerWrites;
    private readonly CancellationToken _cancellationToken;
    private long _offset;
    private JournalRecord _current;

    internal JournalRecordEnumerator(SafeFileHandle fileHandle, int maxPayloadLength, int readAheadSize, long tail, bool allowSkipMarkerWrites, CancellationToken cancellationToken = default)
    {
        _fileHandle = fileHandle;
        _maxPayloadLength = maxPayloadLength;
        _readBuf = new FileReadBuffer(fileHandle, readAheadSize);
        _tail = tail;
        _allowSkipMarkerWrites = allowSkipMarkerWrites;
        _cancellationToken = cancellationToken;
        _offset = JournalFormat.DataStartOffset;
    }

    public JournalRecord Current => _current;
    object IEnumerator.Current => _current;

    public void Reset() => throw new NotSupportedException();

    public void Dispose() => _readBuf.Dispose();

    public ValueTask DisposeAsync()
    {
        _readBuf.Dispose();
        return ValueTask.CompletedTask;
    }

    #region MoveNext / MoveNextAsync

    public bool MoveNext()
    {
        while (_offset + JournalFormat.MinRecordSize <= _tail)
        {
            var (result, originalSlotValue) = ValidateRecord(_offset);
            var (outcome, writeSkipMarker) = Advance(result);
            switch (outcome)
            {
                case ReadOutcome.Yield: return true;
                case ReadOutcome.Done: return false;
                case ReadOutcome.HandleCorruption:
                    var gapStart = _offset;
                    _offset = SkipCorruptedRegion(_offset);
                    if (writeSkipMarker && _allowSkipMarkerWrites)
                        TryWriteSkipMarker(gapStart, _offset, originalSlotValue);
                    continue;
                default: continue;
            }
        }
        return false;
    }

    public async ValueTask<bool> MoveNextAsync()
    {
        while (_offset + JournalFormat.MinRecordSize <= _tail)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var (result, originalSlotValue) = await ValidateRecordAsync(_offset);
            var (outcome, writeSkipMarker) = Advance(result);
            switch (outcome)
            {
                case ReadOutcome.Yield: return true;
                case ReadOutcome.Done: return false;
                case ReadOutcome.HandleCorruption:
                    var gapStart = _offset;
                    _offset = await SkipCorruptedRegionAsync(_offset);
                    if (writeSkipMarker && _allowSkipMarkerWrites)
                        TryWriteSkipMarker(gapStart, _offset, originalSlotValue);
                    continue;
                default: continue;
            }
        }
        return false;
    }

    private enum ReadOutcome { Yield, Continue, HandleCorruption, Done }

    /// <summary>
    /// Shared decision logic: updates <see cref="_current"/> and <see cref="_offset"/> for
    /// non-I/O cases (Record, Skip, Truncated) and returns an action for corruption cases.
    /// </summary>
    private (ReadOutcome Outcome, bool WriteSkipMarker) Advance(RecordReadResult result)
    {
        switch (result.Status)
        {
            case RecordStatus.Record:
                _current = new JournalRecord(_offset, result.Payload);
                _offset += result.TotalLength;
                return (ReadOutcome.Yield, false);
            case RecordStatus.Skip:
                _offset += result.TotalLength;
                return (ReadOutcome.Continue, false);
            case RecordStatus.Incomplete:
                return (ReadOutcome.HandleCorruption, false);
            case RecordStatus.Corrupt:
                return (ReadOutcome.HandleCorruption, true);
            default: // Truncated
                return (ReadOutcome.Done, false);
        }
    }

    #endregion

    #region Record Validation

    private (RecordReadResult Result, long OriginalSlotValue) ValidateRecord(long offset)
    {
        var headerMem = _readBuf.Read(offset, JournalFormat.RecordHeaderSize);
        var (earlyResult, originalSlotValue, totalLength, payloadLength, checksum) = ValidateRecordHeader(headerMem, offset);
        if (earlyResult is not null)
            return (earlyResult.Value, originalSlotValue);

        var payloadMem = _readBuf.Read(offset + JournalFormat.RecordHeaderSize, payloadLength);
        return (ValidateRecordPayload(payloadMem, payloadLength, totalLength, checksum), originalSlotValue);
    }

    private async ValueTask<(RecordReadResult Result, long OriginalSlotValue)> ValidateRecordAsync(long offset)
    {
        var headerMem = await _readBuf.ReadAsync(offset, JournalFormat.RecordHeaderSize, _cancellationToken);
        var (earlyResult, originalSlotValue, totalLength, payloadLength, checksum) = ValidateRecordHeader(headerMem, offset);
        if (earlyResult is not null)
            return (earlyResult.Value, originalSlotValue);

        var payloadMem = await _readBuf.ReadAsync(offset + JournalFormat.RecordHeaderSize, payloadLength, _cancellationToken);
        return (ValidateRecordPayload(payloadMem, payloadLength, totalLength, checksum), originalSlotValue);
    }

    private (RecordReadResult? EarlyResult, long OriginalSlotValue, int TotalLength, int PayloadLength, ulong Checksum) ValidateRecordHeader(
        ReadOnlyMemory<byte> headerMem, long offset)
    {
        if (headerMem.IsEmpty)
            return (new RecordReadResult(RecordStatus.Truncated), 0, 0, 0, 0);

        var header = MemoryMarshal.Read<RecordHeader>(headerMem.Span);
        var originalSlotValue = RecordHeader.MagicAndPayloadLength(ref header);

        var status = header.Validate();

        if (status is RecordStatus.Corrupt or RecordStatus.Incomplete)
            return (new RecordReadResult(status), originalSlotValue, 0, 0, 0);

        var totalLength = JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + header.PayloadLength);

        if (status == RecordStatus.Skip)
        {
            var skipResult = offset + totalLength <= _tail
                ? new RecordReadResult(RecordStatus.Skip, totalLength: totalLength)
                : new RecordReadResult(RecordStatus.Incomplete);
            return (skipResult, originalSlotValue, totalLength, 0, 0);
        }

        if (header.PayloadLength > _maxPayloadLength || offset + totalLength > _tail)
            return (new RecordReadResult(RecordStatus.Incomplete), originalSlotValue, totalLength, 0, 0);

        return (null, originalSlotValue, totalLength, header.PayloadLength, header.Checksum);
    }

    private static RecordReadResult ValidateRecordPayload(ReadOnlyMemory<byte> payloadMem, int payloadLength, int totalLength, ulong checksum)
    {
        if (payloadMem.IsEmpty && payloadLength > 0)
            return new RecordReadResult(RecordStatus.Incomplete);

        if (JournalFormat.ComputeChecksum(payloadMem.Span) != checksum)
            return new RecordReadResult(RecordStatus.Incomplete);

        return new RecordReadResult(RecordStatus.Record, totalLength, payloadMem);
    }

    #endregion

    #region Corruption Recovery

    private long SkipCorruptedRegion(long offset)
    {
        _readBuf.Invalidate();
        var scanLimit = ScanLimit(offset);

        for (var scanOffset = JournalFormat.AlignUp(offset + 1); scanOffset + JournalFormat.MinRecordSize <= scanLimit; scanOffset += JournalFormat.RecordAlignment)
        {
            var mem = _readBuf.Read(scanOffset, sizeof(uint));
            if (mem.IsEmpty)
                break;

            var magic = MemoryMarshal.Read<uint>(mem.Span);
            if (magic is JournalFormat.RecordHeaderMagic or JournalFormat.SkipHeaderMagic)
                return scanOffset;
        }

        return scanLimit;
    }

    private async ValueTask<long> SkipCorruptedRegionAsync(long offset)
    {
        _readBuf.Invalidate();
        var scanLimit = ScanLimit(offset);

        for (var scanOffset = JournalFormat.AlignUp(offset + 1); scanOffset + JournalFormat.MinRecordSize <= scanLimit; scanOffset += JournalFormat.RecordAlignment)
        {
            var mem = await _readBuf.ReadAsync(scanOffset, sizeof(uint), _cancellationToken);
            if (mem.IsEmpty)
                break;

            var magic = MemoryMarshal.Read<uint>(mem.Span);
            if (magic is JournalFormat.RecordHeaderMagic or JournalFormat.SkipHeaderMagic)
                return scanOffset;
        }

        return scanLimit;
    }

    private long ScanLimit(long offset)
    {
        var maxGap = int.MaxValue - JournalFormat.RecordAlignment;
        return (long)Math.Min((ulong)_tail, (ulong)offset + (ulong)maxGap);
    }

    /// <summary>
    /// Attempts to atomically write a skip marker at <paramref name="gapStart"/> covering the gap
    /// up to <paramref name="offset"/>. Uses an 8-byte CAS (magic + PayloadLength) via a temporary
    /// memory-mapped view to avoid overwriting a concurrent writer's record.
    /// </summary>
    private void TryWriteSkipMarker(long gapStart, long offset, long originalSlotValue)
    {
        if (offset > gapStart && offset < _tail && gapStart + JournalFormat.RecordHeaderSize <= RandomAccess.GetLength(_fileHandle))
        {
            using var view = new MemoryMappedView<RecordHeader>(_fileHandle, gapStart);
            var skipHeader = RecordHeader.CreateSkip((int)(offset - gapStart) - JournalFormat.RecordHeaderSize);
            Interlocked.CompareExchange(ref RecordHeader.MagicAndPayloadLength(ref view.Value), RecordHeader.MagicAndPayloadLength(ref skipHeader), originalSlotValue);
        }
    }

    #endregion
}

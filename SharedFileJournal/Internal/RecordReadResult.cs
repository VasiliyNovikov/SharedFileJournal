using System;

namespace SharedFileJournal.Internal;

/// <summary>
/// Result of a full record validation including I/O and checksum verification.
/// </summary>
internal readonly struct RecordReadResult(RecordStatus status, int totalLength = 0, ReadOnlyMemory<byte> payload = default)
{
    public RecordStatus Status => status;
    public int TotalLength => totalLength;
    public ReadOnlyMemory<byte> Payload => payload;
}
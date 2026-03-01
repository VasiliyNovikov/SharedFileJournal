namespace SharedFileJournal.Internal;

/// <summary>
/// Result of a full record validation including I/O and checksum verification.
/// </summary>
internal readonly struct RecordReadResult(RecordStatus status, byte[]? payload = null, int totalLength = 0)
{
    public RecordStatus Status => status;
    public byte[] Payload => payload!;
    public int TotalLength => totalLength;
}
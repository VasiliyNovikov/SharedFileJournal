namespace SharedFileJournal.Internal;

/// <summary>
/// Result of a full record validation including I/O and checksum verification.
/// </summary>
internal readonly struct RecordReadResult(RecordStatus status, int payloadLength = 0, int totalLength = 0)
{
    public RecordStatus Status => status;
    public int PayloadLength => payloadLength;
    public int TotalLength => totalLength;
}
using System.Runtime.InteropServices;

namespace SharedFileJournal.Internal;

/// <summary>
/// On-disk record header (16 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RecordHeader
{
    public uint Magic;
    public int PayloadLength;
    public ulong Checksum;

    public static RecordHeader Create(int payloadLength, ulong checksum) => new()
    {
        Magic = JournalFormat.RecordHeaderMagic,
        PayloadLength = payloadLength,
        Checksum = checksum
    };

    public readonly bool IsValid() =>
        Magic == JournalFormat.RecordHeaderMagic &&
        PayloadLength >= 0 &&
        PayloadLength <= int.MaxValue - JournalFormat.MinRecordSize;
}
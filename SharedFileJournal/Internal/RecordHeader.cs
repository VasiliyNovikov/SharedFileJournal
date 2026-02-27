using System.Runtime.CompilerServices;
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

    /// <summary>
    /// Reinterprets <see cref="Magic"/> and <see cref="PayloadLength"/> as a single <see langword="long"/>
    /// for atomic compare-and-swap operations.
    /// </summary>
    public static ref long MagicAndPayloadLength(ref RecordHeader header) =>
        ref Unsafe.As<uint, long>(ref header.Magic);

    public static RecordHeader Create(int payloadLength, ulong checksum) => new()
    {
        Magic = JournalFormat.RecordHeaderMagic,
        PayloadLength = payloadLength,
        Checksum = checksum
    };

    public static RecordHeader CreateSkip(int payloadLength) => new()
    {
        Magic = JournalFormat.SkipHeaderMagic,
        PayloadLength = payloadLength,
        Checksum = 0
    };

    public readonly bool IsValid() =>
        Magic == JournalFormat.RecordHeaderMagic &&
        PayloadLength >= 0 &&
        PayloadLength <= int.MaxValue - JournalFormat.RecordHeaderSize - JournalFormat.RecordAlignment;

    public readonly bool IsSkip() =>
        Magic == JournalFormat.SkipHeaderMagic &&
        PayloadLength >= 0 &&
        PayloadLength <= int.MaxValue - JournalFormat.RecordHeaderSize - JournalFormat.RecordAlignment;
}
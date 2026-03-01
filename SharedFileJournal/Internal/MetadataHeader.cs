using System.Runtime.InteropServices;

namespace SharedFileJournal.Internal;

/// <summary>
/// Memory-mapped metadata header at the beginning of the journal file.
/// The <see cref="NextWriteOffset"/> field is placed at a cache-line-aligned offset
/// for optimal atomic operation performance.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct MetadataHeader
{
    public ulong Magic;
    public uint Version;
    private fixed byte _reserved[52];
    public long NextWriteOffset;
}
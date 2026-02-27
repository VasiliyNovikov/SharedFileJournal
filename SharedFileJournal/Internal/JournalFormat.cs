using System;
using System.Runtime.InteropServices;

namespace SharedFileJournal.Internal;

/// <summary>
/// Journal format constants, record serialization, and checksum.
/// </summary>
internal static unsafe class JournalFormat
{
    // Metadata constants
    // "SFJMETA\0" as little-endian uint64
    public const ulong MetadataMagic = 0x004154454D4A4653;
    public const uint MetadataVersion = 1;
    public const int MetadataFileSize = 4096;

    /// <summary>
    /// File offset where record data begins (immediately after the metadata header).
    /// </summary>
    public const int DataStartOffset = MetadataFileSize;

    // Record constants
    public const uint RecordHeaderMagic = 0x524A4653;  // "SFJR" little-endian
    public const uint SkipHeaderMagic = 0x534A4653;    // "SFJS" little-endian
    public const int RecordAlignment = 16;

    public static readonly int RecordHeaderSize = sizeof(RecordHeader);
    public static readonly int MinRecordSize = RecordHeaderSize;

    /// <summary>
    /// Returns the on-disk size of a record, rounded up to <see cref="RecordAlignment"/>.
    /// </summary>
    public static int AlignRecordSize(int unalignedSize) => (unalignedSize + RecordAlignment - 1) & ~(RecordAlignment - 1);

    /// <summary>
    /// Rounds a file offset up to the next <see cref="RecordAlignment"/> boundary.
    /// </summary>
    public static long AlignUp(long offset) => (offset + RecordAlignment - 1) & ~(long)(RecordAlignment - 1);

    /// <summary>
    /// Writes a complete record (header + payload) into <paramref name="buffer"/>.
    /// </summary>
    public static void WriteRecord(Span<byte> buffer, ReadOnlySpan<byte> payload)
    {
        var checksum = ComputeChecksum(payload);
        var header = RecordHeader.Create(payload.Length, checksum);

        MemoryMarshal.Write(buffer, in header);
        payload.CopyTo(buffer[RecordHeaderSize..]);
    }

    /// <summary>
    /// Computes an FNV-1a 64-bit checksum over the given data.
    /// </summary>
    public static ulong ComputeChecksum(ReadOnlySpan<byte> data)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        var hash = offsetBasis;
        foreach (var b in data)
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }
}
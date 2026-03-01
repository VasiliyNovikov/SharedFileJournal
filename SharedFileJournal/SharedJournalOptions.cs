using System.IO;

namespace SharedFileJournal;

/// <summary>
/// Configuration options for <see cref="SharedJournal"/>.
/// </summary>
public sealed class SharedJournalOptions
{
    private const int DefaultMaxPayloadLength = 128 * 1024 * 1024; // 128 MB
    private const int DefaultReadAheadSize = 64 * 1024; // 64 KB
    private const FileShare DefaultFileShare = FileShare.ReadWrite;
    private const FlushMode DefaultFlushMode = FlushMode.None;

    internal FileShare FileShare { get; init; } = DefaultFileShare;

    internal bool AcquireLockFile { get; init; } = true;

    /// <summary>
    /// Controls write durability. Default is <see cref="FlushMode.None"/>.
    /// </summary>
    public FlushMode FlushMode { get; init; } = DefaultFlushMode;

    /// <summary>
    /// Maximum allowed payload length in bytes for both writes and reads.
    /// Payloads exceeding this limit are rejected on write and treated as
    /// corrupted on read. Default is 128 MB.
    /// </summary>
    public int MaxPayloadLength { get; init; } = DefaultMaxPayloadLength;

    /// <summary>
    /// Size in bytes of the read-ahead window used when reading records.
    /// Larger values can improve sequential throughput; smaller values reduce
    /// memory overhead per read. Default is 64 KB.
    /// </summary>
    public int ReadAheadSize { get; init; } = DefaultReadAheadSize;

    /// <summary>
    /// Default options with <see cref="FlushMode.None"/>.
    /// </summary>
    public static readonly SharedJournalOptions Default = new();
}
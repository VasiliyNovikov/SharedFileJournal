namespace SharedFileJournal;

/// <summary>
/// Configuration options for <see cref="SharedJournal"/>.
/// </summary>
public sealed class SharedJournalOptions
{
    /// <summary>
    /// Controls write durability. Default is <see cref="FlushMode.None"/>.
    /// </summary>
    public FlushMode FlushMode { get; init; }

    /// <summary>
    /// Maximum allowed payload length in bytes for both writes and reads.
    /// Payloads exceeding this limit are rejected on write and treated as
    /// corrupted on read. Default is <see cref="DefaultMaxPayloadLength"/> (128 MB).
    /// </summary>
    public int MaxPayloadLength { get; init; } = DefaultMaxPayloadLength;

    internal const int DefaultMaxPayloadLength = 128 * 1024 * 1024; // 128 MB

    /// <summary>
    /// Default options with <see cref="FlushMode.None"/>.
    /// </summary>
    public static readonly SharedJournalOptions Default = new() { FlushMode = FlushMode.None };
}
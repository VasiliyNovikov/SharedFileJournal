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
}
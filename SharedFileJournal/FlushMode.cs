namespace SharedFileJournal;

/// <summary>
/// Controls the durability guarantees for journal writes.
/// </summary>
public enum FlushMode
{
    /// <summary>
    /// No explicit flushing. Durability depends on OS page cache behavior.
    /// </summary>
    None,

    /// <summary>
    /// Opens the data file with write-through semantics, bypassing the OS cache.
    /// </summary>
    WriteThrough
}
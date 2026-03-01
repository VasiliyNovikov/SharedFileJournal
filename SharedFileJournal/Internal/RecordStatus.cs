namespace SharedFileJournal.Internal;

/// <summary>
/// Describes the validation outcome of a record header or a full record read.
/// </summary>
internal enum RecordStatus
{
    /// <summary>Valid record with matching checksum.</summary>
    Record,
    /// <summary>Skip marker with valid bounds.</summary>
    Skip,
    /// <summary>Recognized magic but unusable record: invalid bounds, unreadable payload, or checksum mismatch. Not safe to overwrite — may be an in-flight write.</summary>
    Incomplete,
    /// <summary>Unrecognized magic — safe to write a skip marker over this region.</summary>
    Corrupt,
    /// <summary>Record extends past the known tail or header could not be fully read.</summary>
    Truncated
}
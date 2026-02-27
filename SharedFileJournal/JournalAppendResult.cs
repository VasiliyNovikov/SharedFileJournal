namespace SharedFileJournal;

/// <summary>
/// The result of appending a record to the journal.
/// </summary>
/// <param name="Offset">The file offset where the record was written.</param>
/// <param name="TotalRecordLength">The total length of the record including header, payload, and alignment padding.</param>
public readonly record struct JournalAppendResult(long Offset, int TotalRecordLength);
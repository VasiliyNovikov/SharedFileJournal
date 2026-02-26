namespace SharedFileJournal;

/// <summary>
/// The result of a journal recovery operation.
/// </summary>
/// <param name="ValidEndOffset">The file offset immediately after the last valid record.</param>
/// <param name="ValidRecordCount">The number of valid records found.</param>
public readonly record struct JournalRecoveryResult(long ValidEndOffset, int ValidRecordCount);
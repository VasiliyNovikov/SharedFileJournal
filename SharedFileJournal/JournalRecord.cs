using System;

namespace SharedFileJournal;

/// <summary>
/// A record read from the journal.
/// </summary>
/// <param name="Offset">The file offset of the record.</param>
/// <param name="Payload">The record payload.</param>
public readonly record struct JournalRecord(long Offset, ReadOnlyMemory<byte> Payload);
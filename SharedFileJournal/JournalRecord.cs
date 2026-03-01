using System;

namespace SharedFileJournal;

/// <summary>
/// A record read from the journal.
/// </summary>
/// <param name="Offset">The file offset of the record.</param>
/// <param name="Payload">
/// The record payload. <b>Only valid until the next <c>MoveNext</c> call on the enumerator.</b>
/// The underlying buffer is reused across iterations to avoid allocations. Callers that need
/// the payload to outlive the current iteration must copy it (e.g. via
/// <see cref="ReadOnlyMemory{T}.ToArray"/>).
/// </param>
public readonly record struct JournalRecord(long Offset, ReadOnlyMemory<byte> Payload);
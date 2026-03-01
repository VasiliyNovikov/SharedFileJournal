using System.Collections.Generic;
using System.Linq;

namespace SharedFileJournal.Tests;

/// <summary>
/// Helpers for tests that need to materialize records with owned payload copies.
/// </summary>
internal static class TestExtensions
{
    /// <summary>
    /// Materializes records into a list, copying each payload so it remains valid
    /// after enumeration. Required because <see cref="JournalRecord.Payload"/> is
    /// backed by a pooled buffer that is reused across iterations.
    /// </summary>
    public static List<JournalRecord> ToOwnedList(this IEnumerable<JournalRecord> records) =>
        records.Select(r => new JournalRecord(r.Offset, r.Payload.ToArray())).ToList();
}
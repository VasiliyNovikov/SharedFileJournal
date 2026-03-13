using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>
    /// Asynchronously materializes records into a list, copying each payload so it remains valid
    /// after enumeration.
    /// </summary>
    public static async Task<List<JournalRecord>> ToOwnedListAsync(this IAsyncEnumerable<JournalRecord> records, CancellationToken cancellationToken = default)
    {
        var list = new List<JournalRecord>();
        await foreach (var r in records.WithCancellation(cancellationToken))
            list.Add(new JournalRecord(r.Offset, r.Payload.ToArray()));
        return list;
    }
}
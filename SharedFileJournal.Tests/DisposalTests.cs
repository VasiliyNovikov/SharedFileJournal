using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SharedFileJournal.Tests;

[TestClass]
public class DisposalTests
{
    private const int ReadAheadSize = 257;
    private static readonly byte[] FirstPayload = CreatePayload(0x11);
    private static readonly byte[] SecondPayload = CreatePayload(0x22);
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sfj-dispose-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* best effort */ }
    }

    private string JournalPath => Path.Combine(_tempDir, "journal");

    private static byte[] CreatePayload(byte value)
    {
        var payload = new byte[248];
        Array.Fill(payload, value);
        return payload;
    }

    private static void SeedJournal(SharedJournal journal)
    {
        journal.Append(FirstPayload);
        journal.Append(SecondPayload);
    }

    private static void AssertLaterEnumeratorsDoNotShareBuffers(SharedJournal journal)
    {
        var left = journal.ReadAll().GetEnumerator();
        var right = journal.ReadAll().GetEnumerator();

        try
        {
            Assert.IsTrue(left.MoveNext());
            CollectionAssert.AreEqual(FirstPayload, left.Current.Payload.ToArray());
            var leftPayload = left.Current.Payload;

            Assert.IsTrue(right.MoveNext());
            Assert.IsTrue(right.MoveNext());
            CollectionAssert.AreEqual(SecondPayload, right.Current.Payload.ToArray());

            CollectionAssert.AreEqual(FirstPayload, leftPayload.ToArray());
        }
        finally
        {
            right.Dispose();
            left.Dispose();
        }
    }

    [TestMethod]
    public async Task ReadAllEnumerator_Dispose_ThenDisposeAsync_IsSafe()
    {
        using var journal = new SharedJournal(JournalPath, new SharedJournalOptions { ReadAheadSize = ReadAheadSize });
        SeedJournal(journal);

        var victim = journal.ReadAll().GetEnumerator();
        Assert.IsTrue(victim.MoveNext());

        victim.Dispose();
        await ((IAsyncEnumerator<JournalRecord>)victim).DisposeAsync();

        AssertLaterEnumeratorsDoNotShareBuffers(journal);
    }

    [TestMethod]
    public async Task ReadAllAsyncEnumerator_DisposeAsync_ThenDispose_IsSafe()
    {
        using var journal = new SharedJournal(JournalPath, new SharedJournalOptions { ReadAheadSize = ReadAheadSize });
        SeedJournal(journal);

        var victim = journal.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await victim.MoveNextAsync());

        await victim.DisposeAsync();
        ((IDisposable)victim).Dispose();

        AssertLaterEnumeratorsDoNotShareBuffers(journal);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SharedFileJournal.Tests;

[TestClass]
public class ConcurrencyTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sfj-concurrency-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* best effort */ }
    }

    private string JournalPath => Path.Combine(_tempDir, "journal");

    [TestMethod]
    public void ConcurrentAppend_MultipleThreads_NoOverlap()
    {
        const int threadCount = 8;
        const int recordsPerThread = 500;

        using var journal = new SharedJournal(JournalPath);
        var allResults = new List<JournalAppendResult>[threadCount];

        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (var t = 0; t < threadCount; t++)
        {
            var threadId = t;
            allResults[threadId] = new List<JournalAppendResult>(recordsPerThread);
            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < recordsPerThread; i++)
                {
                    var payload = Encoding.UTF8.GetBytes($"t{threadId}-r{i}");
                    var result = journal.Append(payload);
                    allResults[threadId].Add(result);
                }
            });
        }

        Task.WaitAll(tasks);

        // Verify no overlapping ranges
        var ranges = allResults
            .SelectMany(r => r)
            .Select(r => (Start: r.Offset, End: r.Offset + r.TotalRecordLength))
            .OrderBy(r => r.Start)
            .ToList();

        for (var i = 1; i < ranges.Count; i++)
            Assert.IsTrue(ranges[i].Start >= ranges[i - 1].End,
                $"Overlap detected: [{ranges[i - 1].Start}..{ranges[i - 1].End}) and [{ranges[i].Start}..{ranges[i].End})");
    }

    [TestMethod]
    public void ConcurrentAppend_MultipleThreads_AllRecordsReadable()
    {
        const int threadCount = 4;
        const int recordsPerThread = 1000;

        using var journal = new SharedJournal(JournalPath);
        var barrier = new Barrier(threadCount);

        var tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < recordsPerThread; i++)
            {
                var payload = Encoding.UTF8.GetBytes($"thread{t}-record{i}");
                journal.Append(payload);
            }
        })).ToArray();

        Task.WaitAll(tasks);

        var records = journal.ReadAll().ToList();
        Assert.AreEqual(threadCount * recordsPerThread, records.Count);

        // Verify all records have valid payloads
        foreach (var record in records)
        {
            var text = Encoding.UTF8.GetString(record.Payload.Span);
            Assert.IsTrue(text.StartsWith("thread", StringComparison.Ordinal),
                $"Unexpected payload: {text}");
        }
    }

    [TestMethod]
    public void ConcurrentAppend_VariableSizes_AllValid()
    {
        const int threadCount = 4;
        const int recordsPerThread = 500;

        using var journal = new SharedJournal(JournalPath);
        var barrier = new Barrier(threadCount);

        var tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            var rng = new Random(t * 1000);
            barrier.SignalAndWait();
            for (var i = 0; i < recordsPerThread; i++)
            {
                var payload = new byte[rng.Next(1, 4096)];
                rng.NextBytes(payload);
                journal.Append(payload);
            }
        })).ToArray();

        Task.WaitAll(tasks);

        var records = journal.ReadAll().ToList();
        Assert.AreEqual(threadCount * recordsPerThread, records.Count);
    }

    [TestMethod]
    public void ConcurrentAppend_Compaction_AllValidAfterRecovery()
    {
        const int threadCount = 4;
        const int recordsPerThread = 200;

        using var journal = new SharedJournal(JournalPath);
        var barrier = new Barrier(threadCount);

        var tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < recordsPerThread; i++)
            {
                var payload = Encoding.UTF8.GetBytes($"t{t}r{i}");
                journal.Append(payload);
            }
        })).ToArray();

        Task.WaitAll(tasks);
        journal.Dispose();

        SharedJournal.Compact(JournalPath);

        using var compactedJournal = new SharedJournal(JournalPath);
        Assert.AreEqual(threadCount * recordsPerThread, compactedJournal.ReadAll().Count());
    }

    [TestMethod]
    public void MultipleJournalInstances_SameFiles_ConcurrentAppend()
    {
        const int recordsPerInstance = 200;

        // Two journal instances sharing the same files (simulates multi-process)
        using var journal1 = new SharedJournal(JournalPath);
        using var journal2 = new SharedJournal(JournalPath);

        var barrier = new Barrier(2);

        var task1 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < recordsPerInstance; i++)
                journal1.Append(Encoding.UTF8.GetBytes($"j1-{i}"));
        });

        var task2 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < recordsPerInstance; i++)
                journal2.Append(Encoding.UTF8.GetBytes($"j2-{i}"));
        });

        Task.WaitAll(task1, task2);

        // Read from either instance — should see all records
        var records = journal1.ReadAll().ToList();
        Assert.AreEqual(recordsPerInstance * 2, records.Count);

        // Verify both instances' records are present
        var texts = records.Select(r => Encoding.UTF8.GetString(r.Payload.Span)).ToHashSet();
        for (var i = 0; i < recordsPerInstance; i++)
        {
            Assert.IsTrue(texts.Contains($"j1-{i}"), $"Missing j1-{i}");
            Assert.IsTrue(texts.Contains($"j2-{i}"), $"Missing j2-{i}");
        }
    }
}
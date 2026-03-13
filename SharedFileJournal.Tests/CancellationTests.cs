using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SharedFileJournal.Internal;

namespace SharedFileJournal.Tests;

[TestClass]
public class CancellationTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sfj-cancel-" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task AppendAsync_AlreadyCancelled_Throws()
    {
        using var journal = new SharedJournal(JournalPath);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => journal.AppendAsync("data"u8.ToArray(), cancellationToken: cts.Token).AsTask());
    }

    [TestMethod]
    public async Task ReadAllAsync_AlreadyCancelled_Throws()
    {
        using var journal = new SharedJournal(JournalPath);
        journal.Append("data"u8);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in journal.ReadAllAsync(cts.Token))
            {
            }
        });
    }

    [TestMethod]
    public async Task ReadAllAsync_CancelledMidRead_Throws()
    {
        using var journal = new SharedJournal(JournalPath);
        for (var i = 0; i < 100; i++)
            journal.Append(Encoding.UTF8.GetBytes($"record-{i}"));

        var cts = new CancellationTokenSource();
        var count = 0;

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in journal.ReadAllAsync(cts.Token))
            {
                count++;
                if (count == 10)
                    cts.Cancel();
            }
        });

        Assert.IsTrue(count >= 10 && count < 100,
            $"Expected partial read (10-99), got {count}");
    }

    [TestMethod]
    public async Task ReadAllAsync_WithCancellation_HonorsSequenceToken()
    {
        using var journal = new SharedJournal(JournalPath);
        for (var i = 0; i < 100; i++)
            journal.Append(Encoding.UTF8.GetBytes($"record-{i}"));

        using var readAllCts = new CancellationTokenSource();
        using var enumeratorCts = new CancellationTokenSource();
        var count = 0;

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in journal.ReadAllAsync(readAllCts.Token).WithCancellation(enumeratorCts.Token))
            {
                count++;
                if (count == 10)
                    readAllCts.Cancel();
            }
        });

        Assert.IsTrue(count >= 10 && count < 100,
            $"Expected partial read (10-99), got {count}");
    }

    [TestMethod]
    public async Task ReadAllAsync_WithCancellation_HonorsEnumeratorToken()
    {
        using var journal = new SharedJournal(JournalPath);
        for (var i = 0; i < 100; i++)
            journal.Append(Encoding.UTF8.GetBytes($"record-{i}"));

        using var readAllCts = new CancellationTokenSource();
        using var enumeratorCts = new CancellationTokenSource();
        var count = 0;

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in journal.ReadAllAsync(readAllCts.Token).WithCancellation(enumeratorCts.Token))
            {
                count++;
                if (count == 10)
                    enumeratorCts.Cancel();
            }
        });

        Assert.IsTrue(count >= 10 && count < 100,
            $"Expected partial read (10-99), got {count}");
    }

    [TestMethod]
    public async Task ReadAllAsync_CancelledMidRead_JournalStillUsable()
    {
        using var journal = new SharedJournal(JournalPath);
        for (var i = 0; i < 50; i++)
            journal.Append(Encoding.UTF8.GetBytes($"record-{i}"));

        // Cancel mid-read
        var cts = new CancellationTokenSource();
        var count = 0;
        try
        {
            await foreach (var _ in journal.ReadAllAsync(cts.Token))
            {
                count++;
                if (count == 5)
                    cts.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        // Journal should still be fully usable after cancellation
        journal.Append("after-cancel"u8);
        var records = await journal.ReadAllAsync().ToOwnedListAsync();
        Assert.AreEqual(51, records.Count);
        Assert.AreEqual("after-cancel", Encoding.UTF8.GetString(records[^1].Payload.Span));
    }

    [TestMethod]
    public async Task FlushAsync_AlreadyCancelled_Throws()
    {
        using var journal = new SharedJournal(JournalPath);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => journal.FlushAsync(cts.Token).AsTask());
    }

    [TestMethod]
    public async Task CompactAsync_AlreadyCancelled_Throws()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("data"u8);
        }

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => SharedJournal.CompactAsync(JournalPath, cancellationToken: cts.Token).AsTask());

        Assert.IsFalse(File.Exists(JournalPath + ".compact"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CompactAsync_CancelledDuringCopy_LeavesCorruptedSourceUnchanged()
    {
        const int trailingRecordCount = 20_000;
        var payload = new byte[1024];
        new Random(42).NextBytes(payload);

        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("good"u8);
            journal.Append("bad!"u8);
            for (var i = 0; i < trailingRecordCount; i++)
                journal.Append(payload);
        }

        var corruptedRecordOffset = JournalFormat.DataStartOffset
            + JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + 4);
        var originalHeader = new byte[JournalFormat.RecordHeaderSize];
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            fs.Seek(corruptedRecordOffset, SeekOrigin.Begin);
            fs.Write(new byte[JournalFormat.RecordHeaderSize]);
            fs.Seek(corruptedRecordOffset, SeekOrigin.Begin);
            _ = fs.Read(originalHeader);
        }

        var tempPath = JournalPath + ".compact";
        using var cts = new CancellationTokenSource();
        var compactTask = SharedJournal.CompactAsync(JournalPath, cancellationToken: cts.Token).AsTask();
        var cancelTask = Task.Run(async () =>
        {
            while (!compactTask.IsCompleted)
            {
                if (File.Exists(tempPath) && new FileInfo(tempPath).Length > JournalFormat.MetadataFileSize + (256 * 1024))
                {
                    cts.Cancel();
                    return;
                }

                await Task.Delay(5);
            }
        });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => compactTask);
        await cancelTask;

        Assert.IsFalse(File.Exists(tempPath));

        var headerAfter = new byte[JournalFormat.RecordHeaderSize];
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(corruptedRecordOffset, SeekOrigin.Begin);
            _ = fs.Read(headerAfter);
        }

        CollectionAssert.AreEqual(originalHeader, headerAfter);
    }

    [TestMethod]
    public async Task CompactAsync_ValidJournal_PreservesAllRecords()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("record1"u8);
            journal.Append("record2"u8);
            journal.Append("record3"u8);
        }

        await SharedJournal.CompactAsync(JournalPath);

        using (var journal = new SharedJournal(JournalPath))
        {
            var records = await journal.ReadAllAsync().ToOwnedListAsync();
            Assert.AreEqual(3, records.Count);
        }
    }

    [TestMethod]
    public async Task CompactAsync_CorruptedChecksum_SkipsCorruption()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("good"u8);
            journal.Append("will be corrupted"u8);
            journal.Append("also good"u8);
        }

        var firstRecordSize = JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + 4);
        var corruptionOffset = JournalFormat.DataStartOffset + firstRecordSize + JournalFormat.RecordHeaderSize + 2;
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(corruptionOffset, SeekOrigin.Begin);
            fs.WriteByte(0xFF);
            fs.WriteByte(0xFF);
        }

        await SharedJournal.CompactAsync(JournalPath);

        using (var journal = new SharedJournal(JournalPath))
        {
            var records = await journal.ReadAllAsync().ToOwnedListAsync();
            Assert.AreEqual(2, records.Count);
            CollectionAssert.AreEqual("good"u8.ToArray(), records[0].Payload.ToArray());
            CollectionAssert.AreEqual("also good"u8.ToArray(), records[1].Payload.ToArray());
        }
    }

    [TestMethod]
    public async Task CompactAsync_WhileJournalOpen_ThrowsIOException()
    {
        using var journal = new SharedJournal(JournalPath);
        journal.Append("data"u8);

        await Assert.ThrowsExactlyAsync<IOException>(
            () => SharedJournal.CompactAsync(JournalPath).AsTask());
    }

    [TestMethod]
    public async Task ConcurrentAppendAsync_MultipleThreads_AllRecordsReadable()
    {
        const int threadCount = 4;
        const int recordsPerThread = 500;

        using var journal = new SharedJournal(JournalPath);
        var barrier = new Barrier(threadCount);

        var tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < recordsPerThread; i++)
            {
                var payload = Encoding.UTF8.GetBytes($"thread{t}-record{i}");
                await journal.AppendAsync(payload);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        var records = await journal.ReadAllAsync().ToOwnedListAsync();
        Assert.AreEqual(threadCount * recordsPerThread, records.Count);
    }
}

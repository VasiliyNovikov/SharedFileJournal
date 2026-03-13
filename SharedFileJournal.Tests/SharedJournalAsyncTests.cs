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
public class SharedJournalAsyncTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sfj-async-" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task AppendAsync_SingleRecord_CanReadBack()
    {
        using var journal = new SharedJournal(JournalPath);
        var payload = "hello world"u8.ToArray();

        var result = await journal.AppendAsync(payload);

        Assert.AreEqual((long)JournalFormat.DataStartOffset, result.Offset);
        Assert.AreEqual(JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + payload.Length), result.TotalRecordLength);

        var records = await journal.ReadAllAsync().ToOwnedListAsync();
        Assert.AreEqual(1, records.Count);
        CollectionAssert.AreEqual(payload, records[0].Payload.ToArray());
    }

    [TestMethod]
    public async Task AppendAsync_MultipleRecords_AllReadable()
    {
        using var journal = new SharedJournal(JournalPath);
        var payloads = new[]
        {
            "first"u8.ToArray(),
            "second record"u8.ToArray(),
            "third"u8.ToArray()
        };

        foreach (var p in payloads)
            await journal.AppendAsync(p);

        var records = await journal.ReadAllAsync().ToOwnedListAsync();
        Assert.AreEqual(3, records.Count);
        for (var i = 0; i < payloads.Length; i++)
            CollectionAssert.AreEqual(payloads[i], records[i].Payload.ToArray());
    }

    [TestMethod]
    public async Task AppendAsync_EmptyPayload_Works()
    {
        using var journal = new SharedJournal(JournalPath);
        await journal.AppendAsync(ReadOnlySpan<byte>.Empty);

        var records = await journal.ReadAllAsync().ToOwnedListAsync();
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(0, records[0].Payload.Length);
    }

    [TestMethod]
    public async Task AppendAsync_LargePayload_Works()
    {
        using var journal = new SharedJournal(JournalPath);
        var payload = new byte[1024 * 1024]; // 1 MB
        new Random(42).NextBytes(payload);

        await journal.AppendAsync(payload);

        var records = await journal.ReadAllAsync().ToOwnedListAsync();
        Assert.AreEqual(1, records.Count);
        CollectionAssert.AreEqual(payload, records[0].Payload.ToArray());
    }

    [TestMethod]
    public async Task ReadAllAsync_EmptyJournal_ReturnsEmpty()
    {
        using var journal = new SharedJournal(JournalPath);
        var records = await journal.ReadAllAsync().ToOwnedListAsync();
        Assert.AreEqual(0, records.Count);
    }

    [TestMethod]
    public async Task ReadAllAsync_ResultUsesFirstEnumerationSnapshot()
    {
        using var journal = new SharedJournal(JournalPath);

        var records = journal.ReadAllAsync();
        await journal.AppendAsync("first"u8.ToArray());

        var firstPass = await records.ToOwnedListAsync();
        await journal.AppendAsync("second"u8.ToArray());
        var secondPass = await records.ToOwnedListAsync();

        Assert.AreEqual(1, firstPass.Count);
        Assert.AreEqual(1, secondPass.Count);
        Assert.AreEqual("first", Encoding.UTF8.GetString(firstPass[0].Payload.Span));
        Assert.AreEqual("first", Encoding.UTF8.GetString(secondPass[0].Payload.Span));
    }

    [TestMethod]
    public async Task AppendAsync_RecordOffsets_AreSequential()
    {
        using var journal = new SharedJournal(JournalPath);
        var payload1 = "aaa"u8.ToArray();
        var payload2 = "bbbbb"u8.ToArray();

        var r1 = await journal.AppendAsync(payload1);
        var r2 = await journal.AppendAsync(payload2);

        Assert.AreEqual((long)JournalFormat.DataStartOffset, r1.Offset);
        Assert.AreEqual(r1.Offset + r1.TotalRecordLength, r2.Offset);
    }

    [TestMethod]
    public async Task Reopen_CanReadPreviousRecords_Async()
    {
        var payload = "persistent data"u8.ToArray();

        using (var journal = new SharedJournal(JournalPath))
        {
            await journal.AppendAsync(payload);
        }

        using (var journal = new SharedJournal(JournalPath))
        {
            var records = await journal.ReadAllAsync().ToOwnedListAsync();
            Assert.AreEqual(1, records.Count);
            CollectionAssert.AreEqual(payload, records[0].Payload.ToArray());
        }
    }

    [TestMethod]
    public async Task AppendAsync_VariableSizes_ManyRecords()
    {
        using var journal = new SharedJournal(JournalPath);
        var rng = new Random(123);
        var expected = new byte[100][];

        for (var i = 0; i < 100; i++)
        {
            expected[i] = new byte[rng.Next(0, 1024)];
            rng.NextBytes(expected[i]);
            await journal.AppendAsync(expected[i]);
        }

        var records = await journal.ReadAllAsync().ToOwnedListAsync();
        Assert.AreEqual(100, records.Count);
        for (var i = 0; i < 100; i++)
            CollectionAssert.AreEqual(expected[i], records[i].Payload.ToArray());
    }

    [TestMethod]
    public async Task ReadAllAsync_JournalLargerThanReadBuffer_ReadsAllRecords()
    {
        using var journal = new SharedJournal(JournalPath);

        const int recordCount = 2000;
        var expected = new byte[recordCount][];
        var rng = new Random(42);
        for (var i = 0; i < recordCount; i++)
        {
            expected[i] = new byte[200];
            rng.NextBytes(expected[i]);
            await journal.AppendAsync(expected[i]);
        }

        var records = await journal.ReadAllAsync().ToOwnedListAsync();
        Assert.AreEqual(recordCount, records.Count);
        for (var i = 0; i < recordCount; i++)
            CollectionAssert.AreEqual(expected[i], records[i].Payload.ToArray());
    }

    [TestMethod]
    public async Task ReadAllAsync_SkipsBrokenRecord_ReturnsValidRecords()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("first"u8);
            journal.Append("second"u8);
            journal.Append("third"u8);
        }

        var firstRecordSize = JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + 5);
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(JournalFormat.DataStartOffset + firstRecordSize, SeekOrigin.Begin);
            fs.Write(new byte[JournalFormat.RecordHeaderSize]);
        }

        using (var journal = new SharedJournal(JournalPath))
        {
            var records = await journal.ReadAllAsync().ToOwnedListAsync();
            Assert.AreEqual(2, records.Count);
            Assert.AreEqual("first", Encoding.UTF8.GetString(records[0].Payload.Span));
            Assert.AreEqual("third", Encoding.UTF8.GetString(records[1].Payload.Span));
        }
    }

    [TestMethod]
    public async Task ReadAllAsync_SkipsCorruptedChecksum_ReturnsOtherRecords()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("good1"u8);
            journal.Append("bad!!"u8);
            journal.Append("good2"u8);
        }

        var corruptOffset = JournalFormat.DataStartOffset
            + JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + 5)
            + JournalFormat.RecordHeaderSize + 2;
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(corruptOffset, SeekOrigin.Begin);
            fs.WriteByte(0xFF);
        }

        using (var journal = new SharedJournal(JournalPath))
        {
            var records = await journal.ReadAllAsync().ToOwnedListAsync();
            Assert.AreEqual(2, records.Count);
            Assert.AreEqual("good1", Encoding.UTF8.GetString(records[0].Payload.Span));
            Assert.AreEqual("good2", Encoding.UTF8.GetString(records[1].Payload.Span));
        }
    }

    [TestMethod]
    public async Task AppendAsync_AfterDispose_Throws()
    {
        var journal = new SharedJournal(JournalPath);
        journal.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => journal.AppendAsync("data"u8.ToArray()).AsTask());
    }

    [TestMethod]
    public async Task AppendAsync_AfterDispose_WithCancelledToken_ThrowsObjectDisposedException()
    {
        var journal = new SharedJournal(JournalPath);
        journal.Dispose();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => journal.AppendAsync("data"u8.ToArray(), cancellationToken: cts.Token).AsTask());
    }

    [TestMethod]
    public void ReadAllAsync_AfterDispose_Throws()
    {
        var journal = new SharedJournal(JournalPath);
        journal.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => journal.ReadAllAsync());
    }

    [TestMethod]
    public async Task FlushAsync_AfterDispose_Throws()
    {
        var journal = new SharedJournal(JournalPath);
        journal.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => journal.FlushAsync().AsTask());
    }

    [TestMethod]
    public async Task FlushAsync_AfterAppend_DoesNotThrow()
    {
        using var journal = new SharedJournal(JournalPath);
        await journal.AppendAsync("data"u8.ToArray());
        await journal.FlushAsync();

        var records = await journal.ReadAllAsync().ToOwnedListAsync();
        Assert.AreEqual(1, records.Count);
    }

    [TestMethod]
    public async Task CompactAsync_NullPath_Throws()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => SharedJournal.CompactAsync(null!).AsTask());
    }

    [TestMethod]
    public async Task ReadAllAsync_SkipsOversizedPayload_ReturnsOtherRecords()
    {
        var options = new SharedJournalOptions { MaxPayloadLength = 64 };

        using (var journal = new SharedJournal(JournalPath, options))
        {
            journal.Append("first"u8);
            journal.Append("second"u8);
            journal.Append("third"u8);
        }

        var firstRecordSize = JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + 5);
        var secondRecordOffset = JournalFormat.DataStartOffset + firstRecordSize;
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var oversizedLength = BitConverter.GetBytes(1024);
            fs.Seek(secondRecordOffset + sizeof(uint), SeekOrigin.Begin);
            fs.Write(oversizedLength);
        }

        using (var journal = new SharedJournal(JournalPath, options))
        {
            var records = await journal.ReadAllAsync().ToOwnedListAsync();
            Assert.AreEqual(2, records.Count);
            Assert.AreEqual("first", Encoding.UTF8.GetString(records[0].Payload.Span));
            Assert.AreEqual("third", Encoding.UTF8.GetString(records[1].Payload.Span));
        }
    }

    [TestMethod]
    public async Task AppendAsync_PayloadExceedsMaxLength_Throws()
    {
        var options = new SharedJournalOptions { MaxPayloadLength = 64 };
        using var journal = new SharedJournal(JournalPath, options);

        var oversized = new byte[65];
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => journal.AppendAsync(oversized).AsTask());
    }

    [TestMethod]
    public async Task SyncAppend_AsyncReadAll_Interop()
    {
        using var journal = new SharedJournal(JournalPath);
        journal.Append("sync-write"u8);

        var records = await journal.ReadAllAsync().ToOwnedListAsync();
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("sync-write", Encoding.UTF8.GetString(records[0].Payload.Span));
    }

    [TestMethod]
    public async Task AsyncAppend_SyncReadAll_Interop()
    {
        using var journal = new SharedJournal(JournalPath);
        await journal.AppendAsync("async-write"u8.ToArray());

        var records = journal.ReadAll().ToOwnedList();
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("async-write", Encoding.UTF8.GetString(records[0].Payload.Span));
    }
}

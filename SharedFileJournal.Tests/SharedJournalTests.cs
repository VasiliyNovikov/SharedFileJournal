using System;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SharedFileJournal.Internal;
namespace SharedFileJournal.Tests;

[TestClass]
public class SharedJournalTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sfj-test-" + Guid.NewGuid().ToString("N")[..8]);
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
    public void Open_CreatesFile()
    {
        using var journal = new SharedJournal(JournalPath);

        Assert.IsTrue(File.Exists(JournalPath));
    }

    [TestMethod]
    public void Append_SingleRecord_CanReadBack()
    {
        using var journal = new SharedJournal(JournalPath);
        var payload = "hello world"u8.ToArray();

        var result = journal.Append(payload);

        Assert.AreEqual((long)JournalFormat.DataStartOffset, result.Offset);
        Assert.AreEqual(JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + payload.Length), result.TotalRecordLength);

        var records = journal.ReadAll().ToList();
        Assert.AreEqual(1, records.Count);
        CollectionAssert.AreEqual(payload, records[0].Payload.ToArray());
    }

    [TestMethod]
    public void Append_MultipleRecords_AllReadable()
    {
        using var journal = new SharedJournal(JournalPath);
        var payloads = new[]
        {
            "first"u8.ToArray(),
            "second record"u8.ToArray(),
            "third"u8.ToArray()
        };

        foreach (var p in payloads)
            journal.Append(p);

        var records = journal.ReadAll().ToList();
        Assert.AreEqual(3, records.Count);
        for (var i = 0; i < payloads.Length; i++)
            CollectionAssert.AreEqual(payloads[i], records[i].Payload.ToArray());
    }

    [TestMethod]
    public void Append_EmptyPayload_Works()
    {
        using var journal = new SharedJournal(JournalPath);
        journal.Append(ReadOnlySpan<byte>.Empty);

        var records = journal.ReadAll().ToList();
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(0, records[0].Payload.Length);
    }

    [TestMethod]
    public void Append_LargePayload_Works()
    {
        using var journal = new SharedJournal(JournalPath);
        var payload = new byte[1024 * 1024]; // 1 MB
        new Random(42).NextBytes(payload);

        journal.Append(payload);

        var records = journal.ReadAll().ToList();
        Assert.AreEqual(1, records.Count);
        CollectionAssert.AreEqual(payload, records[0].Payload.ToArray());
    }

    [TestMethod]
    public void ReadAll_EmptyJournal_ReturnsEmpty()
    {
        using var journal = new SharedJournal(JournalPath);
        var records = journal.ReadAll().ToList();
        Assert.AreEqual(0, records.Count);
    }

    [TestMethod]
    public void Append_RecordOffsets_AreSequential()
    {
        using var journal = new SharedJournal(JournalPath);
        var payload1 = "aaa"u8.ToArray();
        var payload2 = "bbbbb"u8.ToArray();

        var r1 = journal.Append(payload1);
        var r2 = journal.Append(payload2);

        Assert.AreEqual((long)JournalFormat.DataStartOffset, r1.Offset);
        Assert.AreEqual(r1.Offset + r1.TotalRecordLength, r2.Offset);
    }

    [TestMethod]
    public void Reopen_CanReadPreviousRecords()
    {
        var payload = "persistent data"u8.ToArray();

        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append(payload);
        }

        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(1, records.Count);
            CollectionAssert.AreEqual(payload, records[0].Payload.ToArray());
        }
    }

    [TestMethod]
    public void Append_VariableSizes_ManyRecords()
    {
        using var journal = new SharedJournal(JournalPath);
        var rng = new Random(123);
        var expected = new byte[100][];

        for (var i = 0; i < 100; i++)
        {
            expected[i] = new byte[rng.Next(0, 1024)];
            rng.NextBytes(expected[i]);
            journal.Append(expected[i]);
        }

        var records = journal.ReadAll().ToList();
        Assert.AreEqual(100, records.Count);
        for (var i = 0; i < 100; i++)
            CollectionAssert.AreEqual(expected[i], records[i].Payload.ToArray());
    }

    [TestMethod]
    public void ReadAll_SkipsBrokenRecord_ReturnsValidRecords()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("first"u8);
            journal.Append("second"u8);
            journal.Append("third"u8);
        }

        // Corrupt the second record's header by zeroing it
        var firstRecordSize = JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + 5); // "first" = 5 bytes
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(JournalFormat.DataStartOffset + firstRecordSize, SeekOrigin.Begin);
            fs.Write(new byte[JournalFormat.RecordHeaderSize]); // zero out header
        }

        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(2, records.Count);
            Assert.AreEqual("first", System.Text.Encoding.UTF8.GetString(records[0].Payload.Span));
            Assert.AreEqual("third", System.Text.Encoding.UTF8.GetString(records[1].Payload.Span));
        }
    }

    [TestMethod]
    public void ReadAll_SkipsCorruptedChecksum_ReturnsOtherRecords()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("good1"u8);
            journal.Append("bad!!"u8);
            journal.Append("good2"u8);
        }

        // Corrupt the payload of the second record
        var corruptOffset = JournalFormat.DataStartOffset
            + JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + 5)  // past first record
            + JournalFormat.RecordHeaderSize + 2; // into second record's payload
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(corruptOffset, SeekOrigin.Begin);
            fs.WriteByte(0xFF);
        }

        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(2, records.Count);
            Assert.AreEqual("good1", System.Text.Encoding.UTF8.GetString(records[0].Payload.Span));
            Assert.AreEqual("good2", System.Text.Encoding.UTF8.GetString(records[1].Payload.Span));
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public void Open_RecoversFromPartialInitialization()
    {
        // Simulate a crash that wrote Magic but never wrote Version.
        // Create a properly-sized journal file and write only the Magic header.
        using (var fs = new FileStream(
            JournalPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            fs.SetLength(JournalFormat.MetadataFileSize);
            Span<byte> magic = stackalloc byte[sizeof(ulong)];
            BitConverter.TryWriteBytes(magic, JournalFormat.MetadataMagic);
            fs.Write(magic);
        }

        // Open should detect partial init after spin timeout and complete it
        using var journal = new SharedJournal(JournalPath);
        journal.Append("after recovery"u8);
        var records = journal.ReadAll().ToList();
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("after recovery", Encoding.UTF8.GetString(records[0].Payload.Span));
    }
}
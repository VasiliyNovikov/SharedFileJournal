using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SharedFileJournal.Internal;

namespace SharedFileJournal.Tests;

[TestClass]
public class CompactionTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sfj-recovery-" + Guid.NewGuid().ToString("N")[..8]);
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
    public void Compact_EmptyJournal_ProducesEmptyJournal()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            // empty — just initialize
        }

        SharedJournal.Compact(JournalPath);

        using (var journal = new SharedJournal(JournalPath))
        {
            Assert.AreEqual(0, journal.ReadAll().Count());
        }
    }

    [TestMethod]
    public void Compact_ValidJournal_PreservesAllRecords()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("record1"u8);
            journal.Append("record2"u8);
            journal.Append("record3"u8);
        }

        SharedJournal.Compact(JournalPath);

        using (var journal = new SharedJournal(JournalPath))
        {
            Assert.AreEqual(3, journal.ReadAll().Count());
        }
    }

    [TestMethod]
    public void Compact_PartialLastRecord_RemovesTrailingGarbage()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("good record"u8);
            journal.Append("another good"u8);
        }

        // Append garbage bytes to simulate a partial write
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(new byte[50]);
        }

        SharedJournal.Compact(JournalPath);

        // After compaction, new appends should work
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("after recovery"u8);
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(3, records.Count);
        }
    }

    [TestMethod]
    public void Compact_CorruptedChecksum_SkipsCorruption()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("good"u8);
            journal.Append("will be corrupted"u8);
            journal.Append("also good"u8);
        }

        // Corrupt the payload of the second record
        var firstRecordSize = JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + 4); // "good" = 4 bytes
        var corruptionOffset = JournalFormat.DataStartOffset + firstRecordSize + JournalFormat.RecordHeaderSize + 2;
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(corruptionOffset, SeekOrigin.Begin);
            fs.WriteByte(0xFF);
            fs.WriteByte(0xFF);
        }

        SharedJournal.Compact(JournalPath);

        // Compact skips corruption and recovers records on both sides
        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(2, records.Count);
            CollectionAssert.AreEqual("good"u8.ToArray(), records[0].Payload.ToArray());
            CollectionAssert.AreEqual("also good"u8.ToArray(), records[1].Payload.ToArray());
        }
    }

    [TestMethod]
    public void Compact_CompletelyCorruptedFile_ProducesEmptyJournal()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("will be destroyed"u8);
        }

        // Overwrite the beginning of the data area
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(JournalFormat.DataStartOffset, SeekOrigin.Begin);
            fs.Write(new byte[100]);
        }

        SharedJournal.Compact(JournalPath);

        using (var journal = new SharedJournal(JournalPath))
        {
            Assert.AreEqual(0, journal.ReadAll().Count());
        }
    }

    [TestMethod]
    public void Compact_InvalidTrailingRecord_NextAppendStartsAfterValidRecords()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("good"u8);
            journal.Append("also good"u8);
            journal.Append("corrupt me"u8);
        }

        // Compute where "corrupt me" starts
        var firstSize = JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + 4);
        var secondSize = JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + 9);
        var thirdOffset = JournalFormat.DataStartOffset + firstSize + secondSize;

        // Zero out the third record header to make it invalid
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(thirdOffset, SeekOrigin.Begin);
            fs.Write(new byte[JournalFormat.RecordHeaderSize]);
        }

        SharedJournal.Compact(JournalPath);

        // Verify the tail pointer was actually updated — next append starts right after valid records
        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(2, records.Count);

            var expectedEnd = records[1].Offset +
                JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + records[1].Payload.Length);
            var appendResult = journal.Append("new record"u8);
            Assert.AreEqual(expectedEnd, appendResult.Offset,
                "After compaction, next append should start immediately after last valid record.");
        }
    }

    [TestMethod]
    public void Compact_GapInMiddle_ClosesGap()
    {
        long gapOffset;
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("before gap"u8);
            var r = journal.Append("will become gap"u8);
            gapOffset = r.Offset;
            journal.Append("after gap"u8);
        }

        // Zero out the second record header to simulate a crashed writer gap
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(gapOffset, SeekOrigin.Begin);
            fs.Write(new byte[JournalFormat.RecordHeaderSize]);
        }

        SharedJournal.Compact(JournalPath);

        // Verify records are contiguous (gap was closed)
        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(2, records.Count);
            CollectionAssert.AreEqual("before gap"u8.ToArray(), records[0].Payload.ToArray());
            CollectionAssert.AreEqual("after gap"u8.ToArray(), records[1].Payload.ToArray());

            // Second record should be immediately after the first (no gap)
            var expectedSecondOffset = records[0].Offset +
                JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + records[0].Payload.Length);
            Assert.AreEqual(expectedSecondOffset, records[1].Offset,
                "After compaction, records should be contiguous with no gaps.");
        }
    }

    [TestMethod]
    public void Compact_MultipleGaps_AllClosed()
    {
        long gap1Offset, gap2Offset;
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("record-a"u8);
            var r1 = journal.Append("gap-1"u8);
            gap1Offset = r1.Offset;
            journal.Append("record-b"u8);
            var r2 = journal.Append("gap-2"u8);
            gap2Offset = r2.Offset;
            journal.Append("record-c"u8);
        }

        // Zero out both gap records
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(gap1Offset, SeekOrigin.Begin);
            fs.Write(new byte[JournalFormat.RecordHeaderSize]);
            fs.Seek(gap2Offset, SeekOrigin.Begin);
            fs.Write(new byte[JournalFormat.RecordHeaderSize]);
        }

        SharedJournal.Compact(JournalPath);

        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(3, records.Count);
            CollectionAssert.AreEqual("record-a"u8.ToArray(), records[0].Payload.ToArray());
            CollectionAssert.AreEqual("record-b"u8.ToArray(), records[1].Payload.ToArray());
            CollectionAssert.AreEqual("record-c"u8.ToArray(), records[2].Payload.ToArray());
        }
    }

    [TestMethod]
    public void Compact_FileShrinks_NoTrailingWaste()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("keep"u8);
            journal.Append("will become gap"u8);
            journal.Append("also keep"u8);
        }

        var originalSize = new FileInfo(JournalPath).Length;

        // Zero out the second record header
        var firstRecordSize = JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + 4);
        var gapOffset = JournalFormat.DataStartOffset + firstRecordSize;
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(gapOffset, SeekOrigin.Begin);
            fs.Write(new byte[JournalFormat.RecordHeaderSize]);
        }

        SharedJournal.Compact(JournalPath);

        var compactedSize = new FileInfo(JournalPath).Length;
        Assert.IsTrue(compactedSize < originalSize,
            $"Compacted file ({compactedSize}) should be smaller than original ({originalSize}).");
    }

    [TestMethod]
    public void Compact_StaleCompactFile_CleanedUp()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("data"u8);
        }

        // Create a stale .compact file from a previous interrupted compaction
        var stalePath = JournalPath + ".compact";
        File.WriteAllText(stalePath, "stale data");

        SharedJournal.Compact(JournalPath);

        Assert.IsFalse(File.Exists(stalePath), "Stale .compact file should be cleaned up.");

        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(1, records.Count);
        }
    }

    [TestMethod]
    public void Compact_AppendAfterCompaction_Works()
    {
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("original"u8);
        }

        SharedJournal.Compact(JournalPath);

        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("after compact"u8);
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(2, records.Count);
            CollectionAssert.AreEqual("original"u8.ToArray(), records[0].Payload.ToArray());
            CollectionAssert.AreEqual("after compact"u8.ToArray(), records[1].Payload.ToArray());
        }
    }
}
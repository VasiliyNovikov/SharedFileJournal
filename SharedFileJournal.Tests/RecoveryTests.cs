using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SharedFileJournal.Internal;

namespace SharedFileJournal.Tests;

[TestClass]
public class RecoveryTests
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
    public void Recover_EmptyJournal_ReturnsZeroRecords()
    {
        using var journal = SharedJournal.Open(JournalPath);
        var result = journal.Recover();

        Assert.AreEqual((long)JournalFormat.DataStartOffset, result.ValidEndOffset);
        Assert.AreEqual(0, result.ValidRecordCount);
    }

    [TestMethod]
    public void Recover_ValidJournal_ReturnsAllRecords()
    {
        using var journal = SharedJournal.Open(JournalPath);
        journal.Append("record1"u8);
        journal.Append("record2"u8);
        journal.Append("record3"u8);

        var result = journal.Recover();

        Assert.AreEqual(3, result.ValidRecordCount);
    }

    [TestMethod]
    public void Recover_PartialLastRecord_UpdatesTail()
    {
        long validEnd;
        using (var journal = SharedJournal.Open(JournalPath))
        {
            journal.Append("good record"u8);
            var r = journal.Append("another good"u8);
            validEnd = r.Offset + r.TotalRecordLength;
        }

        // Append garbage bytes to simulate a partial write
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(new byte[50]); // partial/zeroed record
        }

        // Reopen and recover
        using (var journal = SharedJournal.Open(JournalPath))
        {
            var result = journal.Recover();

            Assert.AreEqual(2, result.ValidRecordCount);
            Assert.AreEqual(validEnd, result.ValidEndOffset);

            // After recovery, new appends should work
            journal.Append("after recovery"u8);
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(3, records.Count);
        }
    }

    [TestMethod]
    public void Recover_CorruptedChecksum_StopsAtCorruption()
    {
        using (var journal = SharedJournal.Open(JournalPath))
        {
            journal.Append("good"u8);
            journal.Append("will be corrupted"u8);
            journal.Append("never read"u8);
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

        using (var journal = SharedJournal.Open(JournalPath))
        {
            var result = journal.Recover();

            Assert.AreEqual(1, result.ValidRecordCount);
            Assert.AreEqual((long)(JournalFormat.DataStartOffset + firstRecordSize), result.ValidEndOffset);
        }
    }

    [TestMethod]
    public void Recover_CompletelyCorruptedFile_ReturnsZero()
    {
        using (var journal = SharedJournal.Open(JournalPath))
        {
            journal.Append("will be destroyed"u8);
        }

        // Overwrite the beginning of the data area
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(JournalFormat.DataStartOffset, SeekOrigin.Begin);
            fs.Write(new byte[100]);
        }

        using (var journal = SharedJournal.Open(JournalPath))
        {
            var result = journal.Recover();

            Assert.AreEqual(0, result.ValidRecordCount);
            Assert.AreEqual((long)JournalFormat.DataStartOffset, result.ValidEndOffset);
        }
    }

    [TestMethod]
    public void Recover_UpdatesTail_NextAppendStartsAtValidEnd()
    {
        long validEnd;
        using (var journal = SharedJournal.Open(JournalPath))
        {
            journal.Append("good"u8);
            var r = journal.Append("also good"u8);
            validEnd = r.Offset + r.TotalRecordLength;
            journal.Append("corrupt me"u8);
        }

        // Zero out the third record header to make it invalid
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(validEnd, SeekOrigin.Begin);
            fs.Write(new byte[JournalFormat.RecordHeaderSize]);
        }

        using (var journal = SharedJournal.Open(JournalPath))
        {
            var result = journal.Recover();
            Assert.AreEqual(validEnd, result.ValidEndOffset);

            // Verify the tail pointer was actually updated (CAS succeeded)
            var appendResult = journal.Append("new record"u8);
            Assert.AreEqual(validEnd, appendResult.Offset,
                "After recovery, next append should start at ValidEndOffset.");
        }
    }
}
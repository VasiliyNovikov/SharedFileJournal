using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SharedFileJournal.Internal;

namespace SharedFileJournal.Tests;

[TestClass]
public class SkipMarkerTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sfj-skip-" + Guid.NewGuid().ToString("N")[..8]);
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
    public void ReadAll_GapInMiddle_WritesSkipMarker()
    {
        long gapOffset;
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("before"u8);
            var r = journal.Append("gap record"u8);
            gapOffset = r.Offset;
            journal.Append("after"u8);
        }

        // Zero the second record header to create a gap
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(gapOffset, SeekOrigin.Begin);
            fs.Write(new byte[JournalFormat.RecordHeaderSize]);
        }

        // First read — should scan and write a skip marker
        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(2, records.Count);
        }

        // Verify skip marker was written at the gap offset
        var headerBuf = new byte[JournalFormat.RecordHeaderSize];
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(gapOffset, SeekOrigin.Begin);
            fs.ReadExactly(headerBuf);
        }

        var header = MemoryMarshal.Read<RecordHeader>(headerBuf);
        Assert.IsTrue(header.IsSkip(), "A skip marker should be written at the gap offset.");
        Assert.IsTrue(header.PayloadLength > 0, "Skip marker should have a positive PayloadLength.");
    }

    [TestMethod]
    public void ReadAll_SkipMarkerPresent_JumpsWithoutScanning()
    {
        long gapOffset;
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("before"u8);
            var r = journal.Append("gap record"u8);
            gapOffset = r.Offset;
            journal.Append("after"u8);
        }

        // Zero the second record header
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(gapOffset, SeekOrigin.Begin);
            fs.Write(new byte[JournalFormat.RecordHeaderSize]);
        }

        // First read — writes skip marker
        using (var journal = new SharedJournal(JournalPath))
        {
            _ = journal.ReadAll().ToList();
        }

        // Second read — should use skip marker (same results, faster)
        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(2, records.Count);
            CollectionAssert.AreEqual("before"u8.ToArray(), records[0].Payload.ToArray());
            CollectionAssert.AreEqual("after"u8.ToArray(), records[1].Payload.ToArray());
        }
    }

    [TestMethod]
    public void ReadAll_SkipMarkerSurvivesReopen()
    {
        long gapOffset;
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("first"u8);
            var r = journal.Append("middle"u8);
            gapOffset = r.Offset;
            journal.Append("last"u8);
        }

        // Create gap
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(gapOffset, SeekOrigin.Begin);
            fs.Write(new byte[JournalFormat.RecordHeaderSize]);
        }

        // Read to trigger skip marker write
        using (var journal = new SharedJournal(JournalPath))
        {
            _ = journal.ReadAll().ToList();
        }

        // Reopen and verify skip marker is used correctly
        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(2, records.Count);
            CollectionAssert.AreEqual("first"u8.ToArray(), records[0].Payload.ToArray());
            CollectionAssert.AreEqual("last"u8.ToArray(), records[1].Payload.ToArray());
        }
    }

    [TestMethod]
    public void ReadAll_CorruptedSkipMarker_FallsBackToScan()
    {
        long gapOffset;
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("first"u8);
            var r2 = journal.Append("second"u8);
            gapOffset = r2.Offset;
            journal.Append("third"u8);
        }

        // Write a skip marker with an absurdly large PayloadLength
        var badHeader = RecordHeader.CreateSkip(int.MaxValue / 2);
        var badBuf = new byte[JournalFormat.RecordHeaderSize];
        MemoryMarshal.Write(badBuf, in badHeader);
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(gapOffset, SeekOrigin.Begin);
            fs.Write(badBuf);
        }

        // ReadAll should handle the bad skip marker gracefully — bounds check fails,
        // falls back to scan, and recovers "third"
        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(2, records.Count);
            CollectionAssert.AreEqual("first"u8.ToArray(), records[0].Payload.ToArray());
            CollectionAssert.AreEqual("third"u8.ToArray(), records[1].Payload.ToArray());
        }
    }

    [TestMethod]
    public void ReadAll_MultipleGaps_AllGetSkipMarkers()
    {
        long gap1Offset, gap2Offset;
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("a"u8);
            var r1 = journal.Append("gap1"u8);
            gap1Offset = r1.Offset;
            journal.Append("b"u8);
            var r2 = journal.Append("gap2"u8);
            gap2Offset = r2.Offset;
            journal.Append("c"u8);
        }

        // Zero both gap records
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(gap1Offset, SeekOrigin.Begin);
            fs.Write(new byte[JournalFormat.RecordHeaderSize]);
            fs.Seek(gap2Offset, SeekOrigin.Begin);
            fs.Write(new byte[JournalFormat.RecordHeaderSize]);
        }

        // First read — writes skip markers
        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(3, records.Count);
        }

        // Verify both gaps have skip markers
        var headerBuf = new byte[JournalFormat.RecordHeaderSize];
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(gap1Offset, SeekOrigin.Begin);
            fs.ReadExactly(headerBuf);
            Assert.IsTrue(MemoryMarshal.Read<RecordHeader>(headerBuf).IsSkip(), "First gap should have skip marker.");

            fs.Seek(gap2Offset, SeekOrigin.Begin);
            fs.ReadExactly(headerBuf);
            Assert.IsTrue(MemoryMarshal.Read<RecordHeader>(headerBuf).IsSkip(), "Second gap should have skip marker.");
        }
    }

    [TestMethod]
    public void ReadAll_GapWithNonZeroGarbage_SkipMarkerWritten()
    {
        long gapOffset;
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("before"u8);
            var r = journal.Append("gap record"u8);
            gapOffset = r.Offset;
            journal.Append("after"u8);
        }

        // Write non-zero garbage (not SFJR, not SFJS) at the gap offset
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(gapOffset, SeekOrigin.Begin);
            fs.Write([0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00, 0x00, 0x00]);
        }

        // Read — CAS uses current value as comparand, so it succeeds even for non-zero data
        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(2, records.Count);
        }

        // Verify skip marker was written over the non-zero garbage
        var headerBuf = new byte[JournalFormat.RecordHeaderSize];
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(gapOffset, SeekOrigin.Begin);
            fs.ReadExactly(headerBuf);
        }

        var header = MemoryMarshal.Read<RecordHeader>(headerBuf);
        Assert.IsTrue(header.IsSkip(), "Skip marker should be written over non-zero garbage.");
    }

    [TestMethod]
    public void ReadAll_ChecksumMismatchWithValidMagic_DoesNotWriteSkipMarker()
    {
        long corruptOffset;
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("before"u8);
            var r = journal.Append("corrupt me"u8);
            corruptOffset = r.Offset;
            journal.Append("after"u8);
        }

        // Corrupt the payload (but leave the SFJR magic and PayloadLength intact)
        // This simulates a partially-written record from an in-flight writer
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(corruptOffset + JournalFormat.RecordHeaderSize, SeekOrigin.Begin);
            fs.Write(new byte[10]); // zeros over the payload → checksum mismatch
        }

        // ReadAll should recover past the corrupted record
        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(2, records.Count);
            CollectionAssert.AreEqual("before"u8.ToArray(), records[0].Payload.ToArray());
            CollectionAssert.AreEqual("after"u8.ToArray(), records[1].Payload.ToArray());
        }

        // Verify NO skip marker was written — the SFJR magic must be preserved
        // because a concurrent writer could still be in-flight
        var headerBuf = new byte[JournalFormat.RecordHeaderSize];
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(corruptOffset, SeekOrigin.Begin);
            fs.ReadExactly(headerBuf);
        }

        var header = MemoryMarshal.Read<RecordHeader>(headerBuf);
        Assert.AreEqual(JournalFormat.RecordHeaderMagic, header.Magic,
            "Skip marker must NOT be written when the observed magic is RecordHeaderMagic.");
    }

    [TestMethod]
    public void ReadAll_CorruptChecksumWithValidMagic_StillRecoversOnReread()
    {
        long corruptOffset;
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("first"u8);
            var r = journal.Append("will corrupt"u8);
            corruptOffset = r.Offset;
            journal.Append("last"u8);
        }

        // Corrupt checksum field only (leave magic, payload length, and payload intact)
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(corruptOffset + 8, SeekOrigin.Begin); // checksum is at offset 8 in header
            fs.Write(new byte[8]); // zero the checksum → mismatch
        }

        // Multiple reads should all recover correctly without a skip marker
        for (var i = 0; i < 3; i++)
        {
            using var journal = new SharedJournal(JournalPath);
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(2, records.Count, $"Read pass {i}: expected 2 records");
            CollectionAssert.AreEqual("first"u8.ToArray(), records[0].Payload.ToArray());
            CollectionAssert.AreEqual("last"u8.ToArray(), records[1].Payload.ToArray());
        }
    }

    [TestMethod]
    public void Compact_RemovesSkipMarkers()
    {
        long gapOffset;
        using (var journal = new SharedJournal(JournalPath))
        {
            journal.Append("keep-a"u8);
            var r = journal.Append("will be gap"u8);
            gapOffset = r.Offset;
            journal.Append("keep-b"u8);
        }

        // Create gap and trigger skip marker write
        using (var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(gapOffset, SeekOrigin.Begin);
            fs.Write(new byte[JournalFormat.RecordHeaderSize]);
        }

        using (var journal = new SharedJournal(JournalPath))
        {
            _ = journal.ReadAll().ToList(); // writes skip marker
        }

        // Compact — should produce a clean file with no skip markers
        SharedJournal.Compact(JournalPath);

        using (var journal = new SharedJournal(JournalPath))
        {
            var records = journal.ReadAll().ToList();
            Assert.AreEqual(2, records.Count);
            CollectionAssert.AreEqual("keep-a"u8.ToArray(), records[0].Payload.ToArray());
            CollectionAssert.AreEqual("keep-b"u8.ToArray(), records[1].Payload.ToArray());

            // Verify records are contiguous (no gaps or skip markers)
            var expectedSecondOffset = records[0].Offset +
                JournalFormat.AlignRecordSize(JournalFormat.RecordHeaderSize + records[0].Payload.Length);
            Assert.AreEqual(expectedSecondOffset, records[1].Offset);
        }
    }
}
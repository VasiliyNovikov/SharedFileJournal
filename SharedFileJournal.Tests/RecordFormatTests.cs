using System;
using System.Runtime.InteropServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SharedFileJournal.Internal;

namespace SharedFileJournal.Tests;

[TestClass]
public class RecordFormatTests
{
    [TestMethod]
    public void WriteAndReadRecord_RoundTrips()
    {
        var payload = "hello world"u8.ToArray();
        var totalLength = JournalFormat.RecordHeaderSize + payload.Length;
        var buffer = new byte[totalLength];

        JournalFormat.WriteRecord(buffer, payload);

        var header = MemoryMarshal.Read<RecordHeader>(buffer);
        Assert.AreEqual(RecordStatus.Record, header.Validate());
        Assert.AreEqual(payload.Length, header.PayloadLength);
        Assert.AreEqual(JournalFormat.ComputeChecksum(payload), header.Checksum);
    }

    [TestMethod]
    public void ReadHeader_InvalidMagic_IsInvalid()
    {
        var buffer = new byte[JournalFormat.RecordHeaderSize];
        var header = MemoryMarshal.Read<RecordHeader>(buffer);
        Assert.AreEqual(RecordStatus.Corrupt, header.Validate());
    }

    [TestMethod]
    public void ComputeChecksum_Deterministic()
    {
        var data = "deterministic"u8.ToArray();
        var hash1 = JournalFormat.ComputeChecksum(data);
        var hash2 = JournalFormat.ComputeChecksum(data);
        Assert.AreEqual(hash1, hash2);
    }

    [TestMethod]
    public void ComputeChecksum_DifferentForDifferentInput()
    {
        var hash1 = JournalFormat.ComputeChecksum("hello"u8);
        var hash2 = JournalFormat.ComputeChecksum("world"u8);
        Assert.AreNotEqual(hash1, hash2);
    }

    [TestMethod]
    public void WriteRecord_EmptyPayload_Succeeds()
    {
        var buffer = new byte[JournalFormat.RecordHeaderSize];
        JournalFormat.WriteRecord(buffer, ReadOnlySpan<byte>.Empty);

        var header = MemoryMarshal.Read<RecordHeader>(buffer);
        Assert.AreEqual(RecordStatus.Record, header.Validate());
        Assert.AreEqual(0, header.PayloadLength);
    }

    [TestMethod]
    public void StructSizes_MatchConstants()
    {
        var headerSize = Marshal.SizeOf<RecordHeader>();
        Assert.AreEqual(JournalFormat.RecordHeaderSize, headerSize);
    }
}
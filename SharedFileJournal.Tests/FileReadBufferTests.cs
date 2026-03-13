using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SharedFileJournal.Internal;

namespace SharedFileJournal.Tests;

[TestClass]
public class FileReadBufferTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sfj-buffer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* best effort */ }
    }

    private string FilePath => Path.Combine(_tempDir, "buffer.bin");

    [TestMethod]
    public async Task ReadAsync_CancelledMiss_DoesNotServeStaleBytes()
    {
        await File.WriteAllBytesAsync(FilePath, "ABCDWXYZ"u8.ToArray());

        using var handle = File.OpenHandle(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new FileReadBuffer(handle, readAheadSize: 4);

        CollectionAssert.AreEqual("ABCD"u8.ToArray(), buffer.Read(0, 4).ToArray());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => buffer.ReadAsync(4, 4, cts.Token).AsTask());

        CollectionAssert.AreEqual("WXYZ"u8.ToArray(), buffer.Read(4, 4).ToArray());
    }

    [TestMethod]
    public async Task ReadAsync_CancelledResize_DoesNotSwapActiveBuffer()
    {
        var expected = new byte[8 * 1024];
        new Random(42).NextBytes(expected);
        await File.WriteAllBytesAsync(FilePath, expected);

        using var handle = File.OpenHandle(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new FileReadBuffer(handle, readAheadSize: 16);

        CollectionAssert.AreEqual(expected[..16], buffer.Read(0, 16).ToArray());
        var activeBuffer = GetActiveBuffer(buffer);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => buffer.ReadAsync(1024, 4096, cts.Token).AsTask());

        Assert.AreSame(activeBuffer, GetActiveBuffer(buffer));
        CollectionAssert.AreEqual(expected.AsSpan(1024, 16).ToArray(), buffer.Read(1024, 16).ToArray());
    }

    [TestMethod]
    public void Read_FailedMiss_RetriesInsteadOfServingStaleBytes()
    {
        File.WriteAllBytes(FilePath, "ABCDWXYZ"u8.ToArray());

        using var handle = File.OpenHandle(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new FileReadBuffer(handle, readAheadSize: 4);

        CollectionAssert.AreEqual("ABCD"u8.ToArray(), buffer.Read(0, 4).ToArray());

        handle.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => buffer.Read(4, 4));
        Assert.ThrowsExactly<ObjectDisposedException>(() => buffer.Read(4, 4));
    }

    private static byte[] GetActiveBuffer(FileReadBuffer buffer) =>
        (byte[])typeof(FileReadBuffer)
            .GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(buffer)!;
}

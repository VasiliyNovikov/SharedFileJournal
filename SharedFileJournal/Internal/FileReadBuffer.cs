using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Win32.SafeHandles;

namespace SharedFileJournal.Internal;

/// <summary>
/// Provides buffered positional reads from a file, returning slices of an internal
/// pooled buffer. Automatically refills and resizes the buffer as needed.
/// </summary>
/// <remarks>
/// Returned <see cref="ReadOnlyMemory{T}"/> slices reference the internal buffer
/// and are only valid until the next <see cref="Read"/> or <see cref="ReadAsync"/> call.
/// </remarks>
internal sealed class FileReadBuffer(SafeFileHandle fileHandle, int readAheadSize) : IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(readAheadSize);
    private long _bufferFileOffset = -1;
    private int _bufferBytesRead;

    private bool HasBufferedRange(long fileOffset, int length) =>
        _bufferFileOffset >= 0 && fileOffset >= _bufferFileOffset && fileOffset + length <= _bufferFileOffset + _bufferBytesRead;

    private static void ReturnTemporaryBuffer(byte[] fillBuffer, byte[] activeBuffer)
    {
        if (!ReferenceEquals(fillBuffer, activeBuffer))
            ArrayPool<byte>.Shared.Return(fillBuffer);
    }

    private (byte[] Buffer, int ReadLength) PrepareFill(int length)
    {
        var readLength = Math.Max(length, readAheadSize);
        return _buffer.Length >= readLength
            ? (_buffer, readLength)
            : (ArrayPool<byte>.Shared.Rent(readLength), readLength);
    }

    private void PublishFill(byte[] fillBuffer, long fileOffset, int bytesRead)
    {
        if (!ReferenceEquals(fillBuffer, _buffer))
        {
            var previousBuffer = _buffer;
            _buffer = fillBuffer;
            ArrayPool<byte>.Shared.Return(previousBuffer);
        }

        _bufferFileOffset = fileOffset;
        _bufferBytesRead = bytesRead;
    }

    private void ResetCacheState()
    {
        _bufferFileOffset = -1;
        _bufferBytesRead = 0;
    }

    private ReadOnlyMemory<byte> GetSlice(long fileOffset, int length)
    {
        var bufferIndex = (int)(fileOffset - _bufferFileOffset);
        return bufferIndex + length <= _bufferBytesRead
            ? _buffer.AsMemory(bufferIndex, length)
            : ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>
    /// Reads <paramref name="length"/> bytes starting at file position <paramref name="fileOffset"/>.
    /// Returns the data as a slice of the internal buffer, or an empty memory if
    /// not enough bytes are available in the file.
    /// </summary>
    public ReadOnlyMemory<byte> Read(long fileOffset, int length)
    {
        if (!HasBufferedRange(fileOffset, length))
        {
            var (fillBuffer, readLength) = PrepareFill(length);
            ResetCacheState();
            try
            {
                var bytesRead = RandomAccess.Read(fileHandle, fillBuffer.AsSpan(0, readLength), fileOffset);
                PublishFill(fillBuffer, fileOffset, bytesRead);
            }
            catch
            {
                ReturnTemporaryBuffer(fillBuffer, _buffer);
                throw;
            }
        }
        return GetSlice(fileOffset, length);
    }

    /// <summary>
    /// Asynchronously reads <paramref name="length"/> bytes starting at file position <paramref name="fileOffset"/>.
    /// Returns the data as a slice of the internal buffer, or an empty memory if
    /// not enough bytes are available in the file.
    /// </summary>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long fileOffset, int length, CancellationToken cancellationToken)
    {
        if (!HasBufferedRange(fileOffset, length))
        {
            var (fillBuffer, readLength) = PrepareFill(length);
            ResetCacheState();
            try
            {
                var bytesRead = await RandomAccess.ReadAsync(fileHandle, fillBuffer.AsMemory(0, readLength), fileOffset, cancellationToken);
                PublishFill(fillBuffer, fileOffset, bytesRead);
            }
            catch
            {
                ReturnTemporaryBuffer(fillBuffer, _buffer);
                throw;
            }
        }
        return GetSlice(fileOffset, length);
    }

    /// <summary>
    /// Invalidates the buffer, forcing the next <see cref="Read"/> to fetch from the file.
    /// </summary>
    public void Invalidate()
    {
        _bufferBytesRead = 0;
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
    }
}

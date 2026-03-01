using System;
using System.Buffers;
using System.IO;

using Microsoft.Win32.SafeHandles;

namespace SharedFileJournal.Internal;

/// <summary>
/// Provides buffered positional reads from a file, returning slices of an internal
/// pooled buffer. Automatically refills and resizes the buffer as needed.
/// </summary>
/// <remarks>
/// Returned <see cref="ReadOnlyMemory{T}"/> slices reference the internal buffer
/// and are only valid until the next <see cref="Read"/> call.
/// </remarks>
internal sealed class FileReadBuffer(SafeFileHandle fileHandle, int readAheadSize) : IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(readAheadSize);
    private long _bufferFileOffset = -1;
    private int _bufferBytesRead;

    /// <summary>
    /// Reads <paramref name="length"/> bytes starting at file position <paramref name="fileOffset"/>.
    /// Returns the data as a slice of the internal buffer, or an empty memory if
    /// not enough bytes are available in the file.
    /// </summary>
    public ReadOnlyMemory<byte> Read(long fileOffset, int length)
    {
        if (_bufferFileOffset < 0 || fileOffset < _bufferFileOffset || fileOffset + length > _bufferFileOffset + _bufferBytesRead)
        {
            if (_buffer.Length < length)
            {
                var newBuffer = ArrayPool<byte>.Shared.Rent(length);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = newBuffer;
            }
            _bufferFileOffset = fileOffset;
            _bufferBytesRead = RandomAccess.Read(fileHandle, _buffer.AsSpan(0, Math.Max(length, readAheadSize)), fileOffset);
        }

        var bufferIndex = (int)(fileOffset - _bufferFileOffset);
        return bufferIndex + length <= _bufferBytesRead
            ? _buffer.AsMemory(bufferIndex, length)
            : ReadOnlyMemory<byte>.Empty;
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
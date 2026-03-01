using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

using Microsoft.Win32.SafeHandles;

namespace SharedFileJournal.Internal;

/// <summary>
/// Maps a region of a file-backed memory-mapped file to a pointer of type <typeparamref name="T"/>.
/// </summary>
internal sealed unsafe class MemoryMappedView<T> : IDisposable where T : unmanaged
{
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly T* _pointer;
    private int _disposed;

    public MemoryMappedView(SafeFileHandle fileHandle, long offset)
    {
        _map = MemoryMappedFile.CreateFromFile(fileHandle, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
        try
        {
            _view = _map.CreateViewAccessor(offset, sizeof(T), MemoryMappedFileAccess.ReadWrite);
            byte* rawPtr = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref rawPtr);
            _pointer = (T*)(rawPtr + _view.PointerOffset);
        }
        catch
        {
            _view?.Dispose();
            _map.Dispose();
            throw;
        }
    }

    public ref T Value => ref *_pointer;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _map.Dispose();
    }
}
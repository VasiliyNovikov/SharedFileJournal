using System;
using System.IO;

using Microsoft.Win32.SafeHandles;

namespace SharedFileJournal.Internal;

internal sealed class JournalLockFile(string journalPath, FileShare fileShare) : IDisposable
{
    private readonly SafeFileHandle _handle = File.OpenHandle(journalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, fileShare);

    public void Dispose() => _handle.Dispose();
}
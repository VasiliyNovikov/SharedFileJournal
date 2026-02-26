# SharedFileJournal

A cross-platform .NET library for **high-speed concurrent multi-process append** to a shared journal file — without per-write file locking.

## Key Features

- **Multi-process safe**: Multiple processes can append concurrently via atomic offset reservation
- **No file locks on writes**: Uses `Interlocked.Add` on a memory-mapped metadata region for lock-free space reservation, then `RandomAccess.Write` at the reserved offset
- **Recoverable format**: Self-validating records (header + payload + trailer with FNV-1a checksum) let readers detect and recover from partial/crashed writes
- **Cross-platform**: Works on Windows and Linux with .NET 10+

## Architecture

### Single-file design

The journal is a single file containing a 4 KB metadata header followed by sequential record data.
The first 4096 bytes are memory-mapped for atomic coordination; records start at offset 4096.

### Atomic reservation strategy

The metadata file is mapped via `MemoryMappedFile.CreateFromFile`. A raw pointer to the `NextWriteOffset` field (at cache-line-aligned offset 64 within the file header) is used with `Interlocked.Add` for atomic fetch-add semantics. This works across processes because the mapping is backed by the same physical pages, and `Interlocked` compiles to hardware atomics (`lock xadd` on x86-64) that are coherent across all sharers.

### Record format (48 bytes overhead)

```
Header (32 bytes): Magic "SFJR" | Version | HeaderLen | PayloadLen | TotalLen | Offset | Checksum
Payload:           Variable-length byte data
Trailer (16 bytes): Magic "SFJT" | TotalLen | Offset
```

## Quick Start

```csharp
using SharedFileJournal;

// Open (or create) a journal — safe for multiple processes
using var journal = SharedJournal.Open("/path/to/myjournal");

// Append records (thread-safe, process-safe)
journal.Append("hello"u8);
journal.Append(myPayloadBytes);

// Read all valid records
foreach (var record in journal.ReadAll())
    Console.WriteLine($"offset={record.Offset} len={record.Payload.Length}");

// Recover after a crash (scans and resets tail to last valid record)
var result = journal.Recover();
```

## API

| Type | Description |
|------|-------------|
| `SharedJournal` | Main entry point — `Open`, `Append`, `ReadAll`, `Recover`, `Dispose` |
| `SharedJournalOptions` | Configuration (`FlushMode`) |
| `FlushMode` | `None` (default) or `WriteThrough` |
| `JournalAppendResult` | Offset and total length of appended record |
| `JournalRecord` | Offset and payload of a read record |
| `JournalRecoveryResult` | Valid end offset and record count |

## Durability

| FlushMode | Behavior |
|-----------|----------|
| `None` | Concurrent correctness only; durability depends on OS page cache |
| `WriteThrough` | Data file opened with `FileOptions.WriteThrough` |

## Guarantees (V1)

- ✅ No two writers write to the same byte range
- ✅ Readers detect incomplete/corrupt tail records
- ✅ Multiple processes can append concurrently
- ✅ Recovery stops at first invalid record and resets the tail

## Non-guarantees (V1)

- ❌ No reclamation of space reserved by crashed writers
- ❌ No stable global commit order beyond reservation order
- ❌ No compaction, deletion, or indexing
- ❌ Not a transactional database or queue

## Demo

```bash
# Run the stress test (4 threads × 10,000 records)
dotnet run --project SharedFileJournal.Demo -- stress

# Other commands
dotnet run --project SharedFileJournal.Demo -- init /tmp/myjournal
dotnet run --project SharedFileJournal.Demo -- write /tmp/myjournal "hello world"
dotnet run --project SharedFileJournal.Demo -- read /tmp/myjournal
dotnet run --project SharedFileJournal.Demo -- recover /tmp/myjournal
```

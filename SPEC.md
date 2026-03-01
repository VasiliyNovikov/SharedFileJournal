# SharedFileJournal Format Specification

This document describes the on-disk file format, read/write algorithms, and
concurrency model of the SharedFileJournal.

## 1. File Layout Overview

A journal is a single file divided into two regions:

```
+========================+  offset 0
|   Metadata Header      |
|   (4096 bytes)         |
|   memory-mapped for    |
|   atomic operations    |
+========================+  offset 4096  (DataStartOffset)
|                        |
|   Record Data Region   |
|   (variable length)    |
|                        |
|   [ Record 0 ]         |
|   [ Record 1 ]         |
|   [ ...      ]         |
|   [ Record N ]         |
|                        |
+------------------------+  offset NextWriteOffset
|   (unwritten space)    |
+------------------------+  EOF
```

All multi-byte integers are **little-endian**.

---

## 2. Metadata Header

The first 4096 bytes of the file are the metadata header. Only the first 72
bytes carry structured fields; the remainder is reserved padding. The entire
4096-byte region is memory-mapped (`MemoryMappedFile`) so that the
`NextWriteOffset` field can be updated with hardware atomics across processes.

### 2.1 MetadataHeader Layout (72 bytes)

```
 Byte offset   Size   Field            Description
+-----------+------+----------------+------------------------------------+
| 0         | 8    | Magic          | 0x004154454D4A4653                 |
|           |      |                | ("SFJMETA\0" in little-endian)     |
+-----------+------+----------------+------------------------------------+
| 8         | 4    | Version        | Format version (currently 1)       |
+-----------+------+----------------+------------------------------------+
| 12        | 52   | _reserved      | Zero-filled padding                |
+-----------+------+----------------+------------------------------------+
| 64        | 8    | NextWriteOffset| File offset of next append         |
|           |      |                | (cache-line aligned for atomics)   |
+-----------+------+----------------+------------------------------------+
```

```
 0                   8           12                                  64          72
 +-------------------+-----------+-----------------------------------+-----------+
 |      Magic        |  Version  |          _reserved (52 B)         | NextWrite |
 | 0x004154454D4A4653|     1     |         (all zeros)               |  Offset   |
 +-------------------+-----------+-----------------------------------+-----------+
 |<--- 8 bytes ----->|<- 4 B --->|<------------ 52 bytes ---------->|<- 8 B --->|
```

**Field details:**

- **Magic** (`ulong`): Identifies the file as a SharedFileJournal. Written
  atomically via `Interlocked.CompareExchange` during initialization.
- **Version** (`uint`): Format version. Written with `Volatile.Write` *after*
  `NextWriteOffset` is set, creating a publication barrier.
- **_reserved** (52 bytes): Padding to push `NextWriteOffset` to the 64-byte
  cache-line boundary. Always zero.
- **NextWriteOffset** (`long`): The file offset where the next record will be
  written. Updated atomically via `Interlocked.Add` during appends. Minimum
  valid value is 4096 (`DataStartOffset`). Must be aligned to 16 bytes
  (`RecordAlignment`).

### 2.2 Remaining Header Space (bytes 72..4095)

The rest of the 4096-byte metadata region is implicitly zero and reserved for
future use. The OS zero-fills the file when it is first created and extended
to `MetadataFileSize`.

---

## 3. Record Format

Records are stored sequentially in the data region starting at offset 4096.
Every record is aligned to a **16-byte boundary** (`RecordAlignment`).

### 3.1 Record Header (16 bytes)

```
 Byte offset   Size   Field           Description
+-----------+------+----------------+------------------------------------+
| 0         | 4    | Magic          | 0x524A4653 ("SFJR" little-endian)  |
+-----------+------+----------------+------------------------------------+
| 4         | 4    | PayloadLength  | Payload size in bytes (signed int) |
+-----------+------+----------------+------------------------------------+
| 8         | 8    | Checksum       | FNV-1a 64-bit hash of the payload  |
+-----------+------+----------------+------------------------------------+
```

```
 0           4               8                              16
 +-----------+---------------+------------------------------+
 |   Magic   | PayloadLength |          Checksum            |
 |  "SFJR"   |   (4 bytes)   |         (8 bytes)            |
 +-----------+---------------+------------------------------+
 |<- 4 B --->|<--- 4 B ---->|<---------- 8 B ------------->|
```

### 3.2 Complete Record Layout

```
 +------------------+  record start (16-byte aligned)
 |  Record Header   |  16 bytes (Magic + PayloadLength + Checksum)
 +------------------+
 |  Payload         |  PayloadLength bytes
 +------------------+
 |  Alignment Pad   |  0..15 bytes of zero padding
 +------------------+  next 16-byte boundary
```

**Total on-disk size** = `align16(RecordHeaderSize + PayloadLength)`

where `align16(n) = (n + 15) & ~15`.

### 3.3 Skip Marker

A skip marker uses the same 16-byte header structure but with a different
magic value. It marks a corrupted or gap region that readers should jump over.

```
 Byte offset   Size   Field           Description
+-----------+------+----------------+------------------------------------+
| 0         | 4    | Magic          | 0x534A4653 ("SFJS" little-endian)  |
+-----------+------+----------------+------------------------------------+
| 4         | 4    | PayloadLength  | Size of the region to skip (bytes) |
+-----------+------+----------------+------------------------------------+
| 8         | 8    | Checksum       | Unused (0)                         |
+-----------+------+----------------+------------------------------------+
```

The total region covered by a skip marker is
`align16(RecordHeaderSize + PayloadLength)`, identical to normal record sizing.

Skip markers are written atomically using a CAS on the 8-byte
`Magic + PayloadLength` pair (see section 6.5).

### 3.4 Validity Rules

A record header is **valid** when:
- `Magic == 0x524A4653` (SFJR)
- `PayloadLength >= 0`
- `PayloadLength <= INT_MAX - RecordHeaderSize - RecordAlignment`

A skip header is **valid** when:
- `Magic == 0x534A4653` (SFJS)
- `PayloadLength >= 0`
- `PayloadLength <= INT_MAX - RecordHeaderSize - RecordAlignment`

### 3.5 Checksum Algorithm

FNV-1a 64-bit over the payload bytes:

```
hash = 14695981039346656037  (FNV offset basis)
for each byte b in payload:
    hash = hash XOR b
    hash = hash * 1099511628211  (FNV prime)
return hash
```

The checksum covers only the payload, not the header.

### 3.6 Example: Journal with Three Records

```
 Offset   Content
+--------+------------------------------------------------------------+
| 0      | Metadata Header (4096 bytes)                               |
|        |   Magic=0x004154454D4A4653  Version=1  NextWriteOffset=4144|
+--------+------------------------------------------------------------+
| 4096   | Record 0: Header(SFJR, len=5, checksum) + "hello" + 11 pad|
|        |   total aligned size = align16(16+5) = 32 bytes            |
+--------+------------------------------------------------------------+
| 4128   | Record 1: Header(SFJR, len=0, checksum) + 0 pad            |
|        |   total aligned size = align16(16+0) = 16 bytes            |
+--------+------------------------------------------------------------+
| 4144   | (NextWriteOffset points here -- next append goes here)     |
+--------+------------------------------------------------------------+
```

---

## 4. File Initialization Protocol

When a `SharedJournal` is opened, the constructor either initializes a new
file or validates an existing one. Multiple processes may race to open/create
the same file concurrently.

### 4.1 New File Path (Initializer Wins)

```
  Process A                          File (memory-mapped)
  ─────────                          ────────────────────
  CAS(Magic, 0 -> SFJMETA) ──────>  Magic = SFJMETA
  Volatile.Write(NextWriteOffset,    NextWriteOffset = 4096
                 4096) ───────────>
  Volatile.Write(Version, 1) ─────>  Version = 1
```

The initializer writes `NextWriteOffset` **before** `Version`. This ensures
that any process spinning on `Version` will see a valid `NextWriteOffset`
when the spin completes.

### 4.2 Existing File Path (Follower Joins)

```
  Process B                          File (memory-mapped)
  ─────────                          ────────────────────
  CAS(Magic, 0 -> SFJMETA)          returns SFJMETA (lost race)
  spin while Version == 0 ◄──────── Version = 1 (eventually visible)
  validate Magic == SFJMETA
  validate Version == 1
  validate NextWriteOffset >= 4096
          and aligned to 16
```

### 4.3 Crash Recovery Path

If Process A crashed after writing `Magic` but before writing `Version`,
followers will spin on `Version == 0` indefinitely. After a **5-second
deadline**, the follower assumes the initializer crashed and completes
initialization:

```
  Process B (after 5s timeout)       File (memory-mapped)
  ─────────                          ────────────────────
  validate Magic == SFJMETA
  CAS(NextWriteOffset, 0 -> 4096)   NextWriteOffset = 4096 (if was 0)
  validate NextWriteOffset >= 4096
          and aligned to 16
  Volatile.Write(Version, 1) ─────>  Version = 1
```

The CAS on `NextWriteOffset` only succeeds if it is still 0 (the initializer
may have written it before crashing). After the CAS, `NextWriteOffset` is
validated -- if it holds a corrupted non-zero value that is invalid (below
`DataStartOffset` or misaligned), an `InvalidOperationException` is thrown.

### 4.4 NextWriteOffset Validation

On every open of an existing file, the following invariants are checked:

- `NextWriteOffset >= 4096` (DataStartOffset)
- `NextWriteOffset % 16 == 0` (RecordAlignment)

Violation of either condition throws `InvalidOperationException`, preventing
silent corruption from a damaged metadata header.

---

## 5. Append Algorithm

Appending a record is a two-phase operation: **reserve** then **write**.

### 5.1 Space Reservation (Lock-Free)

```
  Writer                             MetadataHeader (memory-mapped)
  ──────                             ──────────────────────────────
  dataLen = 16 + payloadLength
  alignedLen = align16(dataLen)
  offset = Interlocked.Add(          NextWriteOffset += alignedLen
    &NextWriteOffset, alignedLen)    (atomic fetch-add)
    - alignedLen
```

`Interlocked.Add` compiles to a hardware atomic instruction (e.g., `lock xadd`
on x86-64). Because the memory-mapped region is backed by the same physical
pages across processes, this provides cross-process atomicity without any
locks or mutexes.

After reservation, the writer "owns" the byte range `[offset, offset + alignedLen)`
exclusively. No other writer can receive an overlapping range.

### 5.2 Record Write

```
  1. Serialize header + payload into a local buffer:
       [Magic=SFJR | PayloadLength | Checksum | Payload]

  2. RandomAccess.Write(fileHandle, buffer, offset)
       - OS extends the file automatically if offset > current length
       - No file lock is held during the write

  3. (Optional) If FlushMode.WriteThrough, call FlushToDisk
```

### 5.3 Crash Window

There is a window between reservation and write completion where the reserved
region contains zeros (or stale data from a previous file). If the writer
crashes during this window:

```
  +-------------------+--------------------+-------------------+
  | Record A (valid)  | Reserved but       | Record C (valid)  |
  |                   | never written      |                   |
  |                   | (all zeros / gap)  |                   |
  +-------------------+--------------------+-------------------+
```

This gap is handled by the read algorithm's corruption recovery (section 6.3).

---

## 6. Read Algorithm

`ReadAll` scans records sequentially from `DataStartOffset` up to a snapshot
of `NextWriteOffset` (the "tail"), yielding valid records and recovering from
corruption.

### 6.1 Overall Flow

```
  tail = Volatile.Read(NextWriteOffset)     // snapshot
  offset = 4096                             // DataStartOffset
  payloadBuf = rent from ArrayPool          // reused across records

  while offset + MinRecordSize <= tail:
      read header at offset
      if header is skip marker:
          jump forward by skip marker size
          continue
      if header is invalid or oversized:
          skip corrupted region (section 6.3)
          continue
      if record extends beyond tail:
          stop (incomplete trailing record)
      read payload into payloadBuf (grow if needed)
      if checksum mismatch:
          skip corrupted region (section 6.3)
          continue
      yield record (Payload references payloadBuf)
      advance offset by aligned record size

  return payloadBuf to ArrayPool
```

**Payload lifetime:** Each yielded record's `Payload` references a pooled buffer that
is reused on the next iteration. The payload is only valid until the next `MoveNext`
call on the enumerator (or until enumeration ends). Callers that need to retain the
data must copy it before advancing (e.g. via `Payload.ToArray()`).

### 6.2 Skip Marker Handling

When a skip marker (`Magic == SFJS`) is encountered:

```
  skipLen = align16(16 + header.PayloadLength)
  if offset + skipLen <= tail:
      offset += skipLen        // fast jump past the gap
  else:
      // skip marker itself is corrupted (extends beyond tail)
      // fall through to corruption recovery
```

### 6.3 Corruption Recovery

When an invalid header, checksum mismatch, or truncated read is detected:

```
  1. Compute scan limit = min(tail, offset + ~2GB)
     (bounded to prevent int overflow in skip marker PayloadLength)

  2. Scan forward from offset+1, at 16-byte aligned positions,
     in 4096-byte chunks, looking for the next occurrence of
     RecordHeaderMagic (0x524A4653) or SkipHeaderMagic (0x534A4653).

  3. If a candidate is found at nextOffset:
       - If the observed magic at offset is not a recognized value
         (not RecordHeaderMagic and not SkipHeaderMagic), attempt to
         write a skip marker at offset covering the gap
         [offset, nextOffset) (see section 6.5)
       - Set offset = nextOffset and continue the read loop

  4. If no candidate is found before the scan limit:
       - Set offset = scanLimit (effectively ending the read)
```

This means `ReadAll` does **not** stop at the first corrupted record. It
recovers past gaps and continues yielding valid records found later in the
file.

### 6.4 Corruption Recovery Diagram

```
  Before recovery scan:

  +--------+-----------------+--------+---------+
  | Rec A  | corrupted / gap | Rec C  | Rec D   |
  | (valid)|  (zeros or bad) | (valid)| (valid) |
  +--------+-----------------+--------+---------+
  ^        ^                 ^
  |        |                 scan finds SFJR magic here
  |        offset (corruption detected here)
  yielded

  After skip marker is written:

  +--------+-----------------+--------+---------+
  | Rec A  | SFJS skip marker| Rec C  | Rec D   |
  | (valid)|  (covers gap)   | (valid)| (valid) |
  +--------+-----------------+--------+---------+

  Result: yields Rec A, Rec C, Rec D  (Rec B lost)
```

### 6.5 Skip Marker Write (Atomic CAS)

Skip markers are written atomically to avoid overwriting a concurrent writer's
in-progress record. **A skip marker write is only attempted when the observed
magic at the corrupted offset is not a recognized value** (not `RecordHeaderMagic`
and not `SkipHeaderMagic`). This prevents a race with in-flight writers
(see section 6.6).

```
  0. Pre-check: extract the magic (lower 4 bytes) from the captured
     originalSlotValue. If it equals RecordHeaderMagic or SkipHeaderMagic,
     do NOT attempt the CAS — skip this step entirely.

  1. Capture the original 8-byte value at [offset..offset+8)
     (Magic + PayloadLength as a single long) when first reading the header.

  2. Create a skip header:
       Magic = SFJS
       PayloadLength = (nextOffset - offset) - 16

  3. Memory-map the 16 bytes at offset as a RecordHeader.

  4. CAS on the 8-byte Magic+PayloadLength field:
       Interlocked.CompareExchange(
           ref MagicAndPayloadLength(view),
           skipMagicAndLength,
           originalSlotValue
       )

  5. If CAS succeeds: skip marker is installed.
     If CAS fails: another writer completed its record at this
     offset, so the gap no longer exists. The skip marker is
     silently not written.
```

This is safe because:
- The pre-check ensures we never attempt to overwrite an offset where a
  writer may be in-flight (see section 6.6).
- If the slot still contains the original corrupted/zero value, the CAS
  succeeds and the skip marker is written.
- If a concurrent writer has since written a valid record header at this
  offset (after we captured originalSlotValue), the CAS fails (the
  comparand no longer matches) and the skip marker is not written,
  preserving the valid record.

### 6.6 In-Flight Writer Safety

A `RandomAccess.Write` call is **not atomic** — the OS may make individual
bytes visible to concurrent readers in any order. When a writer has reserved
space and begun writing, a reader may observe a partially-written record:

```
  Writer (in progress)              Reader (concurrent)
  ────────────────────              ───────────────────
  ReserveSpace(alignedLen)
  RandomAccess.Write(header+payload)
    ┊ first 8 bytes land on disk    Read header → Magic=SFJR, PayloadLength=N
    ┊ checksum/payload not yet      originalSlotValue = SFJR|N
    ┊ visible (still zeros)         Read payload → zeros (not yet written)
    ┊                               Checksum mismatch!
    ┊                               SkipCorruptedRegion(originalSlotValue)
    ┊                                 ↓
    ┊                               CAS(SFJR|N → SFJS|skip)  ← DANGEROUS
    ┊                               CAS succeeds (first 8 bytes match)
    ┊                               Skip marker overwrites record header
  Remaining bytes land ──────────>  Writer payload is orphaned
```

The record is **permanently lost**: the header now says "skip" while the
payload data follows it.

**Mitigation:** The corruption recovery code (section 6.3) only attempts
skip marker writes when the observed magic is **not** `RecordHeaderMagic`
or `SkipHeaderMagic`. When the magic is a recognized value but the checksum
does not match, the reader scans forward to the next record without writing,
avoiding interference with in-flight writers.

For unrecognized magic (zeros or garbage), no writer can be in-flight —
a writer always writes `RecordHeaderMagic` as part of its header, so the
CAS comparand (the unrecognized value) would fail if a writer began writing
after the value was captured.

**Trade-off:** If a writer crashes after writing the header but before
completing the payload, the partially-written record will not receive a
skip marker. Subsequent reads must re-scan past it each time. Compaction
(section 7) eliminates these regions.

---

## 7. Compaction Algorithm

Compaction creates a clean copy of the journal with gaps and corruption
removed.

### 7.1 Procedure

```
  1. Acquire an exclusive lock on the sidecar lock file <path>.lock
     (FileShare.None). This blocks concurrent SharedJournal instances
     that hold the lock in shared mode (see section 8.6).

  2. Delete any stale <path>.compact file from a previous interrupted run.

  3. Open the source journal with FileShare.None (exclusive access).
     The inner journal skips lock file acquisition (AcquireLockFile = false)
     since the caller already holds it.

  4. Create a new temporary journal at <path>.compact (also with
     AcquireLockFile = false to avoid creating a spurious .compact.lock file).

  5. Iterate source.ReadAll():
       - For each valid record, Append its payload to the temp journal.
       - Corrupted regions and skip markers are not copied.

  6. Flush the temporary journal to disk.

  7. Close both journals.

  8. File.Move(<path>.compact, <path>, overwrite: true)
       - Atomic on most filesystems.

  9. Release the exclusive lock on <path>.lock (implicit via Dispose).
```

### 7.2 Side Effects

The read pass (step 5) may write skip markers to the **source** file when
corruption is encountered (see section 6.5). The record data in the source
is not otherwise modified.

### 7.3 Result

After compaction, the journal contains only valid records packed contiguously
with no gaps, and `NextWriteOffset` points to the end of the last record.

### 7.4 Lock File Protocol

A sidecar lock file (`<path>.lock`) coordinates Compact with concurrent
journal instances:

- **Normal instances** acquire the lock file in **shared** mode
  (`FileShare.ReadWrite`) during construction. Multiple instances can
  coexist.
- **Compact** acquires the lock file in **exclusive** mode
  (`FileShare.None`) before opening the source journal (step 2). This
  blocks until all shared holders have released, and prevents new
  instances from opening until the entire operation — including the
  file replacement (step 8) — is complete.

This eliminates the race window that previously existed between closing the
source journal (step 7) and the file replacement (step 8). On Unix,
without the lock file, `rename()` succeeds regardless of open handles, and
a late opener's file descriptor would silently refer to the old (unlinked)
inode, causing its writes to be lost.

The lock file is persistent and is never deleted.

### 7.5 Crash Safety

If the process crashes during compaction:
- Before step 7: The original file is untouched (except for skip markers).
  A stale `.compact` file may be left behind and will be cleaned up on the
  next compaction attempt.
- During step 7: Depends on filesystem atomicity of rename. On most
  platforms, `File.Move` with `overwrite: true` is atomic.

---

## 8. Concurrency Model

### 8.1 Multi-Writer Concurrency

Multiple writers (threads or processes) can append concurrently without any
locks:

```
  Writer 1                   Writer 2                   NextWriteOffset
  ────────                   ────────                   ───────────────
                                                        4096
  Interlocked.Add(+32)                                  4128
  gets offset=4096           Interlocked.Add(+48)       4176
                             gets offset=4128
  Write record at 4096       Write record at 4128
  (in any order)             (in any order)
```

Each writer atomically reserves a non-overlapping byte range. The writes
themselves can complete in any order because they target disjoint file
regions. No coordination is needed between the write phases.

### 8.2 Reader-Writer Concurrency

A reader snapshots `NextWriteOffset` at the start and only reads up to that
point. Records appended after the snapshot are not visible to that reader.

Records whose space was reserved before the snapshot but whose write has not
completed will appear as zero-filled gaps. The reader's corruption recovery
handles these gracefully (scanning forward to the next valid record).

### 8.3 Multi-Process Sharing

The 4096-byte metadata header is memory-mapped from the file. All processes
that open the same file share the same physical pages. `Interlocked`
operations on the mapped pointer use hardware atomics (e.g., `lock xadd` on
x86-64) which are coherent across all processes sharing the mapping.

The `NextWriteOffset` field is placed at byte offset 64 to guarantee:
- **8-byte alignment** required by `Interlocked.Add` on `long`.
- **Cache-line alignment** (64 bytes on most architectures) to avoid false
  sharing with the `Magic`/`Version` fields.

### 8.4 Initialization Race

Multiple processes may try to initialize the same file simultaneously. The
`Interlocked.CompareExchange` on `Magic` acts as a one-shot initialization
lock:

```
  Process A (winner)                Process B (loser)
  ──────────────────                ─────────────────
  CAS(Magic, 0->SFJMETA) = 0      CAS(Magic, 0->SFJMETA) = SFJMETA
  (won: Magic was 0)               (lost: Magic already set)
  Write NextWriteOffset = 4096     Spin on Version == 0...
  Write Version = 1 ─────────────> Version becomes visible
                                   Validate Magic, Version,
                                   NextWriteOffset
                                   Proceed
```

### 8.5 Ordering Guarantees

The initialization protocol relies on the following ordering:

1. **Winner** writes `NextWriteOffset` before `Version` (both via
   `Volatile.Write`, which provides release semantics).
2. **Follower** spins on `Version` via `Volatile.Read` (acquire semantics).
   Once `Version != 0`, the follower is guaranteed to see the
   `NextWriteOffset` value written by the winner.

This establishes a happens-before relationship:
`Write(NextWriteOffset) -> Write(Version) -> Read(Version) -> Read(NextWriteOffset)`

### 8.6 Lock File Protocol

A companion lock file (`<path>.lock`) provides cooperative exclusion
between normal journal instances and the `Compact` operation.

- **Normal instances** open the lock file with `FileShare.ReadWrite`
  (shared mode) during construction. Multiple instances can hold the
  lock simultaneously and coexist without interference.
- **Compact** opens the lock file with `FileShare.None` (exclusive mode)
  before opening the source journal. This blocks until all shared holders
  have released their locks, and prevents new instances from opening.
- The exclusive lock is held through the entire compaction operation,
  including the `File.Move` that replaces the journal file. This
  eliminates the race window described in section 7.4.
- Inner journals opened by Compact (source and temp) skip lock file
  acquisition (`AcquireLockFile = false`) since the caller already holds
  the exclusive lock.
- The lock file is persistent and is never deleted.

---

## 9. Configuration

| Parameter          | Default    | Range                                        | Description                              |
|--------------------|------------|----------------------------------------------|------------------------------------------|
| `FlushMode`        | `None`     | `None`, `WriteThrough`                       | Write durability mode                    |
| `MaxPayloadLength` | 128 MB     | `[1, INT_MAX - RecordHeaderSize - Alignment]` | Max payload; enforced on write and read  |
| `FileShare`        | `ReadWrite`| (internal)                                   | File sharing mode for the OS handle      |

`MaxPayloadLength` is enforced on both paths:
- **Write**: `Append` throws `ArgumentOutOfRangeException` if `payload.Length > MaxPayloadLength`.
- **Read**: `ReadRecords` treats any record with `PayloadLength > MaxPayloadLength`
  as corrupted and invokes the corruption recovery scan.

---

## 10. Constants Summary

| Constant            | Value                  | Description                          |
|---------------------|------------------------|--------------------------------------|
| `MetadataMagic`     | `0x004154454D4A4653`   | "SFJMETA\0" (little-endian)         |
| `MetadataVersion`   | `1`                    | Current format version               |
| `MetadataFileSize`  | `4096`                 | Metadata region size                 |
| `DataStartOffset`   | `4096`                 | First record offset                  |
| `RecordHeaderMagic` | `0x524A4653`           | "SFJR" (little-endian)              |
| `SkipHeaderMagic`   | `0x534A4653`           | "SFJS" (little-endian)              |
| `RecordAlignment`   | `16`                   | All records aligned to 16 bytes      |
| `RecordHeaderSize`  | `16`                   | sizeof(RecordHeader)                 |
| `MinRecordSize`     | `16`                   | Smallest possible record (empty)     |
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
| 8         | 4    | Version        | Format version (currently 2)       |
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
 | 0x004154454D4A4653|     2     |         (all zeros)               |  Offset   |
 +-------------------+-----------+-----------------------------------+-----------+
 |<--- 8 bytes ----->|<- 4 B --->|<------------ 52 bytes ----------->|<- 8 B --->|
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
| 8         | 8    | Checksum       | xxHash3 64-bit hash of the payload |
+-----------+------+----------------+------------------------------------+
```

```
 0           4               8                              16
 +-----------+---------------+------------------------------+
 |   Magic   | PayloadLength |          Checksum            |
 |  "SFJR"   |   (4 bytes)   |         (8 bytes)            |
 +-----------+---------------+------------------------------+
 |<- 4 B --->|<---- 4 B ---->|<---------- 8 B ------------->|
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

xxHash3 64-bit over the payload bytes, using the default seed (0).
The implementation uses `System.IO.Hashing.XxHash3.HashToUInt64()`,
which is SIMD-accelerated (AVX2/SSE2/NEON) for large payloads.

The checksum covers only the payload, not the header.

### 3.6 Example: Journal with Three Records

```
 Offset   Content
+--------+------------------------------------------------------------+
| 0      | Metadata Header (4096 bytes)                               |
|        |   Magic=0x004154454D4A4653  Version=2  NextWriteOffset=4144|
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
  CAS(NextWriteOffset, 0 -> 4096)   NextWriteOffset = 4096
  validate NextWriteOffset >= 4096
          and aligned to 16
  Volatile.Write(Version, 2) ─────>  Version = 2
```

The initializer uses CAS for `NextWriteOffset` (rather than a plain write)
so that a concurrent completer cannot overwrite a value that was already
advanced by appends. After the CAS, `NextWriteOffset` is validated — if
the CAS failed because the field already held a corrupt non-zero value,
an `InvalidOperationException` is thrown before `Version` is published,
preventing the file from becoming "initialized" with a corrupt tail pointer.
`NextWriteOffset` is validated **before** `Version` is written so that
`Version != 0` always implies a valid `NextWriteOffset`.

### 4.2 Existing File Path (Follower Joins)

When the CAS on `Magic` fails (another process already set it), the follower
reads `Version`:

- **`Version != 0`**: The file is fully initialized. The follower validates
  `Magic`, `Version`, and `NextWriteOffset`.
- **`Version == 0`**: The initializer is still running or crashed before
  writing `Version`. The follower immediately completes initialization using
  the same lock-free path described in §4.3.

No spinning or timeout is needed — all initialization writes are idempotent
or CAS-guarded, so concurrent completion is safe.

```
  Process B (Version != 0)           File (memory-mapped)
  ─────────                          ────────────────────
  CAS(Magic, 0 -> SFJMETA)          returns SFJMETA (lost race)
  read Version ◄──────────────────── Version = 2
  validate Magic == SFJMETA
  validate Version == 2
  validate NextWriteOffset >= 4096
          and aligned to 16
```

### 4.3 Lock-Free Completion Path

If `Version == 0` after the CAS on `Magic` fails, the follower completes
initialization itself. This handles both the case where the initializer is
still running (concurrent completion) and the case where it crashed:

```
  Process B (Version == 0)           File (memory-mapped)
  ─────────                          ────────────────────
  validate Magic == SFJMETA
  CAS(NextWriteOffset, 0 -> 4096)   NextWriteOffset = 4096 (if was 0)
  validate NextWriteOffset >= 4096
          and aligned to 16
  Volatile.Write(Version, 2) ─────>  Version = 2
```

The CAS on `NextWriteOffset` only succeeds if it is still 0 (the initializer
may have written it before crashing, or another process may have already
completed initialization and appended records). After the CAS,
`NextWriteOffset` is validated — if it holds a corrupted non-zero value that
is invalid (below `DataStartOffset` or misaligned), an
`InvalidOperationException` is thrown.

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
          skip corrupted region (section 6.3)
          continue
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

### 6.7 False Magic in Payloads

Record payloads are arbitrary byte sequences and may contain the 4-byte
pattern `0x524A4653` (`RecordHeaderMagic`) or `0x534A4653` (`SkipHeaderMagic`).
During **normal sequential reading** this is harmless — the reader advances
by the validated record size and never interprets payload bytes as headers.

During **corruption recovery** (section 6.3), however, `ScanForNextMagic`
examines every 16-byte-aligned position for magic patterns. Because payloads
start at `record_start + 16` (also 16-byte aligned), a magic pattern at a
16-byte-aligned offset within a payload will produce a **false positive**.

When the reader resumes at a false-positive offset it calls `ValidateRecord`,
which reinterprets the payload bytes as a record header:

```
  Corrupted region             Payload of a valid record
  ┌────────────────┐ ┌─────────┬──────────────────────────────┐
  │ gap / garbage  │ │ header  │ ... SFJR xx xx xx xx ...     │
  └────────────────┘ └─────────┴──────┬───────────────────────┘
                                      ▲
                          false magic hit (16-byte aligned)
```

**Outcomes at the false-positive offset:**

| Interpreted PayloadLength            | Status returned | Effect                            |
|--------------------------------------|-----------------|-----------------------------------|
| `> MaxPayloadLength`                 | `Incomplete`    | Re-scans forward — **safe**       |
| Valid, checksum mismatch (≈ 1/2⁶⁴)   | `Incomplete`    | Re-scans forward — **safe**       |
| Valid, `offset + totalLength > tail` | `Incomplete`    | Re-scans forward — **safe**       |

All false-positive outcomes result in `Incomplete`, which triggers a forward
scan to the next candidate. The reader never stops prematurely due to a
false magic match. In normal operation the extends-past-tail check is
unreachable — a legitimately reserved record always fits within the snapshot
`tail` because `Interlocked.Add` updates `NextWriteOffset` atomically.

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

  5. Iterate source records using the same validation rules as ReadAll():
       - For each valid record, Append its payload to the temp journal.
       - Corrupted regions and skip markers are not copied.
       - Skip markers are not written back to the source journal.

  6. Flush the temporary journal to disk.

  7. Close both journals.

  8. File.Move(<path>.compact, <path>, overwrite: true)
       - Atomic on most filesystems.

  9. Release the exclusive lock on <path>.lock (implicit via Dispose).
```

### 7.2 Side Effects

The read pass (step 5) does not modify the **source** file. Corrupted
regions are skipped in-memory and only valid records are copied into the
temporary journal.

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
  (`FileShare.None`) before opening the source journal (step 2). If any
  shared holder is still open, this acquisition fails immediately with
  `IOException`. When acquired, it prevents new instances from opening
  until the entire operation — including the file replacement (step 8)
  — is complete.

This eliminates the race window that previously existed between closing the
source journal (step 7) and the file replacement (step 8). On Unix,
without the lock file, `rename()` succeeds regardless of open handles, and
a late opener's file descriptor would silently refer to the old (unlinked)
inode, causing its writes to be lost.

The lock file is persistent and is never deleted.

### 7.5 Crash Safety

If the process crashes during compaction:
- Before step 7: The original file is untouched. A stale `.compact` file may
  be left behind and will be cleaned up on the next compaction attempt.
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
gate:

```
  Process A (winner)                Process B (loser)
  ──────────────────                ─────────────────
  CAS(Magic, 0->SFJMETA) = 0      CAS(Magic, 0->SFJMETA) = SFJMETA
  (won: Magic was 0)               (lost: Magic already set)
  CAS(NextWriteOffset, 0->4096)    read Version == 0
  validate NextWriteOffset         CAS(NextWriteOffset, 0->4096)
  Write Version = 2                validate NextWriteOffset
                                   Write Version = 2
```

No spinning or timeout is required. The loser immediately attempts lock-free
completion (§4.3) — all initialization writes are idempotent or CAS-guarded,
so concurrent completion by both processes is safe.

### 8.5 Ordering Guarantees

The initialization protocol relies on the following ordering:

1. **Winner** uses `Interlocked.CompareExchange` for `NextWriteOffset`
   (CAS from 0), then validates the result, then writes `Version` via
   `Volatile.Write` (release semantics).
2. **Follower** reads `Version` via `Volatile.Read` (acquire semantics).
   If `Version != 0`, the follower is guaranteed to see a valid
   `NextWriteOffset`. If `Version == 0`, the follower completes
   initialization itself using the same CAS-guarded protocol.

This establishes a happens-before relationship for the normal path:
`CAS(NextWriteOffset) -> Write(Version) -> Read(Version) -> Read(NextWriteOffset)`

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

| Parameter          | Default      | Range                                         | Description                                |
|--------------------|--------------|-----------------------------------------------|--------------------------------------------|
| `FlushMode`        | `None`       | `None`, `WriteThrough`                        | Write durability mode                      |
| `MaxPayloadLength` | 128 MB       | `[1, INT_MAX - RecordHeaderSize - Alignment]` | Max payload; enforced on write and read    |
| `ReadAheadSize`    | 64 KB        | `[1, ∞)`                                      | Read-ahead buffer size for sequential reads|
| `FileShare`        | `ReadWrite`  | (internal)                                    | File sharing mode for the OS handle        |

`MaxPayloadLength` is enforced on both paths:
- **Write**: `Append` throws `ArgumentOutOfRangeException` if `payload.Length > MaxPayloadLength`.
- **Read**: `ReadRecords` treats any record with `PayloadLength > MaxPayloadLength`
  as corrupted and invokes the corruption recovery scan.

---

## 10. Constants Summary

| Constant            | Value                | Description                     |
|---------------------|----------------------|---------------------------------|
| `MetadataMagic`     | `0x004154454D4A4653` | "SFJMETA\0" (little-endian)     |
| `MetadataVersion`   | `2`                  | Current format version          |
| `MetadataFileSize`  | `4096`               | Metadata region size            |
| `DataStartOffset`   | `4096`               | First record offset             |
| `RecordHeaderMagic` | `0x524A4653`         | "SFJR" (little-endian)          |
| `SkipHeaderMagic`   | `0x534A4653`         | "SFJS" (little-endian)          |
| `RecordAlignment`   | `16`                 | All records aligned to 16 bytes |
| `RecordHeaderSize`  | `16`                 | sizeof(RecordHeader)            |
| `MinRecordSize`     | `16`                 | Smallest possible record (empty)|

---

## 11. Asynchronous API

All public operations have asynchronous counterparts that return `ValueTask`
or `ValueTask<T>` and accept a `CancellationToken`.

### 11.1 API Mapping

| Synchronous                             | Asynchronous                                                      |
|-----------------------------------------|-------------------------------------------------------------------|
| `Append(ReadOnlySpan<byte>)`            | `AppendAsync(ReadOnlySpan<byte>, FlushMode, CancellationToken)`   |
| `ReadAll()` → `IEnumerable`             | `ReadAllAsync(CancellationToken)` → `IAsyncEnumerable`            |
| `Flush()`                               | `FlushAsync(CancellationToken)`                                   |
| `Compact(string, ...)`                  | `CompactAsync(string, ..., CancellationToken)`                    |

**`AppendAsync` accepts `ReadOnlySpan<byte>`** because the span is consumed
synchronously into a pooled buffer before any asynchronous work begins.
The public method is a non-async wrapper that prepares the record, then
delegates to an async core method for the I/O.

### 11.2 Cancellation Semantics

Cancellation is designed to keep journal state consistent at all times:

- **`AppendAsync`**: The token is checked before the operation begins. After
  `Interlocked.Add` reserves space, cancellation cannot roll back the
  reservation. If cancellation occurs during `RandomAccess.WriteAsync`, the
  reserved region becomes a gap (zeros) — identical to the crash window
  described in §5.3. The read algorithm handles this via corruption recovery
  and skip markers. The journal remains consistent.

- **`ReadAllAsync`**: The token is checked before each record iteration.
  Cancelling mid-read does not change the journal's logical contents. Reads
  are non-destructive, and although recovery may atomically write skip markers
  that physically modify the file, those markers preserve the same logical
  representation by encoding gaps the reader would already skip. Each skip
  marker CAS either succeeds completely or not at all.

- **`FlushAsync`**: The token is checked before the operation. The underlying
  `FlushToDisk` call is synchronous (no async overload exists in .NET).

- **`CompactAsync`**: Cancellation is checked before temporary state is
  created and between records during the copy loop. Cancelling leaves the
  original file intact, and the temporary `.compact` file is deleted before
  the method exits.

### 11.3 Implementation Notes

- Both `Append` and `AppendAsync` share a common `PrepareAppend` method that
  validates, reserves space, allocates a pooled buffer, and serializes the
  record. The sync path then writes synchronously; the async path delegates
  to an async core method. The `stackalloc` optimization for small payloads
  is not used — both paths always rent from `ArrayPool`.
- `ReadAll()` and `ReadAllAsync()` return repeatable sequences: each
  sequence snapshots the tail on its first enumeration, then each pass gets
  a fresh internal enumerator and read buffer over that stable snapshot.
- The internal `FileReadBuffer` provides both `Read` (sync) and `ReadAsync`
  methods, sharing cache-hit detection and buffer management logic.

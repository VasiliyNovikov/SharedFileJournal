# AGENTS.md

This file provides guidance to AI coding agents when working with code in this repository.

## Project Overview

SharedFileJournal is a high-performance Concurrent inter-process file-based journal

## Build and Test Commands

```bash
# Build entire solution
dotnet build

# Run tests
dotnet test

# Run a single test
dotnet test--filter 'FullyQualifiedName~ClassName.MethodName'
```
## Build Configuration

- **Target**: net10.0, C# 14
- **Strict mode**: `TreatWarningsAsErrors`, `AnalysisMode=Recommended`, `EnforceCodeStyleInBuild`, `Nullable=enable`
- Central package versioning via `Directory.Packages.props`

## Architecture

SharedFileJournal uses a single file per journal:
- The first 4096 bytes are a memory-mapped metadata header with atomic `NextWriteOffset` (at offset 64)
- Records follow immediately after, starting at offset 4096

### Core types
- `SharedJournal` — Main entry point (Open/Append/ReadAll/Recover/Dispose)
- `JournalFormat` (internal) — Record serialization, header/trailer layout, FNV-1a checksum
- `SharedJournalOptions` / `FlushMode` — Configuration

### Concurrency model
Writers atomically reserve byte ranges via `Interlocked.Add` on the MMF-backed `NextWriteOffset`, then write records at the reserved offset using `RandomAccess.Write`. No file locks are held during writes.

## Code Style

Configured in `.editorconfig`:
- 4-space indent for C#, 2-space for XML/csproj
- LF line endings, no final newline
- File-scoped namespaces
- `var` usage allowed (IDE0008 suppressed)
- Expression-bodied members allowed (IDE0021/0022/0023 suppressed)
- Test files allow underscores in names (CA1707 suppressed)

## CI Pipeline

GitHub Actions workflow (`.github/workflows/pipeline.yml`):
- **Validate**: Builds and tests on all the available platforms, runs on push/PR to `master`
- **Publish**: Packs and pushes to NuGet when `PUBLISH` repo variable is `true`, or when `PUBLISH` is `auto` and on `master` branch

## Dependencies

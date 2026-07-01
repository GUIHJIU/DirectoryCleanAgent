# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

智能磁盘清理工具 (Smart Disk Cleanup Tool) — a C# .NET 8 Windows desktop application that leverages Everything SDK for high-performance disk scanning and cleanup recommendations. Target users: developers and advanced users, with a simplified mode for general users.

Design baseline: **V3.7** (see `docs/智能磁盘清理工具-总体设计文档-V3.7.md`). All coding should reference that document.

## Tech Stack

- **Language**: C# + .NET 8 (64-bit)
- **UI**: WinUI 3 or WPF (decision TBD)
- **File indexing**: Everything SDK ≥ 1.4.1.1000 (IPC, mandatory — no fallback to traditional traversal)
- **Database**: SQLite (WAL mode, synchronous=NORMAL, batch-write via ConcurrentQueue, 500ms/200-item flush)
- **Path handling**: All internal paths use `\\?\` prefix
- **Recycle Bin**: `IFileOperation` + `SHQueryRecycleBin` (called once before any deletion path)
- **AI**: `HttpClient` with circuit breaker; max 500 files/batch, 30 RPM, concurrency 5
- **Hash**: SHA-256 for deletion records and rollback verification

## Build & Run Commands

```bash
# Restore dependencies
dotnet restore

# Build
dotnet build

# Build in Release mode
dotnet build -c Release

# Run the application
dotnet run --project src/<MainProject>

# Run tests
dotnet test

# Run a single test
dotnet test --filter "FullyQualifiedName=Namespace.Class.Method"

# Create MSI installer
# (tooling TBD in phase D5)
```

## Architecture (4-layer)

```
UI Layer → Control/Coordination Layer → Core Capability Layer → Data/Persistence Layer
```

### Core Interfaces (defined in phase A1.5)

- `IFileListProvider` — Everything streaming adapter, `yield return` enumeration, sort pushdown to Everything
- `IRuleEngine` — hard rules (read-only) + heuristic rules (configurable), outputs `rule_verdict` + `semantic_category`
- `IDecisionEngine` — arbitrates `final_action` from rule verdict + AI label + user policy; generates frozen `ReadOnlyCollection` snapshot before deletion
- `IOperationExecutor` — optimistic delete via `IFileOperation`, recycle bin capacity pre-check, cross-volume `.cleaning.tmp` mechanism, `CancellationToken` binding
- `IBackupManager` — rollback with SHA-256 verification, batch tombstone clear by `operation_id`

### Key DTOs

- `FileItem` — file metadata from Everything
- `DeleteSnapshotEntry` — `{ file_path, file_size, sha256_hash, final_action, operation_id }`, immutable snapshot for deletion
- `FileDecisionCache` — stores only rule-hit files, keyed by `file_path`, versioned with `cache_version`
- `LocalTombstone` — physical-ID-based tombstone (`VolumeGuid + FileReferenceNumber`), falls back to Size+LastWriteTime fingerprint (3-day expiry) when FRN unavailable

### Critical Design Decisions

1. **Everything is mandatory** — if Everything is not running, version < 1.4.1.1000, or IPC fails, the tool blocks entry to the main UI. No conventional traversal fallback.
2. **Streaming only** — memory must stay <200MB for 2M files. All enumeration uses `yield return`. Sorting/grouping is pushed down to Everything native queries.
3. **Batch SQLite writes** — all writes go through `ConcurrentQueue`, flushed every 500ms or 200 items. No direct writes.
4. **Optimistic delete** — attempt delete first; if file-locked (`0x80070020`), downgrade to `manual_review`. No pre-check for file locks.
5. **Decision snapshot freeze** — before deletion, deep-clone all target files into `ReadOnlyCollection<DeleteSnapshotEntry>`; subsequent rule changes or AI responses don't affect in-flight operations.
6. **Rule hot-reload** — `FileSystemWatcher` on rules directory, 500ms debounce, increments `RuleCacheVersion`, triggers async cache invalidation and rescan.
7. **Capacity pre-check ordering** — `SHQueryRecycleBin` runs BEFORE snapshot/hash generation (fail-fast, avoid wasted CPU).
8. **Non-admin read-only mode** — all analysis works; delete/move/clean buttons are disabled with admin prompt.

### Development Phases (from WBS)

| Phase | Content | Key Dependencies |
|-------|---------|-----------------|
| A | Infrastructure: project skeleton, Everything SDK, SQLite, config, path normalization | None |
| B | Core modules: streaming adapter, rule engine, decision engine, operation executor, rollback, quarantine, AI advisor | A complete |
| C | UI: dashboard, file views, wizard, settings, dry-run, audit log viewer, quarantine UI, tray | A1.5 for C1; B phases for others |
| D | Testing & deployment: unit tests (xUnit, >80% coverage), integration tests, perf tests, MSI packaging | All B & C |

### Critical Path

A1 → A1.5 → A2 → A3 → A5 → B1 → B2 → B3 → B4 → B5 → D2 → D3 → D4 → D5 (~34 working days)

C1 can start after A1.5 in parallel with B1, saving ~3 calendar days.

## File Organization (proposed)

```
src/
├── DirectoryCleanAgent/          # Main WPF/WinUI app
├── DirectoryCleanAgent.Core/     # Domain models, interfaces, DTOs
├── DirectoryCleanAgent.Everything/ # Everything SDK adapter
├── DirectoryCleanAgent.Rules/    # Rule engine
├── DirectoryCleanAgent.Decision/ # Decision engine
├── DirectoryCleanAgent.Operations/ # Operation executor, rollback, quarantine
├── DirectoryCleanAgent.AI/       # AI advisor client
├── DirectoryCleanAgent.Data/     # SQLite repositories, batch queue
└── DirectoryCleanAgent.Tests/    # xUnit tests
```

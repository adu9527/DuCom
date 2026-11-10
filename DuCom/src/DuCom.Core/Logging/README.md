# Logging

## Responsibility

Own immutable formatted-log records, STR/HEX text formatting boundaries, per-session UTF-8 text writers, rotation, flush, drain, and explicit fault state.

## Dependencies

May depend on Abstractions, Parsing pure functions, and Diagnostics contracts. Must not depend on Storage or WPF.

## Thread Model

Log records have independent ownership after port processing. Writers batch asynchronously and drain accepted records during normal shutdown.

## Invariants

- Logging is independent from display retention and rendering.
- Disk, permission, space, write, flush, and rotation failures are never silent.
- Default rotation is 40 MiB and is configurable. The default leaf-folder cleanup target is a **soft** 30 MiB: active files and the newest completed segment are retained even when a 40/100 MiB segment exceeds that target. Rotation must not discard the segment it just completed.
- Serial cleanup manages only files with DuCom ownership markers; old unmarked files and unrelated user files are not automatically adopted. The desktop application disables quota cleanup for user-selected log roots. Disabling rotation also leaves the active file outside a strict size ceiling.
- System cleanup recognizes numbered `.log.1`/`.log.2`/`.log.3` backups and checks at rotation as well as startup; open files remain protected.

## Test Strategy

Use fake filesystems for exact output, rotation boundaries, close drain, and fault propagation. Load tests compare accepted and formatted/written accounting.

## M1 Contracts

- `SessionLogWriter` uses a bounded asynchronous Channel with explicit Wait backpressure.
- UTF-8 records are written in order and rotated before a record that would exceed the configured segment threshold.
- File names contain timestamp, segment, and collision suffixes.
- The desktop application uses `{logRoot}/{yyyy-MM-dd}/serial` for serial logs, sibling `system` for diagnostics, and `plugins/{pluginId}` for default plugin artifacts. Quotas apply to leaf folders, not all historical date folders combined.
- Normal session close drains formatter output, the log Channel, file flush, and dispose before reporting completed shutdown drain.
- The writer uses `AutoFlush=false` and combines ordered records into asynchronous batches (64 KiB or a 25 ms maximum collection delay). Physical flush is periodic (500 ms by default, configurable for deterministic tests), not per batch; rotation, normal close, and fault cleanup force a flush/dispose while preserving per-record metrics. This bounds the normal crash window without turning every batch into a disk barrier.

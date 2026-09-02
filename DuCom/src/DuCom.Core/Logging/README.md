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
- Default rotation is 40 MB and is configurable.

## Test Strategy

Use fake filesystems for exact output, rotation boundaries, close drain, and fault propagation. Load tests compare accepted and formatted/written accounting.

## M1 Contracts

- `SessionLogWriter` uses a bounded asynchronous Channel with explicit Wait backpressure.
- UTF-8 records are written in order and rotated before a record that would exceed the configured segment threshold.
- File names contain timestamp, segment, and collision suffixes.
- The desktop application may opt into a per-day subdirectory under its configured log root; the folder format is `yyyy-M'M'-d'D'` (for example `2026-8M-31D`).
- Normal session close drains formatter output, the log Channel, file flush, and dispose before reporting completed shutdown drain.
- The writer uses `AutoFlush=false` and combines ordered records into asynchronous batches (64 KiB or a 25 ms maximum collection delay). Physical flush is periodic (500 ms by default, configurable for deterministic tests), not per batch; rotation, normal close, and fault cleanup force a flush/dispose while preserving per-record metrics. This bounds the normal crash window without turning every batch into a disk barrier.

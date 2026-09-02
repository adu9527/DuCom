# Storage

## Responsibility

Own budgeted line text segments, logical line identity, physical segmentation, style-run references, eviction, clear semantics, and immutable snapshots.

The M1 implementation intentionally uses a simple, testable layout: each logical line owns its physical segment strings, and snapshots copy those value records into a read-only collection. Shared text buffers or segment pooling will only replace this layout when focused benchmarks demonstrate that the added ownership complexity is justified.

## Dependencies

May depend on Parsing value types and Diagnostics contracts. Must not depend on WPF brushes, visuals, or controls.

## Thread Model

Append, clear, and snapshot operations are lock-protected, so appenders and snapshot readers may run concurrently. Mutable internals are never exposed.

## Invariants

- Memory stays within the configured budget and documented tolerance.
- Long physical lines are segmented while logical identity is preserved.
- UTF-8 text bytes are counted per stored segment; eviction always removes the oldest complete logical line.
- Logical IDs increase for the lifetime of the store and are not reused after eviction or clear.
- Display eviction and clear do not affect logging.

## Test Strategy

Cover budget enforcement, stable IDs after eviction, cross-block long lines, concurrent snapshots, and clear behavior.

## M1 Contracts

- Soft-wrapped physical segments retain one logical ID.
- Cursor-based `SnapshotAfter` returns only bounded new ranges for frame-pull UI consumption.
- Eviction removes complete logical lines and IDs are never reused.
- The current string-backed layout is intentionally measurable and replaceable; a shared-buffer layout requires benchmark evidence before changing the public snapshot contract.
- Continuations append to a private growable segment list instead of copying the complete segment array on every receive block, avoiding quadratic work for long unterminated lines. Budget eviction still counts and removes that logical line exactly once.

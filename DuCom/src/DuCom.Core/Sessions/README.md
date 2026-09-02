# Sessions

## Responsibility

Own session orchestration and immutable session-visible state across transport, pipeline, logging, and display storage.

## Dependencies

May coordinate Ports, Pipeline, Logging, Storage, Sending, and Diagnostics through their public contracts. Must not depend on WPF.

## Thread Model

Session commands are asynchronous and cancellation-aware. State snapshots are immutable and safe for application-layer polling.

## Invariants

- One session owns one transport lifecycle and one formatted-log lifecycle.
- Display clear, freeze, eviction, or window closure never truncates accepted log work.
- Open starts logging and receive processing before opening the transport; any failure rolls both back.
- Close stops transport input before draining receive processing, flushing formatter state, and draining logs.
- TX records are committed only after the transport write succeeds.

## Test Strategy

Exercise startup, shutdown drain, faults, cancellation, and multi-session isolation with in-memory collaborators.

## M1 Contracts

- `SerialSession` owns one transport, lifecycle, receive pipeline, formatter, line store, and log writer.
- Open order is log writer, receive pipeline, then transport lifecycle; failures roll back.
- Close order (ADR-0004, 2026-08-28): quiesce and drain the receive pipeline **while the transport is still open** (SerialPort discards its driver buffer on Close), then close the transport, then flush the final logical line and drain logs, and report the full close duration. Once the receive side is quiesced, the close commits and is no longer cancellable. A drain-phase fault returns `Faulted` with an explicit session fault; the port is still closed. The fault/unplug path keeps close-then-drain-accepted order and never masquerades as a normal close.
- `DisposeAsync` runs the same ADR-0004 sequence as `CloseAsync` (quiesce/drain receive → close transport → formatter flush → log drain → file flush → disposal), is idempotent, and concurrent disposers share one disposal task. The transport is always disposed, even when an earlier step fails. The transport is closed **before** the log-side drain so a slow log flush never keeps the port open with nobody reading it.
- Open/Dispose publication race (2026-08-28 review round 2): disposal re-reads the session runtime under the operation lock, so a runtime published by an Open that raced the disposed flag is always drained; Open re-checks the flag right after publishing and rolls back. In every interleaving the runtime is drained exactly once (observable as shutdown drain `Completed`, never `NotStarted`). The detached fault-handling task is awaited after the lock is released but before the lock object is disposed, so it can neither deadlock nor hit a disposed semaphore.
- Transport disconnect cleanup (2026-08-29 review): an unrecoverable read or write disconnect schedules serialized cleanup of that runtime. A direct reopen also drains and detaches any previous runtime before publishing the replacement, so a write-triggered disconnect can never leave duplicate receive subscriptions or an orphaned log writer. A delayed cleanup task checks runtime identity before closing the lifecycle and therefore cannot close a newer connection.
- Runtime settings updates are atomic (2026-08-28 review round 2): the replacement formatter is built before anything changes; the transport applies first (with its own internal rollback); a failed or cancelled formatter swap (the old formatter stays in place on failure) rolls the transport back to the previous settings before rethrowing. If the rollback itself fails, one `AggregateException` surfaces both failures and recommends reopening the port — a half-applied configuration is never silently kept.
- Sustained-input close budget (2026-08-28 review): the stop drain of the transport buffer is bounded by a wall-clock budget (default 5 s) and a maximum appended-byte budget (default 32 MB, both configurable on `ReceivePipeline`). Exceeding either budget faults the pipeline with an explicit reason, the session reports `Faulted` with that fault, the shutdown drain state reports `Faulted`, and the transport is force-closed. User-visible behavior: closing during input faster than the drain can consume produces a visible session fault naming the budget and the bytes still buffered — never a silent success and never an infinite close hang.
- TX is recorded only after transport write succeeds. A post-write log failure is explicit and does not silently report success.
- Runtime serial parameter updates share the session operation lock. Encoding changes publish a new versioned receive-formatting profile only after transport apply succeeds; already captured/queued blocks retain the old profile. The receive sink changes incremental decoder state at the first block carrying the new version, flushing any incomplete old-encoding sequence deterministically before decoding new bytes. In-place updates require a transport implementing the COM-specific `ISerialSettingsTransport` capability; transport-neutral transports reject them (ADR-0004).
- UI consumers use status snapshots plus cursor-based incremental line ranges.

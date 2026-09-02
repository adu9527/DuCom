# ADR-0004: SerialPort Close Quiesce-Drain-Before-Close Order

- Status: Proposed (implemented 2026-08-28; updated by the 2026-08-28 framework-review fix rounds 1 and 2; framework review pending)
- Context: 开发任务.txt phase 2 — real SerialPort close integrity; 2026-08-28 长任务代码审查修复轮
- Supersedes: the close-order sentence in `src/DuCom.Core/Sessions/README.md` (updated in the same change)
- Related: AGENTS.md logging invariants

## Context

`System.IO.Ports.SerialPort` discards its receive buffer when `Close()` runs, and
`SerialPortTransport.BytesAvailable` is defined as `IsOpen ? BytesToRead : 0`. The original
session close orchestration closed the port before draining the driver buffer, silently
losing every still-buffered byte. Existing tests passed because their fake transports keep
`BytesAvailable` alive after "close" — an in-memory proof, not a driver-like proof. This
violates the project rule that target-load receive blocks must all reach the session log and
that no data loss may be silent.

## Decision

1. `SerialSession.CloseCoreAsync` and `SerialSession.DisposeAsync` reorder the normal close to
   quiesce and drain the receive side **while the transport is still open**:

   1. `ReceivePipeline.StopAsync()` — unsubscribe `DataAvailable` (block new external input),
      wait for in-flight receive callbacks, drain every already-arrived driver-buffer byte into
      the receive Channel with backpressure-respecting awaits, complete the Channel writer, and
      drain the per-port processor.
   2. `PortLifecycle.CloseAsync(CancellationToken.None)` — close the port **before** any
      log-side waiting, so a slow log flush can never hold the port open with nobody reading it.
   3. Formatter flush, log-Channel drain, file flush/dispose, runtime disposal
      (`DrainRuntimeAsync`; its internal `StopAsync` is idempotent and shared).
   4. Disposal additionally always disposes the lifecycle (and with it the transport), even
      when an earlier step failed.

   `CloseAsync` and `DisposeAsync` share this exact sequence. `DisposeAsync` is idempotent;
   concurrent disposers await one shared disposal task. Every step is attempted even when an
   earlier one failed (best-effort cleanup), and a lifecycle close failure is recorded as an
   explicit session fault.

2. Once the quiesce point is entered, the close **commits**: the caller's cancellation token is
   no longer honored for the transport-close phase. Rationale: after the receive side is torn
   down, aborting the close would leave the lifecycle reporting `Open` over a dead receive
   pipeline. Cancellation is still honored before the quiesce point (operation-lock wait).

3. **Sustained-input close budget** (2026-08-28 review round). Draining a transport that keeps
   producing bytes can never finish, so the drain inside `ReceivePipeline.StopAsync` is bounded
   by two budgets, both present:

   - a wall-clock budget (default 5 s), and
   - a maximum appended-byte budget (default 32 MB),

   both constructor-configurable on `ReceivePipeline`. Within the budgets the drain is
   best-effort complete. When a budget is exceeded the pipeline faults with an explicit
   `InvalidOperationException` naming the exceeded budget, the bytes already drained, and the
   bytes still buffered; `CloseAsync` returns `Faulted`; the session fault snapshot and the
   shutdown-drain state record the fault; the transport is force-closed. No fixed sleeps are
   used anywhere in the drain. User-visible behavior: closing a port whose device outputs
   faster than the drain consumes produces a visible fault explaining that the remaining
   buffered bytes could not be logged — never a silent success and never an infinite close.

4. **Concurrent Stop/Dispose of the pipeline** (2026-08-28 review round). `ReceivePipeline.StopAsync`
   runs its quiesce/drain sequence exactly once: concurrent callers — including `DisposeAsync` —
   share one stop task and all await it, so no caller returns before the sequence completes.
   `DisposeAsync` is idempotent, shares one disposal task, and releases its synchronization
   objects only after the shared stop task finished. Channel capacities, backpressure policy,
   and ArrayPool ownership are unchanged.

   Review round 2 tightened this to **one budget across every stop phase**: waiting for
   in-flight receive callbacks, each transport-buffer read (a blocking driver read is
   deadline-bounded on a pool thread; its rented buffer is returned when the stuck read
   finally completes after the forced close), each receive-capacity wait, and the final
   processor drain all share the single wall-clock budget. A phase that blows the budget
   faults the pipeline with a phase-specific reason (stuck callback / blocked read /
   blocked capacity / processor not completing, all naming the budget and buffered bytes).
   The synchronous entry of `StopAsync` never blocks: quiescing is a lock-free flag flip
   plus event unsubscribe, so closing from the UI thread cannot stall behind an in-flight
   callback.

4a. **Open/Dispose runtime publication race** (2026-08-28 review round 2). `DisposeAsync` re-reads
   the session runtime under the operation lock, so a runtime published by an `OpenAsync` that
   passed its disposed check while disposal waited for the lock is always drained;
   `OpenAsync` re-checks the disposed flag right after publishing and rolls the freshly
   published runtime back. In every interleaving the runtime is drained exactly once —
   observable as shutdown drain `Completed`, never `NotStarted`. The detached fault-handling
   task is awaited after the lock is released but before the lock object is disposed.

4b. **Atomic runtime settings updates** (2026-08-28 review round 2). `ApplySettingsAsync` builds
   the replacement formatter before anything changes, applies the transport first (which has
   its own internal rollback), and rolls the transport back to the previous settings when the
   formatter swap fails or is cancelled (the old formatter stays in place on swap failure —
   the swap happens only after a successful flush). If the rollback itself fails, one
   `AggregateException` surfaces both failures and recommends reopening the port; a
   half-applied configuration is never silently kept.

5. A drain-phase fault (for example a driver read failure during the final drain) does not
   masquerade as a successful close: `CloseAsync` returns `Faulted`, the session fault snapshot
   records the pipeline fault, the drain state reports `Faulted`, and the port is still closed.

6. The fault/unplug path (`OnRuntimeFault`) keeps its existing order — lifecycle close first,
   then drain of already-accepted blocks. A faulted transport is explicitly not a normal close;
   bytes stranded in the driver buffer at fault time are covered by the explicit fault state,
   not silently reported as success.

## Transport interface history and the settings-update contract

The original version of this ADR claimed "no public transport interface changes" while M2 work
had in fact added `ApplySettings(SerialPortSettings)` to `ISerialTransport`. The 2026-08-28
review round corrected the boundary as follows:

- `ISerialTransport` no longer declares `ApplySettings`. In-place serial settings updates are a
  COM-specific capability declared by the new `ISerialSettingsTransport` interface, implemented
  only by `SerialPortTransport`. Future non-COM transports (Telnet, virtual ports) are not
  forced into Windows SerialPort semantics: `SerialSession.ApplySettingsAsync` throws
  `NotSupportedException` for transports that do not implement the capability.
- Contract of `ISerialSettingsTransport.ApplySettings`:
  - Valid in both the `Closed` and `Open` lifecycle states; applied immediately to the open
    handle (no reopen).
  - Atomicity: the input is validated first (`SerialPortSettings.Validate`, port name must not
    change); apply failures roll back to the previously captured values on a best-effort basis
    before rethrowing. If the rollback itself fails, an `AggregateException` reports both
    failures and recommends reopening the port. A failed update therefore never leaves a
    silently half-applied configuration.
  - Virtual/Telnet transports: do not implement the interface; sessions over them reject
    in-place updates. Their settings are re-established by reopening with new options.
  - Tests: `SerialSessionCloseBudgetTests.ApplySettingsAsyncRejectsTransportsWithoutTheComCapability`
    plus the existing `SerialSessionTests.ApplySettingsAsync_UpdatesOpenTransportWithoutClosingSession`.

## Load-completeness close gate (2026-08-28 review rounds)

`LoadCompletenessEvaluator` additionally verifies, when the load runner supplies a
`SessionCloseGate`: written log bytes > 0 for positive input, the actual log files exist, the
actual on-disk byte total equals `WrittenLogBytes`, every session close returned
Succeeded/AlreadyClosed, and no session fault is present. Formatted block counts and written
record counts are deliberately not compared one-to-one (rotation batches records). Round 2
made the gate **per session**: the load runner verifies each port's own log files
(`{Port}-*.txt`), its own byte totals against its own `WrittenLogBytes`, its own close result
and fault, so one session's log mismatch fails the run even when the aggregate still adds up.
The load report schema moved to version 3 to carry the new fields; a failing gate exits the
CLI non-zero.

## Known Residual Window (accepted)

Bytes that arrive in the driver buffer after the final `BytesAvailable` check inside
`DrainTransportBufferAsync` and before the handle actually closes are discarded by the
operating system driver. This window is inherent to the `SerialPort` API (no "close-after-
drain" fused operation exists) and cannot be closed without driver-level ownership. It is
bounded: no application-level receive path is active in that window, and no accepted block is
ever lost.

## Evidence Model (honest separation)

- In-memory fake transports: prove pipeline mechanics.
- Driver-like fakes (`BytesAvailable` returns 0 after close, close discards the queue): prove
  the orchestration order in CI — `SerialSessionCloseOrderTests`.
- Continuous-producer fakes (2026-08-28 review round): prove the close budget faults explicitly
  and never hangs — `SerialSessionCloseBudgetTests`, `ReceivePipelineStopDisposeTests`.
- Real SerialPort hardware: human acceptance only; the driver-like fakes reduce but do not
  eliminate this need.

## Consequences

- Normal close now preserves driver-buffered-but-unsignaled data (the "silent backlog at
  close" case), matching the project's logging-completeness invariant.
- Close cancellation semantics change after the quiesce point (commit instead of abort);
  documented here and in the Sessions README.
- Close under sustained input is bounded by explicit budgets and reports an explicit fault
  when the budgets are exceeded.
- `ISerialTransport` is transport-neutral again; COM settings updates live behind an explicit
  capability interface.
- The Sessions and Pipeline README close-order contracts are updated in the same change as
  this ADR.

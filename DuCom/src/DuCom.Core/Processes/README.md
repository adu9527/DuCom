# Processes

## Responsibility

Own bounded external-process execution helpers and pure parsing/validation for supported command-line tools such as com0com. Process launching remains in the application service; list parsing and command admission are UI-free and directly testable here.

Bounded external-process execution: concurrent stdout/stderr capture, a wall-clock timeout
that starts when the process starts, process-tree kill on timeout or cancellation, and a
complete result (exit code plus both output streams). Used by the com0com `setupc.exe`
wrapper; the application layer adds the command-verb whitelist.
Path selection is deterministic: prefer an installed standard candidate, then a persisted
user-selected `setupc.exe` path when automatic discovery fails.

## Dependencies

None beyond the BCL. Must not depend on WPF.

## Thread Model

One runner invocation owns one process. stdout and stderr are drained concurrently by two
tasks so a full pipe can never deadlock the child; the timeout/cancellation token kills
the whole process tree, which closes the pipes and completes both drains.

## Invariants

- The timeout clock starts immediately after `Process.Start`, never after the streams end.
- On timeout or cancellation the entire process tree is killed.
- One total deadline (2026-08-28 review round 2) bounds the exit wait, the tree kill, and the final stdout/stderr drains together: the call always returns within roughly the configured timeout even when the kill or the drains stall, with `TimedOut` set and the captured output (possibly truncated) returned.
- Exit code, stdout, and stderr are always returned, including on timeout.
- No fixed sleeps; waits are token- or deadline-driven.

## Test Strategy

Real helper processes (`cmd.exe`) with large stdout, large stderr, a hanging command, a
nonzero exit code, a forced timeout, and caller cancellation.

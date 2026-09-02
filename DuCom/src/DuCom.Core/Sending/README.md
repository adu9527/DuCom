# Sending

## Responsibility

Own STR/HEX conversion, newline policy, command models, send history models, and validation that is independent of UI and persistence.

## Dependencies

May depend on Parsing helpers and shared value types. Must not depend on WPF or concrete SQLite repositories.

## Thread Model

Conversion and validation are pure. Session orchestration owns serialized transport writes and cancellation.

## Invariants

- A send operation captures mode and formatting options before transport write and TX logging.
- M2 advanced commands remain distinct from M4 event-driven scripting.

## Test Strategy

Use table-driven tests for valid and invalid HEX, encoding, escaping, newline combinations, and command validation.

## 2026-08-27 additions (GLM)

- `SendHistory` + `SendHistoryNavigator`: dedupe/capacity history model and draft-preserving up/down cursor. Pure types, fully tested.
- `CommandScript` / `CommandScriptSerializer`: flat command groups (order/delay/result-check substring/timeout/newline) with versioned JSON envelope; bad rows degrade to warnings, future versions rejected.
- `CommandScriptRunner`: sequential group loop executor (send, optional probe with 100 ms polling until timeout, delay slices), continuous playback until cancellation matching reference behavior. Delay is injectable for determinism (`WithDelay`, internal seam enabled via InternalsVisibleTo DuCom.Core.Tests).
- `MultiTargetCommandScriptRunner`: refreshes the selected target snapshot at each group loop, sorts targets by name, and runs each target concurrently. Send failures and result timeouts are reported per target and do not cancel other targets; an empty live target snapshot is an explicit run error.

## 2026-08-28 additions (GLM long task)

- `HexRepresentation`: pure text/HEX representations for the File-menu exports (uppercase space-separated byte pairs) plus strict hex-text parsing used by the clipboard transforms. Round-trip tested.

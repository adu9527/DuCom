# Ports

## Responsibility

Own port settings, discovery contracts, transport adaptation, and serialized port lifecycle behavior.

## Dependencies

May depend on Abstractions and System.IO.Ports. Must not depend on WPF, Logging implementations, or Storage implementations.

## Thread Model

DataReceived performs buffer copy and transfer only. Open and close transitions are serialized per port, and blocking shutdown waits stay off the UI thread.

## Invariants

- Lifecycle moves through Closed, Opening, Open, and Closing without overlapping transitions.
- Receive callbacks do not parse, format, log, or invoke UI code.

## Test Strategy

Use fake transports for transition tables, concurrent commands, cancellation, failures, and unplug behavior. Real devices remain a human acceptance test.

## M1 Contracts

- `PortLifecycle` serializes open/close/shutdown and publishes immutable versioned snapshots.
- Disconnect during Opening cannot be overwritten by a later open completion.
- `SerialPortTransport` is the only Core type that owns `System.IO.Ports.SerialPort`.
- Runtime serial parameter updates are applied through the session operation lock; receive callbacks remain copy-only. In-place settings updates are a COM-specific capability (`ISerialSettingsTransport`, implemented by `SerialPortTransport` only) — deliberately not part of the transport-neutral `ISerialTransport` so future non-COM transports (Telnet, virtual ports) are not forced into SerialPort semantics. Valid in both Closed and Open states; applied immediately; validate-first with best-effort rollback to the previous values when an apply step fails (ADR-0004).
- Default settings are 1152000 baud, 8N1, no handshake, and UTF-8.
- Concurrent `DisposeAsync` callers await one shared disposal task. A close failure is reported as `Faulted` with a fault message and a conservative `Closed` snapshot; failure no longer asserts that the underlying transport is still open.
- `SerialPortTransport` maps unrecoverable open-state read/write/status exceptions to one `Disconnected` event per successful open. Explicit Close/Dispose suppress the event, and DataReceived never enumerates ports, waits, parses, or performs recovery work.

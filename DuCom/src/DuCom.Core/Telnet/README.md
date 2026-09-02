# Telnet

## Responsibility

Transport-neutral Telnet bridge primitives: the TCP listener (bind policy, client
lifecycle, bounded broadcasts), Telnet negotiation filtering, runtime authentication,
incremental UTF-8 command framing, a reference-compatible shell, and the push pump that
replicates serial display lines to connected clients on a fixed cadence.

## Dependencies

May depend on Abstractions, Diagnostics, Parsing (ANSI stripping), and Storage (line
snapshots). Must not depend on WPF or on the application session ViewModels; the app-layer
`TelnetBridgeService` supplies delegates.

## Thread Model

- The listener accepts on one background task; one handler task per client, all tracked
  and awaited on stop.
- Broadcasts run independently per client with a bounded send wait; a slow client is
  disconnected after the timeout rather than delaying others.
- The push pump uses the shared single-flight periodic worker: one evaluation in flight,
  exceptions isolated to the diagnostic log, disposal cancels and waits for the tick.

## Invariants

- Default bind address is `IPAddress.Loopback`. Binding `IPAddress.Any` requires an
  explicit opt-in flag (`AllowRemote`), authentication, and a UI warning.
- Authentication is explicit. Credentials are runtime inputs; production defaults are not
  hard-coded. The application may persist the enabled flag and username, never the password.
- `help`, `clear`, `exit`/`quit`, and `sendtoall` are shell commands and never enter the
  serial send path. In bridge mode, every other authenticated non-empty line is sent to the
  bound port exactly once.
- Telnet IAC negotiation is consumed before UTF-8 framing and cannot become serial payload.
- TCP input is decoded with an incremental UTF-8 decoder; a multibyte character split
  across TCP segments must never be corrupted.
- Client input is line framed: CR, LF, or CRLF each terminate one command line; each
  completed non-empty line becomes exactly one serial send. Newline bytes are frame
  boundaries, not characters to be silently deleted from the payload.
- Framing memory is bounded (2026-08-28 review round 2): a command longer than the
  framer's maximum (default 8 KB) is dropped, counted in `OverflowCount`, discarded
  through the next terminator, and never emitted as fake commands.
- Listener lifecycle (2026-08-28 review round 2): Start/Stop are repeatable — stop then
  start restarts on a fresh listener; concurrent stops join the one in-flight stop;
  disposal permanently prevents restarts. Stop awaits all tracked handler tasks.
- A client command task is registered with the bridge lifecycle before it starts running
  (2026-08-28 review round 2), so a command racing disposal is always awaited or
  cancelled, never lost.

## Test Strategy

Unit tests for the framer (split UTF-8, CR/LF/CRLF, multiple lines, partial tails,
oversized no-newline input) and the bind policy; loopback socket tests for listener
default, remote-refused, disconnect, restart cycles, and broadcast timeout; worker tests
for pump lifecycle (dispose during push, start-after-dispose); bridge tests for the
command/dispose race.

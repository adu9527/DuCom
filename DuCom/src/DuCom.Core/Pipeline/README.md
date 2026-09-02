# Pipeline

## Responsibility

Own receive blocks, Channel topology, capacities, buffer ownership transfer, processing lifecycle, cancellation, and drain accounting.

## Dependencies

May depend on Abstractions and Diagnostics contracts. Product consumers are reached through explicit records or sinks. Must not depend on WPF.

## Thread Model

The receive callback transfers a rented block to one per-port processor. The processor creates independent log and line records before returning the receive buffer exactly once.

## Invariants

- A pooled receive block has exactly one owner at a time.
- Two asynchronous consumers never share a receive buffer.
- Every accepted block reaches a terminal accounted state.
- Capacity and loss behavior are explicit and measurable.

## Test Strategy

Cover ownership return, cancellation at each stage, slow consumers, processor faults, multi-port isolation, and million-block-equivalent flow.

## M1 Contracts

- Receive capacity is reserved before reading from the transport. A full Channel leaves unread bytes in the transport buffer rather than dropping an already copied block.
- The DataAvailable callback copies only and never waits asynchronously, parses, formats, logs, or dispatches UI work.
- Processor failure stops acceptance, completes the Channel, drains queued blocks, and returns every rented buffer.
- Queue peaks and every produced/accepted block are measured.
- `StopAsync` (2026-08-28 review) runs exactly once: concurrent callers — including `DisposeAsync` — await the same shared bounded stop task. `DisposeAsync` is idempotent and shares one disposal task across concurrent callers; if an uncooperative callback or sink outlives the budget, synchronization-object release is deferred until that work actually exits. Channel capacities, backpressure, and ArrayPool ownership are unchanged.
- One close budget, every phase (2026-08-28 review round 2): the whole stop sequence — waiting for in-flight receive callbacks, every transport-buffer read (a blocking driver read is deadline-bounded on a pool thread), every receive-capacity wait, and the final processor drain — shares the single wall-clock budget plus the appended-byte budget. A phase that blows the budget faults the pipeline with a phase-specific reason (callback stuck / blocked read / blocked capacity / processor not completing) and lets the close proceed to a forced transport close. The synchronous entry of `StopAsync` never blocks: quiescing is a lock-free flag flip plus event unsubscribe, safe to call from the UI thread.
- Stop drain budget (2026-08-28 review): the transport-buffer drain inside `StopAsync` is bounded by a wall-clock budget (default 5 s) and a maximum appended-byte budget (default 32 MB; both constructor-configurable). A transport that keeps producing under close exceeds a budget, faults the pipeline with an explicit reason (surfaced as a session fault by the session), and lets the caller proceed to a forced transport close — never a silent success, never an infinite loop, no fixed sleeps.
- Timeout cleanup (2026-08-28 high-risk review): processor cancellation explicitly drains every still-queued block exactly once, including capacities greater than one. `DisposeAsync` may return after the bounded stop, but synchronization objects remain alive until all late callbacks and the processor have actually exited; delayed callback/read completion can therefore return ownership without racing disposed events, tokens, or capacity slots.
- Every `ReceiveBlock` captures one immutable, versioned formatting profile when its bytes are copied. The callback performs only one atomic profile-reference read; parsing and formatter creation remain on the per-port processor. Runtime setting changes publish a new profile for future blocks without reinterpreting queued blocks.

# Presenting

## Responsibility

Own UI-free per-port auxiliary-window registries used by the application layer (for example
one mini log window per port). The registry stores opaque handle types and delegates all
window operations through callbacks, so its logic is testable without WPF.

## Dependencies

None beyond the BCL. Must not depend on WPF or any pipeline type.

## Thread Model

The registry is not thread-safe by itself. The application layer uses it from the UI
dispatcher thread only.

## Invariants

- At most one window is registered per port name (case-insensitive).
- `GetOrOpen` activates an existing window instead of creating a duplicate.
- `Close`/`CloseAll` invoke the close callback once per registered window and clear the
  registration even if a close callback throws.
- `Remove` unregisters without closing (user-initiated window close path).
- `Windows` returns a stable handle snapshot so one application render tick can include every
  auxiliary view without iterating mutable registry state or creating another data consumer.

## Test Strategy

Cover create/activate/no-duplicate, per-port isolation, case-insensitive keys, close,
close-all, remove-without-close, and factory-failure behavior with a plain fake class.

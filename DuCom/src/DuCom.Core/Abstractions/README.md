# Abstractions

## Responsibility

Own narrow seams for clocks, filesystems, transports, and metric sources when deterministic tests require substitution. Do not create interfaces for types that have only one concrete use.

## Dependencies

May use the .NET base class library. Must not depend on product modules or WPF.

## Thread Model

Contracts must state thread safety explicitly. Implementations must not hide Dispatcher affinity or synchronous blocking.

## Invariants

- Abstractions preserve the semantics and ownership rules of the concrete operation.
- Test seams do not weaken production error handling.

## Test Strategy

Use deterministic fakes in Core tests and contract tests where multiple implementations exist.

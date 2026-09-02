# Search

## Responsibility

Provide UI-free current-session log search. Supports plain text and regular expression matching with optional case sensitivity, returning ordered matches against a `LineStoreSnapshot`.

## Dependencies

Depends only on `DuCom.Core.Storage` value types. Must not depend on WPF or the receive pipeline.

## Thread Model

`LogSearchEngine.Search` is a synchronous, CPU-bound pure function. Callers (typically the WPF `SearchViewModel`) run it on a background thread and supply a `CancellationToken`. The engine never allocates long-lived state and yields cancellation checks between lines.

## Invariants

- Empty or null patterns return an empty result, not an error.
- Invalid regular expressions return a result with `ErrorMessage` set; no exception escapes.
- Regex execution uses the unified `LogSearchEngine.MatchTimeout` (100 ms per line). A timed-out pattern aborts the pass and returns the stable marker `LogSearchEngine.RegexTimeoutMessage`; the caller maps it to a localized message. The background task never faults on `RegexMatchTimeoutException`.
- Matches are ordered by line appearance, then by position within the line.
- Text search is non-overlapping; regex search follows `Regex.Matches` semantics.
- The engine never mutates the input snapshot.

## Test Strategy

Cover text matching, case sensitivity, regex matching, regex error handling, cancellation, empty pattern, and ordering across multiple stored lines.

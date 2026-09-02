# Parsing

## Responsibility

Own pure incremental parsing and framing logic for text, ANSI, highlight/filter rules, and later protocol parsing contracts.

## Dependencies

May use only the .NET base class library and Core value types that do not perform I/O. Must not depend on WPF.

## Thread Model

Parsers execute on per-port processing contexts. Pure operations are reentrant; incremental state is owned by one processor unless explicitly documented otherwise.

`StatefulReceiveFormatter` is single-owner incremental state. `Append` returns the latest snapshots of lines completed or updated by that input block; `Flush` emits remaining decoder, pending-CR, or unterminated-line state.

`HighlightFilterRuleMatcher` is a pure function. Given a rule set and a line of text it returns whether the line is visible under the enabled filter rules and a sequence of `HighlightRun` segments colored by the enabled highlight rules. Rules are evaluated in declaration order; earlier rules take precedence over later rules for overlapping highlight ranges.

### Regex timeout (2026-08-27)

- All regex execution in `HighlightFilterRuleMatcher` and `HighlightFilterRuleValidation` uses the unified `HighlightFilterRuleMatcher.MatchTimeout` (100 ms).
- A timed-out highlight rule contributes no runs; the line stays visible unhighlighted.
- A timed-out filter rule is treated as non-matching. If every enabled filter rule times out without a conclusive decision, the line fails open (stays visible) so catastrophic patterns cannot permanently hide all content.
- `HighlightFilterEvaluation.HasRegexTimeout` reports the timeout to the application layer; the UI latches one bilingual session warning plus a diagnostic-log entry per session.

## Invariants

- Malformed input follows defined replacement or recovery behavior without exception-driven control flow.
- Parsing never performs I/O or UI dispatch.
- STR framing recognizes CRLF, CR, and LF without treating receive-block boundaries as newlines.
- Timestamps are attached once at the real logical-line start, and HEX blocks remain one space-delimited line until flushed.
- Highlight/filter rules are pure data: matching and validation run without UI or serial-port dependencies.
- Filter evaluation affects display only; it never changes log records or receive-block ownership.

## Test Strategy

Use golden vectors, boundary splits, property tests, and fuzzing for malformed encodings and escape sequences.

For highlight/filter rules: test Regex/Contains matching with and without case sensitivity, overlapping rule precedence, malformed-regex validation, persistence round-trips, and filter visibility under enabled/disabled rules.

## 2026-08-27 additions (GLM)

- `StyleRun` and `StyledTextComposer.Compose(ansiRuns, highlightRuns)`: pure merge of ANSI-styled runs and highlight-rule runs over clean display text (ANSI foreground precedence over highlight fg; ANSI-owned background; reverse-video swap or inverse marker). Adjacent equal pieces merged.
- Display integration note: escape sequences are stripped at the UI projection layer only - stored/logged text keeps raw bytes; filter evaluation runs on the stripped text.

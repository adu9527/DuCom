# 0002: Load Measurement Contracts

- Status: Accepted
- Date: 2026-08-26

## Context

M0 needs reproducible evidence before the receive pipeline exists. Metrics and report fields must remain stable enough for later M1 comparisons without inventing machine-specific pass thresholds.

## Decision

- Keep measurement contracts in `DuCom.Core.Diagnostics` so the load tool, tests, and later product pipeline share one definition.
- Use monotonic `long` counters with immutable snapshots.
- Track produced, accepted, formatted, written, line, eviction, queue-peak, fault, and shutdown-drain values separately.
- Report input acceptance and log-formatting coverage as separate completeness checks.
- Define process metrics as explicit snapshots supplied by the runner rather than hidden ambient sampling inside report models.
- Version the machine-readable JSON schema, starting at version 1.
- Generate human-readable Markdown using invariant culture.
- Treat deterministic load blocks as generated input records with port index, per-port sequence, scheduled offset, and owned payload bytes.
- Derive pseudo-random data with a repository-owned fixed algorithm rather than runtime `Random` or `HashCode`, whose implementation is not a long-term report contract.
- Model serial baud equivalents as 8N1, or ten transmitted bits per payload byte, for M0 standard scenarios.
- Keep the M0 target interface independent from Channels and pooled buffers; those ownership and capacity contracts remain framework-owned M1 work.

## Consequences

Later pipeline and writer stages can populate the same report without changing M0 field meaning. Generator-only reports must identify that no M1 product pipeline was measured, and thresholds remain unfrozen until standard scenarios are run on the development machine.

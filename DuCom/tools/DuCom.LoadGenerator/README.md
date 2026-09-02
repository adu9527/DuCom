# DuCom.LoadGenerator

## Responsibility

Run deterministic in-memory single- or dual-port generation and emit the versioned JSON and Markdown reports defined by `DuCom.Core.Diagnostics`.

## Boundaries

This M0 tool does not open serial ports, implement the M1 receive pipeline, or claim product throughput. Generated reports establish harness reproducibility and later accept measured pipeline/process metrics.

## Usage

```powershell
dotnet run --project tools\DuCom.LoadGenerator -- --scenario dual-1m-mixed --output reports/generated
```

Standard scenarios are `dual-1m-mixed`, `dual-1152000-sustained`, `dual-3m-burst`, `no-newline-continuous`, `malformed-text-esc`, `slow-log-target`, and `failing-log-target`.

Optional overrides are `--seed`, `--duration-seconds`, `--bytes-per-second`, `--ports`, `--min-chunk`, `--max-chunk`, `--profile`, `--pace`, and `--output`. Overrides are intended for short development checks; archived baseline reports use the unchanged standard scenario definition.

Use `--target serial-session` to drive the real M1 Core receive formatter, line store, bounded log Channel, UTF-8 files, and shutdown drain through in-memory transports.

The M0.2 target only accepts generated blocks. Formatting and file writing do not exist until M1, so those report counters remain zero rather than claiming simulated success.

# DuCom.Core.Benchmarks

## Responsibility

Own BenchmarkDotNet microbenchmarks for isolated Core hot operations. Sustained multi-port load remains in `DuCom.LoadGenerator`; microbenchmarks do not replace it.

## Fixed Commands

Run all release benchmarks:

```powershell
dotnet run -c Release --project benchmarks\DuCom.Core.Benchmarks -- --join
```

Run a fast infrastructure check without treating results as a baseline:

```powershell
dotnet run -c Release --project benchmarks\DuCom.Core.Benchmarks -- --job Dry --filter "*"
```

## Baseline Methodology

- Use Release mode on the same machine and power profile.
- Record commit or worktree identity, .NET runtime, OS, CPU, and benchmark package version.
- Compare the same benchmark and parameter set; do not claim product throughput from a microbenchmark.
- Keep generated `BenchmarkDotNet.Artifacts/` ignored. Archive only reviewed summaries under `docs/testing/` when a milestone requires evidence.
- Add buffer transfer, STR decode, HEX formatting, framing, timestamp, and line-store benchmarks only when those production primitives exist.

## Current Coverage

- Deterministic dual-port block generation.
- JSON and Markdown load-report serialization.

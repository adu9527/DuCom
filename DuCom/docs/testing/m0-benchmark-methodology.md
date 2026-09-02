# M0 Benchmark Methodology

BenchmarkDotNet is reserved for isolated operation costs. The deterministic load runner is the authority for sustained multi-port accounting and report generation.

## Environment

Capture the following with every reviewed baseline summary:

- Worktree or commit identifier.
- Machine, CPU, operating system, and power profile.
- .NET SDK/runtime and BenchmarkDotNet versions.
- Benchmark name, parameters, launch count, warmup count, and iteration count.

## Interpretation

- Compare only like-for-like runs on the same machine configuration.
- Treat allocations and distribution as evidence alongside mean time.
- Do not convert generator or serializer microbenchmarks into claims about serial receive, logging, rendering, or application responsiveness.
- Do not freeze thresholds from Dry jobs; Dry exists only to verify benchmark discovery and execution.

Generated BenchmarkDotNet output remains ignored. Reviewed summaries may be added here when M0 establishes the first measured baseline.

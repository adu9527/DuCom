# Diagnostics

## Responsibility

Own counters, immutable snapshots, process measurements, load-report contracts, and later WatchDog primitives.

## Dependencies

May use Abstractions for deterministic time and process seams. Must not depend on WPF or concrete product modules for metric collection.

## Thread Model

Hot-path counters must be safe for concurrent updates and cheap to sample. Reports use immutable snapshots after or during a scenario.

## Invariants

- Counts are monotonic unless a contract explicitly defines a new measurement scope.
- Completeness is derived from explicit terminal counts, never inferred from absence of errors.
- Load report schema changes require a version increment and compatibility decision.
- Generator output is a pure function of explicit options, including seed and port index.

## Test Strategy

Use deterministic snapshot and serialization tests, concurrency tests for counters, cross-profile generator repeatability tests, exact per-port byte accounting, and end-to-end load-report validation.

## Current M0 Contracts

- `LoadMetrics` owns concurrent monotonic counters and queue peaks.
- `PipelineMetricsSnapshot`, `ProcessMetricsSnapshot`, `MachineInfo`, and `LoadScenarioInfo` are immutable report inputs.
- `LoadReport` schema version 1 is serialized by `LoadReportSerializer` to JSON and invariant-culture Markdown.
- `DeterministicLoadGenerator` emits scheduled blocks for one or two ports without opening transports or implementing the M1 pipeline.
- `StandardLoadScenarios` owns the versioned M0.2 scenario catalog and 8N1 baud-to-byte-rate assumptions.
- `InMemoryLoadRunner` optionally paces blocks by scheduled offsets and sends them to one explicit `ILoadBlockTarget`.
- Immediate, delayed, and failing targets verify harness accounting and explicit faults without defining the future M1 Channel topology.
- `DiagnosticFileLog` provides a dependency-free, thread-safe development log that never throws into application code. It rotates at 5 MB by default and retains three previous files.
- `WatchdogRule` + `WatchdogEvaluator` (2026-08-28): pure watchdog state machine. A rule expects its pattern at least once per `ExpectWithinSeconds`; when not seen, the action fires at most once per `ThrottleSeconds`. Regex execution uses the unified 100 ms timeout and never throws. Evaluation consumes display snapshots incrementally — never receive callbacks or pooled blocks (the application `WatchdogService` owns the timer and dispatches actions).
- `WatchdogEngine` + `VariableMonitorEngine` (2026-08-28 review): pure per-tick orchestration over immutable `*SessionProbe` records (port name, open flag, delegate-based snapshot pull). The application layer builds the probes on the UI thread; engines never touch a ViewModel. Contexts follow the open-session set each tick (added sessions anchor at their current end, closed sessions drop).
- `PeriodicBackgroundWorker` (2026-08-28 review): shared periodic loop for the watchdog, variable monitor, and Telnet push. Ticks are strictly sequential (the next tick never starts while the previous runs), tick exceptions are isolated to a log callback, and disposal cancels and waits for the in-flight tick. Round 2: `Start` after `DisposeAsync` throws `ObjectDisposedException`, and Start/Dispose serialize on one gate so a loop can never be started-but-never-awaited.
- `LoadCompletenessEvaluator` close gate (2026-08-28 review): with a `SessionCloseGate` the evaluator additionally requires written log bytes > 0 when input > 0, the actual on-disk log files to exist, actual file bytes to equal `WrittenLogBytes`, every session close to be Succeeded/AlreadyClosed, and no session fault. Formatted block counts and written record counts are not compared one-to-one (rotation batching makes them intentionally different granularities).

Alignment note: the reference project's "WatchDog" is a memory-limit monitor (`MemoryDog`); DuCom's runtime monitor already covers process metrics, so the M2 watchdog implements the rule/timeout/throttle/action model from the task specification instead.

M0.2 reports distinguish input acceptance from log-formatting coverage. Until M1 exists, a successful generator run has complete input acceptance and intentionally incomplete log-formatting coverage.

The WPF application initializes its diagnostic log before loading `App.xaml`. Runtime logs are written to `%LocalAppData%\DuCom\Logs\ducom.log`; this is application diagnostic output, not a serial-session log.

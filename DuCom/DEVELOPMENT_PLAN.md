# DuCom Development Plan

- Status: Active
- Created: 2026-08-26
- Scope authority: `../AGENTS.md` and `../AI自研串口工具可行性分析.md`
- Product scope: Build an original Windows serial debugging tool. Match all existing SuperCom behavior except the desktop pet, while keeping DuCom's architecture, UI, naming, and implementation original.
- Languages: zh-CN and en-US only.

## 0. Execution Status

Last updated: 2026-08-28 (final M1/M2 automated closure review and hardening)

> Gate-number provenance: earlier summaries (`m1-glm-completion-summary.md`, `m1-m2-glm-completion-summary.md`)
> carry stale test counts (207/219) and one incomplete load run (1,971,644 input bytes instead of 2,000,000).
> Both are marked Superseded. Current numbers below come from the gates actually executed in the latest round;
> automated M1/M2 gates are tracked separately from human real-device acceptance, which remains outstanding.

| Work package | Status | Evidence / decision |
|---|---|---|
| F0.1 Repository Hygiene | Complete | Generated `DuCom/**/bin` and `DuCom/**/obj` content removed from the Git index without deleting local output; ignore rules verified for builds, tests, benchmarks, runtime databases, generated reports, logs, and publish output. |
| F0.2 Shared Build Policy | Complete | Shared compiler/analyzer policy and central package versions added; package ownership recorded in decision 0001; restore/build pass with zero warnings. |
| F0.3 Architecture Guardrails | Complete | Nine Core module READMEs and a reusable template added; three dependency tests pass. A temporary `UseWPF=true` violation was detected by the guard and removed. |
| M0.1 Core Measurement Contracts | Complete | Concurrent pipeline counters, immutable process/scenario snapshots, schema v1 JSON, invariant Markdown output, and deterministic tests added under `DuCom.Core.Diagnostics`. |
| M0.2 In-Memory Load Generator | Complete | Deterministic single/dual-port generation, eight payload profiles, seven versioned standard scenarios, scheduled pacing, slow/failing targets, exact accounting, explicit fault exit status, and JSON/Markdown CLI reports are implemented. This harness does not simulate M1 formatting or file writes. |
| M0.3 Benchmark Project | Complete | An isolated BenchmarkDotNet project, fixed Release/Dry commands, methodology documentation, and initial generator/report-serialization benchmarks are in place. Dry execution discovered and ran all three benchmarks; its timings are not a frozen baseline. |
| M0.4 Application Shell | Implementation complete; awaiting human acceptance | WPF UI Fluent shell, functional TitleBar/resizing, Mica/system theme handling, explicit composition root, pre-XAML persistent diagnostics, global error/shutdown hooks, design tokens, zh-CN/en-US resources, terminology, and original information-architecture brief are implemented. Debug and Release language/theme smoke cases validate 960x640, 1366x768, and 1920x1080 while keeping the log workspace and send action visible. Final visual approval remains human-owned. |
| M0.5 M0 Gate | Automated gate complete; awaiting human milestone approval | Standard dual-1m-mixed run has exact produced/accepted accounting and zero faults; architecture, localization, build, test, format, shell smoke, and Git hygiene gates pass. M1 must not start until the human owner approves M0 exit. |
| M1 Core Pipeline | Automated closure complete; real-device acceptance human-owned and outstanding | Release build, 480 tests, format verification, UI smoke gates, and the `dual-1m-mixed` serial-session load pass. The load accounts for exactly 6,963 blocks / 2,000,000 input bytes, 100% formatted-block coverage, 2,160,876 expected and actual log bytes, zero faults, and drain `Completed`. Receive blocks capture immutable arrival-time formatting profiles; close-time ownership, transport disconnect signaling, periodic batched log flushing, long-line continuation, and frame-coalesced auto-scroll are hardened. Sustained FTDI/CP210x/CH340 at 921600–3M, unplug/replug cycles, and long-line device runs remain human hardware acceptance. |
| M2 Tools Alignment | Automated implementation complete; real-device and visual acceptance human-owned and outstanding | Automated implementation includes ANSI display, highlight/filter/search, STR/HEX sending and history, multi-port advanced command groups, per-port receive/log/send preferences, horizontal/vertical split persistence and tab ordering, independently updating per-port mini windows, com0com parameter parsing and persisted custom `setupc.exe` path, application-owned Telnet shell/bridge with loopback-first security and optional authentication, content WatchDog plus private-memory threshold monitoring, effective settings/theme/shortcut persistence, bilingual resources, and read-only SuperCom database/config-directory migration with field-level reporting and atomic commit. Release build, 480 tests, format, and main/settings/tools/split smoke gates pass. M2 milestone exit still requires the human hardware/driver/network/migration matrix in `docs/testing/final-hardware-checklist.md`; variable plotting and Quad split remain documented product differences. |

M1 and M2 automated implementation gates are green. Human real-device, driver, network, migration, and final visual acceptance remain open, so neither milestone is declared human-approved or fully exited.

## 1. Purpose

This document is the executable master plan for DuCom. It converts the product and architecture decisions into reviewable work packages that can be assigned across models without allowing architecture drift.

The collaboration model is:

- The framework model owns architecture, public contracts, lifecycle, concurrency, memory ownership, performance acceptance, WPF rendering foundations, and integration.
- Smaller models implement bounded tasks only after the framework model has fixed interfaces, invariants, tests, and file scope.
- The human owner makes product decisions, performs real-device acceptance, and approves milestone exits.
- Every task leaves the solution buildable. Pure-logic work is test-first; performance work is benchmark-first.

## 2. Non-Negotiable Decisions

1. Target `.NET 10` with WPF and WPF UI. Do not change the UI framework or runtime.
2. Keep dependency direction `DuCom -> DuCom.Core`. `DuCom.Core` must not reference WPF.
3. Receive topology is `DataReceived copy -> receive Channel -> per-port processing -> line store -> frame-pull UI`, with an independent formatted-log Channel.
4. A pooled receive block has one owner. The port processor creates independent log and line records before returning the receive block.
5. Logs are per-session UTF-8 `.txt` formatted text, not raw binary capture files.
6. Log formatting follows the selected STR/HEX mode, port encoding, timestamp option, and send-prefix rules at the time data arrives.
7. Automatic logging and 40 MB rotation are enabled by default and configurable.
8. Display clearing, freezing, trimming, rendering backlog, and mini-window lifecycle must not lose log records.
9. Disk failures are explicit session errors. Silent log loss is forbidden.
10. UI consumes snapshots on a render cadence. Receive code must never dispatch each packet to WPF.
11. Display memory is budgeted. Long physical lines are segmented in storage while preserving logical line identity.
12. Port lifecycle follows serialized `Closed -> Opening -> Open -> Closing` transitions.
13. DuCom supports zh-CN and en-US only. Every new visible string is added to both resources in the same change.
14. All SuperCom features except the desktop pet are M2 alignment scope. M3 and M4 work must not start before M2 exits.
15. SuperCom is read-only behavior reference. Do not copy its layout, style, naming, or implementation.

## 3. Model Ownership

### 3.1 Framework Model Only

The following work must be designed and initially implemented by the strongest model:

- Project and module topology, dependency graph, shared build settings, analyzers, and architecture tests.
- Receive block representation, `ArrayPool` ownership, Channel topology, capacity policy, cancellation, and shutdown.
- Port/session state machine and event contracts.
- Log record contract, writer lifecycle, rotation, flush, failure propagation, and completeness metrics.
- Line-store memory layout, budget enforcement, stable IDs, segmentation, eviction, and snapshots.
- Parser contracts and incremental parser state boundaries.
- WPF composition root, frame-pull rendering contract, virtualized viewport, selection/copy architecture, and thread affinity.
- SQLite schema, migration versioning, repository boundaries, and SuperCom import boundary.
- M0 load model, metrics definitions, benchmark methodology, report schema, and pass/fail gates.
- Cross-module integration, milestone release branches, and architecture-affecting refactors.

Small models must not change these areas unless the framework model provides an explicit, file-bounded task that preserves an already approved contract.

### 3.2 Delegable Work

After contracts and acceptance tests exist, smaller models may implement:

- Table-driven unit tests and additional edge cases.
- HEX codecs, newline handling, timestamp formatting, encoding helpers, CRC/LRC algorithms, and other pure functions.
- Focused SQLite repository methods against an approved schema.
- Individual ViewModels, resource dictionaries, controls, dialogs, and bilingual resource entries against approved UI contracts.
- Additional load scenarios and benchmark cases using the approved harness.
- One-feature SuperCom behavior summaries and manual verification checklists.
- Documentation, sample data, analyzer cleanup, and bounded bug fixes.

### 3.3 Human Owner

The human owner is responsible for:

- Product priority changes and scope exceptions.
- Real COM hardware, driver, hot-plug, and high-baud tests.
- Visual acceptance at required resolutions.
- Deciding whether milestone exit evidence is sufficient.
- Approving changes to this master plan or `AGENTS.md`.

### 3.4 Selected Small-Model Roster

Use three regular small models and one on-demand visual model. The framework model remains responsible for architecture and integration.

| Model | Primary role | Suitable tasks | Must not own |
|---|---|---|---|
| Kimi-K2.7-Code | Focused implementation | Pure Core functions, approved repository methods, ViewModels, bounded refactors, unit-test implementation | Public contracts, concurrency architecture, buffer ownership, schema design |
| GLM-5.3 | Test and feature completion | Table-driven tests, edge cases, bilingual resources, focused controls, SuperCom behavior extraction, documentation | Cross-module design, performance-policy changes, final integration decisions |
| Deepseek-V4-Pro | Independent review and debugging | Code review, failure diagnosis, concurrency test review, SQL review, benchmark-result analysis, finding missing cases | Directly redesigning approved architecture or broad unbounded rewrites |
| GLM-5v-Turbo | Visual QA on demand | Screenshot analysis, responsive-layout inspection, expected/actual UI comparison, localization clipping and visual-state checks | Core code, architecture, performance conclusions without measured data |

Assignment rules:

1. Kimi-K2.7-Code normally writes the bounded implementation after framework contracts and tests are defined.
2. GLM-5.3 normally writes or expands tests, resources, documentation, and isolated UI details; it may implement simple pure logic when Kimi is occupied.
3. Deepseek-V4-Pro reviews high-risk patches independently before framework-model integration. It should receive the contract, diff, tests, and known invariants rather than the entire repository history.
4. GLM-5v-Turbo is invoked only when screenshots or visual comparisons exist. Do not spend a regular coding task on it.
5. Do not assign the same patch to multiple implementation models. Use a writer and, where risk justifies it, a separate reviewer.
6. Seed, Qwen, MiniMax, other GLM/Kimi variants, Deepseek-V4-Flash, and Hy3 remain reserve models. They may replace a selected model after measured task results, but are not part of the default workflow.
7. Model selection is operational, not architectural. Replacing a small model does not permit changing contracts, ownership, milestones, or acceptance gates.

## 4. Planned Solution Structure

The framework phase may add projects only for clear process isolation. The intended structure is:

```text
DuCom/
├── DuCom.slnx
├── DEVELOPMENT_PLAN.md
├── docs/
│   ├── architecture/
│   ├── design/
│   ├── reference-behavior/
│   ├── testing/
│   └── decisions/
├── src/
│   ├── DuCom/                    WPF views, ViewModels, resources, composition
│   └── DuCom.Core/               UI-free product logic and infrastructure
├── tests/
│   └── DuCom.Core.Tests/         Unit, property, fuzz, architecture, load tests
├── benchmarks/
│   └── DuCom.Core.Benchmarks/    BenchmarkDotNet microbenchmarks
└── tools/
    └── DuCom.LoadGenerator/      Sustained-load runner and report generation
```

Do not split Core into many assemblies prematurely. Use folders and namespaces first. Add another production assembly only when a dependency boundary cannot be enforced cleanly inside the existing two-project structure.

Proposed `DuCom.Core` modules:

```text
Abstractions/     Clock, filesystem, transport, metrics seams used by tests
Ports/            Port settings, discovery contracts, transport adapter, lifecycle
Sessions/         Session orchestration and session-visible state
Pipeline/         Receive blocks, Channels, ownership, processing lifecycle
Logging/          Log records, formatting, writer, rotation, error state
Storage/          Line segments, runs, line store, budgets, snapshots
Parsing/          Text framing, ANSI, filters, protocol parsing contracts
Sending/          STR/HEX conversion, newline policy, commands, history models
Diagnostics/      Counters, snapshots, load metrics, WatchDog primitives
Persistence/      SQLite schema, repositories, migrations, import models
```

Each module directory must contain a short `README.md` describing responsibility, dependencies, thread model, invariants, and test strategy.

## 5. Universal Task Gates

Every implementation task must satisfy these gates before completion:

1. Read `AGENTS.md`, this plan, the target module README, and only the required reference files.
2. State the bounded file scope and acceptance criteria in the task handoff.
3. Add or update tests before implementation for pure logic.
4. Do not weaken an existing invariant to make a test pass.
5. Run `dotnet build` from `DuCom/` after each reviewable change.
6. Run focused tests during development and `dotnet test` before handoff.
7. Run the relevant benchmark or load scenario for performance-sensitive changes.
8. Add all user-visible strings to zh-CN and en-US resources together.
9. Update the module README when contracts, ownership, or threading change.
10. Do not include `bin/`, `obj/`, `.vs/`, logs, reports, databases, or temporary publish output.

Milestone exit additionally requires:

```powershell
dotnet build
dotnet test
dotnet format --verify-no-changes
```

Performance milestones also require the fixed load script and archived summary report. WPF milestones require manual startup and layout verification.

## 6. Phase F0: Repository and Architecture Foundation

Owner: Framework model

Goal: Turn the default template into a governed solution without implementing product features prematurely.

### F0.1 Repository Hygiene

- Stop tracking generated `bin/` and `obj/` content while preserving local files and ignore rules.
- Verify `.gitignore` covers build output, test results, logs, databases, benchmark artifacts, and publish folders.
- Remove placeholder source and unused template imports only when replacement scaffolding is ready.

Exit:

- No generated output is tracked.
- A clean build does not create untracked files that should be committed.

### F0.2 Shared Build Policy

- Add minimal shared build properties if needed for nullable, warnings, analyzers, deterministic builds, and language version consistency.
- Add fixed package references for WPF UI, CommunityToolkit.Mvvm, System.IO.Ports, Microsoft.Data.Sqlite, and BenchmarkDotNet in the projects that need them.
- Do not add a dependency-injection framework unless composition becomes materially simpler than explicit construction.

Exit:

- Restore and build succeed.
- Package ownership is documented.

### F0.3 Architecture Guardrails

- Create the module folders and module README template.
- Add architecture tests proving Core has no WPF dependency and tests do not depend on the WPF application.
- Define namespace and dependency rules among Core modules without over-constraining internal implementation.
- Record architecture decisions under `docs/decisions/` when they affect multiple modules.

Exit:

- Architecture tests fail on an intentional test violation and pass after it is removed.
- All initial modules have README files.

## 7. Milestone M0: Skeleton and Performance Baseline

Owner: Framework model for contracts and harness; smaller models for additional scenarios and reports.

Goal: Automatically run dual-port 1M-baud-equivalent input and produce reproducible evidence before M1 implementation.

### M0.1 Core Measurement Contracts

- Define counters for produced blocks/bytes, accepted blocks/bytes, formatted log blocks, written log records/bytes, line records, evictions, queue peaks, faults, and shutdown drain state.
- Define process metrics: elapsed time, throughput, allocation rate, GC collections, working set, private memory, CPU time, and thread count.
- Define machine-readable JSON and human-readable Markdown report formats.

Exit:

- Metrics are deterministic in unit tests where applicable.
- Report schema is versioned.

### M0.2 In-Memory Load Generator

- Add deterministic single- and dual-port generators.
- Support fixed, random, burst, long-line, mixed-newline, UTF-8, malformed-byte, and HEX-oriented patterns.
- Make seed, duration, rate, chunk distribution, port count, and payload profile explicit inputs.
- Separate sustained load tests from BenchmarkDotNet microbenchmarks.

Initial standard scenarios:

| Scenario | Purpose |
|---|---|
| Dual 1M equivalent, mixed lines | M0 CI baseline |
| Dual 1152000, sustained | SuperCom comparison baseline |
| Dual 3M equivalent, burst | Development-machine stress |
| No newline, continuous | Long-line and memory safety |
| Malformed text/ESC bytes | Parser safety foundation |
| Slow/failing log target | Explicit error propagation |

Exit:

- The runner produces JSON and Markdown reports.
- The same seed produces the same block and payload sequence.

### M0.3 Benchmark Project

- Establish BenchmarkDotNet for buffer transfer, STR decode, HEX formatting, line framing, timestamp formatting, and line-store primitives as they appear.
- Keep microbenchmarks out of normal unit-test execution.
- Store scripts and baseline summaries, not generated benchmark output.

Exit:

- A fixed command runs benchmarks locally.
- Baseline methodology is documented.

### M0.4 Application Shell

- Integrate WPF UI and CommunityToolkit.Mvvm.
- Create the composition root, application lifecycle, global exception reporting, and graceful shutdown hook.
- Establish Fluent 2 design tokens and semantic resources without building the full product layout.
- Establish zh-CN/en-US resource loading and a bilingual terminology list.
- Create an original information-architecture brief centered on connection, log reading, and sending.

Exit:

- Application starts in both themes and both languages.
- No user-visible placeholder text remains untranslated.
- Window remains usable at 1920x1080, 1366x768, and the declared minimum size.

### M0.5 M0 Gate

Required evidence:

- Dual-port 1M-equivalent automated report.
- Zero unexplained block loss in the harness.
- Architecture tests passing.
- Build, tests, and format passing.
- M0 design brief approved by the human owner.

Threshold values are measured during M0 and then frozen in a versioned baseline. Do not invent pass/fail numbers before the harness measures the actual development machine.

## 8. Milestone M1: Core Receive-to-Display Pipeline

Owner: Framework model. Smaller models may add tests and bounded formatters after contracts are fixed.

Goal: Deliver a real single/multi-port receive pipeline that writes complete UTF-8 `.txt` logs and renders a bounded, virtualized, non-ANSI display.

### M1.1 Port Lifecycle State Machine

- Implement serialized `Closed`, `Opening`, `Open`, and `Closing` transitions.
- Define behavior for duplicate open/close, cancellation, open failure, unplug, shutdown, and disposal.
- Keep blocking close waits off the UI thread.
- Expose immutable state snapshots/events to the application.

Tests:

- Full transition table.
- Duplicate and concurrent commands.
- Cancellation during open/close.
- Transport failures and unplug simulation.
- Shutdown idempotency.

### M1.2 Serial Transport Adapter

- Wrap System.IO.Ports behind a narrow Core-owned transport contract.
- Enumerate ports and support baud rate, data bits, stop bits, parity, flow control, and encoding.
- Keep `DataReceived` limited to copying into a rented block and attempting Channel transfer.
- Surface errors and disconnects through the lifecycle model.

Tests:

- Use a fake transport for all CI tests.
- Reserve real SerialPort tests for the human hardware matrix.

### M1.3 Receive Pipeline and Ownership

- Implement receive-block rent, write, transfer, processing, and return lifecycle.
- Make Channel capacities and shutdown drain behavior configurable and measurable.
- Generate independent immutable/pool-owned log records and line records before returning the receive block.
- Prove every accepted block is accounted for by metrics.

Tests:

- Ownership returned exactly once.
- Cancellation at every stage.
- Full/slow consumer behavior.
- Processor exception containment.
- Multi-port isolation.
- Million-block equivalent flow.

### M1.4 Text Framing and Formatting

- Support per-port encoding with UTF-8 default.
- Implement STR and HEX receive formatting based on the mode captured with each block.
- Normalize CRLF/CR/LF according to the approved reference behavior.
- Escape `\0` and define replacement behavior for malformed encoded input.
- Add optional per-line timestamp prefixes and send-prefix formatting.
- Keep formatting pure and testable.

Tests:

- Golden cases derived from SuperCom behavior.
- Split multibyte characters across blocks.
- Mixed newline forms.
- Mode/encoding changes between blocks.
- Null bytes and malformed sequences.

### M1.5 Session Log Writer

- Write one UTF-8 `.txt` stream per port session.
- Enable logging and 40 MB rotation by default; make path, naming format, enabled state, and threshold configurable.
- Batch asynchronous writes and flush on normal close.
- Surface directory, permission, disk-space, write, flush, and rotation failures explicitly.
- Keep logs independent from display storage and UI windows.

Tests:

- Exact formatted output for known block sequences.
- Rotation boundary and collision-safe file naming.
- Close drains accepted records.
- Faulted filesystem produces visible session fault state.
- Display eviction and clear do not affect output.

### M1.6 Budgeted Line Store

- Define logical line IDs, physical segments, source direction, timestamps, text offsets, style-run references, and eviction markers.
- Use shared text segments rather than one string per line where measurement justifies it.
- Enforce a configurable memory budget with a 64 MB initial default.
- Segment long physical lines at a measured safe threshold while retaining logical identity.
- Provide immutable snapshots/ranges for rendering without exposing mutable internals.

Tests:

- Budget never exceeds its defined tolerance.
- Oldest-line eviction and stable remaining IDs.
- Long-line segmentation across receive blocks.
- Concurrent append and snapshot reads.
- Clear affects display state only.

### M1.7 Virtualized Log View

- Implement frame-cadenced snapshot pulls.
- Render only visible lines plus a bounded overscan region.
- Pool reusable visuals/resources where beneficial.
- Implement keyboard focus, standard selection, copy, scrolling, end-follow, freeze, clear, and an evicted-line indicator.
- Do not implement an AvalonEdit fallback in parallel.

Manual acceptance:

- High-rate receive does not enqueue one Dispatcher operation per packet.
- Selection and copying remain usable during receive.
- Freeze stops viewport following, not receive or logging.
- Narrow layouts preserve log and send access.

### M1.8 Session Workspace

- Add multi-session tabs and independent connection state.
- Build the minimum original connection, log, send, and low-priority diagnostic regions.
- Implement STR/HEX sending and newline options sufficient to exercise the pipeline.
- Keep view code free of SerialPort and pipeline details.

### M1.9 M1 Gate

Required automated evidence:

- M0 baseline remains green.
- Accepted receive-block coverage into logging is 100% under target scenarios.
- Display eviction, clear, freeze, and rendering slowdown do not change log output.
- Memory remains within the configured line-store and queue budgets.
- Normal shutdown drains all accepted log records.
- Disk failures are explicit and tested.

Required human evidence:

- Sustained tests at 921600, 1.5M, 2M, and 3M where supported.
- Repeated connect/disconnect and USB unplug tests.
- Long-line device output test.
- Log inspection around device reset/crash.

## 9. Milestone M2: Complete SuperCom Alignment

Owner: Framework model defines each feature boundary and integration contract. Smaller models may implement individual bounded features.

Goal: Match every existing SuperCom feature except the desktop pet, using original DuCom UI and architecture.

M2 is split into integration waves. A wave is not complete until its automated tests, bilingual resources, manual checklist, and performance regression run pass.

### M2.A Display and Log Semantics

- Incremental ANSI/VT color parser with malformed-sequence tolerance.
- Highlight rules and filter rules.
- Timestamp controls and formatting options.
- STR/HEX display behavior.
- Freeze, follow-end, clear, copy, and display budget controls.
- Runtime CPU, memory, GC, and thread diagnostics.

Delegation candidates:

- Parser test vectors and fuzz corpus.
- Highlight/filter pure matchers.
- Bilingual settings controls.

### M2.B Sending and Commands

- STR/HEX sending.
- Send history and history persistence.
- Newline and escape handling.
- Send prefixes and TX logging.
- SuperCom-compatible advanced scripted-command behavior.
- Command import and validation.

Boundary:

- This wave aligns existing command capability.
- Lua/C# receive-event automation remains M4 and must not be introduced here.

### M2.C Sessions and Views

- Multiple concurrent port sessions.
- Split view.
- Mini log window.
- Independent filters/view modes where reference behavior requires them.
- Session reordering, closing, and persisted session preferences.

Performance rule:

- Extra views consume snapshots and must not add consumers of pooled receive blocks or duplicate the logging path.

### M2.D VirtualPort and Bridging Baseline

- Align SuperCom VirtualPort behavior.
- Align Telnet server/client behavior included in the reference project.
- Preserve connection lifecycle, cancellation, logging, and diagnostics invariants for non-COM transports.
- Define transport-neutral session contracts only where actual behavior requires them; do not generalize speculatively.

### M2.E WatchDog

- Align existing WatchDog triggers, actions, persistence, and status reporting.
- Run trigger evaluation outside the receive callback and outside WPF.
- Prevent slow actions from blocking per-port parsing.
- Add explicit throttling and fault reporting.

### M2.F Settings, Theme, and Localization

- Persist global and per-port settings.
- Light/dark/system theme support.
- zh-CN/en-US completeness checks.
- Serial settings, logging settings, display budgets, filenames, shortcuts, and relevant window preferences.
- No ja-JP resources or fallback dependency.

### M2.G Data Migration

- Read SuperCom SQLite and JSON settings without modifying source data.
- Import serial configurations, send history/commands, highlight/filter rules, and common settings.
- Do not block M2 on importing old log text or exact old window geometry.
- Produce an import report with imported, skipped, and invalid item counts.

### M2.H Feature Inventory Verification

Before declaring alignment complete:

- Build a source-backed inventory under `docs/reference-behavior/`.
- For each feature, record reference source paths, visible behavior, persisted fields, edge cases, DuCom implementation path, automated tests, and manual test status.
- Search the reference project again for user-visible commands/windows/services so the 2.2 summary is not treated as exhaustive source truth.
- Explicitly record the desktop pet as excluded.

### M2 Gate

Required evidence:

- Every inventory item is implemented, excluded by the approved pet exception, or explicitly approved for deferral by the human owner.
- All tests and M0/M1 performance regressions pass.
- Migration is read-only and reports results.
- Both languages have complete resource coverage.
- Real-device daily use is viable without returning to SuperCom for included workflows.

## 10. Milestone M3: Differentiation

Start only after M2 exit.

Planned scope:

- Frame view with HEX/ASCII/mixed presentation.
- Search index and large-log retrieval.
- Time-range filtering and interval measurement.
- Send plans with variables, loops, conditions, and queue visualization.
- Throughput and timing statistics.
- BES-focused dual-port workspace and crash-window navigation.

Framework-owned foundations:

- Search/index storage contracts.
- Frame parsing contracts.
- Scheduling and cancellation model.
- Multi-port correlation model.

M3 exit is defined by real-device usefulness and measured advantage, not feature count alone.

## 11. Milestone M4: Automation and Ecosystem

Start only after M3 foundations are stable.

Planned scope:

- Lua or Roslyn C# receive/send-event automation, selected through a dedicated architecture decision.
- Local plugin loading with AssemblyLoadContext isolation.
- COM/TCP/UDP bridge enhancements.
- Numeric visualization and serial waveform views.
- Session recording/replay and optional headless automation mode.
- Multi-window and multi-monitor workflows.

Security, isolation, cancellation, and resource budgets must be designed before loading user code or plugins.

## 12. Testing Strategy

### Unit Tests

- State machines, formatting, framing, line segmentation, eviction, rotation, settings, migrations, and command parsing.

### Property and Fuzz Tests

- ANSI parser, malformed encoding, random byte boundaries, HEX parser, long-line segmentation, and state-machine command sequences.

### Sustained Load Tests

- Multi-port equivalent flows with deterministic seeds and report output.
- Separate short CI scenarios from long local/dogfood scenarios.

### Benchmarks

- Isolated hot operations only. Benchmarks do not replace sustained-load tests.

### Architecture Tests

- Core has no WPF references.
- Views do not reference SerialPort or pipeline implementations.
- Tests target Core rather than UI internals.

### Manual WPF Tests

- Startup, theme, language, focus, copy, scrolling, minimum size, 1366x768, 1920x1080, multi-session, split, and mini-window behavior.

### Hardware Matrix

- FTDI, CP210x, CH340, PL2303 where available.
- com0com or equivalent virtual pair.
- Bluetooth SPP where available.
- 921600, 1.5M, 2M, and 3M where hardware/driver support permits.
- Repeated open/close, unplug/replug, sleep/resume, and application shutdown with pending logs.

## 13. Performance Evidence Policy

Every performance report must include:

- Commit or worktree identifier.
- Machine and runtime information.
- Scenario name and version.
- Seed, duration, port count, target byte rate, and chunk profile.
- Produced, accepted, formatted, written, evicted, and faulted counts.
- Throughput and queue peaks.
- Working-set/private-memory start, peak, end, and growth.
- CPU time, allocation rate, and GC collections.
- Shutdown drain duration and final completeness state.

Rules:

- Do not claim improvement from a single microbenchmark.
- Compare the same scenario and machine configuration.
- Treat missing records or silent writer failure as a correctness failure, not a performance tradeoff.
- Update frozen thresholds only with an explicit decision record and human approval.

## 14. Small-Model Task Handoff Template

Every delegated task should use this structure:

```text
Task ID:
Milestone and module:
Goal:
Allowed files:
Forbidden files/modules:
Contracts that must not change:
Required reference files:
Tests to add first:
Implementation requirements:
Commands to run:
Expected report/output:
Stop conditions requiring framework-model review:
```

Mandatory stop conditions:

- A public contract appears insufficient.
- A new cross-module dependency is required.
- Thread ownership or buffer ownership would change.
- A test requires weakening a documented invariant.
- WPF types appear necessary in Core.
- Persisted schema or migration behavior would change.
- Performance acceptance cannot be met without changing capacity or loss policy.

## 15. Review and Integration Policy

- Keep one task to one coherent concern.
- Do not combine visual redesign, pipeline behavior, and persistence changes in one delegated patch.
- Framework model reviews all public API, concurrency, lifecycle, storage-layout, and schema changes.
- Smaller-model output is accepted only after tests and local build pass; plausible-looking code is not sufficient.
- Unexpected user or agent changes are preserved unless they directly conflict with the task.
- No model commits, pushes, or creates a pull request unless the human owner explicitly requests it.

## 16. Immediate Execution Queue

The recommended first framework-model sequence is:

1. F0.1 clean Git tracking of generated output.
2. F0.2 establish shared build/package policy.
3. F0.3 create module boundaries, README files, and architecture tests.
4. M0.1 define metrics and report contracts.
5. M0.2 build the deterministic dual-port load generator.
6. M0.3 establish benchmarks and fixed scripts.
7. M0.4 integrate the WPF UI application shell, design tokens, MVVM, and localization.
8. Run the M0 gate and freeze the initial baseline.
9. Begin M1.1 through M1.3 as framework-owned core infrastructure.
10. Delegate bounded formatter/test/documentation tasks only after their contracts are merged and documented.

This order intentionally delays feature UI and SuperCom alignment work until the architecture can prove receive ownership, log completeness, shutdown behavior, and measurable load handling.

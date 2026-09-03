# DuCom Developer Guide

[中文](DeveloperGuide.zh-CN.md) · [User Manual](UserManual.en-US.md) · [Web version](Web/developer-guide-en.html) · [GitHub](https://github.com/adu9527/DuCom)

> Stack: .NET 10, WPF, C# 14, WPF-UI, CommunityToolkit.Mvvm  
> Updated: 2026-09-02

## 1. Purpose

DuCom is an open-source Windows serial tool designed to keep reception complete, logging reliable, and the UI responsive while embedded devices continuously produce high-throughput output. Serial I/O, parsing, persistence, and presentation are separated so that callbacks never directly drive the UI.

Repository: https://github.com/adu9527/DuCom

## 2. Development Environment

- Windows 10/11
- .NET 10 SDK
- Visual Studio with the .NET desktop development workload (recommended), or another C#/.NET IDE
- Git
- Optional real serial hardware, USB-to-serial adapters, and com0com

```powershell
dotnet --info
git --version
```

## 3. Clone, Build, and Run

```powershell
git clone https://github.com/adu9527/DuCom.git
cd DuCom\DuCom
dotnet restore
dotnet build
dotnet run --project src\DuCom\DuCom.csproj
```

Run all tests:

```powershell
dotnet test DuCom.slnx
```

Run Core tests only:

```powershell
dotnet test tests\DuCom.Core.Tests\DuCom.Core.Tests.csproj
```

## 4. Repository Layout

```text
DuCom/                         repository root
├─ README.md                   project landing page
├─ Doc/                        public bilingual documentation
├─ Image/                      README screenshots
└─ DuCom/                      .NET solution root
   ├─ DuCom.slnx
   ├─ Directory.Build.props
   ├─ Directory.Packages.props
   ├─ src/DuCom/               WPF application
   ├─ src/DuCom.Core/          UI-independent core
   ├─ tests/DuCom.Core.Tests/  xUnit tests
   ├─ tools/                   load and support tools
   ├─ benchmarks/              BenchmarkDotNet projects
   └─ docs/                    ADRs, design, and verification notes
```

`Doc/` is public-facing documentation. `DuCom/docs/` contains internal engineering decisions and verification records.

## 5. Architecture

### 5.1 Layers

- **DuCom.Core:** serial lifecycle, receive pipeline, parsing, search, sending, storage, Telnet, diagnostics, and persistence primitives.
- **DuCom:** WPF views, ViewModels, application services, configuration composition, localization, and Windows integration.
- **Tests:** automated evidence for Core behavior and critical boundaries.
- **Tools/Benchmarks:** deterministic load, completeness checks, and performance benchmarks.

Core must not reference WPF types. Protocol, parsing, storage, and state-machine logic should be placed in Core whenever practical.

### 5.2 Receive Flow

```text
SerialPort callback
  → copy bytes / bounded receive pipeline
  → incremental formatter (STR / HEX / ANSI)
  → session sink
       ├─ asynchronous file log
       └─ budgeted display store
            → UI reads snapshots on render cadence
```

Invariants:

1. The callback performs only required copy/enqueue work.
2. It never parses, writes files, or dispatches UI work.
3. File logging is independent from display follow and trimming.
4. Display memory is bounded while disk logs remain complete.
5. Open, close, and disconnect transitions are explicitly serialized.

### 5.3 Send Flow

STR/HEX encoders create bytes, an optional line ending is appended, and the session writes asynchronously. Successful sends are recorded as TX log entries. Command groups add multiple targets, delays, expected-result checks, timeouts, and loop control.

## 6. Important Directories

### `src/DuCom.Core`

- `Ports/`: settings, transport, and lifecycle.
- `Pipeline/`: receive blocks and bounded pipelines.
- `Parsing/`: STR/HEX, ANSI, styled output, highlighting, and filtering.
- `Storage/`: budgeted line storage and snapshots.
- `Sending/`: encoding, history, scripts, and multi-target runners.
- `Search/`: safe search and regex timeout handling.
- `Diagnostics/`: load metrics, WatchDog, variable and memory evaluation.
- `Telnet/`: server, authentication, and protocol handling.
- `Persistence/`: atomic file storage and migration infrastructure.

### `src/DuCom`

- `ViewModels/`: UI state and commands, not reusable protocol algorithms.
- `Services/`: Windows/WPF services, stores, and Core adapters.
- `Resources/Languages/`: `zh-CN.xaml` and `en-US.xaml`.
- `Resources/DesignTokens*`: themes, spacing, and styles.
- `Controls/`, `Behaviors/`, `Converters/`: reusable UI building blocks.
- `MainWindow`, `SessionWorkspace`: shell and session workspace.
- `ToolCenterWindow`: virtual ports, Telnet, monitoring, commands, and other tools.

## 7. MVVM and Commands

The project uses `CommunityToolkit.Mvvm`:

- `[ObservableProperty]` generates notification properties.
- `[RelayCommand]` generates commands.
- Long-running I/O should be asynchronous and cancellable.
- ViewModels should delegate reusable pure logic to Core.
- Window launch, file dialogs, clipboard, and similar Windows behavior may be handled by thin application-layer services or window code.

Define testable state/contracts before connecting new UI. Avoid accumulating business logic in XAML code-behind.

## 8. Localization

Put visible strings in:

```text
src/DuCom/Resources/Languages/zh-CN.xaml
src/DuCom/Resources/Languages/en-US.xaml
```

Use identical resource keys and reference them with `{DynamicResource Key}`. Every added or removed key must be synchronized across both files. Do not hard-code status messages in only one language.

## 9. Configuration and User Data

Application data is primarily stored under the user's local application-data DuCom directory: settings, shortcuts, highlight rules, history, command groups, WatchDog rules, and monitor rules.

Persistence rules:

- Use atomic writes.
- Provide defaults and backward compatibility for new fields.
- Never persist the Telnet plaintext password; it is runtime-only.
- Importers validate fields and report imported/skipped/invalid counts.
- Logs and diagnostics are never committed.

## 10. Testing Strategy

- **Unit:** encoding, parsing, search, regex timeouts, state machines, storage budgets, migrations.
- **Integration:** serial-session pipeline, log completeness, shutdown order, multi-target commands.
- **Smoke:** localization keys, settings migration, tool routing, split behavior.
- **Hardware:** real adapters, hot-plug, occupied ports, serial errors, long high-baud runs.
- **Load:** deterministic generation and comparison of generated, received, logged, and display-trim metrics.

Before a PR:

```powershell
dotnet build DuCom.slnx --configuration Release
dotnet test DuCom.slnx --configuration Release
```

Changes involving XAML, localization, serial hardware, or publishing also require a manual startup check.

## 11. Performance and Concurrency Rules

- Never block `SerialPort.DataReceived`.
- Batch and rate-limit UI updates.
- Run disk writes, search, and export off the UI thread.
- Avoid per-byte object allocation in hot paths.
- Background loops must support cancellation and idempotent stop.
- Apply timeouts to user-controlled regular expressions.
- Serialize shared serial lifecycle transitions with explicit state/locking.
- Distinguish display trimming from actual reception loss in metrics and messages.

## 12. Adding a Feature

1. Describe the scenario, I/O, and boundaries in an Issue.
2. Decide whether the behavior belongs in Core or the WPF layer.
3. Add tests/fixtures for pure logic first.
4. Implement the smallest behavior with explicit cancellation, errors, and persistence.
5. Add Chinese and English resources.
6. Update the user guide, developer guide, or internal ADR.
7. Run Debug/Release builds and tests.
8. Perform required hardware/load validation.
9. Open a Pull Request against `test`.

## 13. Code Style

- C# 14, nullable enabled, implicit using enabled.
- PascalCase for types/public members, camelCase for locals/parameters, `_camelCase` for private fields.
- Validate public contract arguments and define exception semantics.
- Do not swallow exceptions; expose recoverable errors as status and log diagnostics.
- Comments explain non-obvious reasons and constraints, not syntax.
- Keep Core free of UI dependencies.

## 14. Branches, Commits, and PRs

- `main`: stable code and Releases, maintained by the owner.
- `test`: day-to-day development and the target for external contributions.

Create feature branches from `test`. A PR should include context, summary, test evidence, screenshots for UI changes, compatibility/performance impact, and documentation updates. Do not commit `bin`, `obj`, release executables, logs, databases, or personal settings.

## 15. Release Publishing

DuCom is published to users as a self-contained single EXE, currently measured at approximately 78 MB (the size may vary slightly between versions). Users download and double-click it without installing .NET.

Recommended settings:

| Setting | Value |
|---|---|
| Configuration | Release |
| Runtime | win-x64 |
| Deployment | Self-contained |
| Produce single file | Enabled |
| ReadyToRun | Optional; evaluate size/startup trade-offs |

```powershell
dotnet publish src\DuCom\DuCom.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true
```

Before publishing, check version metadata, About release date, manuals, README links, release notes, malware scan results, and startup on a clean machine. Attach the output to GitHub Releases; do not commit it.

## 16. Documentation Maintenance

- `Doc/*.md`: GitHub reading and review.
- `Doc/Web/*.html`: browser reading and GitHub Pages.
- Keep Chinese and English facts/sections synchronized.
- The Help menu opens the manual for the active language.
- Update the user manual in the same PR as user-visible behavior.
- Add an ADR under `DuCom/docs/decisions/` for architectural boundaries or important trade-offs.

## 17. Feedback

- QQ group: `1107820408`
- Issues: https://github.com/adu9527/DuCom/issues
- Repository: https://github.com/adu9527/DuCom

Do not publish credentials or device-secret logs. Sanitize data and provide the smallest reproducible example.

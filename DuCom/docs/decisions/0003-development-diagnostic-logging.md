# 0003: Development Diagnostic Logging

- Status: Accepted
- Date: 2026-08-26

## Context

WPF can compile successfully while still failing during resource resolution or window initialization. A `WinExe` may then appear to do nothing when launched by double-click, and `Trace` alone provides no persistent evidence.

## Decision

- Initialize a dependency-free file logger in an explicit `Program.Main` before `App.InitializeComponent()`.
- Write logs under `%LocalAppData%\DuCom\Logs`, outside the repository and user session-log location.
- Use UTF-8 text, immediate flush, a 5 MB active-file limit, and three retained rotated files.
- Log process/runtime identity, startup phases, selected language/theme, main-window creation, global exceptions, and application exit.
- Logging failures must never prevent startup or replace the original application exception.
- Keep development diagnostics separate from M1 per-session formatted serial logs.

## Consequences

Startup failures in XAML resource loading and WPF window initialization leave an actionable stack trace. If the process cannot load the .NET runtime or required assemblies before managed entry executes, Windows loader diagnostics remain necessary because application logging cannot run.

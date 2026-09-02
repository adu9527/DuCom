# 0001: Solution And Dependency Boundaries

- Status: Accepted
- Date: 2026-08-26

## Context

DuCom needs enforceable WPF isolation and clear package ownership before receive or UI features are implemented.

## Decision

- Keep two production assemblies: `DuCom` and `DuCom.Core`.
- Keep dependency direction `DuCom -> DuCom.Core`.
- Target `DuCom.Core` at `net10.0` so it cannot acquire Windows Desktop framework references implicitly.
- Target the application at `net10.0-windows` with WPF enabled.
- Keep tests dependent only on `DuCom.Core`.
- Organize Core boundaries with folders and namespaces before considering more assemblies.
- Centralize package versions in `Directory.Packages.props` and shared compiler policy in `Directory.Build.props`.

## Package Ownership

- `DuCom`: WPF-UI and CommunityToolkit.Mvvm.
- `DuCom.Core`: System.IO.Ports and Microsoft.Data.Sqlite.
- `DuCom.Core.Tests`: xUnit, Microsoft.NET.Test.Sdk, and coverlet collector.
- `DuCom.Core.Benchmarks`: BenchmarkDotNet only; benchmark output remains outside normal tests and Git tracking.

## Consequences

Architecture tests can reject accidental WPF or application dependencies. Core remains directly testable on a non-WPF target, while the application owns all presentation concerns.

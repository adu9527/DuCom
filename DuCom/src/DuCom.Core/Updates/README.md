# Updates

## Responsibility

UI-free building blocks for the application self-update feature: release tag parsing and
comparison, GitHub release API access, portable asset selection, staged downloads, and the
apply script that swaps a portable single-file executable after the running process exits.

Two update channels exist at the application layer:

- **Velopack** (installed builds): the application hooks `VelopackApp` at startup and lets
  `UpdateManager` download and apply delta/full packages from GitHub releases.
- **Portable** (single `DuCom.exe` distributed as a release asset): the application downloads
  the asset named `DuCom.exe` into an `Updates` folder next to the running executable and
  offers a one-click swap.

## Dependencies

BCL only (`System.Net.Http`, `System.Text.Json`). Must not depend on WPF. The Velopack
integration lives in the WPF application project on purpose.

## Version Contract

- Release tags use the `V{major}.{minor}.{patch}.{revision}` form (for example `V0.0.0.3`).
  Legacy four-digit tags such as `V0003` are accepted and map to `0.0.0.3`.
- Comparison uses the full four-part `System.Version`; display keeps the original tag text.
- The portable asset must be named `DuCom.exe` (case-insensitive); the selector falls back to
  the only unambiguous `.exe` asset that does not look like an installer or symbol package.

## Apply Script Invariants

- The script is pure ASCII; paths travel through `DUCOM_UPDATE_*` environment variables so
  non-ASCII installation paths survive the OEM code page of `cmd.exe`.
- The script waits until the current PID disappears, moves the staged file over the target
  (with one retry), restarts the executable, and deletes itself.
- The application exits only after the script process has been started.

## Test Strategy

Unit tests cover tag parsing/ordering, asset selection, repository URL splitting, and script
shape (ASCII, no embedded absolute paths). Network downloads are exercised manually against
a real release.

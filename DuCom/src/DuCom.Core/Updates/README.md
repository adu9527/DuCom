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
- A staged package must pass `PortablePackageVerifier` before it becomes installable:
  non-empty size, size match with the release metadata, a Windows executable (MZ) header,
  and a SHA-256 digest match when the release asset publishes one. An absent or unsupported
  digest algorithm never blocks the update; a published-but-mismatching sha256 always does.

## Apply Script Invariants

- The script is pure ASCII; paths travel through `DUCOM_UPDATE_*` environment variables so
  non-ASCII installation paths survive the OEM code page of `cmd.exe`.
- The script waits until the current PID disappears, but never longer than ~120 seconds, so
  a hung process or PID reuse cannot wedge the update forever; a timeout leaves the staged
  package untouched for the next attempt and writes a `.apply-timeout` marker.
- The PID is matched as a space-delimited tasklist column (`" %PID% "`) so digits inside
  image names, session numbers, or memory values cannot fake a match.
- The previous executable is backed up next to the target (`DuCom.exe.bak`) before the swap
  and kept after a successful swap so a bad update can be rolled back manually.
- The staged file is moved over the target with one retry; when both attempts fail the
  target stays untouched, a `.apply-failed` marker is written, and the staged package is
  kept for retry.
- The script restarts the executable only when the swap succeeded, and deletes itself.
- `ApplyRollback` reuses the same script to restore the `.bak` left by the last update;
- the executable it replaces is kept as `.pre-rollback.bak` so an accidental rollback
  can be undone by hand. A backup without an MZ header is never restored.
- The shared HTTP client has no overall timeout (a portable exe is tens of megabytes);
- metadata requests apply their own 30-second deadline and surface a timeout as a failure.
- Apply markers (`.apply-failed`, `.apply-timeout`) are logged and consumed at startup and
  cleaned up when the staged package is discarded or a retry swap begins.

## Test Strategy

Unit tests cover tag parsing/ordering, asset selection, repository URL splitting, package
verification (MZ header, size, sha256 digest), discard, and script shape (ASCII, no embedded
absolute paths, backup-before-swap, bounded wait). Network downloads are exercised manually
against a real release.

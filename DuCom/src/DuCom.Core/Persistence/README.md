# Persistence

## Responsibility

Own SQLite schema versions, migrations, repositories, persisted models, and all-or-nothing JSON store commits.

## Dependencies

May depend on Microsoft.Data.Sqlite and Core domain value types. Must not depend on WPF or application ViewModels.

## Thread Model

Connections and transactions are scoped explicitly. Database work runs outside receive callbacks and WPF rendering paths.

## Invariants

- Schema changes require versioned migrations and an architecture decision when cross-module contracts change.
- `AtomicFileStore.CommitAll` stages every write to a temp file first, backs up existing destinations, replaces atomically, and rolls every already-replaced file back when any step fails, for every exception type (not only IO), continuing the rollback on per-file failures. Backups that a failed rollback could not restore are kept on disk and named in the thrown aggregate. A rollback that succeeds consumes its backup because its content became the restored file.
- Hidden ports are part of the persisted settings snapshot. `PortVisibility.NormalizeHidden` is the single normalization used by capture and restore, so hidden ports survive restarts.

## Test Strategy

Use temporary databases (created in Temp, never user databases) for repository and migration tests; use temp directories for atomic-commit success, failure rollback, and encoding tests.

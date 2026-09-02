# ADR-0003: Test-Project Application Source-Link Boundary

- Status: Accepted (review-fix round, 2026-08-27)
- Context: Review item nine
- Supersedes: none

## Decision

1. `DuCom.Core.Tests` must not add a `ProjectReference` to the WPF application `DuCom.csproj`,
   and the dependency direction stays `DuCom -> DuCom.Core -> (tests)`.
2. The seven application-layer shortcut sources currently compiled into the test project via
   `<Compile Include>` links are recorded as an **approved, frozen allow-list**:

   - `Services/Shortcuts/ShortcutModifiers.cs`
   - `Services/Shortcuts/ShortcutKeyGesture.cs`
   - `Services/Shortcuts/ShortcutAction.cs`
   - `Services/Shortcuts/ShortcutDefinition.cs`
   - `Services/Shortcuts/ShortcutConflictResult.cs`
   - `Services/Shortcuts/ShortcutConfiguration.cs`
   - `Services/Shortcuts/ShortcutManager.cs`

3. **No new application-source links may be added.** Architecture test
   `TestProjectApplicationSourceLinksStayWithinApprovedBoundary` parses the test csproj and
   fails when the link set differs from the list above. New pure logic that needs testing goes
   into `DuCom.Core` and is referenced as a normal project dependency.
4. This allow-list is accepted technical debt pending a framework-model decision: migrating the
   shortcut sources into `DuCom.Core` would grow Core's public API surface around shortcut
   persistence (`shortcuts.json` lives under `%LocalAppData%\DuCom`) which mixes file-system
   policy into a UI-free library. Do not migrate unilaterally.
5. These linked tests exercise source copies, not the deployed assembly; this is explicitly NOT
   claimed as full architectural correctness of the shortcut feature.

## Consequences

- Adding link entries breaks CI until a new ADR approves them.
- Any future framework-model migration plan should delete this ADR's allow-list together with
  the `<Compile Include>` block in one change.

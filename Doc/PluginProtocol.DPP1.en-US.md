# DuCom Plugin Protocol DPP/1

Status: convergence draft v0.5; not released or frozen.

Date: 2026-09-08. Languages: [Chinese](PluginProtocol.DPP1.zh-CN.md) | [English](PluginProtocol.DPP1.en-US.md). Changes in this revision: added the `files.pickWrite` directory mode, `files.createWriteTarget`, `files.hostPaths`, `ui.notify`, and the slider UI node (all optional capabilities; see §10, §11, §13).

Basis: converged from multi-round model reviews and user decisions; the review drafts were removed and this document is authoritative.

This document specifies target behavior and developer contracts. It does not claim that the repository already implements a plugin runtime, sandbox, or SDK. Examples are design inputs, not an available SDK. Both languages should be updated together. Resolve ambiguity against the Chinese record of user decisions and correct both documents; do not ship incompatible implementations based on translation differences.

## 1. Goals and Normative Language

MUST denotes a conformance requirement; MUST NOT denotes prohibited behavior; SHOULD requires justification for deviation; MAY denotes optional behavior. A "proposed default" is a host policy requiring testing, not a permanently frozen wire constant.

The goal is stable core responsibilities and public contracts, allowing new features within existing capabilities to ship solely as plugins. New host capabilities, security fixes, and performance work may change host implementation without silently breaking published plugins.

Core responsibilities include serial transmission/reception, original data capture, display, reliable log persistence, basic settings, and session management. Plugins are removable features and must not become synchronous dependencies of these responsibilities.

Version 1 must include one process per plugin, identical built-in execution, forced termination on failure, persistent disablement, one warning per failure, a tool-wide resource budget, restart-based updates, and verifiable version compatibility.

## 2. Architecture and Security Boundaries

```text
DuCom Host
  Core: serial / receive / display / log writer
  Plugin supervisor: lifecycle / watchdog / resource policy
  Broker: validated IPC / permissions / bounded queues
  UI renderer: host-owned declarative controls
       | authenticated local IPC
       +-- Restricted Worker A: built-in background plugin
       +-- Restricted Worker B: built-in log-package plugin
       +-- Restricted Worker C: community plugin
```

Each activation instance MUST use a separate managed process. Plugin code, module initialization, constructors, and dependency loading run only there; the host must not instantiate a plugin before moving work out of process. The host does not load plugin DLLs or rely on ALC unloading for fault isolation.

The host MUST control the process tree, prevent unmanaged escape and duplicate instances. Ordinary plugins cannot create child processes by default. Workers and the host must not run elevated; elevated host functionality must not confer elevation on plugins.

Process isolation is a fault boundary, not automatically a malicious-code sandbox. Windows access restrictions such as restricted tokens/AppContainer and resource/lifetime mechanisms such as Job Objects must be designed separately. A Job Object alone is not a file, network, or process-access sandbox.

Plugins cannot directly access serial devices, the host process, host settings/log directories, or the network by default; required resources are granted through broker services. The runtime MUST enforce these restrictions rather than merely document them. If the required restrictions cannot be established on a target machine, the host must refuse execution with a reason, not silently fall back to an unrestricted worker.

The concrete Windows policy requires implementation validation and is a release gate in §18. This does not promise protection against OS vulnerabilities, kernel failures, or every malicious program; this draft must not be advertised as an already verified sandbox.

Official signatures, an online approval service, and a marketplace are not currently required. The maintainer reviews changes and controls main-branch merges; this is source governance, not a substitute for installation consent, package validation, and runtime isolation. First activation of an unreviewed package must display its unverified origin and requested permissions. Neither a directory name nor the `com.ducom.*` prefix proves safety.

## 3. Packages, Identity, and Manifest

A directory is the installed form; `.dcpack` is the ZIP distribution form. A package contains at least its manifest, entry DLL, and runtime dependencies, and may contain assets and localization tables.

```text
<plugin-id>/<version>/
  plugin.manifest.json
  Example.Plugin.dll
  [private dependencies and runtime metadata]
  assets/
  i18n/en-US.json
  i18n/zh-CN.json
```

| Field | Requirement | Meaning |
|---|---|---|
| `manifestVersion` | Required integer | Manifest format version; initially 1. |
| `id` | Required string | Stable identity matching `^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)+$`. |
| `name` | Required string | Readable fallback name, not a missing translation key. |
| `version` | Required SemVer | Plugin version. |
| `protocolVersion` | Required `major.minor` | Minimum required DPP version, not the SDK build version. |
| `minHostVersion` | Required SemVer | Minimum host version, checked even when protocol requirements match. |
| `entryAssembly` | Required string | DLL filename at package root, with no path separators. |
| `entryType` | Required string | Entry type inside the worker; type and construction rules freeze with the SDK. |
| `runtime` | Required object | `framework` and `rid`, e.g. `net10.0` and `win-x64`; the host must support that combination. |
| `capabilities` | Required string array | Required capabilities; v1 treats every entry as required and rejects unknown/unimplemented entries. |
| `permissions` | Optional string array | Requested broker permissions; defaults to empty; unknown entries rejected. |
| `defaultCulture` | Optional string | Required when localization resources exist; an explicit culture name. |
| `description`, `author`, `homepage` | Optional strings | Display metadata, not identity authentication. |

The manifest is the single source of metadata. Before any plugin code executes, the host validates UTF-8 JSON, field types, lengths, versions, capabilities, permissions, paths, package size, and identity conflicts. Duplicate JSON properties should be rejected. Unknown nonessential metadata fields may be ignored but cannot confer permissions. Declarations never self-grant access.

Only one installed version of an id may be selected at a time; a replacement requires the explicit update flow. Ambiguous origins or duplicate installation records require resolution rather than filesystem enumeration order. `com.ducom.*` is reserved for official packages and cannot be registered by third parties, but the prefix is not authentication.

The installer must reject traversal, absolute paths, ADS, link/reparse-point escapes, case collisions, and decompression bombs; it limits file count, per-file/total extracted size, and manifest size. Plugins must not modify installed content between validation and loading. A digest identifies content and detects replacement; it does not prove publisher trust.

## 4. Protocol, SDK, and Compatibility

DPP's long-lived contract is messages and behavior, not internal host .NET objects. The C# SDK adapts the worker side and must not reference `DuCom`, `DuCom.Core`, or expose their types as contracts. Host and plugin share no Dispatcher, Control, Stream, SerialPort, delegate, or in-process object reference.

For required plugin version P and supported host version H:

| Condition | Behavior |
|---|---|
| Same major, P.minor ≤ H.minor, all required capabilities available | Eligible for subsequent validation; dependency loading is not guaranteed. |
| Same major, P.minor > H.minor | Reject and request a host upgrade. |
| Different major | Reject explicitly unless the host separately implements and tests an older-major adapter. |
| Host below minHostVersion, or unsupported runtime/RID | Reject with the specific reason. |

A minor version may add optional capabilities/messages but cannot change published field meanings. Ignore unknown optional response fields; return `UnsupportedOperation` for unknown required operations; treat unknown error codes as generic failure while retaining the original code. Unknown enum values require safe rejection or fallback as specified by each DTO.

Do not add required members to a published plugin-implemented interface. New message fields require default semantics; removal, renaming, unit changes, or narrowing valid ranges require compatibility analysis. A breaking major upgrade is a last resort, not something that can honestly be ruled out forever.

SDK package version, protocol version, and CLR AssemblyVersion are separate. A fixed AssemblyVersion may be an SDK engineering policy, not an IPC compatibility guarantee. The runner unifies SDK identity inside its worker; private dependencies must not replace the runner SDK. Release gates must run real plugins built with older SDKs without recompiling them first.

## 5. IPC and Sessions

Version 1 proposes local Windows named pipes, asynchronous bidirectional messages, and JSON DTOs. Pipes must restrict access to the expected local principals, use unpredictable endpoints and one-time session credentials, and verify the connecting process; a claimed plugin id or guessable pipe name is insufficient. Credentials must not appear in logs or public command lines. Connection failure is bounded by the startup deadline.

Proposed framing: a 4-byte unsigned little-endian payload length followed by UTF-8 JSON; a default 1 MiB frame maximum, checked before payload allocation. Limits include base64 or other encoding expansion. Large data uses chunks, never unbounded read-to-end. Exact schemas, numeric limits, and test vectors must freeze before SDK 1.0 release.

Every message contains `protocolVersion`, `sessionId`, `activationId`, `kind`, and `operation`. Requests/responses also contain `requestId`; responses contain exactly one of `result` or structured `error`. Events include subscription identity and sequence. Identifiers are bounded strings and cannot be used directly as paths.

The handshake exchanges runner version, package digest, required capabilities, final host grants, and limits; installation records determine identity. After the handshake only operations granted to that activation may be used. Stale, duplicate, or cross-activation messages must not cause side effects.

A request completes at most once. A lost response or process exit does not prove that the operation did not run; v1 does not automatically replay side-effecting requests. Output commit uses a stable caller-generated `commitId`; the host retains a bounded activation-scoped terminal record queryable through `output.commitStatus`, distinguishing `preparing`, `committing`, `committed`, `aborted`, and `unknown`. Reusing the same `commitId` may only return its recorded result and must not publish again. Cancellation is a request; forced termination is the last resort for unresponsive work. Late results after cancellation cannot recommit a task. Disconnection invalidates ordinary session tokens, while a recorded commit result remains queryable for the host retention period.

The host limits connections, queued bytes, in-flight requests, subscriptions, rates, and nesting depth, parsing and validating off the UI/serial paths. Those paths must not parse or wait for plugin messages. Malformed frames, authentication failures, flooding, and repeated serious violations close the connection and fault-disable the plugin.

## 6. Lifecycle and Recovery

```text
Discovered -> Validated -> Starting -> Activating -> Active
Active -> Stopping -> Disabled
Starting / Activating / Active / Stopping -> FaultDisabled
Validated -> Rejected
FaultDisabled -> Starting  [explicit user retry only]
```

`Rejected` means an invalid package was not executed; `FaultDisabled` means an execution/startup attempt failed and disablement was persisted. The host owns state. Every start gets a new `activationId` and process; retry is prohibited until the previous instance is confirmed exited.

Atomically persist attempt intent before starting the worker, including plugin id, version, digest, activation identity, host run identity, and stage. Do not automatically execute if persistence fails. Multiple host instances must maintain separate records rather than overwrite each other.

UI/subscription registrations during activation are staged and published together on success; failure rolls them all back. Construction, loading, initialization, and activation failures each belong to the same single attempt-level fault event.

Normal stop immediately closes admission and revokes business broker access and commit eligibility, removes UI, stops delivery, discards queued work, and cancels in-flight requests. It requests worker cleanup within a budget, then terminates the process tree and verifies exit. During cleanup only bounded cancellation acknowledgments, cleanup responses, and diagnostics are allowed, not business access or output commits. Faults or resource emergencies may skip the grace period. Reclaim handles and temporary output afterward and reject late messages.

If termination fails, remain disconnected and disabled, report "exit not confirmed," and never falsely report completion or wait indefinitely. OS lifetime management should reap managed workers after abnormal host exit.

Unexpected crashes, startup failures, unresponsiveness, expired calls, sustained backlog, resource violations, and serious protocol violations MUST disable the plugin. Expected business failures such as cancellation, invalid input, or missing files return normally rather than imply process failure. Classification must use a host-controlled finite set, not allow plugins to relabel every exception as expected.

## 7. Warnings and Re-enablement

After a fault, the host MUST persist disablement by plugin id and record the failing version/digest. Restarting the app, installing a new version, or rescanning packages must not clear it; only explicit user action may retry. A new version may be announced as a possible fix but does not replace consent.

Each failed activation MUST display exactly one fault warning. Process exit, IPC loss, task failures, and cleanup exceptions merge into one fault record; diagnostics may accumulate without repeated popups. A new user-initiated attempt that fails must display one new warning. If the host has exited and cannot display it, persist a pending notification and show one recovery notice when the UI is next available, without automatically starting the plugin.

Warnings are nonmodal and preserve the ability to operate the serial workflow. They show name/version, reason, stopped or unconfirmed-exit status, disabled status, advice to investigate/fix before enabling again, and a details entry. Simultaneous failures of different plugins may be aggregated without omitting entries.

A "start with all plugins disabled" entry must take effect before plugin startup and include BuiltIn packages. Abnormal-exit records indicate suspicion, not precise attribution; corrupted records trigger conservative recovery. Never automatically delete packages or user data.

## 8. Tool-Wide Resource Budget

The user's "1 GB" applies to the entire tool, not each plugin. This draft makes the default explicit as **1 GB = 1,000,000,000 bytes**. The final UI must display accurate units without mixing GB and GiB or increasing the budget through unit conversion.

The primary metric is the sum of Windows private committed bytes for every managed process: host, all workers, and managed helpers. It is not GC heap size or resident physical memory; simply summing working sets does not reliably account for shared pages. Observe system available memory and working sets separately without conflating these metrics.

The proposed warning threshold is 80% of the total budget, configurable but below the maximum. On warning, pause new plugin activations/large tasks and collect per-process usage, growth, queues, in-flight work, and recent operations; constrain or discard plugin-derived caches and side-channel work. Do not dump heaps or run lengthy GC work on the main thread.

At the maximum, or when reserved headroom is about to be exhausted, enter emergency control: first terminate plugins violating quotas, growing abnormally, or already unresponsive. If no definite offender exists, a deterministic resource policy may stop high-usage plugins to protect the core, reporting "disabled to protect the total budget," not claiming a bug. Handle multiple offenders using evidence without restart loops.

Reserve a stress-tested core budget and safety margin, distributing only the remainder to plugins; plugin quotas must not sum beyond that pool. Report grants in the handshake. Use OS-level allocation limits and supervision, not sampling alone. CPU, process count, IPC, disk output, and UI refresh require limits too; plugins cannot lift them.

**This draft does not prove that instantaneous tool-wide memory can never exceed 1 GB.** Host growth, kernel buffering, and sampling latency require validation. Do not place the core in a kill-all-on-limit process group to fake this guarantee. If the core remains over budget after all plugins stop, report host pressure, constrain noncore caches/new work, and prioritize reception/logging without inventing plugin blame. If legitimate core workloads cannot fit the budget, block release and change the implementation or explicitly revise the budget; do not silently claim compliance.

## 9. Deadlines and Slow Plugins

| Operation | Proposed default policy |
|---|---|
| Startup and handshake | 10 seconds; disable on expiry. |
| Activation | 10 seconds; rollback and disable on expiry. |
| Ordinary short call | 2 seconds; disable on expiry, no automatic retry. |
| Heartbeat | Every 2 seconds; 3 consecutive misses trigger unresponsive detection. |
| Long-task progress | First report within 5 seconds, then at most 10 seconds apart; not unlimited renewal. |
| Normal stop | At most 3 seconds, then terminate the process tree. |

Long work such as log packaging must return a task handle first, with cancellation, progress, maximum input/output, and a host-approved absolute deadline. The deadline depends on operation type and known workload and is disclosed before work starts. Expiry is a fault even while heartbeats continue. Progress alone does not prove useful work; the host also checks processed bytes, backlog trends, and resource use.

For real-time observers that keep falling behind or dropping data, evaluate a bounded window and stop the plugin after published backlog/loss thresholds are exceeded. A single transient dropped block is not itself a plugin fault. Policies may evolve with the host but remain bounded and expose actual values to plugins.

## 10. Capabilities, Permissions, and Host APIs

Capabilities identify registration points; permissions authorize broker resources. Validate them separately. Version 1 treats all manifest entries as required: if a required permission is declined, do not activate. Optional permissions require a new schema or explicit extension, not a silent semantic change.

| Capability | Version 1 behavior |
|---|---|
| `menu` | Host menu descriptions and command ids. |
| `tool-page` | Host-rendered tool page descriptions. |
| `settings-panel` | Host-rendered fields, validation, and submit commands. |
| `background-image` | Safe image asset tokens, opacity, and switching requests; not arbitrary overlays. |

| Permission | Broker resource |
|---|---|
| `storage.own` | Private plugin configuration and quota-limited storage. |
| `serial.read` | Receive side-channel subscriptions and read-only session information. |
| `serial.logs.read` | Bounded log snapshots and chunked reads; does not require serial.read. |
| `files.user-selected.read` | Scoped file/directory read tokens obtained through user selection. |
| `files.user-selected.write` | Transactional output tokens for user-selected destinations. |

Version 1 exposes no serial transmission/control, network access, ReceiveTransform, SendTransform, arbitrary window embedding, or direct plugin-to-plugin calls. Reserved names are not implemented capabilities; unsupported requests must be rejected.

System pickers are host-owned, require a user gesture or explicit operation, and are rate-limited/cancellable. They return tokens with scope, expiry, and operation restrictions, not unrestricted host objects. Denied authorization returns `PermissionDenied`; normal cancellation returns `Cancelled`. Every handle use checks permissions and activation identity; stop invalidates it immediately. `files.pickWrite` supports two modes: save-file (default) and output-directory (`mode: "directory"`); the directory mode returns a directory token, and the host records that directory as the plugin's authorized output location for later dialog-free target creation (§13).

Suggested stable resource error codes are `InvalidArgument`, `PermissionDenied`, `Cancelled`, `NotFound`, `ResourceLimit`, `DeadlineExceeded`, `UnsupportedOperation`, `SessionExpired`, and `InternalError`. User messages may be localized; developers must not parse message text to determine status.

## 11. UI Contributions and Localization

The host MUST NOT load plugin UserControls, arbitrary XAML, scripts, binding expressions, executable converters, or plugin DLLs. Version 1 supports bounded text fields, labels, buttons, checkboxes, selects, sliders, lists, progress, images, and layout descriptions; control types and DTO schemas freeze before SDK release. The slider node (`slider`) participates in form collection by `fieldId` and carries minimum, maximum, and step; the host owns rendering and value round-tripping. Complex independent plugin windows are outside version 1.

Validate all UI updates before batching onto the host UI thread. Limit node counts, depth, decoded image dimensions, list length, refresh rate, and work per update. Declarative UI can still cause expensive layout or decoding and is not exempt from budgets. High-risk decoding should run in restricted helpers counted in the tool-wide budget.

Contributions have stable `contributionId` values, unique within a plugin; cross-plugin identity is `(pluginId, contributionId)`. Default ordering is `(order, pluginId, contributionId)` with order 100; persist user ordering by stable identity. Background images are exclusive in v1: user selection takes priority, otherwise use ordering. When the winner stops, choose the next already active eligible contribution; do not activate a disabled plugin automatically.

A plugin holding the `tool-page` capability may call `ui.notify(title, message, revealPath?)` for a one-shot result notice: the host shows a modal dialog owned by that plugin's tool window and, when `revealPath` is non-empty, reveals the file in Explorer after dismissal. Title, message, and path lengths are bounded. A display failure returns `InternalError` without retry or queueing and must not replace the fault-notice channel (§7).

Localization uses bounded UTF-8 string maps and falls back through UI culture, parent culture, plugin defaultCulture, and DTO/manifest fallback text. Translations execute no code and do not automatically load external URLs. Missing keys produce diagnostics, not `@key` as the final readable name.

## 12. Serial Side Channels and Log Snapshots

Core reception, transmission, and persistence MUST NOT wait for plugins. Primary logs retain existing original semantics; plugins cannot replace, filter, or delay them. Existing internal synchronous taps cannot directly serve as third-party entry points.

The host delivers bounded copies on the receive side channel, never pooled core buffers across the boundary. Each block includes session-instance identity, subscription identity, sequence, byte offset, length, and timestamp. Time base, reopening, and counter-reset semantics must freeze with DTOs. IPC uses bounded encoded chunks and counts every copy/queue against the resource budget.

By default, a full queue drops the oldest unsent observation block to preserve freshness and reports gap ranges plus cumulative losses. Retained blocks are ordered within a subscription, with no global ordering across subscriptions. Stop discards remaining blocks; gaps cannot be presented as contiguous data. Sustained lag triggers §9 disablement.

Plugins needing complete data read log snapshots. Snapshot requests flush at a defined position in the host log writer queue and capture length boundaries without pausing serial reception; the host returns unforgeable managed tokens and segment metadata. Chunked reads stop at snapshot lengths and cannot include later appends.

Specify snapshot retention, byte/handle quotas, rotation, deletion, and cancellation. Unavailable content produces an explicit error or expired-snapshot result, not an apparently complete truncated output. Log and real-time services are authorized separately; packaging does not require `serial.read`.

## 13. Storage and External Side Effects

Configuration is isolated by plugin id with host-managed atomic writes and size limits. Plugins define schemaVersion; migration failure preserves the original. Uninstall does not delete user data by default; deletion requires separate consent. Plugins cannot directly open global host configuration.

Outputs such as archives are written to host-managed temporary results. The current DPP/1 draft uses `output.begin`, strictly ordered `output.write(token, offset, base64Chunk)`, `output.commit(token, targetToken, commitId)`, `output.commitStatus(commitId)`, and `output.discard`: begin returns only an opaque token, total quota, and chunk limit, and **must not return a writable path**; chunks append continuously from offset 0, while duplicate, out-of-order, empty, oversized, or over-quota chunks are rejected before writing. The host exclusively owns output and snapshot staging; workers use only authorized chunk reads/writes. Commit has Preparing/Committing/Committed/Aborted phases: lengthy copy and flush work occurs outside the revocation lock, while the final same-volume rename is the short publication linearization point, and publication succeeds at most once. Output, snapshots, and destination-side publication staging share a cumulative per-plugin disk-staging limit and a separate host-process-wide staging disk limit; growth or copying reserves atomically before I/O and releases only after actual deletion. The shared host limit covers one host process only and is not advertised as a cross-process total when multiple hosts share a directory. It is also separate from the tool-wide 1,000,000,000-byte memory budget. Cross-volume publication performs a bounded copy to a ledger-registered file on the destination volume, keeps the verified staging object open, and renames that exact handle rather than reopening a path after close. Replacement authorization is path-scoped, not bound to the file identity observed at selection time. Only explicit user approval authorizes replacement at that path; without it the host uses atomic create-only publication, so a target appearing later causes failure and remains intact. Resource intent is durably recorded before creating any file. Failed deletion retains the ledger record and matching quota for online or later-start retry. Recovery cleans only registered resources whose owning host is confirmed gone, never guesses by recursive scanning or deletes resources of another live host.

There is no universal rollback guarantee for external effects. Each writable service defines its commit point and post-failure state. If commit completed before worker exit, the host retains a queryable final result. Version 1 never automatically replays writes.

Commit target tokens (`targetToken`) come from two sources: a single file chosen through the `files.pickWrite` save dialog, or an automatically named target created by `files.createWriteTarget(directory?, suggestName)`. The latter is dialog-free: an empty `directory` means the host-managed log directory (the default output location for built-in semantics such as packaging), while a non-empty one must resolve to a directory this plugin was granted through a picker and that the host remembered; resolution failure returns `PermissionDenied`. The host sanitizes illegal characters in the suggested name, disambiguates collisions with two-digit numeric suffixes, and follows the create-only publication rules above. `files.hostPaths` returns host paths such as the log directory to plugins holding the write permission; this information is for display and messaging only and grants no file access.

## 14. Installation, Updates, and Restart

Installation/updates require consent and basic package validation, not a signature-approval platform at this stage. Write new packages to separate version directories, validate them, and mark them pending; never overwrite executing DLLs or assets. New permissions require renewed consent.

The plugin manager provides "Apply and Restart," lists pending updates, and warns that serial sessions will close and send jobs will stop. Users may cancel; restart is not automatic. Plugin dependency graphs, hot replacement, and clearing fault disablement after updates are unsupported.

Apply sequence: stop admitting new work, stop sending and close serial ports according to host policy, flush logs/save settings, stop workers within limits, persist the pending version switch, exit normally, then switch and verify before plugin loading on the next startup.

Core cleanup failure must be reported and abort automatic restart or require explicit user handling. A plugin exit deadline must not force-kill the host. Power loss or switch failure must recover a complete old version; configuration migrations require separate backups, and DLL rollback alone does not prove recovery. Fault disablement survives updates.

## 15. Built-ins and Distribution

BuiltIn is a distribution-origin label, not a separate execution API. Background images and log packaging MUST use the same manifest, runner, IPC, permissions, budgets, disablement, and recovery. A shipping inventory may preapprove initial permissions for identified versions; arbitrary DLLs added to the install directory do not inherit trust.

Background images use `background-image`, `settings-panel`, `storage.own`, and `files.user-selected.read`, with host rendering and no main-window object access. Log packaging uses `menu`, an optional manifest-level selection of UI capabilities, `serial.logs.read`, `storage.own`, and `files.user-selected.write`; it does not access SessionViewModel, and its output target is resolved and named by the host — the host-managed log directory by default, or a directory the user authorized through the directory picker (§13) — with the plugin holding only opaque tokens throughout. "Optional selection" means the developer chooses whether to declare it; declared capabilities remain required.

Migration must retain background playback settings, log-output follow-directory behavior, and existing standalone preferences. The previous implementation has no established public execution ABI; do not invent an unnecessary legacy binary adapter.

Both installer and portable distributions must carry the runner, managed dependencies, and built-in packages. Shipping one EXE does not automatically include external packages. Removing BuiltIn packages must leave serial transmission/reception, display, logging, and basic settings working while backgrounds/packaging are correctly absent, not pretend that removed optional features still exist.

## 16. Developer Starting Example

This is a manifest design example for log packaging. Host version `0.1.0` is a placeholder; use the first actual DPP/1 host release. Runtime APIs/schemas are not published yet, and this example does not imply current DuCom can load the package.

```json
{
  "manifestVersion": 1,
  "id": "org.example.log-export",
  "name": "Log Export",
  "version": "1.0.0",
  "protocolVersion": "1.0",
  "minHostVersion": "0.1.0",
  "entryAssembly": "Example.LogExport.dll",
  "entryType": "Example.LogExport.Plugin",
  "runtime": { "framework": "net10.0", "rid": "win-x64" },
  "capabilities": ["menu"],
  "permissions": [
    "serial.logs.read", "storage.own", "files.user-selected.write"
  ],
  "defaultCulture": "en-US"
}
```

Expected workflow: choose a released SDK, declare minimal capabilities/permissions, register declarative menus/pages in the worker, request broker snapshots/output tokens, implement cancellation and bounded processing, explicitly install/enable locally, run §18 conformance tests, and submit the package/source for maintainer review.

GitHub branches, PRs, and main-branch merge rules belong to repository governance, not the wire protocol. Maintainer source review does not authenticate every subsequently installed local package; documentation and UI must preserve that distinction.

## 17. Diagnostics and Privacy

Diagnostics include plugin id/version/digest, activation identity, host version, fault stage, error code, deadline, resource samples, and termination outcome. Limit each plugin's log rate and message length, merge duplicates, and drop/count excess without blocking the core.

Do not log raw serial content, file bodies, IPC credentials, sensitive settings, or full user paths by default. Diagnostic export requires preview/redaction. Dumps may contain device data and credentials and must not be silently collected/uploaded on memory warning. Version 1 requires neither telemetry nor online reporting.

## 18. Release Gates and Unfrozen Details

| Test | Required result |
|---|---|
| Constructor/initialization/runtime crash and FailFast | Only the worker exits; core continues; persistent disablement and one warning. |
| Infinite loop, blocking, heartbeat with stuck business work | Bounded detection/termination; old instances cannot revive. |
| Multi-plugin memory, CPU, process, and message floods | Tool-wide control, evidence-based stopping, no core kill; validate private-commit accounting. |
| Malicious file/network/device/host-process access | OS restrictions actually deny access, not just manifest/log checks. |
| Malformed UI, oversized images, complex layout | Bounded rendering, restricted decoding, responsive main window. |
| Fast/slow consumers and sustained loss | No core reception/persistence wait, accurate gaps, persistent laggards stopped. |
| Log append, rotation, deletion, cancellation | Correct snapshot boundaries, explicit expiry, no false-success partial output. |
| Repeated errors, user retry, restart after update | Exactly one warning per failed activation; only user action clears disablement. |
| Old SDK binaries and version matrix | No recompilation of old plugins; explicit rejection paths. |
| Install-path attacks, interrupted update, migration failure | No out-of-root writes; complete packages and original settings recoverable. |
| Remove BuiltIn and start with all plugins disabled | Core remains usable; safe startup includes built-ins. |
| Installer and portable | Complete runner/packages and consistent restart switching. |

Stress results must include a no-plugin baseline, serial configuration/throughput, duration, CPU, memory, losses, and log integrity, not merely "the UI looks fine." Run dangerous plugins in terminable test environments.

Before DPP/1.0 freezes, deliver machine-validatable manifest/IPC/UI JSON Schemas, the complete operation/DTO catalog, versioned errors, Windows restriction profiles and compatibility matrix, measured memory/CPU quotas and deadlines, SDK APIs/minimal examples, old-binary compatibility tests, installation/recovery vectors, and bilingual consistency checks.

These are implementation/release gates, not product-direction questions awaiting the user again. Until complete, this remains a draft without a compatibility certification label.

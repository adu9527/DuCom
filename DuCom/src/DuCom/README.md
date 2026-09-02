# DuCom WPF Application

## Responsibility

Own the WPF composition root, localized Fluent workspace, application ViewModels, and frame-cadenced projection of Core session snapshots.

## Thread Model

Visible session snapshots are pulled from `CompositionTarget.Rendering` with a 144 Hz upper bound. Each frame processes only incremental line-store ranges; views and ViewModels never receive one Dispatcher operation per serial packet and do not reference receive-pipeline, Channel, or pooled-buffer implementations.

## M1 Status

The main log uses a bounded, read-only AvalonEdit projection over the frame-pulled `SerialSession` snapshots. Native editor selection, copy, caret, and search are available without moving serial, Channel, pooled-buffer, or logging ownership into the view. While following the end, the main display retains only the newest 10,000 segments or 1 MiB of text; older display content is trimmed without affecting session log files. Pausing follow keeps updating and retains the paused display without these two UI limits until the user resumes or clears it; crossing the configured private-memory threshold produces one warning per clear cycle.

## Current Command Dock

The bottom command dock intentionally contains only:

- Connect / disconnect.
- Clear display.
- Follow-end scrolling.
- Open log folder.
- Baud rate.
- Serial settings.
- Send.

Data bits, stop bits, parity, flow control, encoding, receive STR/HEX, timestamps, automatic logging, log directory, send STR/HEX, and newline policy are consolidated into the serial-settings panel.

## Default Configuration

- 1152000 baud, 8N1.
- No parity or flow control.
- UTF-8.
- Receive STR mode.
- Timestamps enabled.
- Automatic UTF-8 `.txt` logging enabled.
- 40 MB rotation.
- Log directory `%LocalAppData%\DuCom\SessionLogs`.

Transport/receive/log settings are captured when a session is created and are disabled while it is open. Close and reopen the port to apply changed settings.

## Theme-Adaptive Design Tokens

DuCom color tokens (`Brush.*` in `Resources/DesignTokens.Colors.Dark.xaml` / `.Light.xaml`) are swapped by `App` whenever `ApplicationThemeManager.Changed` fires, so System/Light/Dark all restyle the shell, panels, log surface, and status colors. The theme-independent sizes, spacing, radii, and keyed styles stay in `Resources/DesignTokens.xaml`. Keep every `Brush.*` consumer on `DynamicResource`; keyed button styles must stay `BasedOn` the WPF UI implicit styles so the Fluent control templates (hover/pressed/focus/disabled) are preserved. The connect and send primary actions use `ui:Button Appearance="Primary"`; a normal disconnect is never styled as persistent danger red.

## Workspace Layout

- Every discovered serial port is shown directly in the left list; port selection is not hidden in a ComboBox.
- The left header contains three compact tools modeled on the reference behavior: refresh, show/hide hidden ports, and ascending/descending name sort. Port rows expose hide/restore through their context menu; hidden state is currently runtime-only until the settings persistence schema is frozen.
- The log region and send editor are separated by a vertical `GridSplitter`, so send height is user-adjustable and ready for later split-view work.
- Incremental receive segments with the same logical-line ID are merged in the UI up to 4096 characters, preventing short blocks from appearing as unrelated rows.
- The compact command dock keeps only the seven approved entries.

File, Edit, Tools, and About now live inside the Fluent title-bar header at the same visual level as the system window buttons. There is no separate application-menu row, preserving vertical log space and maintaining high-contrast menu text in both themes.

## Top Menus

The original DuCom top menu aligns the reference behavior categories without copying its implementation:

- File: load log, save visible log, import/export configuration, application folder, exit.
- Edit: copy visible log, clear display, follow-end toggle.
- Tools: serial settings, log folder, diagnostic folder, documentation, and explicit future-module entries for VirtualPort/Telnet.
- About: DuCom information, feedback, and help.

The About window displays DuCom, `V0001`, release date `2026年8月27日 09:31:26`, author `du`, and a one-second live clock.

The main log workspace now reserves the dominant area: the sidebar is 208 px and can be hidden, outer margins/panel padding are compact, and the send editor defaults to 88 px and remains vertically resizable.

Split view is intentionally left/right only. Drag a port row or an existing session tab into the right half of the log workspace to open the right pane automatically. An unopened port is opened with the current settings first; an existing session is reused. Both panes project the same Core session snapshots and never create an additional receive-block or log consumer. Close the right pane with its `×` button to return to a single pane.

## Reference Tools Alignment

The reference project's Tools menu has been aligned with original DuCom implementations. The desktop-pet settings remain explicitly excluded by project scope.

- Settings: opens DuCom's consolidated serial/settings panel.
- Theme/skin: system, light, dark, plus Mica, Acrylic, and solid backdrops.
- Sidebar: shows or hides the complete port list.
- Shortcuts: editable, searchable shortcut page with conflict detection, per-action restore, restore-all, and JSON persistence to `%LocalAppData%\DuCom\shortcuts.json`.
- Plugins: manages the local `%LocalAppData%\DuCom\Plugins` directory and inspects .NET assembly names/versions. Executing third-party DLLs is prohibited by the pending security model.
- Runtime monitor: live CPU, working set, private memory, GC, and thread count, plus variable-monitor rules (regex first-capture-group extraction from display snapshots, live value grid, CSV export; JSON persistence to `monitor-rules.json`).
- Private-memory threshold warning: application-owned ten-second private-memory sampling with a persisted enable switch and MiB threshold (1024 MiB reference default, disabled by default). Tool-center windows only observe its state; closing them does not stop the service. Threshold crossings show a localized warning and diagnostic entry but never terminate the process. This remains independent of the content Watchdog.
- Virtual port: detects com0com and manages port pairs through `setupc.exe` (list/install/remove/change with EmuBR/EmuOverrun/HiddenMode/PlugInMode/ExclusiveMode/EmuNoise/RTTO/RITO). A browsed/manual `setupc.exe` path is persisted and used when standard-location discovery fails. Output is captured; verbs remain whitelisted and there is no implicit elevation.
- ASCII table: complete 0-127 decimal/HEX/character reference.
- Reference documents: opens DuCom's local architecture/testing/reference documentation.
- Telnet service + serial bridge: configurable local TCP listener with start/stop, client list, and a bidirectional bridge — serial RX lines are pushed to clients from display snapshots and client input is sent through the bound session (recorded as TX). Slow clients are dropped after a bounded wait.
- Watchdog: rules expecting a pattern within a window; timeout fires hint / diagnostic-log / send-command actions with throttling, evaluated on a one-second snapshot pump (never in receive callbacks). JSON persistence to `watchdog-rules.json`.
- Command groups and send history pages.
- Advanced command groups expose a persisted multi-select target list covering current ports and sessions. Every group loop refreshes the selected open-session snapshot, orders targets by port name, and runs targets independently; result checks use a separate receive tail per port, while one port's send error or timeout remains visible without stopping the others.

## 2026-08-28 additions (GLM long task)

- Edit menu in the title bar: copy visible log, clear, follow toggle, FormatJson/JoinLines, clipboard text↔HEX and timestamp→local-time transforms, display options (word wrap, line numbers, current-line highlight, CR/LF, spaces, tabs, font size), all persisted in settings.json.
- File menu: save visible log as text / HEX text / binary, all written on background threads from the visible-line snapshot; DuCom JSON configuration import and export remain available.
- Per-port mini log windows: one window per port (Core `PortWindowRegistry`), fixed session binding, self-close on port close, independent follow toggle, and per-port geometry/topmost/follow/send-mode/newline persistence. Mini windows use isolated display taps and do not add receive-block or logging consumers.
- Sustained split-view rendering keeps the 144 Hz pull ceiling while batching each session's visible-line mutations into one notification, incrementally tracking the display budget, evaluating filter/highlight rules once per segment, publishing search snapshots only for an active search target, and scheduling AvalonEdit synchronization below input priority.
- `StyledRunsTextBlock` renders Core style runs as one TextBlock's inlines so word wrap and invisible-character substitution compose with ANSI/highlight styling in both panes and the mini windows.
- Serial driver warnings (Frame/RXOver/Overrun/RXParity/TXFull) surface as localized warnings; send failures surface as localized status messages.

## Shortcuts

Shortcut configuration lives in `Services/Shortcuts/` and is split between a pure-logic manager (linked into `DuCom.Core.Tests`) and a WPF input engine.

- `ShortcutManager` owns the action registry, conflict detection, default reset, and JSON persistence.
- `ShortcutEngine` handles `MainWindow.PreviewKeyDown`, maps WPF keys to the manager, and executes the whitelist of `MainViewModel` commands.
- `ToolCenterViewModel` exposes a searchable list, an edit popup that captures key combinations, and per-action / reset-all commands.
- Default bindings include `Ctrl+Enter` (open/close), `F5` (refresh), `Ctrl+L` (clear), `Ctrl+S` (save visible log), `Ctrl+P` (toggle follow), `Ctrl+B` (sidebar), `Ctrl+T` (tools), `F11` (maximize), `Ctrl+D` (focus send), and `Ctrl+Shift+W` (close right pane).

Invalid `shortcuts.json` falls back to defaults and logs a warning to the diagnostic log. Conflicts cannot be saved.

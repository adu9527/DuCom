# DuCom User Manual

[中文](UserManual.zh-CN.md) · [Developer Guide](DeveloperGuide.en-US.md) · [Web version](Web/user-manual-en.html) · [GitHub](https://github.com/adu9527/DuCom)

> Platform: Windows 10 or later
> Updated: 2026-11-04

## 1. What DuCom is

A Windows serial tool for embedded debugging and Bluetooth audio development.

It opens several serial ports at once, writes logs to disk, highlights and filters output by rule, and runs saved command sequences across multiple ports. Plugins cover narrower jobs: firmware checksums, CRC calculation, earbud production testing, and speech synthesis.

## 2. Install and start

The Releases page offers three packages:

| File | Description | Size |
| --- | --- | --- |
| `DuCom-win-Setup.exe` | Installer, adds Start menu entries, uses the installer update channel | about 130 MB |
| `DuCom.exe` | Full portable build, single file, bundles the .NET runtime | about 79 MB |
| `DuCom-lite.exe` | Light portable build, single file, uses the .NET 10 runtime already on the system | about 17 MB |

Sizes change between releases. Check the Releases page.

The portable builds need no installation. Put the EXE in a writable folder and run it. Do not run it from inside a zip archive, or logs and settings may fail to save.

The lite build requires the .NET 10 Desktop Runtime. If it is missing, Windows will prompt you to install it. Use the full build if you would rather not deal with that.

Windows may show a security warning on first launch. Confirm the file came from this repository's Releases before running it.

A cover page appears at startup. You can turn it off in Settings → Appearance, or keep the page and disable only the animation.

## 3. Connect a port

1. Connect the USB-serial adapter or development board.
2. Click refresh in the port sidebar.
3. Select the port.
4. Check baud rate, data bits, stop bits, parity, flow control, and encoding under Serial parameters.
5. Click Connect, or press `Ctrl+Enter`.

Each port gets its own session. Parameters, logs, and send history stay separate.

`115200 / 8 / 1 / None` is a common default. Use whatever the firmware specifies. If another program holds the port, close it first.

When a port fails to open, the session shows the reason and offers a retry.

## 4. Main window

- **Menu bar**: File, Edit, View, Tools, Plugins, About.
- **Port sidebar**: refresh, sort, filter, hide ports, open and close.
- **Session area**: one tab per port, with horizontal or vertical split.
- **Log area**: received and sent data, with search, highlight, filter, follow, and save.
- **Send area**: STR/HEX, line ending, escape handling, history, timed send.
- **Status bar**: connection state, byte counters, errors.

Hide the sidebar with View → Sidebar or `Ctrl+B` when you need the width for logs.

The title bar shows current memory use. It can be turned off in Settings.

## 5. Serial parameters

DuCom supports baud rate, data bits, stop bits, parity, flow control, encoding, DTR, RTS, and discarding NUL bytes. Baud rates can be typed in, and custom values are remembered.

Some receive settings are fixed when the port opens. Close and reopen the session after changing them.

With auto-reconnect enabled, DuCom tries to reopen the same port after an unexpected unplug. A manual disconnect does not trigger reconnection.

## 6. Receiving and display

### 6.1 STR and HEX

STR mode decodes text with the selected encoding: UTF-8, ASCII, GB2312, GBK, and others. HEX mode shows raw bytes, which suits binary protocols.

STR mode renders common ANSI/VT sequences, including color, bold, underline, reverse video, 256-color, and RGB.

### 6.2 Timestamps and display options

Timestamp format is configurable; `[HH:mm:ss.fff]` is typical. You can also enable line numbers, word wrap, current-line highlight, and visible markers for CR/LF, spaces, and tabs.

Change font size under View → Font size. Change the log font under Settings → Appearance.

Pausing follow only stops scrolling. Receiving and disk logging continue.

### 6.3 Automatic logging and rotation

With auto-save on, received data is written to disk asynchronously. You can set:

- Log directory
- File name pattern, with `{Port}`, `{yyyy}`, `{MM}`, `{dd}`, `{HH}`, `{mm}`, `{ss}`, `{fff}`
- Whether to rotate by size, and the size limit per file

A live preview shows the resulting file name. Rotation is size-based only; it does not roll over on a timer.

### 6.4 Display budget

Each session has a display budget, 64 MB by default. When the log exceeds it, older content is dropped from the view. The file on disk keeps everything.

DuCom shows a notice when lines are evicted. This is not data loss on the serial side.

### 6.5 Save and open

File → Save log as writes the visible content as text, HEX, or binary.

File → Open log opens a log file with the system default program, read-only. It does not import the file into the current session. To analyze a log inside DuCom, use Tools → Log Analyzer.

## 7. Search, highlight, and filter

### 7.1 Search

Search supports plain text and regular expressions, case matching, whole-line matching, and next/previous navigation. Regex evaluation has a timeout, so a bad pattern cannot freeze the UI.

### 7.2 Highlight

Manage rules under Tools → Highlight & Filter. Matching is by substring or regex. A match can set foreground color, background color, bold, and italic.

Built-in templates cover common BES log levels: Error, Warning, Status, Fault, Audio, Address, Unit, and Version. Add your own rules, or import and export rule sets.

### 7.3 Filter

Filter rules decide which lines appear in the view. They do not affect receiving or file logging. Rules can be enabled per port.

## 8. Sending

### 8.1 STR send

Type text and choose None, CR, LF, or CRLF as the line ending.

With Parse escapes enabled, `\r`, `\n`, `\t`, `\\`, and `\xNN` are translated.

### 8.2 HEX send

Enter complete byte pairs, for example `AA 55 01 0D 0A`. Spaces are optional.

Incomplete bytes or invalid characters are not sent. HEX mode does not process STR escapes.

### 8.3 Send prefix in the log

A prefix can be added to sent lines in the log. The default is `TX > `.

It affects display only and is never written to the serial port. Turn it off in Settings if you do not want it.

### 8.4 Send history

Sent content is recorded and de-duplicated. Use the up and down arrows in the input box, or open Tools → Send History to search, delete, clear, or reuse an entry.

### 8.5 Timed send

Send the current content repeatedly at a fixed interval. The minimum is 50 ms.

Check that the device can handle the rate before starting. Flooding firmware with commands can hang it.

### 8.6 Send from file

Load a text file as the send payload. Useful for long scripts or batch commands.

## 9. Command groups

Open Tools → Command Groups.

A group holds an ordered list of commands. Each command sets:

- Payload (STR or HEX)
- Delay after sending
- Reply timeout
- Expected reply

At run time, select one or more target ports, for example both earbuds. Each command shows its state: sending, waiting for reply, passed, timed out, or send failed.

A failure on one port does not stop the others. Stopping a run marks the current round as aborted.

Groups can be created, renamed, deleted, imported, and exported. An exported group file can be shared directly.

## 10. Multiple ports, split view, and the floating window

Several ports can run at the same time. Each session keeps its own parameters and state.

Drag a session to the right edge to split the view horizontally, or switch to a vertical split. The split ratio is saved.

View → Floating Send opens a small always-on-top window bound to the current port. It can stay on top, keep a fixed scroll position, clear, save, switch STR/HEX, and send commands. It keeps updating when the main window switches to another tab.

Position and size are stored per port.

## 11. Tool Center

Tools → Tool Center has seven tabs:

| Tool | Purpose |
| --- | --- |
| Monitor | CPU, memory, private memory, thread count; extract variables by regex and export them |
| Virtual ports | Detect com0com, create and remove virtual port pairs; some actions need administrator rights |
| ASCII table | Look up DEC, HEX, and ASCII |
| Reference | Open the repository's architecture, test, and developer docs |
| Telnet | Local Telnet service that can bridge client input to a serial port |
| WatchDog | Alert, log, or send a command when expected content does not arrive in time |
| Send history | Search, reuse, and clear everything sent |

Telnet listens on localhost by default. Remote listening exposes serial data; enable authentication and use it only on trusted networks.

## 12. Log Analyzer

Tools → Log Analyzer opens archived logs or attaches to a live session.

Filter by time, level, module, and keyword. Rules can be imported and exported, and results can be exported as a table.

For BES logs, the analyzer decodes startup flow, faults, Bluetooth HCI, and profile events. The analysis tree jumps straight to the relevant section, which is faster than scrolling through a log someone sent you.

## 13. Plugins

Plugins follow the DPP/1 protocol and run in separate processes. If one crashes, serial traffic in the main app keeps running.

Settings → Plugins lists installed plugins with version, capabilities, permissions, and status. From there you can enable, disable, or open a plugin page.

Seven plugins ship with the repository.

### 13.1 Firmware checksum

Drop a firmware file on the window, or press `Ctrl+O`. JieLi and BlueX are supported directly. Bestechnic (BES) needs a one-time setup following the integration guide, which is available offline from the plugin details page.

Results show the checksum, default Bluetooth name, chip, vendor, and build time. The status line distinguishes a fully verified image, a CRC-verified image, a checksum-only read, and an unrecognized image.

The window can stay on top, and the checksum can be copied in one click.

### 13.2 CRC calculator

Accepts HEX or ASCII input, or a file up to 64 MiB.

Twenty-one common CRC models are built in. Custom parameters support 1 to 32 bits, including polynomial, init, xor-out, and input/output reflection.

Results can be copied as HEX or binary. Press `Ctrl+Enter` to calculate; the window supports always-on-top.

### 13.3 Speech synthesis

Uses Microsoft online speech with 318 voices. Adjust rate, pitch, and volume, then preview before generating.

TXT and MD files can be imported. Long text is split into chunks with a progress display. MP3 audio and SRT subtitles can be exported separately.

Output goes to the current log directory by default, or a directory you choose. A network connection is required. Proxies follow the system settings by default and can be set separately for HTTP or SOCKS5.

### 13.4 Earbud production test

Connect the left and right earbuds to two serial ports. Two channels maximum.

The flow is: choose a profile, tick left and/or right and their COM ports, connect, start. Only ticked channels are tested. In loop mode, each round starts fresh.

Test items are defined in Profile Configuration:

- Send frame: header, length, command, CRC. Leave length empty to calculate it.
- Reply fields: added in protocol order. A MAC is 6 bytes; a command returning two MACs gets two rows.
- Timeout and post-send delay
- Enabled flag

The default header is `55 AA` with CRC8/MAXIM.

Calculate frame (do not send) builds the frame and CRC locally without opening the port, which is useful for checking the configuration.

Compound commands combine several sub-commands into one test item. Left/right roles and data references are supported, for example writing the MAC read from the left earbud to the right one.

Each round writes a `.log` and a `.csv`. The CSV uses UTF-8 BOM and opens directly in Excel. Statistics separate passed, failed, and aborted rounds; aborted rounds do not count as completed.

Profiles are JSON files under `profiles` in the program directory and can be imported or exported.

### 13.5 Background image

Draws an image behind the main window without affecting selection, copying, or split view.

Point it at a folder to rotate images, with adjustable opacity and interval.

### 13.6 Log package

The One-click Package button in the title bar collects a problem report into a zip.

Fill in project, tester, firmware version, reproduction rate, device name, description, and steps, then select the ports to include. System logs can be included too.

The archive uses a structured layout, with log copies named after the device. The output directory is configurable.

### 13.7 Stopwatch

High-resolution timing with lap, pause, and resume. Results export to CSV or Excel, which is handy for timing measurements.

## 14. Settings, appearance, and shortcuts

Settings has ten pages:

| Page | Contents |
| --- | --- |
| General | Cover page, close to tray, prevent sleep, configuration backup, private memory warning, scroll behavior |
| Serial parameters | Default baud rate, data bits, stop bits, parity, flow control, encoding, DTR/RTS |
| Receive & log | Receive mode, timestamps, auto-save, rotation, display budget, send recording |
| Highlight & filter | Rule editing, templates, import/export |
| Plugins | Installed plugins, details, enable/disable |
| Send | Line ending, escapes, send prefix, timed send defaults |
| Appearance | Light/dark, Chinese/English, log font, search panel opacity, cover page |
| Shortcuts | View, edit, conflict detection, restore defaults |
| Tool Center | Which tool tabs appear and in what order |
| Updates | Automatic checks and update channel |

Theme, language, and font changes apply immediately; no restart is needed.

Configuration is backed up on a schedule, every 7 days by default. Restore from a backup after a bad change or on a new machine.

Shortcuts come with a default set. Common ones:

| Shortcut | Action |
| --- | --- |
| `Ctrl+Enter` | Open or close the selected port |
| `F5` | Refresh the port list |
| `Ctrl+L` | Clear the log |
| `Ctrl+S` | Save the log |
| `Ctrl+F` | Search |
| `Ctrl+B` | Show or hide the sidebar |
| `Ctrl+T` | Open Tool Center |
| `Ctrl+D` | Focus the send box |
| `Ctrl+W` | Close the current session |
| `Ctrl+Shift+W` | Close the right split |
| `Alt+E` | Toggle HEX display |
| `Alt+D` | Toggle timestamps |

## 15. Updates

About → Check for updates queries GitHub for the latest release.

Each package has its own update channel: the installer updates through the installer, while the full and lite portables download a replacement single file. Automatic checks can be enabled in Settings.

## 16. Troubleshooting

### No ports listed

Check the driver, confirm the cable supports data, then refresh. Also check whether the port was hidden and use Restore hidden ports if needed.

### Port will not open

Usually the port is in use, permissions are insufficient, or the parameters are invalid. Close other serial software, reconnect the device, and try again.

### Garbled text

Check baud rate and encoding. Chinese firmware often uses GBK/GB2312; newer firmware usually uses UTF-8.

### HEX send does nothing

Each byte needs two hex digits, for example `01 0A FF`. DuCom refuses to send when invalid characters are present.

### Scrolling stopped. Is logging still running?

Yes. Pausing follow only changes the view position. Receiving and file logging continue.

### Older lines disappeared

The display budget was reached. Old lines are dropped from the view, not from the file on disk. Raise the budget or open the log file to see everything.

### Telnet cannot connect

Check listen address, port, firewall, and authentication. Remote mode requires authentication.

### A plugin will not open

Check its status under Settings → Plugins. Disabled plugins, version mismatches, and unapproved permissions prevent startup, and the details panel shows the reason.

## 17. Support

- QQ group: `1107820408`
- GitHub: https://github.com/adu9527/DuCom
- Issues: https://github.com/adu9527/DuCom/issues

Include the DuCom version, Windows version, serial chip and driver, port settings, reproduction steps, and relevant logs.

Logs may contain device keys, accounts, or identity data. Remove them before posting.

## 18. Security notes

- Download only from the official GitHub Releases.
- Be careful with commands that erase firmware, rewrite flash, or change security state.
- Enable remote Telnet only on trusted networks.
- Sanitize logs before sharing.
- DuCom is licensed under GPL-3.0. See the repository license file.

# DuCom User Manual

[中文](UserManual.zh-CN.md) · [Developer Guide](DeveloperGuide.en-US.md) · [Web version](Web/user-manual-en.html) · [GitHub](https://github.com/adu9527/DuCom)

> Platform: Windows 10 or later  
> Updated: 2026-09-02

## 1. Overview

DuCom is a Windows serial communication and debugging tool designed for embedded development, high-throughput device logs, and multi-port workflows. It provides independent serial sessions, STR/HEX communication, automatic logging, search and filtering, command groups, split views, a Telnet bridge, virtual-port tools, and runtime monitoring.

## 2. Install and Start

Each GitHub Release provides a single `DuCom.exe` of approximately 78 MB (the size may vary slightly between versions). The executable is self-contained and includes the required .NET runtime and dependencies. There is no installer and no separate runtime package is required.

1. Open [GitHub Releases](https://github.com/adu9527/DuCom/releases).
2. Download the latest `DuCom.exe`.
3. Put it in a permanent folder where you have write permission.
4. Double-click `DuCom.exe`.

If Windows displays a security warning, verify that the file came from the official DuCom GitHub repository before running it. Do not run the executable directly inside an archive.

## 3. Quick Serial Connection

1. Connect the USB-to-serial adapter or development board.
2. Refresh the port list in the left sidebar.
3. Select the target COM port.
4. Review baud rate, data bits, stop bits, parity, flow control, and encoding.
5. Connect. DuCom creates an independent session tab for the port.
6. Device output appears in the log area; enter commands in the send area.

A common setup is `115200 / 8 / 1 / None`, but the values must match the firmware. If another program owns the port, close that program first.

## 4. Main Window

- **Top menu:** File, View, Tools, and About.
- **Port sidebar:** refresh, visibility, sorting, connection, and device details.
- **Session tabs:** independent connection, display, send, and logging settings per port.
- **Log area:** received/transmitted records, follow, pause, clear, search, and save.
- **Send area:** STR/HEX, line ending, escape decoding, file send, and timed send.
- **Status area:** connection state, serial warnings, and operation results.

## 5. Serial Parameters

DuCom supports baud rate, data bits, stop bits, parity, flow control, encoding, DTR, RTS, and optional NUL-byte discard. Custom baud rates can be added. Some receive/display values are captured when a session is created, so close and reopen the port after changing them.

Auto reconnect waits for the same device after an unexpected removal. A manual disconnect does not trigger reconnection.

## 6. Receive Display and Logging

### 6.1 STR / HEX

- **STR:** decoded text using UTF-8, ASCII, GB2312, GBK, or another configured encoding.
- **HEX:** hexadecimal byte display for binary protocols.
- **ANSI/VT:** STR mode supports common colors, bold, underline, reverse video, 256-color, and RGB sequences.

### 6.2 Timestamps and Display

Configure timestamp format, word wrap, line numbers, current-line highlight, CR/LF markers, spaces, tabs, font family, and font size. Pausing auto-scroll does not stop reception or file logging.

### 6.3 Automatic Logs

When automatic logging is enabled, received data is written asynchronously. Configure the directory, filename pattern, rotation, and segment size. The display uses a memory budget and may trim old visible lines, while data already written to disk remains intact.

### 6.4 Save and Open

The File menu saves the visible snapshot as text, hexadecimal, or binary. Opening an existing log launches the system-associated viewer in read-only fashion; it does not import the file into an active serial session.

## 7. Search, Highlight, and Filter

- Session search supports plain text, regular expressions, case sensitivity, and previous/next navigation.
- Highlight rules support Contains/Regex, foreground/background colors, bold, and italic.
- Filtering can be enabled independently per port.
- Regular-expression timeouts protect the UI from expensive patterns.
- Rule projects can be saved, imported, and exported.

## 8. Sending Data

### 8.1 STR Mode

Send normal text with None, CR, LF, or CRLF line endings. Escape decoding supports `\r`, `\n`, `\t`, `\\`, and `\xNN`.

### 8.2 HEX Mode

Enter complete byte pairs, for example `AA 55 01 0D 0A`. HEX mode does not decode STR escapes. Invalid or incomplete bytes are rejected.

### 8.3 History and Timed Send

Send history is deduplicated and persisted. Use Up/Down in the editor, or use Tool Center to search, delete, clear, or restore entries. The minimum timed-send interval is 50 ms; verify that the target can handle the selected rate.

### 8.4 Files and Command Groups

Text files can be loaded as send content. Advanced command groups support CRUD, import/export, delay, expected-result checks, timeout, loop execution, and multiple target ports. A failure on one port does not automatically stop other ports.

## 9. Multiple Sessions, Split View, and Floating Windows

Multiple ports can run concurrently with independent state. Drag a session into the secondary pane for horizontal or vertical two-pane layouts, resize the split, and persist the layout. Quad view is not available in the current version.

A floating send/mini-log window remains bound to one port and can stay on top, clear, save, switch STR/HEX, and send commands even when another main tab is selected.

## 10. Tool Center

- **Virtual ports:** detect com0com and manage pairs through `setupc.exe`; some operations require elevation.
- **Telnet:** run a local shell and optionally bridge client input with a serial session. Remote listening exposes serial data and therefore requires authentication and a trusted network.
- **WatchDog:** alert, log, or send a command when expected content is absent for a configured period.
- **Runtime/variable monitor:** CPU, memory, GC, threads, regex-captured values, and CSV export. Charts are not implemented.
- **ASCII table:** DEC, HEX, and ASCII lookup.
- **Plugins:** the background-image feature is available; external DLLs are discovered for metadata only and are not executed.
- **Compatible data import:** discover and migrate supported settings from another serial tool without modifying the source data.

## 11. Settings, Themes, and Shortcuts

Light/dark themes and Simplified Chinese/English can be switched immediately. General settings, per-port overrides, session layout, hidden ports, history, command groups, and rules are persisted.

Shortcut management supports search, edit, conflict detection, clearing, and default restoration for connection, refresh, clear, save, follow, sidebar, tools, search, and send actions.

## 12. Troubleshooting

### Port not found

Install the correct driver, use a data-capable cable, refresh, check hidden-port filters, and restore hidden ports.

### Port cannot open

Close other serial software, reconnect the device, verify permissions, and review the serial parameters.

### Garbled text

Match both baud rate and encoding. UTF-8 is common; some Chinese firmware uses GBK or GB2312.

### HEX send rejected

Every byte requires two hexadecimal characters, optionally separated by spaces, such as `01 0A FF`.

### Does pausing the display stop logging?

No. Pausing follow/scroll changes the view only. Reception and automatic file logging continue.

### Remote Telnet cannot connect

Check remote-listen settings, address, firewall, and port. Authentication is mandatory for remote listening. Never expose it to an untrusted network.

## 13. Feedback and Support

- QQ group: `1107820408`
- GitHub: [https://github.com/adu9527/DuCom](https://github.com/adu9527/DuCom)
- Issues: [https://github.com/adu9527/DuCom/issues](https://github.com/adu9527/DuCom/issues)

Include the DuCom version, Windows version, serial chipset/driver, settings, reproduction steps, and relevant logs. Remove credentials, device identities, and other sensitive information first.

## 14. Safety

- Download releases from the official GitHub repository.
- Review commands that can erase firmware, write Flash, or change device security state.
- Enable remote Telnet only on a trusted network.
- Sanitize logs before sharing.
- DuCom is licensed under GPL-3.0; see the repository license file.

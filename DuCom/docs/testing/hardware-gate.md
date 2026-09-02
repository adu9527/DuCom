# DuCom Real Hardware Gate

This gate records real serial usage locally. It does not upload telemetry or device data.

Run from the `DuCom` directory:

```powershell
.\scripts\run-hardware-gate.ps1 -Port COM7 -BaudRate 1152000 -DurationMinutes 10 -Device FTDI
```

The script records:

- Port and baud rate.
- Device label supplied by the user.
- Requested and actual duration.
- User pass/fail/observation result.
- User-observed faults or loss.
- Latest DuCom system diagnostic log path.
- Matching session log paths and sizes.

Reports are written under `reports/hardware/` as JSON and Markdown. They are local runtime evidence and should not be committed by default.

The gate supplements daily dogfood usage. It does not replace user judgment and does not transmit private serial content.

The application system diagnostic log used by the report is stored beside DuCom under:

```text
Logs\System_log\ducom-yyyyMMdd-HHmmss-processId.log
```

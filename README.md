# ViLogger

[![Build](https://github.com/congsy3004/ViLogger/actions/workflows/build.yml/badge.svg)](https://github.com/congsy3004/ViLogger/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/congsy3004/ViLogger)](https://github.com/congsy3004/ViLogger/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**ViLogger** is a fast, portable serial port (UART / COM) monitor and data logger for Windows.
Watch up to 4 ports side by side, send commands, and record the raw received data to files — no installation required.

---

## Features

- **Multi-port** — monitor up to **4 COM ports** at once (1×1, 1×2, 1×3 or 2×2 layout).
- **Full UART settings** — baud rate, data bits, parity, stop bits and handshake (flow control).
- **Custom port names** — label each port ("GPS", "Debug UART", …) next to the detected device name.
- **Live display** in **ASCII**, **Hex** or **Binary**, switchable at any time.
- **File logging** per port in ASCII, Hex or raw Binary. Log files contain **only the received bytes** — no timestamps or extra text.
- **Transmit bar** with ASCII / HEX mode, visible `<CR>` / `<LF>` tokens and selectable line endings.
- **Hardware removed indicator** — a red badge appears when a USB-serial device is unplugged; the COM list refreshes automatically.
- **High performance** — dedicated receive thread per port, background log writer, virtualized terminal (30 fps UI updates).
- **Single portable `.exe`** — the .NET runtime is bundled, nothing to install.

## Download

1. Go to the [**Releases**](https://github.com/congsy3004/ViLogger/releases/latest) page.
2. Download **`ViLogger.exe`** (or the `.zip`).
3. Put it in a folder you can write to (e.g. `Documents\ViLogger`) and run it.

**Requirements:** Windows 10 or 11, 64-bit.

> [!NOTE]
> The executable is not code-signed, so Windows SmartScreen may show *"Windows protected your PC"*.
> Click **More info → Run anyway**.

> [!TIP]
> By default, logs are saved to a `Logs` folder next to `ViLogger.exe`. Avoid running it from
> `C:\Program Files`, or choose a different log directory in the toolbar.

## Quick start

1. Click **+ Add Port** (up to 4 panels).
2. In the port panel, choose the **COM port**, **baud rate** and other UART settings, optionally enter a name, then click **Apply Configuration**.
3. Click **Disconnected** to connect (the button turns green: **Connected**).
4. Received data appears in the monitor. Use **View** to switch between ASCII / Hex / Binary.
5. Pick a **Log format** and click **Log** to start recording. Click it again to stop.
6. Click the folder icon next to **Log** to open the most recent log file (available when logging is stopped).

Click **? Help** in the app for a full description of every button.

### Sending data

| Key / control | Action |
|---|---|
| **Enter** | Send the text in the TX box |
| **Ctrl+Enter** | Insert `<CR>`; press again right after it to insert `<LF>` (HEX mode: `0D`, then `0A`) |
| **ASCII / HEX** | In HEX mode, type bytes such as `48 65 6C 6C 6F` |
| **Line ending** | Append None / CR / LF / CRLF automatically |

Sent data is echoed in the monitor as `TX>> ...` (it is not written to the log file).

### Log files

- **Directory:** shared by all ports, set in the main toolbar (📁 opens it in Explorer).
- **File name template:** default `{port}_{datetime}`. Available tokens:

  | Token | Example |
  |---|---|
  | `{port}` | `COM3` |
  | `{date}` | `2026-10-05` |
  | `{time}` | `14-30-00` |
  | `{datetime}` | `2026-10-05_14-30-00` |
  | `{timestamp}` | `20261005143000` |

- **Formats:**
  - **ASCII** (`.log`) — received text; control characters other than CR, LF and TAB are written as `.`.
  - **Hex** (`.log`) — bytes as hex, up to 16 per line.
  - **Binary** (`.bin`) — exact raw bytes.
- A log file is locked while it is being written; stop logging before opening it.

## Known limitations

- Maximum 4 ports at the same time.
- After a device is unplugged, reconnect manually (press **Connect**, then **Log** again).
- Transmitted (TX) data is not written to log files.
- ASCII view shows non-printable / non-ASCII bytes as `.` (no UTF-8 decoding).
- No log rotation and no protocol decoders yet.

See [RELEASE_NOTES.md](RELEASE_NOTES.md) for the full change history.

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```powershell
git clone https://github.com/congsy3004/ViLogger.git
cd ViLogger

# Build and run
dotnet build EverLogger.sln -c Release
dotnet run --project src/EverLogger.App -c Release

# Create the portable single-file exe -> publish\ViLogger.exe
dotnet publish src\EverLogger.App\EverLogger.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

### Project layout

| Path | Description |
|---|---|
| `src/EverLogger.App` | WPF user interface (MVVM) |
| `src/EverLogger.Core` | Serial port handling, data queues and log writers |

### Releasing

Push a version tag and GitHub Actions builds `ViLogger.exe` and creates the release automatically,
using the matching `## vX.Y.Z` section of `RELEASE_NOTES.md` as the description:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

## Contributing

Bug reports and pull requests are welcome. Please open an [issue](https://github.com/congsy3004/ViLogger/issues)
and include your Windows version, the USB-serial adapter you use and steps to reproduce.

## License

ViLogger is released under the [MIT License](LICENSE).
It uses [RJCP.SerialPortStream](https://github.com/jcurl/SerialPortLib2) (MS-PL) and the .NET runtime (MIT) —
see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

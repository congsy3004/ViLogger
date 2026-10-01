# ViLogger 窶� Release Notes

> **ViLogger** is a high-performance, portable serial port monitor and data logger for Windows.
> Built with .NET 8 / WPF. Self-contained 窶� no installation required.

---

## v1.0.0 窶� 2026-10-01

**First public release.**

---

### 笨ｨ Features

#### Multi-Port Monitoring
- Monitor up to **4 serial ports simultaneously** in a single window.
- Use **+ Add Port** to add a new port panel at any time. Available ports are automatically re-scanned on each click. Layout adjusts automatically:
  - 1 port 竊� full-width single panel (1ﾃ�1)
  - 2 ports 竊� side by side (1ﾃ�2)
  - 3 ports 竊� three columns (1ﾃ�3)
  - 4 ports 竊� 2ﾃ�2 grid
- Each port panel can be **closed independently** with its own 笨� button. The last remaining panel cannot be closed.

#### Per-Port Configuration
- Select **COM port** and **baud rate** from dropdowns populated from the live system scan.
- Assign a **custom memorable name** to each port (e.g. "GPS Module", "IMU", "Debug UART"). The name is editable only before applying configuration.
- Port panel title shows: **`Custom Name 窶� Recognized Device Name`** (or just the device/COM name if no custom name is set).
- **竊ｺ Reset** button returns a configured panel to the unconfigured state (only available while disconnected).

#### High-Performance Terminal Monitor
- **Per-port terminal** display with monospace font.
- Every received line is **time-stamped** with millisecond precision: `[HH:mm:ss.fff]`.
- Supports three **display formats**: ASCII, Hex, Binary 窶� switchable live per port.
- **Auto-scroll** toggle keeps the latest data in view.
- **Clear** button wipes the display without affecting the log file.
- Up to **5,000 lines** retained in memory; virtualized rendering keeps the UI smooth.
- Data is drained from a background queue at **30 fps** 窶� no UI freezes under high baud rates.

#### Serial Connection
- Each port has a **Connected/Disconnected toggle button** 窶� green when connected, red when not.
- **All Connected / All Disconnected** single-button toggle for all configured ports at once.
- Reconnection-safe: the manager clears stale state before re-opening a port.
- Connection state also indicated by a colored dot in the port header (泙 / 閥).

#### File Logging
- Each port has its own **independent log file**.
- Configurable **Log Directory** (shared across all ports) with a browse button and a quick-open button (刀).
- Configurable **file name template** with tokens: `{port}`, `{datetime}`, `{date}`, `{time}`.
- Supports three **log formats**: ASCII, Hex, Binary 窶� selectable per port, independent of the display format.
- **Log All** toggle starts/stops logging on all configured ports simultaneously.
- Log writer runs on a **dedicated background thread** with a 64 KB buffered stream, flushed every 500 ms.

#### TX Transmit
- Each port panel has a dedicated **TX input bar**.
- Send text or **raw hex bytes** (e.g. `48 65 6C 6C 6F`) 窶� toggle with the Hex checkbox.
- Selectable **line endings**: None, CR (`\r`), LF (`\n`), CRLF (`\r\n`).
- Sent data is echoed in the terminal monitor as `TX>> ...`.
- **Enter to Send** global toggle:
  - **ON (default):** pressing Enter sends immediately.
  - **OFF:** pressing Enter inserts `\r`; pressing Enter again right after a `\r` inserts `\n`.
- TX byte counter shown in the per-port status bar.

#### Global Controls (Toolbar)
| Button | Action |
|--------|--------|
| All Connected / All Disconnected | Toggle-connects or disconnects all configured ports |
| Log All ON / Log All OFF | Toggle logging on all configured ports |
| Enter to send | Toggle Enter key behavior in all TX inputs |
| 刀 Open Dir | Opens the log folder in Windows Explorer |
| `...` | Browse to a different log directory |
| + Add Port | Adds a new port panel (max 4); auto-rescans COM ports |

#### Dark Theme UI
- Full dark theme with a modern card-based layout.
- Custom-styled controls: buttons, ComboBoxes, TextBoxes, CheckBoxes, ScrollBars, ListBox, ToolTips.
- Semantic button colors: green = connected/active, red = disconnected/inactive, blue = action.

---

### 笞呻ｸ� Technical Highlights

| Area | Detail |
|------|--------|
| **Runtime** | .NET 8, self-contained single-file EXE 窶� no installation needed |
| **Portability** | Windows 10 / 11 (x64) 窶� copy and run |
| **Serial library** | [RJCP.SerialPortStream](https://github.com/jcurl/SerialPortLib2) |
| **Receive thread** | Dedicated thread per port, `AboveNormal` priority, 8 KB buffer |
| **Port routing** | `ConcurrentDictionary` for O(1) lock-free packet dispatch |
| **UI timer** | `DispatcherTimer` at 33 ms (30 fps), draining up to 5,000 packets per tick |
| **Log writer** | Background thread, `ConcurrentQueue`, 64 KB `BufferedStream`, 500 ms flush |
| **Write timeout** | 500 ms per TX write 窶� prevents UI hangs on blocked ports |

---

### 逃 Download

| File | Description |
|------|-------------|
| `EverLogger.exe` | Self-contained portable executable for Windows 10/11 x64 |

No installer. No .NET runtime required on the target machine. Just copy and run.

---

### 菅 Known Limitations

- Maximum **4 ports** simultaneously.
- Some exotic USB adapters may not expose a friendly device description in the system registry.
- No log file rotation 窶� each session creates a new file.
- No protocol decoder (NMEA, custom framing, etc.) in this release 窶� raw bytes only.

---

### 発 Planned for Future Releases

- [ ] Protocol decoders (NMEA 0183, custom framing)
- [ ] Log file rotation / size limits
- [ ] Search / filter in the terminal monitor
- [ ] Save and restore port configurations between sessions

---

*Released by Cong Sy 窶� 2026-10-01*

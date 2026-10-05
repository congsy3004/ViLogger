# ViLogger — Release Notes

> **ViLogger** is a high-performance, portable serial port monitor and data logger for Windows.
> Built with .NET 8 / WPF. Self-contained — no installation required.

---

## Unreleased

**Stable terminal view and a lossless, byte-exact log.**

---

### 🛠 Fixed

#### Terminal view
- The terminal no longer freezes or falls behind under heavy traffic (previously it could stop updating until the port was reconnected). The view is now updated once per UI tick (50 ms) per port, no matter how many chunks arrive.
- Auto-scroll no longer jumps around. The terminal is a plain read-only text view: text can be selected and copied.
- A view error can no longer stop the display or crash the app; unexpected UI errors are shown in the status bar.

#### Log file
- **No data loss under load:** received data could previously be dropped if the disk fell behind (bounded queue). The log queue is now unbounded and every received byte is written.
- Stopping logging writes everything received up to that moment before the file is closed. On app exit, pending data is written before the process ends.
- If the log file cannot be created (locked, no permission, bad path) or a write fails later (e.g. disk full), logging stops and the reason is shown in the status and the monitor — no more silent empty logs.
- A transmit (TX) error no longer disconnects the port or stops logging; it is shown as **Tx Error** instead.
- Closing a port can no longer interfere with a newly opened connection on the same port.

### 🔄 Changed
- **AutoScroll OFF now pauses the view** so you can scroll back, read and copy; data is still received and logged. Turning AutoScroll ON jumps to the latest data.
- **ASCII log = exact received bytes** (control bytes are no longer replaced with `.`).
- **Hex log and Hex view:** fixed 16 bytes per line, independent of how the driver split the data.
- **ASCII view:** CR, LF and CRLF each start one new line; other non-printable bytes are shown as `.`.
- **Binary view:** byte counts are combined per update (`[Binary data: N bytes]`).
- The terminal keeps roughly the latest 120,000 characters (was 2,000 lines).
- The **LOG** counter shows the bytes actually written to the file.

---

## v1.1.1 — 2026-10-05

**New application icon.**

---

### ✨ New

- ViLogger now has its own icon — a serial pulse with a **V** in the middle — shown on the taskbar, the window title bar, Alt+Tab and on `ViLogger.exe` in Explorer (previously the generic Windows placeholder).

### 📦 Download

| File | Description |
|------|-------------|
| `ViLogger.exe` | Self-contained portable executable for Windows 10/11 x64 |
| `ViLogger-v1.1.1-win-x64.zip` | Same executable plus LICENSE, README and third-party notices |

---

## v1.1.0 — 2026-10-03

**First public release on GitHub — hardware-removed indicator, full UART settings, a reworked TX bar and a new "Nightfall" theme.**

---

### ✨ New

#### Hardware Removed Indicator
- When a device is unplugged (e.g. USB-to-serial cable removed), the port header shows a red **⚠ HARDWARE REMOVED** badge and the status bar explains what happened.
- A notice is printed in the monitor (never written to the log file).
- The COM port list refreshes automatically when devices are plugged in or removed.
- Plug the device back in and press **Connect** to continue.

#### Full UART Configuration
- The port configuration card now includes **Data Bits** (5–8), **Parity**, **Stop Bits** and **Handshake** (flow control), in addition to COM port, baud rate and name.

#### Log File Shortcut
- New **Open log file** button (folder icon) next to each port's Log button opens the most recent log file in its default viewer.
- Available only while logging is **stopped** — the active log file is held with an exclusive lock.

#### TX Transmit Bar
- **ASCII / HEX** mode dropdown (replaces the Hex checkbox).
  - HEX input is filtered to valid hex digits and auto-grouped into byte pairs (`AB4D3D` → `AB 4D 3D`).
  - Switching mode converts the current input (ASCII ↔ HEX).
- CR/LF are shown as explicit **`<CR>` / `<LF>`** tokens in ASCII mode — no invisible characters. Pasted line breaks are converted to tokens automatically and turned back into real bytes when sent.
- New **➤ Send** button.

#### Other
- **Clear All** button clears the monitor of every port at once.
- **? Help** button opens a full button-reference window.
- Prompts without a line ending (e.g. `COM1>`) now appear **immediately** instead of waiting for a newline.
- Long lines **wrap** inside the terminal — no horizontal scroll bar.

---

### 🔄 Changed

- **Keyboard in the TX box:**
  - **Enter** — sends immediately.
  - **Ctrl+Enter** — inserts `<CR>`; pressing it again right after `<CR>` inserts `<LF>` (in HEX mode: `0D`, then `0A`).
- **Global toolbar:**
  - **▶ Connect All** now only connects configured ports (it is no longer a connect/disconnect toggle).
  - **Log All** is split into two buttons: **▶ Log All** (start) and **■ Log All** (stop).
- **Per-port toolbar** order: `Connected/Disconnected` | `AutoScroll` | `Clear` | `View` | `Log format` | `Log` | `Open log file`.
- **AutoScroll** is now a green/red toggle button.
- The **Log format** dropdown is locked while logging is active.
- **Terminal buffer** reduced from 5,000 to **2,000 lines**. When full, the oldest lines are trimmed in one batch down to 1,500 for a smoother UI.
- Log files contain the received data only — **no timestamps are added**.
- Logging on a port **stops automatically** if that port disconnects unexpectedly.
- New **"Nightfall" theme** — deep blue-charcoal with a sky-blue accent and 6 px rounded controls.
- Window title is now **"ViLogger — Serial Monitor & Logger"**.
- The executable is now named **`ViLogger.exe`** and is a true **single file** (native WPF libraries are embedded and compressed — ~70 MB instead of 147 MB + 5 DLLs).
- Released under the **MIT License**.

---

### ❌ Removed

- Per-line **timestamps** in the monitor and the per-port timestamp toggle.
- Global **Enter to send** toggle (see the new keyboard behavior above).
- TX **Hex** checkbox (replaced by the ASCII / HEX dropdown).
- Most hover tooltips (replaced by the Help window).

---

### 🐛 Fixed

- Per-port **Connect** button was unresponsive right after applying the configuration.
- Port indicator stayed **green** after the device was unplugged, and the port could not be reconnected.
- **UI freeze** when disconnecting a port. A spurious error is no longer reported when a port is closed intentionally.
- **Auto-scroll** did not always reach the bottom when lines were wrapped.
- An unfinished received line could be merged into the next line after a `TX>>` echo.

---

### ⚙️ Technical Highlights

| Area | Detail |
|------|--------|
| **Runtime** | .NET 8, self-contained single-file EXE — no installation needed |
| **Portability** | Windows 10 / 11 (x64) — copy and run |
| **Serial library** | [RJCP.SerialPortStream](https://github.com/jcurl/SerialPortLib2) |
| **Receive thread** | Dedicated thread per port, `AboveNormal` priority, 8 KB buffer, 64 KB driver read buffer |
| **Port routing** | `ConcurrentDictionary` for O(1) lock-free packet dispatch |
| **UI timer** | `DispatcherTimer` at 33 ms (30 fps), draining up to 5,000 packets per tick |
| **Terminal** | Virtualized `ListBox`, 2,000-line cap with single-notification bulk trim |
| **Log writer** | Background thread, `ConcurrentQueue`, 64 KB `BufferedStream`, flush every 500 ms or 64 KB, exclusive file lock |
| **Device detection** | `WM_DEVICECHANGE` hook — port list refreshed only when the set of COM ports actually changes |
| **Write timeout** | 500 ms per TX write — prevents UI hangs on blocked ports |

---

### 📦 Download

| File | Description |
|------|-------------|
| `ViLogger.exe` | Self-contained portable executable for Windows 10/11 x64 |
| `ViLogger-v1.1.0-win-x64.zip` | Same executable plus LICENSE, README and third-party notices |

No installer. No .NET runtime required on the target machine. Just copy and run.

> The executable is not code-signed, so Windows SmartScreen may show "Windows protected your PC". Click **More info → Run anyway**.

---

### 🐛 Known Limitations

- Maximum **4 ports** simultaneously.
- After a device is unplugged, reconnecting is manual — press **Connect** (and **Log**) again.
- Transmitted (TX) data is shown in the monitor but **not written to the log file**.
- ASCII view shows non-printable and non-ASCII bytes as `.` (no UTF-8 decoding).
- In Hex view, line breaks follow how the driver delivers data chunks, not a fixed width.
- Some exotic USB adapters may not expose a friendly device description in the system registry.
- No log file rotation — each session creates a new file.
- No protocol decoder (NMEA, custom framing, etc.) — raw bytes only.

---

### 🚀 Planned for Future Releases

- [ ] Protocol decoders (NMEA 0183, custom framing)
- [ ] Log file rotation / size limits
- [ ] Search / filter in the terminal monitor
- [ ] Save and restore port configurations between sessions

---

## v1.0.0 — 2026-10-01

**First public release.**

---

### ✨ Features

#### Multi-Port Monitoring
- Monitor up to **4 serial ports simultaneously** in a single window.
- Use **+ Add Port** to add a new port panel at any time. Available ports are automatically re-scanned on each click. Layout adjusts automatically:
  - 1 port → full-width single panel (1×1)
  - 2 ports → side by side (1×2)
  - 3 ports → three columns (1×3)
  - 4 ports → 2×2 grid
- Each port panel can be **closed independently** with its own ✕ button. The last remaining panel cannot be closed.

#### Per-Port Configuration
- Select **COM port** and **baud rate** from dropdowns populated from the live system scan.
- Assign a **custom memorable name** to each port (e.g. "GPS Module", "IMU", "Debug UART"). The name is editable only before applying configuration.
- Port panel title shows: **`Custom Name — Recognized Device Name`** (or just the device/COM name if no custom name is set).
- **↺ Reset** button returns a configured panel to the unconfigured state (only available while disconnected).

#### High-Performance Terminal Monitor
- **Per-port terminal** display with monospace font.
- Every received line is **time-stamped** with millisecond precision: `[HH:mm:ss.fff]`.
- Supports three **display formats**: ASCII, Hex, Binary — switchable live per port.
- **Auto-scroll** toggle keeps the latest data in view.
- **Clear** button wipes the display without affecting the log file.
- Up to **5,000 lines** retained in memory; virtualized rendering keeps the UI smooth.
- Data is drained from a background queue at **30 fps** — no UI freezes under high baud rates.

#### Serial Connection
- Each port has a **Connected/Disconnected toggle button** — green when connected, red when not.
- **All Connected / All Disconnected** single-button toggle for all configured ports at once.
- Reconnection-safe: the manager clears stale state before re-opening a port.
- Connection state also indicated by a colored dot in the port header (🟢 / 🔴).

#### File Logging
- Each port has its own **independent log file**.
- Configurable **Log Directory** (shared across all ports) with a browse button and a quick-open button (📁).
- Configurable **file name template** with tokens: `{port}`, `{datetime}`, `{date}`, `{time}`.
- Supports three **log formats**: ASCII, Hex, Binary — selectable per port, independent of the display format.
- **Log All** toggle starts/stops logging on all configured ports simultaneously.
- Log writer runs on a **dedicated background thread** with a 64 KB buffered stream, flushed every 500 ms.

#### TX Transmit
- Each port panel has a dedicated **TX input bar**.
- Send text or **raw hex bytes** (e.g. `48 65 6C 6C 6F`) — toggle with the Hex checkbox.
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
| 📁 Open Dir | Opens the log folder in Windows Explorer |
| `...` | Browse to a different log directory |
| + Add Port | Adds a new port panel (max 4); auto-rescans COM ports |

#### Dark Theme UI
- Full dark theme with a modern card-based layout.
- Custom-styled controls: buttons, ComboBoxes, TextBoxes, CheckBoxes, ScrollBars, ListBox, ToolTips.
- Semantic button colors: green = connected/active, red = disconnected/inactive, blue = action.

---

*Released by Cong Sy — 2026-10-05*

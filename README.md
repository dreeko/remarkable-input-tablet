# remarkable-input-tablet

Use a reMarkable 2 as a pressure-sensitive drawing tablet on Windows — no drivers, no root modifications, no third-party services.

The tablet connects over SSH (USB or Wi-Fi). The pen's raw evdev events are streamed to the PC and injected as Windows Ink pointer input, giving you full pressure sensitivity, tilt, hover detection, and eraser support in any compatible application.

## Requirements

- **Tablet**: reMarkable 2 (firmware tested: Wacom I2C Digitizer, version 1231)
- **PC**: Windows 10 1809 or later (Windows Ink pointer injection API)
- **Connection**: USB cable (SSH at `10.11.99.1`) or Wi-Fi (same SSH, different IP)
- **Root password**: Settings → Help → Copyrights and licenses → scroll to bottom

## Quick start

### GUI app

Download `RemtabletApp-*.zip` from Releases, extract, run `RemarkableTablet.App.exe`.

The app lives in the system tray. Right-click → **Connect...** to open Settings, enter your device IP and root password, and click **Connect**.

### CLI

Download `remtablet-*.zip` from Releases, extract, and run:

```
remtablet.exe --password <root-password>
```

Press **Ctrl-C** to stop.

## CLI options

| Flag | Default | Description |
|------|---------|-------------|
| `--password <pw>` | — | Root password (required unless `--key` is set) |
| `--key <path>` | — | Path to SSH private key file |
| `--address <ip>` | `10.11.99.1` | Device IP address |
| `--orientation <value>` | `portrait` | `portrait`, `landscape`, `portraitflipped`, `landscapeflipped` |
| `--output <value>` | `ink` | `ink` (Windows Ink, pressure+tilt) or `mouse` (cursor only) |
| `--debug` | off | Print pipeline stage info on startup |

## Orientation

Hold the tablet with the **pen slot at the bottom** for portrait (default). Orientation controls how the tablet's native coordinate space maps to the screen:

| Value | Physical position |
|-------|-------------------|
| `portrait` | Pen slot at bottom (default drawing position) |
| `landscape` | Pen slot on right |
| `portraitflipped` | Pen slot at top |
| `landscapeflipped` | Pen slot on left |

## App compatibility

The **Windows Ink** output mode (default) works with:

- **Krita** — Settings → Configure Krita → Tablet → set input to *Windows 8+ Pointer Input*
- **Photoshop 2018+** — works out of the box
- **Affinity Photo / Designer** — works out of the box
- **Clip Studio Paint** — works out of the box
- **Paint Tool SAI v2** — requires "Tablet" input mode in preferences

Use **Mouse** mode only as a fallback for applications that don't support Windows Ink.

## Settings persistence

The GUI app saves settings (address, orientation, monitor, output mode, auto-connect flag) to:

```
%APPDATA%\remarkable-input-tablet\settings.json
```

Passwords are never stored. You will be prompted each session.

## Reconnection

The pipeline reconnects automatically if the SSH stream drops (USB unplugged, device sleeps, Wi-Fi hiccup). Before each reconnect attempt a synthetic pen-up is injected so drawing applications don't get a stuck pen. Retry delays follow exponential backoff: 1 s, 2 s, 4 s, 8 s, 16 s, 30 s (capped).

## Building from source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone <repo>
cd remarkable-input-tablet
dotnet build
dotnet test
```

### Publish CLI (NativeAOT, ~12 MB single file)

```powershell
dotnet publish src/RemarkableTablet.Cli -c Release -r win-x64 `
  -p:PublishAot=true -p:InvariantGlobalization=true -o out/cli
```

### Publish GUI app (self-contained trimmed, ~35 MB single file)

```powershell
dotnet publish src/RemarkableTablet.App -c Release -r win-x64 `
  -p:SelfContained=true -p:PublishTrimmed=true -p:PublishSingleFile=true -o out/app
```

## Architecture

```
reMarkable 2                         Windows PC
─────────────────────────────        ─────────────────────────────────────
/dev/input/event1 (evdev)
        │
   cat (SSH stdout)
        │
   ─── SSH (TCP) ──────────────────► SshTransport
                                            │  PipeReader (System.IO.Pipelines)
                                     EvdevParser
                                            │  Channel<EvdevEvent>
                                    TabletStateMachine
                                            │  Channel<PenFrame>
                                    CoordinateMapper
                                            │  MappedFrame
                                     IOutputMode
                                      ├─ WindowsInkOutput  ← InjectSyntheticPointerInput
                                      └─ MouseOutput        ← SetCursorPos / mouse_event
```

**Projects:**

| Project | Role |
|---------|------|
| `RemarkableTablet.Core` | Platform-agnostic pipeline: evdev parser, state machine, coordinate mapper |
| `RemarkableTablet.Windows` | Win32 P/Invoke layer: Windows Ink pointer injection, mouse output |
| `RemarkableTablet.Cli` | `remtablet.exe` — headless CLI, NativeAOT |
| `RemarkableTablet.App` | `RemarkableTablet.App.exe` — system tray GUI, WPF + WinForms |
| `tools/EventDiagnostics` | Live evdev event stream logger — streams events to console for debugging |
| `tools/Phase0Diagnostics` | One-shot SSH capture tool — validates evdev struct layout and saves a fixture |

## Hardware details

Digitizer confirmed via `evtest` on firmware version 1231:

| Axis | Range | Notes |
|------|-------|-------|
| ABS_X | 0 – 20966 | Long axis: 0 = USB/bottom, max = top of device (portrait) |
| ABS_Y | 0 – 15725 | Short axis: 0 = left, max = right of device (portrait) |
| Pressure | 0 – 4095 | 12-bit, mapped to Windows 0–1024 via Bézier curve |
| Distance | 0 – 255 | Hover height above surface |
| Tilt X/Y | −9000 – 9000 | Firmware units, mapped to ±90° |

## License

MIT

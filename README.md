# remarkable-input-tablet

Use a reMarkable 2 as a pressure-sensitive drawing tablet on Windows and Linux — no drivers, no root modifications, no third-party services.

The tablet connects over SSH (USB or Wi-Fi). The pen's raw evdev events are streamed to the PC and injected as native pointer input, giving you full pressure sensitivity, tilt, hover detection, and eraser support in any compatible application.

## Requirements

### Windows

- **Tablet**: reMarkable 2 (firmware tested: Wacom I2C Digitizer, version 1231)
- **PC**: Windows 10 1809 or later (Windows Ink pointer injection API)
- **Connection**: USB cable (SSH at `10.11.99.1`) or Wi-Fi (same SSH, different IP)
- **Root password**: Settings → Help → Copyrights and licenses → scroll to bottom

### Linux

- **Tablet**: reMarkable 2 (same as above)
- **PC**: Any x86-64 Linux with kernel 4.5+ (uinput module)
- **Connection**: USB cable or Wi-Fi (same as Windows)
- **Permissions**: membership in the `input` group (one-time setup, see below)

## Quick start

### Windows — GUI app

Download `RemtabletApp-*.zip` from Releases, extract, run `RemarkableTablet.App.exe`.

The app lives in the system tray. Right-click → **Connect...** to open Settings, enter your device IP and root password, and click **Connect**.

### Windows — CLI

Download `remtablet-*-win-x64.zip` from Releases, extract, and run:

```
remtablet.exe --password <root-password>
```

### Linux — CLI

Download `remtablet-*-linux-x64.tar.gz` from Releases, extract, and run:

```bash
# One-time: grant /dev/uinput access (log out and back in after)
sudo usermod -aG input $USER

# Run
./remtablet --password <root-password>
```

If your display resolution is not 1920×1080, pass it explicitly:

```bash
./remtablet --password <root-password> --width 2560 --height 1440
```

Press **Ctrl-C** to stop.

## CLI options

| Flag | Default | Description |
|------|---------|-------------|
| `--password <pw>` | — | Root password (required unless `--key` is set) |
| `--key <path>` | — | Path to SSH private key file |
| `--address <ip>` | `10.11.99.1` | Device IP address |
| `--orientation <value>` | `portrait` | `portrait`, `landscape`, `portraitflipped`, `landscapeflipped` |
| `--output <value>` | `ink` | `ink` (full pressure+tilt) or `mouse` (cursor only, Windows only) |
| `--width <px>` | auto (Windows) / 1920 (Linux) | Screen width in pixels |
| `--height <px>` | auto (Windows) / 1080 (Linux) | Screen height in pixels |
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

### Windows (Windows Ink output)

- **Krita** — Settings → Configure Krita → Tablet → set input to *Windows 8+ Pointer Input*
- **Photoshop 2018+** — works out of the box
- **Affinity Photo / Designer** — works out of the box
- **Clip Studio Paint** — works out of the box
- **Paint Tool SAI v2** — requires "Tablet" input mode in preferences

Use **Mouse** mode only as a fallback for applications that don't support Windows Ink.

### Linux (uinput output)

The virtual device appears as a standard pen tablet to any app that reads from the Linux input subsystem:

- **Krita** — works out of the box with pressure and tilt
- **GIMP** — enable extended input devices: Edit → Input Devices
- **Inkscape** — Input Devices dialog, set the virtual tablet to "Screen" mode
- **MyPaint** — works out of the box
- **Blender** — works with tablet pressure in sculpt and paint modes

The device uses `INPUT_PROP_DIRECT` so absolute coordinates map 1:1 to screen pixels. Pressure is reported on the 0–1024 scale.

## Linux uinput setup

The `/dev/uinput` device node requires write access. The cleanest approach is a udev rule (no sudo required after setup):

```bash
# Option A: add yourself to the input group (requires log out/in)
sudo usermod -aG input $USER

# Option B: udev rule for uinput specifically
echo 'KERNEL=="uinput", GROUP="input", MODE="0660"' \
  | sudo tee /etc/udev/rules.d/70-uinput.rules
sudo udevadm control --reload && sudo udevadm trigger
```

## Settings persistence

The GUI app (Windows) saves settings to:

```
%APPDATA%\remarkable-input-tablet\settings.json
```

Passwords are never stored. You will be prompted each session.

## Reconnection

The pipeline reconnects automatically if the SSH stream drops (USB unplugged, device sleeps, Wi-Fi hiccup). Before each reconnect attempt a synthetic pen-up is injected so drawing applications don't get a stuck pen. Retry delays follow exponential backoff: 1 s, 2 s, 4 s, 8 s, 16 s, 30 s (capped).

## Building from source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone <repo>
cd remarkable-input-tablet
```

### Windows

```powershell
dotnet build
dotnet test
```

### Linux

```bash
# Build and test Linux-compatible projects.
# -f net10.0 skips the Windows TFM (CLI is multi-targeted).
dotnet build src/RemarkableTablet.Cli -f net10.0
dotnet test tests/RemarkableTablet.Core.Tests
```

### Publish CLI — Windows (NativeAOT, ~12 MB single file)

```powershell
dotnet publish src/RemarkableTablet.Cli -c Release -r win-x64 -f net10.0-windows `
  -p:PublishAot=true -p:InvariantGlobalization=true -o out/cli
```

### Publish CLI — Linux (NativeAOT, ~12 MB single file)

```bash
# Prerequisite: clang and zlib headers
sudo apt-get install -y clang zlib1g-dev   # Debian/Ubuntu

dotnet publish src/RemarkableTablet.Cli -c Release -r linux-x64 -f net10.0 \
  -p:PublishAot=true -p:InvariantGlobalization=true -o out/cli
```

### Publish GUI app — Windows (self-contained single file, ~70 MB)

```powershell
dotnet publish src/RemarkableTablet.App -c Release -r win-x64 `
  -p:SelfContained=true -p:PublishSingleFile=true -o out/app
```

> Trimming is intentionally **not** enabled: the tray icon uses `System.Windows.Forms.NotifyIcon`,
> and WinForms is not trim-compatible (NETSDK1175). The CLI ships smaller because it uses
> NativeAOT instead.

## Architecture

```
reMarkable 2                         Host PC
─────────────────────────────        ─────────────────────────────────────────────
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
                                      ├─ WindowsInkOutput  ← InjectSyntheticPointerInput (Windows)
                                      ├─ MouseOutput        ← SetCursorPos / mouse_event  (Windows)
                                      └─ UinputOutput       ← /dev/uinput kernel module   (Linux)
```

**Projects:**

| Project | Platform | Role |
|---------|----------|------|
| `RemarkableTablet.Core` | Any | Platform-agnostic pipeline: evdev parser, state machine, coordinate mapper |
| `RemarkableTablet.Windows` | Windows | Win32 P/Invoke layer: Windows Ink pointer injection, mouse output |
| `RemarkableTablet.Linux` | Linux | uinput P/Invoke layer: virtual pen tablet via `/dev/uinput` |
| `RemarkableTablet.Cli` | Windows + Linux | `remtablet` — headless CLI, NativeAOT |
| `RemarkableTablet.App` | Windows | `RemarkableTablet.App.exe` — system tray GUI, WPF + WinForms |
| `tools/EventDiagnostics` | Windows | Live evdev event stream logger — streams events to console for debugging |
| `tools/Phase0Diagnostics` | Windows | One-shot SSH capture tool — validates evdev struct layout and saves a fixture |

## Hardware details

Digitizer confirmed via `evtest` on firmware version 1231:

| Axis | Range | Notes |
|------|-------|-------|
| ABS_X | 0 – 20966 | Long axis: 0 = USB/bottom, max = top of device (portrait) |
| ABS_Y | 0 – 15725 | Short axis: 0 = left, max = right of device (portrait) |
| Pressure | 0 – 4095 | 12-bit, mapped to 0–1024 via shaping curve (Windows Ink scale) |
| Distance | 0 – 255 | Hover height above surface |
| Tilt X/Y | −9000 – 9000 | Firmware units, mapped to ±90° |

> **Note on tilt:** the tilt vector is rotated by the same orientation transform as
> position, but the sign convention vs. Windows Ink (positive tilt-X = pen leans
> toward the +X screen axis) has not been empirically verified. If your brushes
> highlight the wrong direction in non-Portrait orientations, the four cases in
> `CoordinateMapper.RotateTilt` are the place to flip signs.

## License

MIT

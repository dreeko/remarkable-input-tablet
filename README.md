# remarkable-input-tablet

Use a reMarkable 2 (stable) or reMarkable Paper Pro (experimental — see [Paper Pro status](#paper-pro-status)) as a pressure-sensitive drawing tablet on Windows and Linux. Nothing is installed on the tablet: the host reads its existing Linux input devices over SSH and creates native pointer devices locally.

The tablet connects over SSH (USB or Wi-Fi). The pen's raw evdev events are streamed to the PC and injected as native pointer input, giving you full pressure sensitivity, tilt, hover detection, and eraser support in any compatible application. Optional multi-touch gestures (pinch / pan / rotate) inject as real touch contacts to apps that consume them.

## Requirements

### Tablet — reMarkable 2

- Firmware/hardware tested: Wacom I2C Digitizer, version 1231
- SSH enabled and the tablet's root password or an SSH private key. The password is shown under Settings → Help → Copyrights and licenses → GPLv3 Compliance.

### Tablet — reMarkable Paper Pro

- **Developer Mode must be enabled first**: Settings → General → Paper Tablet → Software → Advanced → Developer Mode. The root password then appears at Settings → Help → About → Copyrights and Licenses. See reMarkable's [Developer mode article](https://support.remarkable.com/s/article/Developer-mode).
- Experimental — most device constants are seeded from community data (`Evidlo/remarkable_mouse` `rmpro` branch) and have not been independently verified against hardware. See [Paper Pro status](#paper-pro-status) for the current verification gaps.

### Host — Windows

- Windows 10 1809 or later (Windows Ink pointer injection API)
- Connection: USB cable (SSH at `10.11.99.1`) or Wi-Fi (same SSH, different IP)

### Host — Linux

- x86-64 Linux with kernel 4.5+ and the `uinput` module (the published archive targets `linux-x64`)
- Connection: USB cable or Wi-Fi (same as Windows)
- Permissions: membership in the `input` group (one-time setup, see below)

## Quick start

### Windows — GUI app

Download `RemtabletApp-*.zip` from Releases, extract, run `RemarkableTablet.App.exe`.

The app lives in the system tray. Right-click → **Connect...** to open Settings, enter your device IP and root password, and click **Connect**.

The Settings dialog also exposes a **Touch Gestures** checkbox to enable pinch / pan / rotate from the rM2 touchscreen (see the [Touch gestures](#touch-gestures) section), and a **Pressure** dropdown to pick between Linear, Soft (boosts light strokes), and Hard (suppresses light strokes) response curves.

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

The Linux CLI detects the current display size using `xrandr`, `kscreen-doctor`, `wlr-randr`, or DRM sysfs. If detection fails, it warns and falls back to 1920×1080. Override detection when it chooses the wrong output or desktop extent:

```bash
./remtablet --password <root-password> --width 2560 --height 1440
```

Press **Ctrl-C** to stop.

For unattended use, prefer `--key ~/.ssh/id_ed25519` over putting a password in shell history or a process command line. Run `remtablet --help` for built-in usage information and `remtablet --version` for the installed version.

## CLI options

| Flag | Default | Description |
|------|---------|-------------|
| `--password <pw>` | — | Root password (required unless `--key` is set) |
| `--key <path>` | — | Path to SSH private key file |
| `--address <host>` | `10.11.99.1` | Device IP address or hostname |
| `--orientation <value>` | `portrait` | `portrait`, `landscape`, `portraitflipped`, `landscapeflipped` |
| `--output <value>` | `ink` | `ink` (full pressure+tilt) or `mouse` (cursor only, Windows only) |
| `--width <px>` | auto | Positive screen width in pixels; must be used with `--height` |
| `--height <px>` | auto | Positive screen height in pixels; must be used with `--width` |
| `--debug` | off | Print pipeline stage info on startup |
| `--gestures <value>` | `off` | `touch` (inject multi-touch contacts for pinch / pan / rotate) or `off`. The rM2 firmware suppresses touch while the pen is in proximity, so two-finger gestures only register when the pen is set aside. |
| `--pressure <value>` | `linear` | Pressure response curve. `linear` (1:1), `soft` (boosts light strokes — pen feels lighter), or `hard` (suppresses light strokes — pen feels stiffer). |
| `--device <value>` | `auto` | `auto` (probe via `uname -m`), `rm2`, or `rmpp`. Auto-detect runs a short SSH command before the streaming pipeline starts; force a specific profile only if detection fails. |
| `-h`, `--help` | — | Print usage and exit |
| `--version` | — | Print the version and exit |

Option names are case-sensitive; enumerated values are case-insensitive. Unknown, duplicate, incomplete, and conflicting options are rejected before connecting. `--output mouse` is available only in the Windows CLI.

## Orientation

Hold the tablet with the **pen slot at the bottom** for portrait (default). Orientation controls how the tablet's native coordinate space maps to the screen:

| Value | Physical position |
|-------|-------------------|
| `portrait` | Pen slot at bottom (default drawing position) |
| `landscape` | Pen slot on right |
| `portraitflipped` | Pen slot at top |
| `landscapeflipped` | Pen slot on left |

## Touch gestures

Pass `--gestures touch` to enable pinch / pan / rotate gestures from the rM2's
capacitive touchscreen. The tool opens a second SSH stream against
`/dev/input/event2` and injects multi-touch contacts to the host:

- **Windows:** synthetic touch contacts via `CreateSyntheticPointerDevice(PT_TOUCH)`
  + `InjectSyntheticPointerInput`. Apps that handle Windows touch (Krita,
  Photoshop, Affinity, browsers) run their own gesture recognition on the
  injected contacts.
- **Linux:** a second uinput device (`reMarkable 2 Touch`) using the MT-B slot
  protocol with `INPUT_PROP_DIRECT`. Apps reading the input subsystem see
  real multi-touch contacts.

**Important hardware behavior:** the rM2 firmware suppresses touch reporting
while the pen is in proximity (verified via `evtest`). This means
*simultaneous draw + pinch is not possible at the hardware level* — the
workflow is "lift pen → pinch / pan / rotate → resume drawing." This is a
property of the device, not the tool.

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

### Touch gesture compatibility (`--gestures touch`)

Pinch / pan / rotate compatibility depends on whether the host application
consumes Windows touch (or Linux multi-touch) gestures. Confirmed working
where verified; rows marked "untested" are expected to work but haven't
been hands-on validated.

| App                          | Pinch zoom | Pan       | Rotate    | Notes |
|------------------------------|------------|-----------|-----------|-------|
| **Windows**                  |            |           |           |       |
| Krita                        | ✅         | ✅        | ✅        | Real touch + Windows Ink coexist cleanly. |
| Photoshop                    | untested   | untested  | untested  | Expected to work — Photoshop has full Windows touch support. |
| Affinity Photo / Designer    | untested   | untested  | untested  | Expected to work. |
| Clip Studio Paint            | untested   | untested  | untested  | |
| Microsoft Edge / Chrome      | untested   | untested  | n/a       | Browser pinch-zoom of pages. |
| Windows Photos               | untested   | untested  | n/a       | Native Windows app — should work. |
| **Linux**                    |            |           |           |       |
| Krita                        | untested   | untested  | untested  | |
| GIMP                         | untested   | untested  | n/a       | |
| Inkscape                     | untested   | untested  | n/a       | |
| Blender                      | untested   | untested  | n/a       | |

If you confirm or find a failure, the table above is the place to record it.

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

Ensure the module is loaded with `sudo modprobe uinput`. Group membership changes require a new login session; verify access with `test -w /dev/uinput`. Some distributions recreate `/dev/uinput` with their own permissions, in which case use the udev rule rather than a one-off `chmod`.

## Troubleshooting

- **Connection refused or timed out:** confirm SSH is enabled, reconnect USB, and test `ssh root@10.11.99.1`. For Wi-Fi, pass the tablet's current address with `--address`.
- **Authentication failed:** verify the root password on the tablet, or confirm the private key path and permissions. `--password` and `--key` cannot be combined.
- **Linux reports `/dev/uinput` access denied:** follow [Linux uinput setup](#linux-uinput-setup), start a new login session, and check `test -w /dev/uinput`.
- **Pointer mapping is scaled or offset on Linux:** pass the intended output's pixel dimensions with both `--width` and `--height`. This is especially useful for mixed-DPI and multi-monitor desktops.
- **No touch gestures while drawing:** this is expected on rM2 hardware; move the pen out of proximity before touching the screen.
- **Unexpected disconnects:** rerun with `--debug` for pipeline details. Normal SSH drops reconnect automatically as described below.

## Settings persistence

The GUI app (Windows) saves settings to:

```
%APPDATA%\remarkable-input-tablet\settings.json
```

Passwords are never stored. You will be prompted each session.

## Reconnection

The pipeline reconnects automatically if the SSH stream drops (USB unplugged, device sleeps, Wi-Fi hiccup). Before each reconnect attempt a synthetic pen-up *and* an "all touch contacts released" event are injected so drawing applications don't get a stuck pen or stuck touch contacts. Retry delays follow exponential backoff: 1 s, 2 s, 4 s, 8 s, 16 s, 30 s (capped).

## Building from source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/dreeko/remarkable-input-tablet.git
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
/dev/input/event1 (pen)
        │
   cat (SSH stdout) ──┐
                      │
/dev/input/event2 (touch, when --gestures touch)
        │             │
   cat (SSH stdout) ──┤
                      │
   ─── SSH (TCP) ────►┴► SshTransport ── one SshClient, one SshDeviceStream per evdev device
                            │
                            ├──► EvdevParser → Channel<EvdevEvent> ──► TabletStateMachine
                            │                                              │  Channel<PenFrame>
                            │                                         CoordinateMapper
                            │                                              │  MappedFrame
                            │                                          IOutputMode
                            │                                           ├─ WindowsInkOutput      (Windows Ink injection)
                            │                                           ├─ MouseOutput           (cursor only, Windows)
                            │                                           └─ UinputOutput          (Linux pen tablet)
                            │
                            └──► EvdevParser → Channel<EvdevEvent> ──► TouchStateMachine
                                                                           │  Channel<TouchFrame>
                                                                       TouchCoordinateMapper
                                                                           │  MappedTouchFrame
                                                                       ITouchOutput
                                                                        ├─ WindowsTouchInjectionOutput  (synthetic touch, Windows)
                                                                        └─ UinputTouchOutput            (Linux MT-B touchscreen)
```

**Projects:**

| Project | Platform | Role |
|---------|----------|------|
| `RemarkableTablet.Core` | Any | Platform-agnostic pipeline: evdev parser, pen/touch state machines, coordinate mappers, device profiles, and multi-stream SSH transport |
| `RemarkableTablet.Windows` | Windows | Win32 P/Invoke layer: Windows Ink pen injection, mouse output, synthetic touch injection (`PT_TOUCH`) |
| `RemarkableTablet.Linux` | Linux | uinput P/Invoke layer for virtual pen/touch devices, plus host display-size detection |
| `RemarkableTablet.Cli` | Windows + Linux | `remtablet` — headless CLI, NativeAOT |
| `RemarkableTablet.App` | Windows | `RemarkableTablet.App.exe` — system tray GUI, WPF + WinForms |
| `tools/EventDiagnostics` | Windows | Live evdev event stream logger — streams events to console for debugging |
| `tools/LinuxInjectionSmoke` | Linux | Creates temporary virtual pen/touch devices and injects a short smoke-test sequence |

## Paper Pro status

The reMarkable Paper Pro is on a 64-bit aarch64 platform (NXP i.MX 8MM) versus the rM2's 32-bit armv7l, which changes the kernel `struct input_event` size from 16 to 24 bytes. The shared event parser is parameterised by `EvdevLayout` (see `src/RemarkableTablet.Core/Devices/`) and a regression test feeds a 24-byte stream to guard against the desync signature documented in [`Evidlo/remarkable_mouse` Issue #92](https://github.com/Evidlo/remarkable_mouse/issues/92).

Auto-detection via `uname -m` routes the right profile automatically. You can also pass `--device rmpp` explicitly.

**What is verified:**

- Build + unit tests (Windows + Linux, Core + Windows test suites).
- 64-bit event-struct decoding (synthetic frames).
- Device-name to profile mapping.

**What is not yet verified on real hardware (search the source for `TODO(rmpp-phase0)`):**

- Pen tilt and hover-distance axis ranges (placeholders match rM2 conventions).
- Touchscreen axis ranges (placeholders match the 1620 × 2160 display).
- Whether the pen suppresses touch in proximity (assumed true; rM2 behavior).
- That `uname -m` returns `aarch64` on the production firmware build (community sources strongly imply yes).

If you have a Paper Pro and want to help, run `tools/EventDiagnostics` against `/dev/input/event2` and `/dev/input/event3` and open an issue with the captured axis ranges. Until those replace the placeholders, treat Paper Pro support as *experimental*.

## Hardware details

### Pen digitizer (`/dev/input/event1`)

Confirmed via `evtest` on firmware version 1231 (Wacom I2C Digitizer).
Axis convention re-verified 2026-05-07 against the touchscreen — earlier docs
had ABS_X / ABS_Y rotated 180°.

| Axis | Range | Notes |
|------|-------|-------|
| ABS_X | 0 – 20966 | Long axis: 0 = top of device, max = USB/bottom (portrait) |
| ABS_Y | 0 – 15725 | Short axis: 0 = right, max = left (portrait) |
| Pressure | 0 – 4095 | 12-bit, mapped to 0–1024 via shaping curve (Windows Ink scale) |
| Distance | 0 – 255 | Hover height above surface |
| Tilt X/Y | −9000 – 9000 | Firmware units, mapped to ±90° |

### Touchscreen (`/dev/input/event2`)

Capacitive multi-touch panel, driver `pt_mt`. Confirmed via `evtest` 2026-05-07.

| Axis | Range | Notes |
|------|-------|-------|
| ABS_MT_POSITION_X | 0 – 1403 | Display-aligned, INPUT_PROP_DIRECT |
| ABS_MT_POSITION_Y | 0 – 1871 | Display-aligned, INPUT_PROP_DIRECT |
| ABS_MT_PRESSURE | 0 – 255 | Per-contact pressure |
| ABS_MT_SLOT | 0 – 31 | Hardware-reported; tool caps tracking at 5 |
| ABS_MT_TRACKING_ID | 0 – 65535 | Monotonically incrementing per-contact ID |
| Sample rate | ~85 Hz | Measured under continuous motion |

> **Pen-priority hardware behavior:** the rM2 firmware suppresses the
> touchscreen entirely while the pen is in proximity. This is verified — the
> `PenToolGate` was originally planned as a host-side workaround but turned
> out to be unnecessary. Workflow: lift the pen, gesture, then resume drawing.

> **Note on tilt:** tilt rotation matches the position transform that aligns
> with the touchscreen. The Windows Ink sign convention (positive tilt-X =
> pen leans toward +X screen axis) is not independently verified — if brush
> highlights point the wrong direction, the four cases in
> `CoordinateMapper.RotateTilt` are the place to flip signs.

## Hardware details — Paper Pro

> **Experimental.** Values below are from the [`Evidlo/remarkable_mouse`](https://github.com/Evidlo/remarkable_mouse) `rmpro` branch (Issue #92), not independently verified. See [Paper Pro status](#paper-pro-status) for what still needs hardware confirmation. The placement of pen / touch device nodes matches the community reports: `event0` = power button, `event1` = pen attach/detach, `event2` = pen, `event3` = touch.

### Pen digitizer (`/dev/input/event2`)

| Axis | Range | Notes |
|------|-------|-------|
| ABS_X | 0 – 11180 | Resolution 2832 ticks/mm |
| ABS_Y | 0 – 15340 | Resolution 2064 ticks/mm |
| Pressure | 0 – 4096 | 12-bit, mapped to 0–1024 via shaping curve |
| Tilt X/Y | ±9000 | Placeholder; not in the community data |
| Distance | 0 – 255 | Placeholder; not in the community data |

The active "Marker Plus" stylus is battery-powered and inductively charged, not Wacom EMR. Old rM2/LAMY EMR pens do not work on the Paper Pro.

### Touchscreen (`/dev/input/event3`)

| Axis | Range | Notes |
|------|-------|-------|
| ABS_MT_POSITION_X | 0 – 1619 | Placeholder; assumes display-aligned 1620 × 2160 |
| ABS_MT_POSITION_Y | 0 – 2159 | Placeholder; assumes display-aligned |
| ABS_MT_PRESSURE | 0 – 255 | Per-contact pressure (assumed; not verified) |

### Event struct

`struct input_event` on aarch64 Linux is 24 bytes: 8-byte `tv_sec`, 8-byte `tv_usec`, then `__u16 type`, `__u16 code`, `__s32 value`. Field offsets are 16 / 18 / 20 versus rM2's 8 / 10 / 12. The shared `EvdevParser` reads these from a per-profile `EvdevLayout`.

## License

MIT

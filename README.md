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
| `--orientation <value>` | `portrait` | `portrait`, `landscape`, `portraitflipped`, `landscapeflipped` — named by where the USB-C port sits |
| `--fit <value>` | `crop` | Aspect handling. `crop` (use a centred strip of the tablet matching the screen's shape — whole screen reachable, no distortion), `letterbox` (use the whole tablet, map to a centred part of the screen), or `stretch` (full tablet to full screen, distorts strokes) |
| `--output <value>` | `ink` | `ink` (full pressure+tilt) or `mouse` (cursor only, Windows only) |
| `--width <px>` | auto | Positive screen width in pixels; must be used with `--height` |
| `--height <px>` | auto | Positive screen height in pixels; must be used with `--width` |
| `--debug` | off | Print pipeline stage info on startup, and touch counters on exit (contacts dropped, stale releases, pen-gate closures) |
| `--gestures <value>` | `off` | `touch` (inject multi-touch contacts for pinch / pan / rotate) or `off`. The rM2 firmware suppresses touch while the pen is in proximity, so two-finger gestures only register when the pen is set aside. |
| `--pressure <value>` | `linear` | Pressure response curve. `linear` (1:1), `soft` (boosts light strokes — pen feels lighter), or `hard` (suppresses light strokes — pen feels stiffer). |
| `--device <value>` | `auto` | `auto` (probe via `uname -m`), `rm2`, or `rmpp`. Auto-detect runs a short SSH command before the streaming pipeline starts; force a specific profile only if detection fails. |
| `-h`, `--help` | — | Print usage and exit |
| `--version` | — | Print the version and exit |

Option names are case-sensitive; enumerated values are case-insensitive. Unknown, duplicate, incomplete, and conflicting options are rejected before connecting. `--output mouse` is available only in the Windows CLI.

## Orientation

Orientations are named by where the **USB-C port** ends up — the port is on a short
edge, so it identifies the rotation unambiguously. (Earlier docs said "pen slot at the
bottom", which is wrong: the Marker attaches magnetically to a *long* edge, so
following that literally left you holding the tablet 90° away from what the code
assumes.)

| Value | Physical position |
|-------|-------------------|
| `portrait` | USB-C port at the bottom (default drawing position) |
| `landscape` | Portrait rotated 90° counter-clockwise — USB-C port on the right |
| `portraitflipped` | Upside down — USB-C port at the top |
| `landscapeflipped` | Portrait rotated 90° clockwise — USB-C port on the left |

## Aspect ratio

The tablet surface is 3:4 (157.5 × 210 mm). Mapping all of it onto a wider screen
stretches every stroke — on a 1920×1080 display that is 1.33× horizontally in
landscape and 2.37× in portrait, which turns drawn circles into ellipses. `--fit`
controls what happens instead:

| Value | Behavior |
|-------|----------|
| `crop` (default) | Use a centred strip of the tablet with the screen's aspect ratio. Whole screen reachable, nothing distorted, outer strip of the tablet unused. |
| `letterbox` | Use the whole tablet surface, mapped onto a centred rectangle of the screen. Nothing on the tablet wasted, screen edges unreachable. |
| `stretch` | Full tablet to full screen, distortion included. The pre-0.4 behavior. |

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

## Palm rejection

Three layers, in the order they act:

1. **Firmware — half a solution.** The rM2 blocks *new* contacts while the pen is in
   proximity, so you can't start a gesture mid-stroke. But a contact that is already
   established keeps streaming right through: measured 2026-07-25, a held fingertip
   reported without interruption across three proximity windows, one at
   `ABS_DISTANCE 0`. A hand already resting when you start drawing is therefore fully
   visible to the host for the whole stroke, which is why layer 2 is not optional.
   (The Paper Pro's behavior here is unverified.)
2. **Pen proximity gate** (`Core/Pipeline/PenProximityGate.cs`, always on). While the
   pen is in range, touch is withheld and whatever the host was holding is released.
   Contacts that were already down when the pen arrived stay suppressed until they are
   lifted, so a resting hand can neither drag during the stroke nor spring back to
   life on pen-up. Driven from the pen loop rather than the touch loop, so it fires
   even if the panel says nothing. Stays closed for 150 ms after the pen leaves, so a
   hand still settling doesn't land.
3. **Stale-contact sweep** (`TouchOptions.StaleContactMs`, default 3 s). Backstop for a
   contact abandoned with no release event: it is dropped and its slot returned to the
   pool. **Precaution, not a fix for observed behavior** — no contact was abandoned in
   any of four capture sessions. Deliberately not shorter: a motionless contact on this
   panel can go over a second without reporting (measured: 1085 ms in
   `tools/EventDiagnostics/samples/touch-pen.log`), because the panel only reports on
   change.

A **contact-size filter** (`TouchOptions.MaxTouchMajor`) exists but is **off by
default**. The rM2 cannot report `MT_TOOL_PALM` — its `ABS_MT_TOOL_TYPE` range is 0–1
and the kernel's palm value is 2 — so size is the only available signal, and no
calibrated threshold exists yet. To set one: capture a palm rest and a fingertip with
`tools/EventDiagnostics` against `/dev/input/event2`, compare their
`ABS_MT_TOUCH_MAJOR` values, and pick a threshold between them. Contact size is
forwarded to hosts on Linux (`ABS_MT_TOUCH_MAJOR`/`MINOR`); on Windows it is not,
because `rcContact` is a pixel rectangle and this panel's size units are unknown —
see `MappedTouchContact` for the details.

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

- **Krita** — works out of the box with pressure and tilt. On a Wayland session launch it as
  `QT_QPA_PLATFORM=xcb krita`: Qt's native Wayland tablet path ignores the uinput device, while
  XWayland's X11 path picks it up. Verified on sway 2026-07-25.
- **GIMP** — enable extended input devices: Edit → Input Devices
- **Inkscape** — Input Devices dialog, set the virtual tablet to "Screen" mode
- **MyPaint** — works out of the box
- **Blender** — works with tablet pressure in sculpt and paint modes

The device uses `INPUT_PROP_DIRECT` so absolute coordinates map 1:1 to screen pixels. Pressure is reported on the 0–1024 scale, and hover height on `ABS_DISTANCE` (0–255).

Sanity check that the virtual device looks right to the input stack (libinput 1.31.3, 1920×1080 screen, default `--fit crop`):

```
$ sudo libinput list-devices          # while remtablet is running
Device:       reMarkable 2 Pen
Capabilities: tablet
Size:         160x90mm
```

`Capabilities: tablet` is what makes Wayland compositors and Krita treat it as a pen tablet rather than
a generic absolute pointer. The size comes from the declared axis resolution; it should be close to the
mapped tablet area (157.5 × 88.6 mm here — the small overshoot is the kernel's integer units-per-mm).
Something like `19x11mm` means the resolution is being derived from tablet ticks instead of screen
pixels.

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
| Krita                        | ✅         | ✅        | ✅        | Verified 2026-07-25 on sway/Wayland with `QT_QPA_PLATFORM=xcb`, rM2 over Wi-Fi, `--orientation landscape --gestures touch`. Pen pressure, pinch zoom, pan and twist all correct. Native Wayland Qt ignores the uinput tablet — use XWayland. |
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
- Surface size in millimetres, used for aspect-correct fitting (derived from the
  marketed 11.8" display, not measured).
- Whether the pen suppresses touch in proximity. No longer a correctness
  dependency — the host-side pen gate runs on every device — but it decides
  whether draw-plus-gesture is physically possible.
- That `uname -m` returns `aarch64` on the production firmware build (community sources strongly imply yes).

If you have a Paper Pro and want to help, run `tools/EventDiagnostics` against `/dev/input/event2` and `/dev/input/event3` and open an issue with the captured axis ranges. Until those replace the placeholders, treat Paper Pro support as *experimental*.

## Hardware details

### Pen digitizer (`/dev/input/event1`)

Confirmed via `evtest` on firmware version 1231 (Wacom I2C Digitizer).
Axis convention measured 2026-07-25 by corner calibration on real hardware — earlier docs
had ABS_X / ABS_Y rotated 180°.

| Axis | Range | Notes |
|------|-------|-------|
| ABS_X | 0 – 20966 | Long axis: **0 = bottom (USB edge), max = top** (portrait). Measured: 20258–20584 along the top edge |
| ABS_Y | 0 – 15725 | Short axis: **0 = left, max = right** (portrait). Measured: 672 at top-left, 15258 at top-right |
| Pressure | 0 – 4095 | 12-bit, mapped to 0–1024 via shaping curve (Windows Ink scale) |
| Distance | 0 – 255 | Hover height above surface |
| Tilt X/Y | −9000 – 9000 | Firmware units, mapped to ±90° |

### Touchscreen (`/dev/input/event2`)

Capacitive multi-touch panel, driver `pt_mt`. Confirmed via `evtest` 2026-05-07.

| Axis | Range | Notes |
|------|-------|-------|
| ABS_MT_POSITION_X | 0 – 1403 | Short axis: **0 = left**. Measured: 85 at top-left, 1379 at top-right |
| ABS_MT_POSITION_Y | 0 – 1871 | Long axis: **0 = bottom**. Measured: ≈1836 all along the top edge |
| ABS_MT_PRESSURE | 0 – 255 | Per-contact pressure |
| ABS_MT_SLOT | 0 – 31 | Hardware-reported; tool caps tracking at 5 |
| ABS_MT_TRACKING_ID | 0 – 65535 | Monotonically incrementing per-contact ID |
| Sample rate | ~85 Hz | Measured under continuous motion |

> **Origin, settled by measurement (2026-07-25).** The panel is `INPUT_PROP_DIRECT`,
> but that only means the digitizer overlays a display — it says nothing about which
> corner is the origin. Here the origin is the **bottom-left**, not the display's
> top-left, and the pen's axes are inverted on both axes relative to what the docs
> claimed. Until this was measured, pen and touch disagreed with each other by a
> horizontal mirror: a pen stroke on the physical top-left corner landed at the
> screen's bottom-right while a finger on the same spot landed bottom-left.
>
> Method and raw captures: [Corner calibration](../tools/EventDiagnostics/samples/README.md#corner-calibration-2026-07-25).
> Two corners of the *same edge*, not diagonal ones — diagonal corners can't tell a
> rotation from a mirror. The measured values are pinned as test data, including a
> cross-device test that pen and touch land within 40 px of each other.

> **Pen-priority hardware behavior — only half of it is real.** Measured
> 2026-07-25: the firmware blocks *new* contacts while the pen is in proximity, but a
> contact that was already established keeps streaming right through. A fingertip
> held down for 27 s reported without interruption (max gap 35 ms) across three
> proximity windows, one at `ABS_DISTANCE 0`. So a hand already resting when you
> start a stroke goes on injecting touch for the whole stroke unless the host stops
> it — which is what the pen gate is for (see [Palm rejection](#palm-rejection)).
> Workflow is unchanged: lift the pen, gesture, then resume drawing.

> **Note on tilt:** the tilt vector rotates with the corrected position transform —
> `+ABS_TILT_X` leans along `+ABS_X`, which the corner captures show points *up* the
> device (screen −Y in portrait), and `+ABS_TILT_Y` leans along `+ABS_Y`, which points
> right. All four cases were negated by the 2026-07-25 correction, since the pen axes
> turned out to be 180° out. What is still unverified is the *hardware's* sign
> convention — whether leaning the pen away from you increases or decreases
> `ABS_TILT_X`. If brush highlights point the wrong way, `CoordinateMapper.RotateTilt`
> is where to flip.

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

Maintained by [Keegan Ott](https://dreeko.me/).

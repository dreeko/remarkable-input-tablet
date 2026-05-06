# reMarkable 2 → Windows Drawing Tablet — Implementation Plan

## Context and goal

The reMarkable 2 (rM2) is an e-ink writing tablet whose Wacom EMR digitizer exposes full
pen telemetry: absolute X/Y position, pressure (4096 levels), tilt X/Y, hover distance, and
tool type (pen tip / eraser). The tablet runs 32-bit Linux on an ARM Cortex-A7 (i.MX7D SoC)
and is SSH-accessible over USB at `10.11.99.1` (or over WiFi at its DHCP address).

No existing public tool delivers a robust, pressure-sensitive, installable drawing tablet
experience on Windows. The closest project, `DCS-87/remarkable_mouse_winpress`, proves the
mechanism works but is a rough Python fork with no GUI, no reconnection, and no packaging.

**Goal:** A polished, distributable .NET 10 tool that makes the rM2 function as a
pressure-sensitive pen tablet on Windows 11, with full pressure, tilt, hover, and eraser
support — no kernel driver required for v1.

---

## Licensing decision — make this first

`DCS-87/remarkable_mouse_winpress` and `Evidlo/remarkable_mouse` are GPL-3.0. If you read
their source while writing the Windows pen injection layer, the derivative is GPL-3.0.

**Recommended approach (clean room):** Write the Windows output layer from Microsoft's public
Win32 documentation only. Do not read the Python fork's pen injection code. The API is
straightforward P/Invoke (~50 lines). This keeps the project license free to choose.

If you accept GPL-3.0, you can read the fork freely — just license the whole project GPL-3.0.
Decide and record the license in `LICENSE` before writing any code.

---

## Background: why this works

### Transport

The rM2 provides a root SSH shell over USB (`10.11.99.1:22`) and optionally over WiFi.
The pen digitizer exposes a standard Linux `evdev` input device at `/dev/input/event1`.
Touch input is at `/dev/input/event2` (not needed for v1).

Streaming `cat /dev/input/event1` over an SSH channel delivers a continuous binary stream
of `input_event` structs. The SSH connection requires the root password (found at
Settings → Help → Copyrights and licenses on the device) or an installed SSH key pair.

### evdev protocol

The Linux `input_event` struct on a 32-bit ARM system is **16 bytes**:

```c
struct input_event {
    uint32_t sec;    // 4 bytes — timestamp seconds
    uint32_t usec;   // 4 bytes — timestamp microseconds
    uint16_t type;   // 2 bytes — event type
    uint16_t code;   // 2 bytes — event code within type
    int32_t  value;  // 4 bytes — event value
};
// Total: 16 bytes, little-endian
```

The protocol batches related events into **frames** terminated by `EV_SYN / SYN_REPORT`
(`type=0, code=0`). Within a frame, you accumulate `EV_ABS` (absolute axis) and `EV_KEY`
(button) events, then emit a complete pen state snapshot on `SYN_REPORT`.

### Relevant event types and codes

```
Type 0  EV_SYN
  Code 0  SYN_REPORT    — end of frame, emit pen state
  Code 3  SYN_DROPPED   — kernel dropped events; flush and emit pen-up

Type 1  EV_KEY
  Code 320  BTN_TOOL_PEN      — pen tool in range (value 1=enter, 0=leave)
  Code 321  BTN_TOOL_RUBBER   — eraser tool in range
  Code 330  BTN_TOUCH         — pen tip touching surface
  Code 331  BTN_STYLUS        — barrel button 1 (side button on pen)
  Code 332  BTN_STYLUS2       — barrel button 2

Type 3  EV_ABS
  Code 0   ABS_X          — pen X position
  Code 1   ABS_Y          — pen Y position
  Code 24  ABS_PRESSURE   — tip pressure
  Code 25  ABS_DISTANCE   — hover distance from surface
  Code 26  ABS_TILT_X     — tilt around X axis
  Code 27  ABS_TILT_Y     — tilt around Y axis
```

### rM2 axis ranges

**Verify these empirically in Phase 0** using `evtest /dev/input/event1` on your specific
device and firmware. Do not trust values from blog posts. Record the kernel-reported
`min`, `max`, `fuzz`, `flat`, `resolution` for each axis.

Known values from `Evidlo/remarkable_mouse` (cross-check against your evtest output):

| Axis | Min | Max | Notes |
|---|---|---|---|
| ABS_X | 0 | 20966 | Landscape orientation, long axis |
| ABS_Y | 0 | 15725 | Landscape orientation, short axis |
| ABS_PRESSURE | 0 | 4095 | 4096 levels |
| ABS_DISTANCE | 0 | 255 | Hover distance (0 = touching) |
| ABS_TILT_X | ? | ? | Measure with evtest |
| ABS_TILT_Y | ? | ? | Measure with evtest |

**Orientation note:** The rM2's native coordinate space is landscape (pen slot at bottom).
When held in portrait (tall) orientation for drawing, X and Y need to be remapped.
In portrait mode: screen_x = native_y, screen_y = native_x_max - native_x.

### Windows output mechanism

**Windows Pointer Injection API (Win32, user32.dll)** — available since Windows 10 1809.
No kernel driver required. Works in user-mode from any desktop process.

```
CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_DEFAULT)
  → returns HSYNTHETICPOINTERDEVICE handle

InjectSyntheticPointerInput(device, &POINTER_TYPE_INFO, 1)
  → called per frame to inject pen state

DestroySyntheticPointerDevice(device)
  → called on shutdown
```

`POINTER_TYPE_INFO` with `PT_PEN` encodes: absolute screen position (pixels), pressure
(0–1024), tilt X/Y (−90° to +90°), pointer flags (INRANGE, INCONTACT, UP), and pen flags
(barrel button, inverted/eraser).

**App compatibility (Windows Ink path):**

| App | Notes |
|---|---|
| Krita | Enable "Windows 8+ Pointer Input" in Settings → Configure Krita → Tablet |
| Photoshop 2018+ | Default; or set `UseSystemStylus 1` in PSUserConfig.txt |
| Clip Studio Paint | Enable Windows Ink in tablet settings |
| Affinity Designer/Photo | Works out of the box |
| Procreate (Windows) | Works out of the box |
| ZBrush, old Photoshop | Wintab only — deferred to v2 with VMulti |

### Why not WinRT injection?

`Windows.UI.Input.Preview.Injection.InputInjector` (WinRT) also supports pen injection but
requires the `inputInjectionBrokered` restricted capability and an MSIX package manifest.
That forces MSIX packaging constraints. Use the Win32 API instead — same capability, no
packaging requirement.

---

## Technology stack

| Component | Choice | Rationale |
|---|---|---|
| Runtime | .NET 10 | LTS, AOT-mature, `System.IO.Pipelines` built-in |
| SSH | SSH.NET 2024.0.0+ | NativeAOT/trim-safe since 2024.0.0 release |
| Serialization | `System.Text.Json` with source generation | AOT-safe, no reflection |
| GUI | WPF (`net10.0-windows`) | Native Windows look, XAML |
| Tray | `System.Windows.Forms.NotifyIcon` | Available in net10.0-windows, works in WPF app |
| CLI build | NativeAOT self-contained | Single ~10–15 MB exe, no runtime install |
| App build | Self-contained trimmed (WPF blocks AOT) | ~30–50 MB, no runtime install |
| CI | GitHub Actions | Standard |
| Installer | WiX v4 or NSIS | No admin required for v1 (no driver) |

---

## Architecture

### Pipeline

```
rM2 /dev/input/event1
        │
        │  SSH over USB (10.11.99.1) or WiFi
        ▼
  SshTransport                    ← SSH.NET; wraps stdout as PipeReader
        │
        │  byte stream via System.IO.Pipelines PipeReader
        ▼
  EvdevParser                     ← reads 16-byte structs from PipeReader
        │
        │  EvdevEvent via Channel<EvdevEvent>(bounded, drop-oldest)
        ▼
  TabletStateMachine              ← accumulates events per EV_SYN frame
        │
        │  PenFrame via Channel<PenFrame>(bounded)
        ▼
  CoordinateMapper                ← tablet coords → screen coords + pressure curve
        │
        │  MappedFrame
        ▼
  WindowsInkOutput                ← Win32 CreateSyntheticPointerDevice + Inject
        │
        ▼
  Windows input system → drawing apps
```

Each stage runs in a single `async` loop. `Channel<T>` between stages decouples SSH read
timing from injection cadence and provides backpressure without blocking the SSH reader.
`System.IO.Pipelines` on the transport prevents per-event allocations at ~180 events/sec.

### Interface contracts

```csharp
// Transport
interface ITabletTransport : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct);
    PipeReader GetReader();
    event Action<ConnectionState> StateChanged;
}

// Output
interface IOutputMode : IDisposable
{
    void Initialize();
    void Send(MappedFrame frame);
}

// Domain types (all readonly record struct — no heap allocation in hot path)
readonly record struct EvdevEvent(ushort Type, ushort Code, int Value);

readonly record struct PenFrame(
    int X, int Y, int Pressure, int TiltX, int TiltY,
    int Distance, bool IsTouch, bool IsEraser,
    bool BarrelButton1, bool BarrelButton2, bool InRange);

readonly record struct MappedFrame(
    int ScreenX, int ScreenY,
    uint Pressure,   // 0–1024 (Windows Ink scale)
    int TiltX,       // −90 to +90 degrees
    int TiltY,
    bool IsTouch, bool IsEraser, bool BarrelButton1, bool InRange);
```

---

## Project structure

```
remarkable-input-tablet/
├── src/
│   ├── RemarkableTablet.Core/
│   │   ├── RemarkableTablet.Core.csproj
│   │   ├── Transport/
│   │   │   ├── ITabletTransport.cs
│   │   │   ├── SshTransport.cs
│   │   │   ├── ConnectionOptions.cs       # address, port, auth
│   │   │   └── ConnectionState.cs        # enum: Disconnected/Connecting/Connected
│   │   ├── Evdev/
│   │   │   ├── EvdevParser.cs
│   │   │   ├── EvdevEvent.cs
│   │   │   ├── EvdevTypes.cs             # EV_SYN=0, EV_KEY=1, EV_ABS=3
│   │   │   └── EvdevCodes.cs             # all relevant codes as const ushort
│   │   ├── Tablet/
│   │   │   ├── TabletStateMachine.cs
│   │   │   ├── PenFrame.cs
│   │   │   └── ReMarkable2Constants.cs   # axis ranges — populate from Phase 0 evtest
│   │   ├── Mapping/
│   │   │   ├── CoordinateMapper.cs
│   │   │   ├── PressureCurve.cs          # configurable Bézier, 4 control points
│   │   │   └── MappingOptions.cs
│   │   ├── Output/
│   │   │   ├── IOutputMode.cs
│   │   │   └── MappedFrame.cs
│   │   └── Pipeline/
│   │       └── TabletPipeline.cs         # wires all stages, owns CancellationTokenSource
│   │
│   ├── RemarkableTablet.Windows/
│   │   ├── RemarkableTablet.Windows.csproj
│   │   ├── Output/
│   │   │   └── WindowsInkOutput.cs
│   │   └── Interop/
│   │       ├── User32.cs                 # all DllImport declarations
│   │       └── PointerStructs.cs         # POINTER_INFO, POINTER_PEN_INFO, POINTER_TYPE_INFO
│   │
│   ├── RemarkableTablet.Cli/
│   │   ├── RemarkableTablet.Cli.csproj   # PublishAot=true, net10.0-windows
│   │   └── Program.cs
│   │
│   └── RemarkableTablet.App/
│       ├── RemarkableTablet.App.csproj   # WPF, net10.0-windows, self-contained trimmed
│       ├── App.xaml / App.xaml.cs
│       ├── TrayIcon.cs
│       ├── SettingsWindow.xaml / .cs
│       └── PressureCurveEditor.xaml / .cs
│
├── tests/
│   ├── RemarkableTablet.Core.Tests/
│   │   ├── EvdevParserTests.cs           # uses Phase 0 .bin fixture file
│   │   ├── TabletStateMachineTests.cs
│   │   └── CoordinateMapperTests.cs
│   └── RemarkableTablet.Windows.Tests/
│       └── WindowsInkOutputTests.cs
│
├── fixtures/
│   └── pen_capture.bin                   # captured in Phase 0 — commit this
│
├── .github/workflows/
│   └── build.yml
│
├── IMPLEMENTATION_PLAN.md                # this file
├── LICENSE
└── README.md
```

### Project file notes

**`RemarkableTablet.Core.csproj`** — pure logic, AOT-safe:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SSH.NET" Version="2024.*" />
  </ItemGroup>
</Project>
```

**`RemarkableTablet.Cli.csproj`** — NativeAOT single binary:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <OutputType>Exe</OutputType>
    <PublishAot>true</PublishAot>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <InvariantGlobalization>true</InvariantGlobalization>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

**`RemarkableTablet.App.csproj`** — WPF, trimmed (WPF blocks AOT):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <OutputType>WinExe</OutputType>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishTrimmed>true</PublishTrimmed>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

---

## Phase 0 — Validate prerequisites

**Complete before writing any code.** All subsequent phases depend on these results.

### 0.1 — Verify SSH access

On the rM2: Settings → Help → Copyrights and licenses — scroll to the very bottom to find
the root password and the USB IP address (should be `10.11.99.1`).

```powershell
ssh root@10.11.99.1
```

On firmware 3.x, SSH access may require enabling a developer mode toggle. If `ssh` hangs,
check reMarkable's current developer documentation for your firmware version.

If SSH works but disconnects intermittently over USB, try disabling USB power management:
```powershell
powercfg /setacvaluesetting SUB_USB 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0
```

### 0.2 — Capture a raw evdev fixture

With the rM2 connected via SSH, draw varied strokes (light, heavy, tilted), hover the pen
above the screen, use the eraser end, tap with the barrel button. Then:

```bash
# On rM2 via SSH — do not stop until you have ~10 seconds of drawing
cat /dev/input/event1 > /tmp/pen_capture.bin
# Ctrl-C to stop
```

Copy to PC and commit to `fixtures/pen_capture.bin`:
```powershell
scp root@10.11.99.1:/tmp/pen_capture.bin fixtures/pen_capture.bin
```

This file is the parser test fixture. Without it, parser tests cannot run.

### 0.3 — Read axis limits from the kernel

```bash
# On rM2 via SSH
evtest /dev/input/event1
```

If `evtest` is not installed, use `cat /proc/bus/input/devices` to find the device name,
then read raw ABS info:

```bash
python3 -c "
import struct, fcntl
EVIOCGABS = lambda axis: 0x40184540 | (axis << 16)  # IOCTL for abs info
with open('/dev/input/event1', 'rb') as f:
    for axis in [0,1,24,25,26,27]:  # X,Y,PRESSURE,DISTANCE,TILT_X,TILT_Y
        buf = bytearray(24)
        fcntl.ioctl(f, EVIOCGABS(axis), buf)
        vals = struct.unpack('<6i', buf)
        print(f'ABS {axis}: value={vals[0]} min={vals[1]} max={vals[2]} fuzz={vals[3]} flat={vals[4]} res={vals[5]}')
"
```

Record the output and fill in `ReMarkable2Constants.cs`:

```csharp
// ReMarkable2Constants.cs — populate from Phase 0 evtest/ioctl output
public static class ReMarkable2Constants
{
    // Pen axis ranges — verify these on your device and firmware
    public const int PenXMin = 0;
    public const int PenXMax = 20966;  // VERIFY
    public const int PenYMin = 0;
    public const int PenYMax = 15725;  // VERIFY
    public const int PressureMin = 0;
    public const int PressureMax = 4095; // VERIFY
    public const int DistanceMin = 0;
    public const int DistanceMax = 255;  // VERIFY
    public const int TiltXMin = -9600;   // VERIFY — units depend on firmware
    public const int TiltXMax = 9600;    // VERIFY
    public const int TiltYMin = -9600;   // VERIFY
    public const int TiltYMax = 9600;    // VERIFY

    // Event device path
    public const string PenDevicePath = "/dev/input/event1";

    // evdev struct size on 32-bit ARM
    public const int EventStructSize = 16;
}
```

### 0.4 — Verify the evdev struct size

With the capture file in hand, check that its size is a multiple of 16:

```powershell
(Get-Item fixtures\pen_capture.bin).Length % 16
# Should be 0
```

If it's a multiple of 24 instead, the tablet runs 64-bit userspace (unlikely for rM2 but
possible on custom firmware). Adjust `EventStructSize` accordingly.

### 0.5 — Verify Windows Ink injection is available

Write a minimal C# console app and run it:

```csharp
using System.Runtime.InteropServices;

[DllImport("user32.dll", SetLastError = true)]
static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);
[DllImport("user32.dll")]
static extern bool DestroySyntheticPointerDevice(IntPtr device);

const uint PT_PEN = 3;
const uint POINTER_FEEDBACK_DEFAULT = 1;
var handle = CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_DEFAULT);
Console.WriteLine(handle == IntPtr.Zero
    ? $"FAILED: {Marshal.GetLastWin32Error()}"
    : $"OK: handle={handle}");
DestroySyntheticPointerDevice(handle);
```

Expected output: `OK: handle=<some nonzero value>`. If it fails with error 5 (access denied),
the process needs to run with the "UIAccess" flag or the application must be in a trusted
location — not typically required for drawing tablet use, but note it if it occurs.

### 0.6 — Verify SSH.NET NativeAOT

Create a throwaway project with `<PublishAot>true</PublishAot>`, add SSH.NET 2024.0.0+,
call `new SshClient("10.11.99.1", "root", "password")`, and run `dotnet publish`. Confirm
there are zero AOT warnings from SSH.NET itself. Ignore warnings from your own code until
you write trim-safe JSON serialization.

---

## Phase 1 — Console MVP (cursor movement, no pressure)

**Goal:** Drawing on the rM2 moves the cursor on the Windows desktop. Verifies the full
pipeline end-to-end before adding complexity.

**Deliverable:** `remtablet.exe --address 10.11.99.1 --password <pw>` works.

### 1.1 — SSH transport (`SshTransport.cs`)

```csharp
public sealed class SshTransport : ITabletTransport
{
    private SshClient? _client;
    private SshCommand? _command;
    private Pipe? _pipe;

    public event Action<ConnectionState>? StateChanged;

    public async Task ConnectAsync(CancellationToken ct)
    {
        StateChanged?.Invoke(ConnectionState.Connecting);
        _client = new SshClient(options.Address, options.Port, options.Username, options.Auth);
        _client.Connect();

        _pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 64 * 1024));
        _command = _client.CreateCommand($"cat {ReMarkable2Constants.PenDevicePath}");

        // Start the SSH command and pump stdout into the PipeWriter on a background task
        _ = Task.Run(() => PumpAsync(_pipe.Writer, ct), ct);

        StateChanged?.Invoke(ConnectionState.Connected);
    }

    private async Task PumpAsync(PipeWriter writer, CancellationToken ct)
    {
        var stream = _command.ExecuteAsync();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var buffer = writer.GetMemory(ReMarkable2Constants.EventStructSize);
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break; // SSH stream closed
                writer.Advance(read);
                await writer.FlushAsync(ct);
            }
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    public PipeReader GetReader() => _pipe!.Reader;
}
```

Add reconnection in Phase 3. In Phase 1, fail fast and require manual restart.

### 1.2 — evdev parser (`EvdevParser.cs`)

```csharp
public static class EvdevParser
{
    // Reads from PipeReader, writes to Channel<EvdevEvent>
    // Runs as a long-lived async loop on a dedicated Task
    public static async Task RunAsync(
        PipeReader reader,
        ChannelWriter<EvdevEvent> output,
        CancellationToken ct)
    {
        const int Stride = ReMarkable2Constants.EventStructSize;

        while (!ct.IsCancellationRequested)
        {
            var result = await reader.ReadAtLeastAsync(Stride, ct);
            var buffer = result.Buffer;

            while (buffer.Length >= Stride)
            {
                // Copy to stack span to avoid heap allocation
                Span<byte> span = stackalloc byte[Stride];
                buffer.Slice(0, Stride).CopyTo(span);
                buffer = buffer.Slice(Stride);

                // Layout: sec(4) + usec(4) + type(2) + code(2) + value(4)
                var type  = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
                var code  = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
                var value = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);

                await output.WriteAsync(new EvdevEvent(type, code, value), ct);
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted) break;
        }

        output.Complete();
    }
}
```

### 1.3 — Tablet state machine (`TabletStateMachine.cs`)

```csharp
public sealed class TabletStateMachine
{
    // Mutable accumulator — stack-allocated equivalent, reset between frames
    private int _x, _y, _pressure, _tiltX, _tiltY, _distance;
    private bool _isTouch, _isEraser, _isPen, _barrel1, _barrel2;

    public static async Task RunAsync(
        ChannelReader<EvdevEvent> input,
        ChannelWriter<PenFrame> output,
        CancellationToken ct)
    {
        var sm = new TabletStateMachine();
        await foreach (var ev in input.ReadAllAsync(ct))
            sm.Process(ev, output);
    }

    private void Process(EvdevEvent ev, ChannelWriter<PenFrame> output)
    {
        switch (ev.Type)
        {
            case EvdevTypes.EV_SYN when ev.Code == EvdevCodes.SYN_REPORT:
                output.TryWrite(new PenFrame(
                    _x, _y, _pressure, _tiltX, _tiltY, _distance,
                    _isTouch, _isEraser, _barrel1, _barrel2, _isPen));
                break;

            case EvdevTypes.EV_SYN when ev.Code == EvdevCodes.SYN_DROPPED:
                // Kernel dropped events — emit pen-up and flush state
                _isTouch = false;
                output.TryWrite(new PenFrame(
                    _x, _y, 0, _tiltX, _tiltY, _distance,
                    false, _isEraser, false, false, _isPen));
                break;

            case EvdevTypes.EV_ABS:
                switch (ev.Code)
                {
                    case EvdevCodes.ABS_X:        _x        = ev.Value; break;
                    case EvdevCodes.ABS_Y:        _y        = ev.Value; break;
                    case EvdevCodes.ABS_PRESSURE: _pressure = ev.Value; break;
                    case EvdevCodes.ABS_DISTANCE: _distance = ev.Value; break;
                    case EvdevCodes.ABS_TILT_X:   _tiltX    = ev.Value; break;
                    case EvdevCodes.ABS_TILT_Y:   _tiltY    = ev.Value; break;
                }
                break;

            case EvdevTypes.EV_KEY:
                switch (ev.Code)
                {
                    case EvdevCodes.BTN_TOUCH:       _isTouch  = ev.Value != 0; break;
                    case EvdevCodes.BTN_TOOL_PEN:    _isPen    = ev.Value != 0; break;
                    case EvdevCodes.BTN_TOOL_RUBBER: _isEraser = ev.Value != 0; break;
                    case EvdevCodes.BTN_STYLUS:      _barrel1  = ev.Value != 0; break;
                    case EvdevCodes.BTN_STYLUS2:     _barrel2  = ev.Value != 0; break;
                }
                break;
        }
    }
}
```

### 1.4 — Coordinate mapper v1 (`CoordinateMapper.cs`)

Portrait orientation (tall, pen slot at bottom) maps native coordinates as:
- `screen_x = native_y / PenYMax * screen_width`
- `screen_y = (PenXMax - native_x) / PenXMax * screen_height`

Landscape orientation (held wide) maps straight through.

```csharp
public sealed class CoordinateMapper
{
    private readonly MappingOptions _opts;
    private readonly PressureCurve _curve;

    public MappedFrame Map(PenFrame frame)
    {
        double nx = frame.X / (double)ReMarkable2Constants.PenXMax;
        double ny = frame.Y / (double)ReMarkable2Constants.PenYMax;

        (double rx, double ry) = _opts.Orientation switch
        {
            Orientation.Portrait  => (ny, 1.0 - nx),
            Orientation.Landscape => (nx, ny),
            Orientation.PortraitFlipped  => (1.0 - ny, nx),
            Orientation.LandscapeFlipped => (1.0 - nx, 1.0 - ny),
            _ => (nx, ny)
        };

        // Apply tablet area crop (if user selected a sub-region)
        rx = (_opts.TabletAreaX + rx * _opts.TabletAreaW);
        ry = (_opts.TabletAreaY + ry * _opts.TabletAreaH);

        int sx = _opts.MonitorX + (int)(rx * _opts.MonitorW);
        int sy = _opts.MonitorY + (int)(ry * _opts.MonitorH);

        double normalizedPressure = frame.Pressure / (double)ReMarkable2Constants.PressureMax;
        double curvedPressure = _curve.Apply(normalizedPressure);
        uint windowsPressure = (uint)(curvedPressure * 1024.0);

        // Scale tilt from rM2 units to ±90 degrees
        int tiltX = (int)(frame.TiltX / (double)ReMarkable2Constants.TiltXMax * 90.0);
        int tiltY = (int)(frame.TiltY / (double)ReMarkable2Constants.TiltYMax * 90.0);

        return new MappedFrame(sx, sy, windowsPressure, tiltX, tiltY,
            frame.IsTouch, frame.IsEraser, frame.BarrelButton1, frame.InRange);
    }
}
```

### 1.5 — Phase 1 output: mouse movement

Use `SetCursorPos` and `mouse_event` P/Invoke for the first runnable version. This avoids
pen injection complexity until the pipeline is confirmed working.

```csharp
[DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
const uint MOUSEEVENTF_LEFTUP   = 0x0004;

void Send(MappedFrame f)
{
    SetCursorPos(f.ScreenX, f.ScreenY);
    if (f.IsTouch && !_wasTouch) mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
    if (!f.IsTouch && _wasTouch)  mouse_event(MOUSEEVENTF_LEFTUP,   0, 0, 0, UIntPtr.Zero);
    _wasTouch = f.IsTouch;
}
```

Replace with `WindowsInkOutput` in Phase 2.

### 1.6 — Console entry point

```csharp
// Program.cs (Phase 1 — minimal)
var address  = args.GetOption("--address", "10.11.99.1");
var password = args.GetOption("--password", "");
var orientation = args.GetOption("--orientation", "portrait");

var transport = new SshTransport(new ConnectionOptions(address, 22, "root", password));
await transport.ConnectAsync(ct);

var evdevChannel = Channel.CreateBounded<EvdevEvent>(new BoundedChannelOptions(512)
    { FullMode = BoundedChannelFullMode.DropOldest });
var frameChannel = Channel.CreateBounded<PenFrame>(new BoundedChannelOptions(64)
    { FullMode = BoundedChannelFullMode.DropOldest });

var mapper = new CoordinateMapper(MappingOptions.FullScreen(orientation));
var output = new MouseOutput();  // replaced with WindowsInkOutput in Phase 2

await Task.WhenAll(
    EvdevParser.RunAsync(transport.GetReader(), evdevChannel.Writer, ct),
    TabletStateMachine.RunAsync(evdevChannel.Reader, frameChannel.Writer, ct),
    OutputLoop(frameChannel.Reader, mapper, output, ct)
);
```

---

## Phase 2 — Windows Ink pen output (pressure + tilt + hover + eraser)

**Goal:** Replace mouse output with full Windows Pointer Injection. Apps receive pressure,
tilt, hover events, and eraser detection.

### 2.1 — P/Invoke declarations (`Interop/User32.cs` and `Interop/PointerStructs.cs`)

Transcribed from Microsoft Win32 documentation. Do not copy from other projects.

```csharp
// User32.cs
internal static partial class User32
{
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreateSyntheticPointerDevice(
        uint pointerType, uint maxCount, uint feedbackMode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InjectSyntheticPointerInput(
        IntPtr device,
        in POINTER_TYPE_INFO pointerInfo,
        uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroySyntheticPointerDevice(IntPtr device);

    internal const uint PT_PEN = 3;
    internal const uint POINTER_FEEDBACK_DEFAULT = 1;
}
```

```csharp
// PointerStructs.cs
[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X; public int Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct RECT { public int Left, Top, Right, Bottom; }

[Flags]
internal enum PointerFlags : uint
{
    None          = 0x00000000,
    New           = 0x00000001,
    InRange       = 0x00000002,
    InContact     = 0x00000004,
    FirstButton   = 0x00000010,
    SecondButton  = 0x00000020,
    ThirdButton   = 0x00000040,
    FourthButton  = 0x00000080,
    FifthButton   = 0x00000100,
    Primary       = 0x00002000,
    Confidence    = 0x00004000,
    Canceled      = 0x00008000,
    Down          = 0x00010000,
    Update        = 0x00020000,
    Up            = 0x00040000,
    Wheel         = 0x00080000,
    HWheel        = 0x00100000,
    CaptureChanged = 0x00200000,
    HasTransform  = 0x00400000,
}

[Flags]
internal enum PenFlags : uint
{
    None     = 0x00000000,
    Barrel   = 0x00000001,
    Inverted = 0x00000002,  // eraser end
    Eraser   = 0x00000004,
}

[Flags]
internal enum PenMask : uint
{
    None     = 0x00000000,
    Pressure = 0x00000001,
    Rotation = 0x00000002,
    TiltX    = 0x00000004,
    TiltY    = 0x00000008,
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_INFO
{
    public uint         pointerType;
    public uint         pointerId;
    public uint         frameId;
    public PointerFlags pointerFlags;
    public IntPtr       sourceDevice;
    public IntPtr       hwndTarget;
    public POINT        ptPixelLocation;
    public POINT        ptHimetricLocation;
    public POINT        ptPixelLocationRaw;
    public POINT        ptHimetricLocationRaw;
    public uint         dwTime;
    public uint         historyCount;
    public int          inputData;
    public uint         dwKeyStates;
    public ulong        PerformanceCount;
    public uint         ButtonChangeType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_PEN_INFO
{
    public POINTER_INFO pointerInfo;
    public PenFlags     penFlags;
    public PenMask      penMask;
    public uint         pressure;    // 0–1024
    public uint         rotation;    // 0–359 degrees
    public int          tiltX;       // −90 to +90
    public int          tiltY;       // −90 to +90
}

[StructLayout(LayoutKind.Explicit)]
internal struct POINTER_TYPE_INFO
{
    [FieldOffset(0)] public uint            type;
    [FieldOffset(4)] public POINTER_PEN_INFO penInfo;
}
```

### 2.2 — Windows Ink output (`WindowsInkOutput.cs`)

```csharp
public sealed class WindowsInkOutput : IOutputMode
{
    private IntPtr _device = IntPtr.Zero;
    private uint _frameId = 0;
    private bool _wasInContact = false;

    public void Initialize()
    {
        _device = User32.CreateSyntheticPointerDevice(
            User32.PT_PEN, 1, User32.POINTER_FEEDBACK_DEFAULT);
        if (_device == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CreateSyntheticPointerDevice failed: {Marshal.GetLastWin32Error()}");
    }

    public void Send(MappedFrame frame)
    {
        _frameId++;

        // Determine pointer flags
        var flags = PointerFlags.Update;
        if (frame.InRange)    flags |= PointerFlags.InRange;
        if (frame.IsTouch)    flags |= PointerFlags.InContact;
        if (!_wasInContact && frame.IsTouch) flags |= PointerFlags.Down;
        if (_wasInContact && !frame.IsTouch) flags = PointerFlags.Up | PointerFlags.InRange;

        _wasInContact = frame.IsTouch;

        var penInfo = new POINTER_PEN_INFO
        {
            pointerInfo = new POINTER_INFO
            {
                pointerType      = User32.PT_PEN,
                pointerId        = 0,
                frameId          = _frameId,
                pointerFlags     = flags,
                ptPixelLocation  = new POINT { X = frame.ScreenX, Y = frame.ScreenY },
            },
            penFlags = (frame.IsEraser    ? PenFlags.Inverted | PenFlags.Eraser : PenFlags.None)
                     | (frame.BarrelButton ? PenFlags.Barrel : PenFlags.None),
            penMask  = PenMask.Pressure | PenMask.TiltX | PenMask.TiltY,
            pressure = frame.Pressure,      // 0–1024
            tiltX    = frame.TiltX,         // −90 to +90
            tiltY    = frame.TiltY,
        };

        var typeInfo = new POINTER_TYPE_INFO
        {
            type    = User32.PT_PEN,
            penInfo = penInfo,
        };

        if (!User32.InjectSyntheticPointerInput(_device, in typeInfo, 1))
        {
            var err = Marshal.GetLastWin32Error();
            // Log but do not throw — a single failed injection is recoverable
        }
    }

    public void Dispose()
    {
        if (_device != IntPtr.Zero)
        {
            // Emit final pen-up before destroying device
            // (prevents stuck pen state in apps)
            User32.DestroySyntheticPointerDevice(_device);
            _device = IntPtr.Zero;
        }
    }
}
```

### 2.3 — Pressure curve (`PressureCurve.cs`)

A cubic Bézier curve with configurable control points. The default is a linear identity
curve (`(0,0)→(0.33,0.33)→(0.67,0.67)→(1,1)`). Artists can soften or harden pressure
response in the settings UI.

```csharp
public sealed class PressureCurve
{
    // p0 = (0,0), p3 = (1,1) always. p1 and p2 are the control handles.
    private readonly (double x, double y) _p1;
    private readonly (double x, double y) _p2;

    public static PressureCurve Linear() => new((0.33, 0.33), (0.67, 0.67));

    public double Apply(double t)
    {
        // De Casteljau, t → output [0,1]
        // p0=(0,0), p1=_p1, p2=_p2, p3=(1,1)
        double u = 1.0 - t;
        return 3 * u * u * t * _p1.y
             + 3 * u * t * t * _p2.y
             + t * t * t;
    }
}
```

### 2.4 — Krita test checklist

With `WindowsInkOutput` in place:
1. Open Krita. Go to Settings → Configure Krita → Tablet. Select "Windows 8+ Pointer Input". Restart.
2. Select a pressure-sensitive brush (e.g., Basic-5 Size).
3. Draw on canvas — verify strokes vary in width with pressure.
4. Hover pen above tablet — verify cursor appears without drawing.
5. Press harder/softer — verify smooth pressure gradation.
6. Enable a tilt-sensitive brush — verify tilt response.
7. Flip pen to eraser end — verify eraser tool activates.
8. Press barrel button — verify it registers in Krita input.

---

## Phase 3 — Robustness and performance

**Goal:** Reliable for multi-hour drawing sessions. Handles disconnection, firmware quirks,
and edge cases without user intervention.

### 3.1 — Reconnection with exponential backoff

Wrap the SSH connection loop in `SshTransport`:

```csharp
// Retry delays: 1s, 2s, 4s, 8s, 16s, 30s (cap), then repeat 30s
private static readonly TimeSpan[] BackoffDelays =
    [1, 2, 4, 8, 16, 30].Select(TimeSpan.FromSeconds).ToArray();

private async Task ConnectWithRetryAsync(CancellationToken ct)
{
    int attempt = 0;
    while (!ct.IsCancellationRequested)
    {
        try
        {
            StateChanged?.Invoke(ConnectionState.Connecting);
            await ConnectOnceAsync(ct);
            attempt = 0;
            StateChanged?.Invoke(ConnectionState.Connected);
            await WaitForDisconnectAsync(ct);  // blocks until SSH drops
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            StateChanged?.Invoke(ConnectionState.Disconnected);
            var delay = BackoffDelays[Math.Min(attempt++, BackoffDelays.Length - 1)];
            await Task.Delay(delay, ct);
        }
        finally
        {
            // Always emit pen-up before reconnecting to prevent stuck buttons
            _onPenUp?.Invoke();
        }
    }
}
```

### 3.2 — SSH key authentication

On first run (no key found), generate an RSA key pair and install the public key on the rM2:

```csharp
// Generate: RSA 4096-bit PEM
// Store private key: Windows Credential Manager (via CredRead/CredWrite P/Invoke)
//   or %APPDATA%\remarkable-input-tablet\id_rsa (mode 0600 equivalent via ACL)
// Install public key: SSH.NET SftpClient, append to /home/root/.ssh/authorized_keys
```

Prefer key auth over stored passwords for security and for passwordless reconnection.

### 3.3 — SYN_DROPPED handling

If `EV_SYN` with `code=3` (`SYN_DROPPED`) appears, the kernel ring buffer overflowed and
events were lost. The tablet state is now undefined. Reset all state and emit a pen-up
frame immediately. Log a warning — if this happens frequently, investigate SSH throughput
or reduce the scope of events being read.

### 3.4 — Latency measurement and logging

Log a rolling p50/p95/p99 of end-to-end latency (from `input_event.sec/usec` to
`InjectSyntheticPointerInput` call time). Expose this in the tray tooltip or a debug overlay.
Target: < 20 ms p99 on USB, < 50 ms p99 on WiFi.

### 3.5 — Unit tests against the Phase 0 fixture

```csharp
// EvdevParserTests.cs
[Fact]
public async Task ParsesFixtureWithoutErrors()
{
    var bytes = await File.ReadAllBytesAsync("fixtures/pen_capture.bin");
    var pipe = new Pipe();
    await pipe.Writer.WriteAsync(bytes);
    pipe.Writer.Complete();

    var channel = Channel.CreateUnbounded<EvdevEvent>();
    await EvdevParser.RunAsync(pipe.Reader, channel.Writer, CancellationToken.None);
    var events = await channel.Reader.ReadAllAsync().ToListAsync();

    Assert.NotEmpty(events);
    Assert.All(events, e => Assert.True(e.Type <= 31));  // valid type range
}

[Fact]
public async Task EmitsPenFramesOnSynReport()
{
    // Build a minimal synthetic byte sequence:
    // EV_ABS ABS_X 10000 | EV_ABS ABS_Y 8000 | EV_ABS ABS_PRESSURE 2048
    // EV_KEY BTN_TOUCH 1 | EV_SYN SYN_REPORT 0
    // Then verify TabletStateMachine emits one PenFrame with those values
}
```

Write at least:
- `ParsesFixtureWithoutErrors` — fixture parses to non-empty event list
- `EmitsPenFramesOnSynReport` — state machine emits frame on SYN_REPORT
- `ResetsPenStateOnSynDropped` — SYN_DROPPED causes pen-up emission
- `CoordinateMappingPortrait` — known input coords map to expected screen coords
- `PressureCurveLinearIdentity` — linear curve maps 0→0, 0.5→0.5, 1→1

---

## Phase 4 — Tray app and settings UI

**Goal:** Normal users can install and use the tool without a terminal or config files.

**Note on AOT:** WPF cannot be NativeAOT-compiled. The app project targets self-contained
trimmed. `Core` and `Windows` assemblies are AOT-safe and shared with the CLI.

### 4.1 — Application entry point

The WPF app starts hidden (no main window visible). A `NotifyIcon` in the system tray
provides the user-facing entry point.

```csharp
// App.xaml.cs
protected override void OnStartup(StartupEventArgs e)
{
    ShutdownMode = ShutdownMode.OnExplicitShutdown;
    _trayIcon = new TrayIcon();  // sets up NotifyIcon and pipeline
    base.OnStartup(e);
}
```

### 4.2 — Tray icon

```
Context menu:
  ● Connected to 10.11.99.1    ← status indicator (green dot / red dot)
  ───────────────────────────
  Open Settings...
  ───────────────────────────
  Exit
```

The icon itself changes between a "connected" and "disconnected" state by swapping the
`NotifyIcon.Icon` on `StateChanged` events from `SshTransport`.

Tooltip: `remarkable-input-tablet — Connected (USB) — 12ms p95`

### 4.3 — Settings window

Sections:

**Connection**
- Address field (default: `10.11.99.1`)
- Port (default: `22`)
- Auth: radio buttons → Password (masked text box) / SSH Key (auto-managed)
- [Test Connection] button — attempts SSH and reports success or error inline

**Mapping**
- Orientation: dropdown (Portrait / Landscape / Portrait Flipped / Landscape Flipped)
- Target monitor: dropdown populated from `Screen.AllScreens`
- Tablet area: optional crop via a visual rectangle editor (draw a region on a to-scale
  tablet diagram) — defaults to full tablet

**Pressure**
- Bézier pressure curve editor: WPF Canvas with 4 draggable handles, live preview bar
- [Reset to linear] button

**Output mode** (v1: only Windows Ink)
- Label: "Windows Ink (recommended for Krita, Photoshop 2018+, Affinity)"
- Link to setup instructions per app

**Startup**
- [x] Start with Windows (writes to `HKCU\...\Run`)

**Debug**
- [x] Show latency in tray tooltip
- Current latency: p50 / p95 / p99 display

### 4.4 — Settings persistence

```csharp
// Settings.cs
[JsonSerializable(typeof(AppSettings))]
partial class AppSettingsContext : JsonSerializerContext { }

public sealed class AppSettings
{
    public string Address { get; set; } = "10.11.99.1";
    public int Port { get; set; } = 22;
    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;
    public Orientation Orientation { get; set; } = Orientation.Portrait;
    public int MonitorIndex { get; set; } = 0;
    public TabletArea TabletArea { get; set; } = TabletArea.Full;
    public PressureCurveSettings Curve { get; set; } = PressureCurveSettings.Linear;
    public bool StartWithWindows { get; set; } = false;
    public bool ShowLatencyInTray { get; set; } = true;
}
```

Store at: `%APPDATA%\remarkable-input-tablet\settings.json`

Use `System.Text.Json` with source generation (the `[JsonSerializable]` attribute pattern
above) — required for trim-safe serialization.

Passwords are **never** written to this file. Store using Windows Credential Manager via
`CredRead`/`CredWrite` P/Invoke, target name `remarkable-input-tablet:password`.

---

## Phase 5 — Distribution

### 5.1 — Build targets

| Artifact | Project | Publish flags | Size |
|---|---|---|---|
| `remtablet.exe` | `RemarkableTablet.Cli` | NativeAOT, win-x64, InvariantGlobalization | ~10–15 MB |
| `RemtabletApp.exe` | `RemarkableTablet.App` | Self-contained trimmed, win-x64 | ~30–50 MB |

```powershell
# CLI
dotnet publish src/RemarkableTablet.Cli -c Release -r win-x64

# App
dotnet publish src/RemarkableTablet.App -c Release -r win-x64 `
  --self-contained true -p:PublishTrimmed=true -p:PublishSingleFile=true
```

### 5.2 — Installer

Use WiX v4. Requirements:
- No administrator privileges needed for v1 (no kernel driver)
- Install to `%LOCALAPPDATA%\remarkable-input-tablet\` (user-scoped, no UAC)
- Create a Start Menu shortcut to `RemtabletApp.exe`
- Optionally register auto-start on install (ask user)
- Uninstaller removes binaries, shortcut, auto-start entry (but not `%APPDATA%` settings)

### 5.3 — GitHub Actions CI

```yaml
# .github/workflows/build.yml
name: Build and Test
on: [push, pull_request]
jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.x'
      - run: dotnet build -c Release
      - run: dotnet test
      - name: Publish CLI
        if: startsWith(github.ref, 'refs/tags/')
        run: dotnet publish src/RemarkableTablet.Cli -c Release -r win-x64
      - name: Upload release artifact
        if: startsWith(github.ref, 'refs/tags/')
        uses: actions/upload-artifact@v4
        with:
          name: remtablet-win-x64
          path: src/RemarkableTablet.Cli/bin/Release/net10.0-windows/win-x64/publish/remtablet.exe
```

---

## Phase 6 (v2) — VMulti / Wintab support

This phase is intentionally deferred. Implement only after v1 ships.

**Motivation:** Some apps (ZBrush, Blender pre-4.x, Adobe Photoshop pre-2018) only support
Wintab — the legacy tablet API. Windows Ink injection is invisible to these apps.

**Mechanism:**
- VMulti is a signed kernel driver shipped with OpenTabletDriver (GPL-2.0)
- It creates a virtual USB HID device that Windows presents to apps as a real tablet
- Wintab reads from this virtual device exactly as it would from a physical Wacom tablet
- The driver is already signed and distributed by the OTD project

**Implementation steps:**
1. Add a `VMultiOutput` class implementing `IOutputMode`
2. Communicate with the VMulti driver via its named pipe protocol (documented in OTD source)
3. Add an output mode toggle in the settings UI
4. In the installer, optionally run OTD's VMulti driver installer (requires admin, UAC prompt)
5. Document which apps need this mode and how to configure them

**License note:** Communicating with VMulti via its pipe protocol does not make your code
GPL. Only incorporating VMulti's source into your project would. The driver is a separate
binary that users install separately (or your installer runs its own installer).

---

## Key constants and codes

```csharp
// EvdevTypes.cs
public static class EvdevTypes
{
    public const ushort EV_SYN = 0;
    public const ushort EV_KEY = 1;
    public const ushort EV_ABS = 3;
}

// EvdevCodes.cs
public static class EvdevCodes
{
    // EV_SYN codes
    public const ushort SYN_REPORT  = 0;
    public const ushort SYN_DROPPED = 3;

    // EV_KEY codes (pen)
    public const ushort BTN_TOOL_PEN    = 320;
    public const ushort BTN_TOOL_RUBBER = 321;
    public const ushort BTN_TOUCH       = 330;
    public const ushort BTN_STYLUS      = 331;
    public const ushort BTN_STYLUS2     = 332;

    // EV_ABS codes
    public const ushort ABS_X        = 0;
    public const ushort ABS_Y        = 1;
    public const ushort ABS_PRESSURE = 24;
    public const ushort ABS_DISTANCE = 25;
    public const ushort ABS_TILT_X   = 26;
    public const ushort ABS_TILT_Y   = 27;
}
```

---

## Reference links

| Topic | URL |
|---|---|
| Win32 InjectSyntheticPointerInput | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-injectsyntheticpointerinput |
| Win32 CreateSyntheticPointerDevice | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createsyntheticpointerdevice |
| POINTER_PEN_INFO struct | https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-pointer_pen_info |
| POINTER_INFO struct | https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-pointer_info |
| PointerFlags enum | https://learn.microsoft.com/en-us/windows/win32/api/winuser/ne-winuser-tagpointer_flags |
| SSH.NET 2024.0.0 (AOT) | https://github.com/sshnet/SSH.NET/releases/tag/2024.0.0 |
| System.IO.Pipelines docs | https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines |
| .NET 10 NativeAOT overview | https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ |
| OpenTabletDriver (VMulti) | https://github.com/OpenTabletDriver/OpenTabletDriver |
| remarkable_mouse (evdev codes) | https://github.com/Evidlo/remarkable_mouse |
| remarkable_mouse_winpress (proof of concept) | https://github.com/DCS-87/remarkable_mouse_winpress |
| reMarkable hacks info | https://remarkable.jms1.info/hacks/ |
| Krita tablet settings | https://docs.krita.org/en/KritaFAQ.html |
| WinTab vs Windows Ink overview | https://docs.thesevenpens.com/drawtab/developers/wintab-vs-windows-ink |

---

## Phase summary

| Phase | Deliverable | Prerequisite | Blocker risk |
|---|---|---|---|
| **0** | SSH confirmed, evdev .bin fixture, ABS constants, injection API tested | Hardware in hand | Medium — new firmware may gate SSH |
| **1** | Console exe moves cursor | Phase 0 complete | Low |
| **2** | Full pen injection: pressure, tilt, hover, eraser; works in Krita | Phase 1 | Low |
| **3** | Auto-reconnect, fixture-based tests, latency logging | Phase 2 | Low |
| **4** | WPF tray app, settings UI, pressure curve editor | Phase 3 | Low |
| **5** | Single-file distributables, WiX installer, GitHub Actions CI | Phase 4 | Low |
| **6 (v2)** | VMulti/Wintab output for legacy apps | Phase 5 | Medium — driver distribution |

---

## Decision log

| Decision | Choice | Rationale |
|---|---|---|
| Output API | Win32 `InjectSyntheticPointerInput` (not WinRT) | No MSIX/packaging requirement |
| Wintab support | Deferred to v2 | Covers ~95% of modern apps without kernel driver |
| Transport | SSH over USB primary, WiFi optional | Lower latency, no router dependency |
| Pipeline | `System.IO.Pipelines` + `Channel<T>` | Zero-alloc hot path, backpressure |
| CLI build | NativeAOT | Single binary, no runtime install, fast startup |
| App build | Self-contained trimmed (not AOT) | WPF is not AOT-compatible |
| Auth | SSH key (auto-managed) preferred over stored password | Security; enables passwordless reconnect |
| Settings serialization | `System.Text.Json` with source generation | Trim/AOT safe |
| Licensing | Clean-room Win32 layer → choose freely | Avoids GPL-3.0 from remarkable_mouse_winpress |

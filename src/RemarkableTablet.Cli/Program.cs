using RemarkableTablet.Core.Devices;
using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Pipeline;
using RemarkableTablet.Core.Transport;
#if WINDOWS_PLATFORM
using RemarkableTablet.Windows.Interop;
using RemarkableTablet.Windows.Output;
#elif LINUX_PLATFORM
using RemarkableTablet.Linux.Output;
#endif

// ── Parse args ───────────────────────────────────────────────────────────────
var address     = GetArg(args, "--address",     "10.11.99.1")!;
var password    = GetArg(args, "--password",    null);
var keyPath     = GetArg(args, "--key",         null);
var orientation = GetArg(args, "--orientation", "portrait")!;
var outputMode  = GetArg(args, "--output",      "ink")!;
var gestures    = GetArg(args, "--gestures",    "off")!;
var pressure    = GetArg(args, "--pressure",    "linear")!;
var debug       = args.Contains("--debug");

var widthArg  = ParseInt(GetArg(args, "--width",  null));
var heightArg = ParseInt(GetArg(args, "--height", null));

if (password is null && keyPath is null)
{
    await Console.Error.WriteLineAsync("Error: provide --password <pw> or --key <path>");
    await Console.Error.WriteLineAsync();
    await Console.Error.WriteLineAsync("Usage:");
#if WINDOWS_PLATFORM
    await Console.Error.WriteLineAsync("  remtablet --password <pw> [--address <ip>] [--orientation portrait|landscape] [--output ink|mouse] [--pressure linear|soft|hard] [--gestures touch|off] [--width <px>] [--height <px>] [--debug]");
#else
    await Console.Error.WriteLineAsync("  remtablet --password <pw> [--address <ip>] [--orientation portrait|landscape] [--pressure linear|soft|hard] [--gestures touch|off] [--width <px>] [--height <px>] [--debug]");
#endif
    await Console.Error.WriteLineAsync("  remtablet --key <path/to/id_rsa> [--address <ip>]");
    return 1;
}

// ── Resolve screen dimensions ─────────────────────────────────────────────────
int screenW, screenH;
#if WINDOWS_PLATFORM
// Declare per-monitor DPI awareness before querying screen metrics — otherwise
// GetSystemMetrics returns scaled pixels on high-DPI displays.
ScreenMetrics.EnablePerMonitorDpiAwareness();

if (widthArg > 0 && heightArg > 0)
{
    (screenW, screenH) = (widthArg, heightArg);
}
else
{
    (screenW, screenH) = ScreenMetrics.GetPrimarySize();
}
#elif LINUX_PLATFORM
if (widthArg > 0 && heightArg > 0)
{
    (screenW, screenH) = (widthArg, heightArg);
}
else
{
    Console.Error.WriteLine("Warning: --width/--height not specified; defaulting to 1920×1080.");
    Console.Error.WriteLine("Pass --width <px> --height <px> to match your display resolution.");
    (screenW, screenH) = (1920, 1080);
}
#else
#error Unsupported platform — add WINDOWS_PLATFORM or LINUX_PLATFORM to DefineConstants
#endif

// ── Build pipeline ────────────────────────────────────────────────────────────
var connOpts = keyPath is not null
    ? ConnectionOptions.WithKey(keyPath, address)
    : ConnectionOptions.WithPassword(password!, address);

var orient = orientation.ToLowerInvariant() switch
{
    "landscape"        => Orientation.Landscape,
    "portraitflipped"  => Orientation.PortraitFlipped,
    "landscapeflipped" => Orientation.LandscapeFlipped,
    _                  => Orientation.Portrait
};

var profile     = ReMarkable2Profile.Instance;
var mappingOpts = MappingOptions.ForScreen(screenW, screenH, orient);
var curve       = PressureCurve.FromName(pressure);
var mapper      = new CoordinateMapper(mappingOpts, profile, curve);

IOutputMode output;
#if WINDOWS_PLATFORM
output = outputMode == "mouse" ? new MouseOutput() : new WindowsInkOutput();
#elif LINUX_PLATFORM
output = new UinputOutput(screenW, screenH, profile.Pen);
#endif

// Touch wiring — currently only `touch` mode and only on Linux. Windows
// touch injection lands in M4; `synth` fallback in M5.
TouchCoordinateMapper? touchMapper = null;
ITouchOutput? touchOutput = null;
if (gestures == "touch")
{
    touchMapper = new TouchCoordinateMapper(mappingOpts, profile);
#if LINUX_PLATFORM
    touchOutput = new UinputTouchOutput(screenW, screenH, profile.Touch.MaxTracked);
#elif WINDOWS_PLATFORM
    touchOutput = new WindowsTouchInjectionOutput(profile.Touch.MaxTracked);
#endif
}
else if (gestures == "synth")
{
    Console.Error.WriteLine("Warning: --gestures synth is not yet implemented; ignoring.");
}
else if (gestures != "off")
{
    Console.Error.WriteLine($"Warning: unknown --gestures value '{gestures}'; expected touch|synth|off.");
}

var transport = new SshTransport(connOpts);
transport.StateChanged += state =>
{
    Console.ForegroundColor = state == ConnectionState.Connected
        ? ConsoleColor.Green
        : ConsoleColor.Yellow;
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {state}");
    Console.ResetColor();
};

await using var pipeline = new TabletPipeline(transport, profile, mapper, output, touchMapper, touchOutput);
pipeline.Error += ex =>
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error: {ex.Message}");
    Console.ResetColor();
};

// ── Shutdown on Ctrl-C ───────────────────────────────────────────────────────
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nStopping...");
    pipeline.Stop();
};

Console.WriteLine($"Connecting to {address} ({orient} orientation, {screenW}×{screenH})...");
Console.WriteLine("Press Ctrl-C to stop.");

if (debug)
    Console.WriteLine("[debug] Pipeline: SshTransport → EvdevParser → TabletStateMachine → CoordinateMapper → Output");

try
{
    await pipeline.RunAsync();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"Fatal: {ex.Message}");
    Console.ResetColor();
    return 1;
}

return 0;

// ── Helpers ───────────────────────────────────────────────────────────────────
static string? GetArg(string[] args, string flag, string? fallback)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

static int ParseInt(string? s) => int.TryParse(s, out var v) ? v : 0;

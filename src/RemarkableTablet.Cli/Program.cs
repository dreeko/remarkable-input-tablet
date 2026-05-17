using System.Net.Sockets;
using Renci.SshNet.Common;
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
var address = GetArg(args, "--address", "10.11.99.1")!;
var password = GetArg(args, "--password", null);
var keyPath = GetArg(args, "--key", null);
var orientation = GetArg(args, "--orientation", "portrait")!;
var outputMode = GetArg(args, "--output", "ink")!;
var gestures = GetArg(args, "--gestures", "off")!;
var pressure = GetArg(args, "--pressure", "linear")!;
var deviceArg = GetArg(args, "--device", "auto")!;
var debug = args.Contains("--debug");

var widthArg = ParseInt(GetArg(args, "--width", null));
var heightArg = ParseInt(GetArg(args, "--height", null));

if (password is null && keyPath is null)
{
    await Console.Error.WriteLineAsync("Error: provide --password <pw> or --key <path>");
    await Console.Error.WriteLineAsync();
    await Console.Error.WriteLineAsync("Usage:");
#if WINDOWS_PLATFORM
    await Console.Error.WriteLineAsync("  remtablet --password <pw> [--address <ip>] [--device auto|rm2|rmpp] [--orientation portrait|landscape] [--output ink|mouse] [--pressure linear|soft|hard] [--gestures touch|off] [--width <px>] [--height <px>] [--debug]");
#else
    await Console.Error.WriteLineAsync("  remtablet --password <pw> [--address <ip>] [--device auto|rm2|rmpp] [--orientation portrait|landscape] [--pressure linear|soft|hard] [--gestures touch|off] [--width <px>] [--height <px>] [--debug]");
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
    (screenW, screenH) = (widthArg, heightArg);
else
    (screenW, screenH) = ScreenMetrics.GetPrimarySize();
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
    "landscape" => Orientation.Landscape,
    "portraitflipped" => Orientation.PortraitFlipped,
    "landscapeflipped" => Orientation.LandscapeFlipped,
    _ => Orientation.Portrait
};

// Resolve the device profile: explicit name → that profile, "auto" → probe
// over SSH via `uname -m`. Auto-detect runs a short-lived SSH session here;
// the main pipeline then connects again on its own when RunAsync starts.
DeviceProfile profile;
if (deviceArg.Equals("auto", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"Detecting device at {address}...");
    try
    {
        await using var probeTransport = new SshTransport(connOpts);
        await probeTransport.ConnectAsync(CancellationToken.None);
        profile = await DeviceDetector.DetectAsync(probeTransport, CancellationToken.None);
        Console.WriteLine($"Detected: {profile.Name}");
    }
    catch (Exception ex)
    {
        return ReportFatal(ex, debug);
    }
}
else
{
    profile = DeviceDetector.ByName(deviceArg)
              ?? throw new ArgumentException(
                  $"Unknown --device '{deviceArg}'. Use auto, rm2, or rmpp.");
    Console.WriteLine($"Using profile: {profile.Name}");
}

var mappingOpts = MappingOptions.ForScreen(screenW, screenH, orient);
var curve = PressureCurve.FromName(pressure);
var mapper = new CoordinateMapper(mappingOpts, profile, curve);

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
    Console.Error.WriteLine("Warning: --gestures synth is not yet implemented; ignoring.");
else if (gestures != "off")
    Console.Error.WriteLine($"Warning: unknown --gestures value '{gestures}'; expected touch|synth|off.");

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
    return ReportFatal(ex, debug);
}

return 0;

// ── Helpers ───────────────────────────────────────────────────────────────────
static string? GetArg(string[] args, string flag, string? fallback)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

static int ParseInt(string? s)
{
    return int.TryParse(s, out var v) ? v : 0;
}

static int ReportFatal(Exception ex, bool debug)
{
    var (message, hint) = ClassifyFatal(ex);

    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"Error: {message}");
    Console.ResetColor();

    if (hint is not null)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Error.WriteLine($"Hint:  {hint}");
        Console.ResetColor();
    }

    if (debug)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("--- debug stack ---");
        Console.Error.WriteLine(ex.ToString());
    }

    return 1;
}

static (string Message, string? Hint) ClassifyFatal(Exception root)
{
    // SSH.NET wraps socket errors in SshConnectionException; AggregateException
    // wraps task faults. Walk to the most informative layer before classifying.
    var ex = root;
    while (ex.InnerException is not null && ex is not SocketException && ex is not SshException)
        ex = ex.InnerException;

    return ex switch
    {
        SocketException { SocketErrorCode: SocketError.HostUnreachable } =>
            ("No route to host. The reMarkable is not reachable.",
                "Confirm USB-Ethernet or Wi-Fi is up and that `ping <address>` succeeds."),
        SocketException { SocketErrorCode: SocketError.NetworkUnreachable } =>
            ("Network unreachable.",
                "The USB-Ethernet interface may be down — re-plug the device."),
        SocketException { SocketErrorCode: SocketError.TimedOut } =>
            ("Connection timed out.",
                "The reMarkable may be asleep — tap the screen to wake it."),
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            ("Connection refused.",
                "SSH is not enabled on the device. Turn on developer mode in Settings."),
        SocketException { SocketErrorCode: SocketError.HostNotFound } =>
            ("Host not found.",
                "Check the --address value."),
        SocketException se =>
            ($"Network error: {se.Message} ({se.SocketErrorCode}).", null),
        SshAuthenticationException =>
            ("SSH authentication failed.",
                "Wrong --password or --key. The root password is on the device under Settings → Help → Copyrights and licenses → 'GPLv3 Compliance'."),
        SshOperationTimeoutException =>
            ("SSH handshake timed out.",
                "The device is reachable but did not respond to SSH in time."),
        SshConnectionException sce =>
            ($"SSH connection failed: {sce.Message}.", null),
        _ => (root.Message, null)
    };
}

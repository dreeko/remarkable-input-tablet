#if WINDOWS_PLATFORM
using RemarkableTablet.Windows.Interop;
using RemarkableTablet.Windows.Output;
#elif LINUX_PLATFORM
using RemarkableTablet.Linux.Display;
using RemarkableTablet.Linux.Output;
#endif
using System.Net.Sockets;
using RemarkableTablet.Core.Devices;
using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Pipeline;
using RemarkableTablet.Core.Transport;
using Renci.SshNet.Common;

// ── Parse args ───────────────────────────────────────────────────────────────
if (args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage(Console.Out);
    return 0;
}

if (args.Contains("--version"))
{
    Console.WriteLine(typeof(TabletPipeline).Assembly.GetName().Version?.ToString(3) ?? "unknown");
    return 0;
}

if (ValidateArgs(args) is { } argumentError)
{
    Console.Error.WriteLine($"Error: {argumentError}");
    Console.Error.WriteLine("Run 'remtablet --help' for usage.");
    return 2;
}

var address = GetArg(args, "--address", "10.11.99.1")!;
var password = GetArg(args, "--password", null);
var keyPath = GetArg(args, "--key", null);
var orientation = GetArg(args, "--orientation", "portrait")!;
var outputMode = GetArg(args, "--output", "ink")!;
var gestures = GetArg(args, "--gestures", "off")!;
var pressure = GetArg(args, "--pressure", "linear")!;
var fitArg = GetArg(args, "--fit", "crop")!;
var deviceArg = GetArg(args, "--device", "auto")!;
var debug = args.Contains("--debug");

var widthArg = ParseInt(GetArg(args, "--width", null));
var heightArg = ParseInt(GetArg(args, "--height", null));

if (password is null && keyPath is null)
{
    await Console.Error.WriteLineAsync("Error: provide --password <pw> or --key <path>");
    await Console.Error.WriteLineAsync("Run 'remtablet --help' for usage.");
    return 2;
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
    var detected = LinuxScreenDetector.Detect();
    if (detected is { } screen)
    {
        (screenW, screenH) = (screen.Width, screen.Height);
        Console.WriteLine($"Detected Linux display: {screenW}×{screenH} ({screen.Source}).");
    }
    else
    {
        Console.Error.WriteLine("Warning: display detection failed; defaulting to 1920×1080.");
        Console.Error.WriteLine("Pass --width <px> --height <px> to set it explicitly.");
        (screenW, screenH) = (1920, 1080);
    }
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

var fit = fitArg.ToLowerInvariant() switch
{
    "stretch" => FitMode.Stretch,
    "letterbox" => FitMode.Letterbox,
    _ => FitMode.Crop
};

var mappingOpts = MappingOptions.ForScreen(screenW, screenH, orient, fit);
var curve = PressureCurve.FromName(pressure);
var mapper = new CoordinateMapper(mappingOpts, profile, curve);

IOutputMode output;
#if WINDOWS_PLATFORM
output = outputMode == "mouse" ? new MouseOutput() : new WindowsInkOutput();
#elif LINUX_PLATFORM
output = new UinputOutput(screenW, screenH, mapper.Transform, profile.Pen);
#endif

// Touch wiring — currently only `touch` mode and only on Linux. Windows
// touch injection lands in M4; `synth` fallback in M5.
TouchCoordinateMapper? touchMapper = null;
ITouchOutput? touchOutput = null;
if (gestures == "touch")
{
    // Share the pen's fitted geometry so pen and touch land on the same pixel.
    touchMapper = new TouchCoordinateMapper(mappingOpts, profile, mapper.Transform);
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

Console.WriteLine(
    $"Connecting to {address} ({orient} orientation, {fit} fit, {screenW}×{screenH})...");
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

if (debug && touchOutput is not null)
{
    var stats = pipeline.TouchStats;
    Console.WriteLine(
        $"[debug] touch: {stats.DroppedContacts} contact(s) dropped, " +
        $"{stats.StaleReleases} stale release(s), {stats.PenGateClosures} pen-gate closure(s)");
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

static string? ValidateArgs(string[] values)
{
    var valueFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "--address", "--password", "--key", "--orientation", "--output",
        "--gestures", "--pressure", "--device", "--width", "--height", "--fit"
    };
    var switchFlags = new HashSet<string>(StringComparer.Ordinal) { "--debug" };
    var seen = new HashSet<string>(StringComparer.Ordinal);

    for (var i = 0; i < values.Length; i++)
    {
        var token = values[i];
        if (!valueFlags.Contains(token) && !switchFlags.Contains(token))
            return token.StartsWith('-')
                ? $"unknown option '{token}'."
                : $"unexpected positional argument '{token}'.";
        if (!seen.Add(token))
            return $"option '{token}' was specified more than once.";
        if (!valueFlags.Contains(token)) continue;
        if (i + 1 >= values.Length || values[i + 1].StartsWith('-'))
            return $"option '{token}' requires a value.";
        i++;
    }

    if (seen.Contains("--password") && seen.Contains("--key"))
        return "--password and --key are mutually exclusive.";

    if (string.IsNullOrWhiteSpace(GetArg(values, "--address", "10.11.99.1")))
        return "--address cannot be empty.";

    var orientationValue = GetArg(values, "--orientation", "portrait")!;
    if (!OneOf(orientationValue, "portrait", "landscape", "portraitflipped", "landscapeflipped"))
        return
            $"invalid --orientation '{orientationValue}'; expected portrait, landscape, portraitflipped, or landscapeflipped.";

    var fitValue = GetArg(values, "--fit", "crop")!;
    if (!OneOf(fitValue, "crop", "letterbox", "stretch"))
        return $"invalid --fit '{fitValue}'; expected crop, letterbox, or stretch.";

    var pressureValue = GetArg(values, "--pressure", "linear")!;
    if (!OneOf(pressureValue, "linear", "soft", "hard"))
        return $"invalid --pressure '{pressureValue}'; expected linear, soft, or hard.";

    var gesturesValue = GetArg(values, "--gestures", "off")!;
    if (!OneOf(gesturesValue, "off", "touch"))
        return $"invalid --gestures '{gesturesValue}'; expected off or touch.";

    var deviceValue = GetArg(values, "--device", "auto")!;
    if (!OneOf(deviceValue, "auto", "rm2", "rmpp"))
        return $"invalid --device '{deviceValue}'; expected auto, rm2, or rmpp.";

    var outputValue = GetArg(values, "--output", "ink")!;
#if WINDOWS_PLATFORM
    if (!OneOf(outputValue, "ink", "mouse"))
        return $"invalid --output '{outputValue}'; expected ink or mouse.";
#else
    if (!OneOf(outputValue, "ink"))
        return $"invalid --output '{outputValue}'; Linux supports only ink.";
#endif

    var hasWidth = seen.Contains("--width");
    var hasHeight = seen.Contains("--height");
    if (hasWidth != hasHeight)
        return "--width and --height must be specified together.";
    if (hasWidth && (!int.TryParse(GetArg(values, "--width", null), out var width) || width <= 0))
        return "--width must be a positive integer.";
    if (hasHeight && (!int.TryParse(GetArg(values, "--height", null), out var height) || height <= 0))
        return "--height must be a positive integer.";

    var key = GetArg(values, "--key", null);
    if (key is not null && !File.Exists(key))
        return $"SSH key file '{key}' does not exist.";

    return null;
}

static bool OneOf(string value, params string[] allowed)
{
    return allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}

static void PrintUsage(TextWriter writer)
{
    writer.WriteLine("Use a reMarkable tablet as a native pen input device.");
    writer.WriteLine();
    writer.WriteLine("Usage:");
    writer.WriteLine("  remtablet (--password <pw> | --key <path>) [options]");
    writer.WriteLine();
    writer.WriteLine("Connection:");
    writer.WriteLine("  --address <host>       Tablet address (default: 10.11.99.1)");
    writer.WriteLine("  --password <pw>        Tablet root password");
    writer.WriteLine("  --key <path>           SSH private key file");
    writer.WriteLine("  --device <value>       auto, rm2, or rmpp (default: auto)");
    writer.WriteLine();
    writer.WriteLine("Mapping:");
    writer.WriteLine("  --orientation <value>  portrait, landscape, portraitflipped, or landscapeflipped");
    writer.WriteLine("  --fit <value>          crop (default, aspect-correct), letterbox, or stretch");
    writer.WriteLine("  --pressure <value>     linear, soft, or hard (default: linear)");
    writer.WriteLine("  --gestures <value>     off or touch (default: off)");
    writer.WriteLine("  --width <px>           Override detected screen width; requires --height");
    writer.WriteLine("  --height <px>          Override detected screen height; requires --width");
#if WINDOWS_PLATFORM
    writer.WriteLine("  --output <value>       ink or mouse (default: ink)");
#else
    writer.WriteLine("  --output <value>       ink (default: ink)");
#endif
    writer.WriteLine();
    writer.WriteLine("Other:");
    writer.WriteLine("  --debug                 Print pipeline details and fatal stack traces");
    writer.WriteLine("  -h, --help              Show this help");
    writer.WriteLine("  --version               Show version");
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
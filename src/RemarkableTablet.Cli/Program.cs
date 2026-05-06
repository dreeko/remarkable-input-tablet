using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Pipeline;
using RemarkableTablet.Core.Transport;
using RemarkableTablet.Windows.Output;

// ── Parse args ───────────────────────────────────────────────────────────────
var address = GetArg(args, "--address", "10.11.99.1")!;
var password = GetArg(args, "--password", null);
var keyPath = GetArg(args, "--key", null);
var orientation = GetArg(args, "--orientation", "portrait")!;
var outputMode = GetArg(args, "--output", "ink")!;
var debug = args.Contains("--debug");

if (password is null && keyPath is null)
{
    await Console.Error.WriteLineAsync("Error: provide --password <pw> or --key <path>");
    await Console.Error.WriteLineAsync();
    await Console.Error.WriteLineAsync("Usage:");
    await Console.Error.WriteLineAsync("  remtablet --password <pw> [--address <ip>] [--orientation portrait|landscape] [--output ink|mouse] [--debug]");
    await Console.Error.WriteLineAsync("  remtablet --key <path/to/id_rsa> [--address <ip>]");
    return 1;
}

// ── Build pipeline ────────────────────────────────────────────────────────────
var connOpts = keyPath is not null
    ? ConnectionOptions.WithKey(keyPath, address)
    : ConnectionOptions.WithPassword(password, address);

var orient = orientation.ToLowerInvariant() switch
{
    "landscape" => Orientation.Landscape,
    "portraitflipped" => Orientation.PortraitFlipped,
    "landscapeflipped" => Orientation.LandscapeFlipped,
    _ => Orientation.Portrait
};

var mappingOpts = MappingOptions.PrimaryMonitor(orient);
var mapper = new CoordinateMapper(mappingOpts);

IOutputMode output = outputMode == "mouse"
    ? new MouseOutput()
    : new WindowsInkOutput();

var transport = new SshTransport(connOpts);
transport.StateChanged += state =>
{
    Console.ForegroundColor = state == ConnectionState.Connected
        ? ConsoleColor.Green
        : ConsoleColor.Yellow;
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {state}");
    Console.ResetColor();
};

await using var pipeline = new TabletPipeline(transport, mapper, output);

// ── Shutdown on Ctrl-C ───────────────────────────────────────────────────────
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nStopping...");
    pipeline.Stop();
};

Console.WriteLine($"Connecting to {address} ({orient} orientation, {outputMode} output)...");
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

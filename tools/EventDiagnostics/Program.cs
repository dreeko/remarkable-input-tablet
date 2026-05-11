using System.Threading.Channels;
using RemarkableTablet.Core.Devices;
using RemarkableTablet.Core.Evdev;
using RemarkableTablet.Core.Transport;

// Usage: EventDiagnostics <host> <password>
//        EventDiagnostics <host> --key <path-to-private-key>
//        EventDiagnostics          (defaults: host=10.11.99.1, prompts for auth)
//
// Streams /dev/input/event1 from the rM2 and prints every evdev event live.
// EV_SYN/SYN_REPORT lines are suppressed to dots; BTN_TOUCH and ABS_PRESSURE highlighted.
// Press Ctrl+C to stop.

var host = "10.11.99.1";
string? password = null;
string? keyPath = null;

if (args.Length >= 1) host = args[0];

if (args.Length >= 3 && args[1] == "--key")
    keyPath = args[2];
else if (args.Length >= 2)
    password = args[1];
else
{
    Console.Write("Password (or press Enter to use SSH key at ~/.ssh/id_rsa): ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input))
    {
        keyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_rsa");
    }
    else
        password = input;
}

var connOpts = keyPath is not null
    ? ConnectionOptions.WithKey(keyPath, host)
    : ConnectionOptions.WithPassword(password!, host);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"Connecting to {host}...");
var transport = new SshTransport(connOpts);
transport.StateChanged += s => Console.WriteLine($"[transport] {s}");

await transport.ConnectAsync(cts.Token);

Console.WriteLine("Streaming events — move pen over tablet. Ctrl+C to stop.\n");

var channel = Channel.CreateBounded<EvdevEvent>(new BoundedChannelOptions(512)
{
    FullMode = BoundedChannelFullMode.DropOldest
});

var profile = ReMarkable2Profile.Instance;
var penStream = transport.OpenStream(profile.PenDevicePath, cts.Token);
var parseTask = EvdevParser.RunAsync(penStream.Reader, channel.Writer, profile.EventLayout, cts.Token);

// Tracks whether a dot was the last thing written (needs a newline before the next real line)
var needsNewline = false;

void MaybeNewline()
{
    if (!needsNewline) return;
    Console.WriteLine();
    needsNewline = false;
}

void PrintEvent(EvdevEvent ev)
{
    // Suppress chatty SYN_REPORT — print as a dim dot instead of a full line
    if (ev.Type == EvdevTypes.EV_SYN && ev.Code == EvdevCodes.SYN_REPORT)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write('.');
        Console.ResetColor();
        needsNewline = true;
        return;
    }

    MaybeNewline();

    var (typeName, codeName, color) = Decode(ev);
    Console.ForegroundColor = color;
    Console.WriteLine($"{typeName,-8} {codeName,-20} = {ev.Value}");
    Console.ResetColor();
}

try
{
    await foreach (var ev in channel.Reader.ReadAllAsync(cts.Token))
        PrintEvent(ev);
}
catch (OperationCanceledException) { }

MaybeNewline();
await parseTask;
await transport.DisposeAsync();
Console.WriteLine("Done.");

static (string type, string code, ConsoleColor color) Decode(EvdevEvent ev)
{
    return ev.Type switch
    {
        EvdevTypes.EV_SYN => ("EV_SYN", DecodeSyn(ev.Code), ConsoleColor.DarkGray),
        EvdevTypes.EV_KEY => ("EV_KEY", DecodeKey(ev.Code), KeyColor(ev.Code)),
        EvdevTypes.EV_ABS => ("EV_ABS", DecodeAbs(ev.Code), AbsColor(ev.Code, ev.Value)),
        _ => ($"EV_{ev.Type:X2}", $"code={ev.Code}", ConsoleColor.Gray)
    };
}

static string DecodeSyn(ushort code)
{
    return code switch
    {
        EvdevCodes.SYN_REPORT => "SYN_REPORT",
        EvdevCodes.SYN_DROPPED => "SYN_DROPPED",
        _ => $"code={code}"
    };
}

static string DecodeKey(ushort code)
{
    return code switch
    {
        EvdevCodes.BTN_TOOL_PEN => "BTN_TOOL_PEN",
        EvdevCodes.BTN_TOOL_RUBBER => "BTN_TOOL_RUBBER",
        EvdevCodes.BTN_TOUCH => "BTN_TOUCH",
        EvdevCodes.BTN_STYLUS => "BTN_STYLUS",
        EvdevCodes.BTN_STYLUS2 => "BTN_STYLUS2",
        _ => $"code={code}"
    };
}

static string DecodeAbs(ushort code)
{
    return code switch
    {
        EvdevCodes.ABS_X => "ABS_X",
        EvdevCodes.ABS_Y => "ABS_Y",
        EvdevCodes.ABS_PRESSURE => "ABS_PRESSURE",
        EvdevCodes.ABS_DISTANCE => "ABS_DISTANCE",
        EvdevCodes.ABS_TILT_X => "ABS_TILT_X",
        EvdevCodes.ABS_TILT_Y => "ABS_TILT_Y",
        _ => $"code={code}"
    };
}

static ConsoleColor KeyColor(ushort code)
{
    return code switch
    {
        EvdevCodes.BTN_TOUCH => ConsoleColor.Cyan,
        EvdevCodes.BTN_TOOL_PEN => ConsoleColor.Yellow,
        EvdevCodes.BTN_TOOL_RUBBER => ConsoleColor.Magenta,
        _ => ConsoleColor.White
    };
}

static ConsoleColor AbsColor(ushort code, int value)
{
    return code switch
    {
        EvdevCodes.ABS_PRESSURE => value > 0 ? ConsoleColor.Green : ConsoleColor.DarkGreen,
        EvdevCodes.ABS_DISTANCE => ConsoleColor.DarkYellow,
        _ => ConsoleColor.White
    };
}

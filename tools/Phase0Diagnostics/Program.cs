using Renci.SshNet;

string host     = args.Length > 0 ? args[0] : "10.11.99.1";
string password = args.Length > 1 ? args[1] : throw new Exception("Usage: Phase0Diagnostics <host> <password>");

Console.WriteLine($"Connecting to {host}...");
using var client = new SshClient(host, 22, "root", password);
client.Connect();
Console.WriteLine("Connected.\n");

Run("=== architecture ===", "uname -m");
Run("=== pen device (event1) ===", "cat /proc/bus/input/devices | grep -A8 'Wacom'");

// evtest for ABS ranges — show full output, not just matching lines
Console.WriteLine("=== ABS axis ranges (evtest) ===");
var evtest = client.RunCommand("evtest /dev/input/event1 &\nEVPID=$!\nsleep 2\nkill $EVPID 2>/dev/null\nwait $EVPID 2>/dev/null\necho '---evtest-done---'");
var evtestOut = evtest.Result;
// Print every line — min/max values are on lines following the ABS_X etc. labels
foreach (var line in evtestOut.Split('\n'))
    if (!string.IsNullOrWhiteSpace(line))
        Console.WriteLine(line);
Console.WriteLine();

// Capture events: background dd + blocking 5s sleep in same command, then sync
Console.WriteLine("=== Capturing pen events (5-second window) ===");
Console.WriteLine(">>> PICK UP YOUR PEN AND DRAW ON THE TABLET NOW <<<");
Console.WriteLine("(starting 5-second capture window...)");

// This single command: start dd in bg, sleep 5 so we block here, kill dd, sync
var capCmd = client.RunCommand(
    "rm -f /tmp/pen_capture.bin; " +
    "dd if=/dev/input/event1 bs=16 of=/tmp/pen_capture.bin 2>/dev/null & DDPID=$!; " +
    "sleep 5; " +
    "kill $DDPID 2>/dev/null; " +
    "wait $DDPID 2>/dev/null; " +
    "sync; " +
    "wc -c < /tmp/pen_capture.bin");
// RunCommand blocks until the remote shell exits — so this returns after ~5s
int capturedBytes = 0;
foreach (var line in capCmd.Result.Split('\n'))
    if (int.TryParse(line.Trim(), out int b)) { capturedBytes = b; break; }

Console.WriteLine($"Captured {capturedBytes} bytes = {capturedBytes / 16} events\n");

if (capturedBytes == 0)
{
    Console.WriteLine("WARNING: No events captured. Make sure you drew on the tablet.");
}
else
{
    if (capturedBytes % 16 == 0)
        Console.WriteLine($"✓ Struct size = 16 bytes (32-bit ARM, as expected)");
    else if (capturedBytes % 24 == 0)
        Console.WriteLine($"! Struct size = 24 bytes — update EventStructSize to 24 in ReMarkable2Constants.cs");

    // Event type distribution — BusyBox-compatible (use -n flag for head)
    Run("Event type distribution in capture",
        "hexdump -v -e '4/1 \"%02x\" \" \" 4/1 \"%02x\" \" \" 2/1 \"%02x\" \" \" 2/1 \"%02x\" \" \" 4/1 \"%02x\" \"\\n\"' /tmp/pen_capture.bin " +
        "| awk '{print $3}' | sort | uniq -c | sort -rn | head -n 10");

    // Verify file is readable and has the right size before SFTP
    Console.WriteLine("Verifying remote file...");
    var lsResult = client.RunCommand("ls -la /tmp/pen_capture.bin");
    Console.WriteLine(lsResult.Result.TrimEnd());

    // Download via SFTP
    Console.WriteLine("\nDownloading pen_capture.bin...");
    using var sftp = new SftpClient(host, 22, "root", password);
    sftp.Connect();

    Directory.CreateDirectory("fixtures");
    using var fs = File.Create("fixtures/pen_capture.bin");
    sftp.DownloadFile("/tmp/pen_capture.bin", fs);
    fs.Flush();
    sftp.Disconnect();

    var savedSize = new FileInfo("fixtures/pen_capture.bin").Length;
    Console.WriteLine($"✓ Saved fixtures/pen_capture.bin ({savedSize} bytes)");
    if (savedSize != capturedBytes)
        Console.WriteLine($"  WARNING: size mismatch! Remote={capturedBytes} Local={savedSize}");
    else
        Console.WriteLine("  Copy this to the project root fixtures/ folder if needed.");
}

client.Disconnect();
Console.WriteLine("\nPhase 0 complete.");

void Run(string label, string cmd)
{
    Console.WriteLine(label);
    var result = client.RunCommand(cmd);
    Console.WriteLine(result.Result.TrimEnd());
    if (!string.IsNullOrWhiteSpace(result.Error))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("stderr: ");
        Console.WriteLine(result.Error.TrimEnd());
        Console.ResetColor();
    }
    Console.WriteLine();
}

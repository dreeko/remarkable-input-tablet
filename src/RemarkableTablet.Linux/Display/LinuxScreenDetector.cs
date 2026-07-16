using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RemarkableTablet.Linux.Display;

public readonly record struct DetectedScreen(int Width, int Height, string Source);

/// <summary>
///     Detects the logical desktop size exposed by common Linux display stacks.
///     Explicit CLI dimensions should always take precedence over this result.
/// </summary>
public static partial class LinuxScreenDetector
{
    public static DetectedScreen? Detect()
    {
        var probes = new (string Command, string Arguments, Func<string, (int Width, int Height)?> Parse)[]
        {
            ("xrandr", "--current", ParseXrandr),
            ("kscreen-doctor", "-o", ParseKScreenDoctor),
            ("wlr-randr", "", ParseWlrRandr)
        };

        foreach (var (command, arguments, parse) in probes)
        {
            var output = Run(command, arguments);
            if (output is null) continue;
            var size = parse(output);
            if (size is { Width: > 0, Height: > 0 })
                return new DetectedScreen(size.Value.Width, size.Value.Height, command);
        }

        return DetectFromDrm();
    }

    public static (int Width, int Height)? ParseXrandr(string output)
    {
        var match = XrandrCurrentRegex().Match(output);
        return ParseMatch(match);
    }

    public static (int Width, int Height)? ParseKScreenDoctor(string output)
    {
        // Prefer the primary output. If no primary marker is present, use the
        // first enabled output geometry.
        var blocks = Regex.Split(output, @"(?m)(?=^Output:)");
        foreach (var primaryOnly in new[] { true, false })
        foreach (var block in blocks)
        {
            if (!block.Contains("enabled", StringComparison.OrdinalIgnoreCase)) continue;
            if (primaryOnly && !block.Contains("primary", StringComparison.OrdinalIgnoreCase)) continue;
            var match = KscreenGeometryRegex().Match(block);
            var size = ParseMatch(match);
            if (size is not null) return size;
        }

        return null;
    }

    public static (int Width, int Height)? ParseWlrRandr(string output)
    {
        var match = WlrCurrentModeRegex().Match(output);
        if (!match.Success)
            match = WlrPreferredModeRegex().Match(output);
        return ParseMatch(match);
    }

    private static DetectedScreen? DetectFromDrm()
    {
        try
        {
            foreach (var statusPath in
                     Directory.EnumerateFiles("/sys/class/drm", "status", SearchOption.AllDirectories))
            {
                if (!File.ReadAllText(statusPath).Trim().Equals("connected", StringComparison.OrdinalIgnoreCase))
                    continue;

                var modesPath = Path.Combine(Path.GetDirectoryName(statusPath)!, "modes");
                if (!File.Exists(modesPath)) continue;
                var mode = File.ReadLines(modesPath).FirstOrDefault();
                if (mode is null) continue;
                var size = ParseMatch(ModeRegex().Match(mode));
                if (size is not null)
                    return new DetectedScreen(size.Value.Width, size.Value.Height, "DRM sysfs");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static string? Run(string command, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            if (!process.WaitForExit(1500))
            {
                process.Kill(true);
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static (int Width, int Height)? ParseMatch(Match match)
    {
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out var width) ||
            !int.TryParse(match.Groups[2].Value, out var height) ||
            width <= 0 || height <= 0)
            return null;
        return (width, height);
    }

    [GeneratedRegex(@"\bcurrent\s+(\d+)\s+x\s+(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex XrandrCurrentRegex();

    [GeneratedRegex(@"Geometry:\s*[-+]?\d+\s*,\s*[-+]?\d+\s+(\d+)\s*x\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex KscreenGeometryRegex();

    [GeneratedRegex(@"(?m)^\s+(\d+)x(\d+).*\([^)]*\bcurrent\b[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex WlrCurrentModeRegex();

    [GeneratedRegex(@"(?m)^\s+(\d+)x(\d+).*\([^)]*\bpreferred\b[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex WlrPreferredModeRegex();

    [GeneratedRegex(@"^(\d+)x(\d+)$")]
    private static partial Regex ModeRegex();
}
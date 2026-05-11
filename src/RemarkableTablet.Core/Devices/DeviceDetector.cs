using RemarkableTablet.Core.Transport;

namespace RemarkableTablet.Core.Devices;

/// <summary>
///     Identifies the connected reMarkable model by probing the device's CPU
///     architecture over SSH. rM1/rM2 report <c>armv7l</c>; rMPP reports
///     <c>aarch64</c>. The transport must already be connected.
/// </summary>
public static class DeviceDetector
{
    public static async Task<DeviceProfile> DetectAsync(SshTransport transport, CancellationToken ct)
    {
        var arch = await transport.RunCommandAsync("uname -m", ct);
        return ResolveProfile(arch);
    }

    /// <summary>
    ///     Maps a string from <c>uname -m</c> to the matching profile. Exposed
    ///     for unit tests; production code should call <see cref="DetectAsync" />.
    /// </summary>
    public static DeviceProfile ResolveProfile(string unameOutput)
    {
        var arch = unameOutput.Trim().ToLowerInvariant();
        return arch switch
        {
            "armv7l" => ReMarkable2Profile.Instance,
            "aarch64" or "arm64" => ReMarkablePaperProProfile.Instance,
            _ => throw new InvalidOperationException(
                $"Unrecognised device architecture '{arch}'. " +
                "Expected armv7l (reMarkable 2) or aarch64 (Paper Pro). " +
                "Pass --device rm2|rmpp explicitly to override auto-detection.")
        };
    }

    /// <summary>
    ///     Selects a profile by short name. Used for the <c>--device</c> CLI
    ///     flag and the GUI dropdown.
    /// </summary>
    public static DeviceProfile? ByName(string? name)
    {
        return (name ?? "").Trim().ToLowerInvariant() switch
        {
            "rm2" or "remarkable2" or "remarkable 2" => ReMarkable2Profile.Instance,
            "rmpp" or "paperpro" or "paper pro" or "remarkable paper pro" => ReMarkablePaperProProfile.Instance,
            _ => null
        };
    }
}
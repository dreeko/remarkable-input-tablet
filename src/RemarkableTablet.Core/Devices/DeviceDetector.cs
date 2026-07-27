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
    ///     Reads <c>/proc/bus/input/devices</c> and locates this profile's pen and
    ///     touch nodes by driver name, falling back to the profile's hard-coded
    ///     paths. Never throws: a failed probe simply yields the fallbacks, since
    ///     a stale path is no worse than not having looked.
    /// </summary>
    public static async Task<ResolvedInputDevices> ResolveDevicesAsync(
        SshTransport transport, DeviceProfile profile, CancellationToken ct)
    {
        try
        {
            var table = await transport.RunCommandAsync("cat /proc/bus/input/devices", ct);
            return InputDeviceMap.Parse(table).Resolve(profile);
        }
        catch (Exception ex)
        {
            return new ResolvedInputDevices(
                profile.PenDevicePath,
                profile.TouchDevicePath,
                [$"could not read /proc/bus/input/devices ({ex.Message}); using default node paths"]);
        }
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
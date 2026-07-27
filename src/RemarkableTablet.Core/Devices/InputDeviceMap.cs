namespace RemarkableTablet.Core.Devices;

/// <summary>
///     The device's <c>/proc/bus/input/devices</c> table, parsed into name →
///     node-path pairs.
///     <para>
///         Event node numbering is the most fragile assumption in this codebase:
///         it differs per model (rM1 pen on event0, rM2 on event1, rMPP on event2)
///         and is not guaranteed across firmware revisions, since it reflects
///         driver probe order. Hard-coding it fails as a silent hang — the stream
///         opens against the wrong device and simply never produces pen events.
///         Resolving by name turns that into either a correct connection or a
///         precise error.
///     </para>
/// </summary>
public sealed class InputDeviceMap
{
    private readonly IReadOnlyList<InputDeviceEntry> _entries;

    private InputDeviceMap(IReadOnlyList<InputDeviceEntry> entries)
    {
        _entries = entries;
    }

    public IReadOnlyList<InputDeviceEntry> Entries => _entries;

    /// <summary>
    ///     Parses the output of <c>cat /proc/bus/input/devices</c>. Records are
    ///     blank-line separated; we need the <c>N: Name="…"</c> and
    ///     <c>H: Handlers=… eventN …</c> lines and can ignore the rest.
    /// </summary>
    public static InputDeviceMap Parse(string procOutput)
    {
        var entries = new List<InputDeviceEntry>();
        string? name = null;
        string? node = null;

        foreach (var raw in procOutput.Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith("N: Name=", StringComparison.Ordinal))
            {
                name = line["N: Name=".Length..].Trim().Trim('"');
            }
            else if (line.StartsWith("H: Handlers=", StringComparison.Ordinal))
            {
                foreach (var handler in line["H: Handlers=".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (handler.StartsWith("event", StringComparison.Ordinal))
                    {
                        node = "/dev/input/" + handler;
                        break;
                    }
            }
            else if (line.Length == 0)
            {
                if (name is not null && node is not null) entries.Add(new InputDeviceEntry(name, node));
                name = null;
                node = null;
            }
        }

        // Trailing record with no blank line after it.
        if (name is not null && node is not null) entries.Add(new InputDeviceEntry(name, node));

        return new InputDeviceMap(entries);
    }

    /// <summary>
    ///     Node for the device whose name matches <paramref name="deviceName" />,
    ///     or null. Three passes, narrowest first: exact, prefix, then a
    ///     normalised substring match ignoring case, spaces and underscores.
    ///     <para>
    ///         The looseness is deliberate. A name that drifts across firmware
    ///         revisions ("Wacom I2C Digitizer" vs "wacom_i2c") should still
    ///         resolve, because the alternative — falling back to a hard-coded
    ///         node path and warning about it — is noisier and less correct than
    ///         matching the obvious candidate. Passes are ordered so a loose match
    ///         can never win over an exact one.
    ///     </para>
    /// </summary>
    public string? FindByName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return null;

        foreach (var e in _entries)
            if (e.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                return e.Node;

        foreach (var e in _entries)
            if (e.Name.StartsWith(deviceName, StringComparison.OrdinalIgnoreCase))
                return e.Node;

        var needle = Normalize(deviceName);
        foreach (var e in _entries)
            if (Normalize(e.Name).Contains(needle, StringComparison.Ordinal))
                return e.Node;

        return null;
    }

    private static string Normalize(string s)
    {
        return string.Concat(s.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    /// <summary>
    ///     Resolves the profile's pen and touch nodes against this table. Falls
    ///     back to the profile's hard-coded path for anything not found, and
    ///     reports what happened so the caller can warn rather than fail silently.
    /// </summary>
    public ResolvedInputDevices Resolve(DeviceProfile profile)
    {
        var pen = FindByName(profile.PenDeviceName);
        var touch = FindByName(profile.TouchDeviceName);

        var notes = new List<string>();

        if (pen is null && profile.PenDeviceName is not null)
            notes.Add($"pen device '{profile.PenDeviceName}' not found; using {profile.PenDevicePath}");
        else if (pen is not null && pen != profile.PenDevicePath)
            notes.Add($"pen device moved: expected {profile.PenDevicePath}, found at {pen}");

        if (touch is null && profile.TouchDeviceName is not null)
            notes.Add($"touch device '{profile.TouchDeviceName}' not found; using {profile.TouchDevicePath}");
        else if (touch is not null && touch != profile.TouchDevicePath)
            notes.Add($"touch device moved: expected {profile.TouchDevicePath}, found at {touch}");

        // A touch panel we can't find by name usually means a different driver is
        // bound — a custom or mainline kernel rather than stock firmware. The
        // axis conventions this tool maps with were measured on stock firmware and
        // are not guaranteed to hold there, so say so plainly.
        if (touch is null && profile.TouchDeviceName is not null && _entries.Count > 0)
            notes.Add(
                "the touch driver does not match this profile — if you are running a custom or " +
                "mainline kernel, the coordinate mapping may be wrong (see docs: axis conventions " +
                "were measured on stock firmware)");

        return new ResolvedInputDevices(
            pen ?? profile.PenDevicePath,
            touch ?? profile.TouchDevicePath,
            notes);
    }
}

public sealed record InputDeviceEntry(string Name, string Node);

/// <summary>Node paths to stream from, plus anything worth telling the user.</summary>
public sealed record ResolvedInputDevices(string PenPath, string TouchPath, IReadOnlyList<string> Notes);

using System.Globalization;
using RemarkableTablet.Core.Devices;

namespace RemarkableTablet.Core.Mapping;

/// <summary>
///     A sub-rectangle of the tablet surface, as fractions of the surface in the
///     chosen orientation (so <c>X</c> runs along whichever edge is horizontal once
///     rotated). Only this part of the tablet is live, and it spans the whole
///     target rectangle.
/// </summary>
public readonly record struct TabletArea(double X, double Y, double W, double H)
{
    public static readonly TabletArea Full = new(0, 0, 1, 1);

    /// <summary>
    ///     Parses <c>x,y,w,h</c> where each value is either a fraction of the
    ///     surface (<c>0.25,0,0.5,1</c>) or a millimetre length with an <c>mm</c>
    ///     suffix (<c>20mm,0mm,100mm,210mm</c>). Millimetres are converted using
    ///     the profile's surface size, rotated to match the orientation.
    ///     <para>
    ///         Shared by the CLI and the settings dialog so the two can't drift —
    ///         a flag that accepts millimetres in one and not the other is the kind
    ///         of difference nobody notices until it bites.
    ///     </para>
    /// </summary>
    /// <returns>True when parsed; otherwise false, with <paramref name="error" /> set.</returns>
    public static bool TryParse(
        string? spec,
        DeviceProfile profile,
        Orientation orientation,
        out TabletArea area,
        out string? error)
    {
        area = Full;
        error = null;

        if (string.IsNullOrWhiteSpace(spec)) return true;

        error = ValidateSyntax(spec);
        if (error is not null) return false;

        var parts = spec.Split(',');

        var landscape = orientation is Orientation.Landscape or Orientation.LandscapeFlipped;
        var widthMm = landscape ? profile.Surface.HeightMm : profile.Surface.WidthMm;
        var heightMm = landscape ? profile.Surface.WidthMm : profile.Surface.HeightMm;

        var v = new double[4];
        for (var i = 0; i < 4; i++)
        {
            var raw = parts[i].Trim();
            var isMm = raw.EndsWith("mm", StringComparison.OrdinalIgnoreCase);
            var body = isMm ? raw[..^2].Trim() : raw;
            var value = double.Parse(body, NumberStyles.Float, CultureInfo.InvariantCulture);

            // x/w scale by the horizontal extent, y/h by the vertical one.
            v[i] = isMm ? value / (i % 2 == 0 ? widthMm : heightMm) : value;
        }

        var candidate = new TabletArea(v[0], v[1], v[2], v[3]);

        if (candidate.W <= 0 || candidate.H <= 0)
        {
            error = "the active area needs a positive width and height.";
            return false;
        }

        // A millimetre value larger than the surface is the likely mistake here,
        // so say what the surface actually is rather than just rejecting.
        const double slack = 1.0001;
        if (candidate.X + candidate.W > slack || candidate.Y + candidate.H > slack)
        {
            error = $"the active area runs past the edge of the tablet " +
                    $"({widthMm:0.#} × {heightMm:0.#} mm in this orientation).";
            return false;
        }

        area = candidate;
        return true;
    }

    /// <summary>
    ///     Checks the spec's shape without needing a device: four comma-separated
    ///     non-negative numbers, each optionally suffixed with <c>mm</c>. Returns
    ///     null when it looks usable.
    ///     <para>
    ///         Separate from <see cref="TryParse" /> because argument validation
    ///         happens before the device is probed, and whether a millimetre area
    ///         fits depends on which tablet is attached and how it's rotated. The
    ///         full check runs once the profile is known.
    ///     </para>
    /// </summary>
    public static string? ValidateSyntax(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;

        var parts = spec.Split(',');
        if (parts.Length != 4)
            return $"'{spec}' should be four comma-separated values, " +
                   "e.g. 0,0.25,1,0.5 or 0mm,50mm,157mm,100mm.";

        foreach (var part in parts)
        {
            var raw = part.Trim();
            var body = raw.EndsWith("mm", StringComparison.OrdinalIgnoreCase) ? raw[..^2].Trim() : raw;

            if (!double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                return $"'{raw}' should be a non-negative number, optionally suffixed with mm.";
        }

        return null;
    }
}

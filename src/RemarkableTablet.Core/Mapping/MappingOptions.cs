namespace RemarkableTablet.Core.Mapping;

public enum Orientation
{
    Portrait, // USB-C port at the bottom (default drawing position)
    Landscape, // Portrait rotated 90° counter-clockwise — USB-C port on the right
    PortraitFlipped, // Portrait upside down — USB-C port at the top
    LandscapeFlipped // Portrait rotated 90° clockwise — USB-C port on the left
}

/// <summary>
///     How the tablet surface is fitted to a screen whose aspect ratio differs
///     from the tablet's. The rM2 surface is 3:4; on a 16:9 display a stretched
///     mapping distorts by 1.33× in landscape and 2.37× in portrait, so circles
///     come out as ellipses.
/// </summary>
public enum FitMode
{
    /// <summary>
    ///     Shrink the active tablet area to the screen's aspect ratio (centred).
    ///     The whole screen stays reachable; a strip of the tablet goes unused.
    ///     Default — this is what a drawing tablet normally wants.
    /// </summary>
    Crop,

    /// <summary>
    ///     Map the full tablet surface onto the largest centred screen rectangle
    ///     with the tablet's aspect ratio. Nothing on the tablet is wasted; the
    ///     screen gets unreachable borders.
    /// </summary>
    Letterbox,

    /// <summary>Full tablet to full screen, distortion and all (pre-0.4 behavior).</summary>
    Stretch
}

/// <summary>
///     Defines how tablet coordinates map to the host screen.
/// </summary>
public sealed class MappingOptions
{
    // Target monitor bounds in screen pixels
    public int MonitorX { get; init; }
    public int MonitorY { get; init; }
    public int MonitorW { get; init; }
    public int MonitorH { get; init; }

    // Active tablet area as a fraction of the full surface, in the *rotated*
    // (screen-aligned) frame: X/Y are the top-left corner, W/H the extent.
    // Only this sub-rectangle of the tablet is live, and it spans the whole
    // target rectangle. Default: the full tablet (0,0,1,1).
    public double TabletAreaX { get; init; }
    public double TabletAreaY { get; init; }
    public double TabletAreaW { get; init; } = 1.0;
    public double TabletAreaH { get; init; } = 1.0;

    public Orientation Orientation { get; init; } = Orientation.Portrait;

    /// <summary>Aspect-ratio handling. See <see cref="FitMode" />.</summary>
    public FitMode Fit { get; init; } = FitMode.Crop;

    /// <summary>Map the full area of a screen with explicit dimensions.</summary>
    public static MappingOptions ForScreen(
        int w,
        int h,
        Orientation orientation = Orientation.Portrait,
        FitMode fit = FitMode.Crop)
    {
        return new MappingOptions
        {
            MonitorX = 0, MonitorY = 0, MonitorW = w, MonitorH = h, Orientation = orientation, Fit = fit
        };
    }
}

namespace RemarkableTablet.Core.Mapping;

public enum Orientation
{
    Portrait, // rM2 held tall, pen slot at bottom (default drawing position)
    Landscape, // rM2 held wide, pen slot on right
    PortraitFlipped, // Portrait, upside down
    LandscapeFlipped // Landscape, upside down
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

    // Active tablet area as fraction of full tablet (0.0–1.0)
    // Default: full tablet (0,0,1,1)
    public double TabletAreaX { get; init; } = 0.0;
    public double TabletAreaY { get; init; } = 0.0;
    public double TabletAreaW { get; init; } = 1.0;
    public double TabletAreaH { get; init; } = 1.0;

    public Orientation Orientation { get; init; } = Orientation.Portrait;

    /// <summary>Map the full area of a screen with explicit dimensions.</summary>
    public static MappingOptions ForScreen(int w, int h, Orientation orientation = Orientation.Portrait)
    {
        return new MappingOptions { MonitorX = 0, MonitorY = 0, MonitorW = w, MonitorH = h, Orientation = orientation };
    }
}
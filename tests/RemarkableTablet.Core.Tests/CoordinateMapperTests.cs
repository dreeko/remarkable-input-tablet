using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Tablet;
using Xunit;

namespace RemarkableTablet.Core.Tests;

public class CoordinateMapperTests
{
    private static CoordinateMapper MakeMapper(Orientation orientation, int monW = 1920, int monH = 1080)
    {
        var opts = new MappingOptions
        {
            MonitorX = 0,
            MonitorY = 0,
            MonitorW = monW,
            MonitorH = monH,
            Orientation = orientation
        };
        return new CoordinateMapper(opts, PressureCurve.Linear);
    }

    private static PenFrame MakeFrame(
        int x, int y,
        int pressure = 0,
        int tiltX = 0, int tiltY = 0,
        bool touch = false, bool inRange = true)
    {
        return new PenFrame(x, y, pressure, tiltX, tiltY, 0, touch, false, false, false, inRange);
    }

    [Fact]
    public void PortraitOrigin_MapsToTopLeft()
    {
        // ABS_X is the long axis (0=USB/bottom, PenXMax=top); ABS_Y is the short axis (0=left).
        // Physical top-left = (ABS_X=PenXMax, ABS_Y=0).
        // Formula (ny, 1-nx): rx=0, ry=1-1=0 → screen (0, 0).
        var mapper = MakeMapper(Orientation.Portrait);
        var frame = MakeFrame(ReMarkable2Constants.PenXMax, 0);
        var mapped = mapper.Map(frame);
        Assert.Equal(0, mapped.ScreenX);
        Assert.Equal(0, mapped.ScreenY);
    }

    [Fact]
    public void PortraitCenter_MapsToScreenCenter()
    {
        var mapper = MakeMapper(Orientation.Portrait);
        var frame = MakeFrame(ReMarkable2Constants.PenXMax / 2, ReMarkable2Constants.PenYMax / 2);
        var mapped = mapper.Map(frame);
        // Should be approximately center (±2px tolerance for integer rounding)
        Assert.InRange(mapped.ScreenX, 958, 962);
        Assert.InRange(mapped.ScreenY, 538, 542);
    }

    [Fact]
    public void LandscapeOrientation_MapsDirectly()
    {
        var mapper = MakeMapper(Orientation.Landscape);
        var frame = MakeFrame(ReMarkable2Constants.PenXMax / 2, ReMarkable2Constants.PenYMax / 2);
        var mapped = mapper.Map(frame);
        Assert.InRange(mapped.ScreenX, 958, 962);
        Assert.InRange(mapped.ScreenY, 538, 542);
    }


    // Orientation corner tests — each orientation maps one physical corner to screen (0,0).
    // ABS_X = long axis (0=USB/bottom, PenXMax=top); ABS_Y = short axis (0=left, PenYMax=right).

    [Fact]
    public void LandscapeOrigin_MapsToTopLeft()
    {
        // Landscape = 90° CCW from portrait; USB/pen slot on the right.
        // Physical top-left in landscape = (ABS_X=PenXMax, ABS_Y=PenYMax).
        // Formula (1-nx, 1-ny): rx=0, ry=0 → screen (0,0).
        var mapper = MakeMapper(Orientation.Landscape);
        var frame = MakeFrame(ReMarkable2Constants.PenXMax, ReMarkable2Constants.PenYMax);
        var mapped = mapper.Map(frame);
        Assert.Equal(0, mapped.ScreenX);
        Assert.Equal(0, mapped.ScreenY);
    }

    [Fact]
    public void PortraitFlippedOrigin_MapsToTopLeft()
    {
        // PortraitFlipped = 180° from portrait; USB at top.
        // Physical top-left = (ABS_X=0, ABS_Y=PenYMax).
        // Formula (1-ny, nx): rx=0, ry=0 → screen (0,0).
        var mapper = MakeMapper(Orientation.PortraitFlipped);
        var frame = MakeFrame(0, ReMarkable2Constants.PenYMax);
        var mapped = mapper.Map(frame);
        Assert.Equal(0, mapped.ScreenX);
        Assert.Equal(0, mapped.ScreenY);
    }

    [Fact]
    public void LandscapeFlippedOrigin_MapsToTopLeft()
    {
        // LandscapeFlipped = 90° CW from portrait; USB/pen slot on the left.
        // Physical top-left = (ABS_X=0, ABS_Y=0).
        // Formula (nx, ny): rx=0, ry=0 → screen (0,0).
        var mapper = MakeMapper(Orientation.LandscapeFlipped);
        var frame = MakeFrame(0, 0);
        var mapped = mapper.Map(frame);
        Assert.Equal(0, mapped.ScreenX);
        Assert.Equal(0, mapped.ScreenY);
    }

    [Fact]
    public void PressureLinearCurve_MapsCorrectly()
    {
        var mapper = MakeMapper(Orientation.Portrait);

        // Zero pressure
        Assert.Equal(0u, mapper.Map(MakeFrame(0, 0)).Pressure);

        // Full pressure → 1024
        Assert.Equal(1024u, mapper.Map(MakeFrame(0, 0, ReMarkable2Constants.PressureMax)).Pressure);

        // Half pressure → ~512
        var half = mapper.Map(MakeFrame(0, 0, ReMarkable2Constants.PressureMax / 2));
        Assert.InRange(half.Pressure, 510u, 514u);
    }

    [Fact]
    public void PressureCurveSoft_BoostsLowPressure()
    {
        var opts = new MappingOptions
        {
            MonitorX = 0, MonitorY = 0, MonitorW = 1920, MonitorH = 1080,
            Orientation = Orientation.Portrait
        };
        var softMapper = new CoordinateMapper(opts, PressureCurve.Soft);
        var linMapper = new CoordinateMapper(opts, PressureCurve.Linear);

        // At 25% input, soft curve should produce noticeably higher output than linear.
        // Linear at t=0.25 ≈ 0.25 → ~256/1024.
        // Soft   at t=0.25 (y1=0.40, y2=0.90) ≈ 0.311 → ~318/1024.
        var rawPressure = ReMarkable2Constants.PressureMax / 4;
        var softMapped = softMapper.Map(MakeFrame(0, 0, rawPressure));
        var linMapped = linMapper.Map(MakeFrame(0, 0, rawPressure));

        Assert.InRange(linMapped.Pressure,  254u, 258u);
        Assert.InRange(softMapped.Pressure, 315u, 322u);
    }

    // ── Tilt rotation: tilt vector must rotate in lockstep with position ───────

    // We pick a deliberately asymmetric raw tilt that produces (+90,0) after scaling
    // (TiltX = +TiltXMax, TiltY = 0) so each orientation produces a distinct expected
    // output. After ScaleTilt: (90, 0). After RotateTilt:
    //   Portrait        → (0,  -90)
    //   Landscape       → (-90, 0)
    //   PortraitFlipped → (0,   90)
    //   LandscapeFlip   → (90,  0)
    private static (int X, int Y) TiltAfter(Orientation o)
    {
        var mapper = MakeMapper(o);
        var f = MakeFrame(0, 0, tiltX: ReMarkable2Constants.TiltXMax, tiltY: 0);
        var m = mapper.Map(f);
        return (m.TiltX, m.TiltY);
    }

    [Fact] public void Tilt_PortraitRotates()         => Assert.Equal((0,  -90), TiltAfter(Orientation.Portrait));
    [Fact] public void Tilt_LandscapeRotates()        => Assert.Equal((-90,  0), TiltAfter(Orientation.Landscape));
    [Fact] public void Tilt_PortraitFlippedRotates()  => Assert.Equal((0,   90), TiltAfter(Orientation.PortraitFlipped));
    [Fact] public void Tilt_LandscapeFlippedPasses()  => Assert.Equal((90,   0), TiltAfter(Orientation.LandscapeFlipped));
}

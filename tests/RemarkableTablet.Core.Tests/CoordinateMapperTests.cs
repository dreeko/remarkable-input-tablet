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
            MonitorX    = 0,
            MonitorY    = 0,
            MonitorW    = monW,
            MonitorH    = monH,
            Orientation = orientation,
        };
        return new CoordinateMapper(opts, PressureCurve.Linear);
    }

    private static PenFrame MakeFrame(int x, int y, int pressure = 0, bool touch = false, bool inRange = true) =>
        new(x, y, pressure, 0, 0, 0, touch, false, false, false, inRange);

    [Fact]
    public void PortraitOrigin_MapsToTopLeft()
    {
        // In portrait: native (0,0) → screen (0,0)
        // portrait: rx=ny, ry=1-nx  → ny=0, 1-nx=1 → (0,1) → (0, screenH)
        // Hmm — let's think: native (0,0) means x=0, y=0
        // rx = ny = 0/PenYMax = 0
        // ry = 1 - nx = 1 - 0/PenXMax = 1.0  → maps to bottom of screen
        // So native top-left in portrait = screen bottom-left. That's the pen-slot side.
        // The "top" of a portrait rM2 (away from pen slot) = native x=PenXMax, y=0
        // rx=0, ry=1-1=0 → screen (0,0) ✓
        var mapper = MakeMapper(Orientation.Portrait);
        var frame  = MakeFrame(ReMarkable2Constants.PenXMax, 0);
        var mapped = mapper.Map(frame);
        Assert.Equal(0, mapped.ScreenX);
        Assert.Equal(0, mapped.ScreenY);
    }

    [Fact]
    public void PortraitCenter_MapsToScreenCenter()
    {
        var mapper = MakeMapper(Orientation.Portrait, 1920, 1080);
        var frame  = MakeFrame(ReMarkable2Constants.PenXMax / 2, ReMarkable2Constants.PenYMax / 2);
        var mapped = mapper.Map(frame);
        // Should be approximately center (±2px tolerance for integer rounding)
        Assert.InRange(mapped.ScreenX, 958, 962);
        Assert.InRange(mapped.ScreenY, 538, 542);
    }

    [Fact]
    public void LandscapeOrientation_MapsDirectly()
    {
        var mapper = MakeMapper(Orientation.Landscape, 1920, 1080);
        // Native center
        var frame  = MakeFrame(ReMarkable2Constants.PenXMax / 2, ReMarkable2Constants.PenYMax / 2);
        var mapped = mapper.Map(frame);
        Assert.InRange(mapped.ScreenX, 958, 962);
        Assert.InRange(mapped.ScreenY, 538, 542);
    }

    [Fact]
    public void PressureLinearCurve_MapsCorrectly()
    {
        var mapper = MakeMapper(Orientation.Portrait);

        // Zero pressure
        Assert.Equal(0u, mapper.Map(MakeFrame(0, 0, 0)).Pressure);

        // Full pressure → 1024
        Assert.Equal(1024u, mapper.Map(MakeFrame(0, 0, ReMarkable2Constants.PressureMax)).Pressure);

        // Half pressure → ~512
        var half = mapper.Map(MakeFrame(0, 0, ReMarkable2Constants.PressureMax / 2));
        Assert.InRange(half.Pressure, 510u, 514u);
    }

    [Fact]
    public void PressureCurveSoft_BooststLowPressure()
    {
        var opts = new MappingOptions
        {
            MonitorX = 0, MonitorY = 0, MonitorW = 1920, MonitorH = 1080,
            Orientation = Orientation.Portrait,
        };
        var softMapper = new CoordinateMapper(opts, PressureCurve.Soft);
        var linMapper  = new CoordinateMapper(opts, PressureCurve.Linear);

        // At 25% pressure, soft curve should give higher output than linear
        int rawPressure = ReMarkable2Constants.PressureMax / 4;
        var softMapped = softMapper.Map(MakeFrame(0, 0, rawPressure));
        var linMapped  = linMapper.Map(MakeFrame(0, 0, rawPressure));

        Assert.True(softMapped.Pressure > linMapped.Pressure,
            "Soft curve should produce higher pressure at low input");
    }
}

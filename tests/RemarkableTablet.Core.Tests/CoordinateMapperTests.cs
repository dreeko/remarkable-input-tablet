using RemarkableTablet.Core.Devices;
using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Tablet;
using Xunit;

namespace RemarkableTablet.Core.Tests;

public class CoordinateMapperTests
{
    private static readonly DeviceProfile Rm2 = ReMarkable2Profile.Instance;

    // Stretch, because these tests are about the orientation/tilt rotation: the
    // full surface must reach the full screen for a corner assertion to mean
    // anything. Aspect fitting is covered by ScreenTransformTests.
    private static CoordinateMapper MakeMapper(Orientation orientation, int monW = 1920, int monH = 1080)
    {
        var opts = new MappingOptions
        {
            MonitorX = 0,
            MonitorY = 0,
            MonitorW = monW,
            MonitorH = monH,
            Orientation = orientation,
            Fit = FitMode.Stretch
        };
        return new CoordinateMapper(opts, Rm2, PressureCurve.Linear);
    }

    private static PenFrame MakeFrame(
        int x, int y,
        int pressure = 0,
        int tiltX = 0, int tiltY = 0,
        bool touch = false, bool inRange = true,
        int distance = 0)
    {
        return new PenFrame(x, y, pressure, tiltX, tiltY, distance, touch, false, false, false, inRange);
    }

    [Fact]
    public void HoverDistance_IsCarriedToTheMappedFrame()
    {
        // Parsed off ABS_DISTANCE and forwarded to the virtual pen device — apps
        // like Krita and GIMP use it for hover feedback.
        var mapped = MakeMapper(Orientation.Portrait).Map(MakeFrame(0, 0, distance: 42));
        Assert.Equal(42, mapped.Distance);
    }

    [Fact]
    public void PenAndTouch_AgreeOnTheSamePhysicalPoint()
    {
        // The two mappers are a matched pair: the same physical spot must land on
        // the same pixel, in every orientation, or drawing and gestures disagree.
        foreach (var o in Enum.GetValues<Orientation>())
        {
            var opts = MappingOptions.ForScreen(1920, 1080, o);
            var pen = new CoordinateMapper(opts, Rm2, PressureCurve.Linear);
            var touch = new TouchCoordinateMapper(opts, Rm2, pen.Transform);

            // Physical centre of the surface, expressed in each device's axes.
            var penPoint = pen.Map(MakeFrame(Rm2.Pen.XMax / 2, Rm2.Pen.YMax / 2));
            var touchFrame = touch.Map(new TouchFrame([
                new TouchContact(0, 1, Rm2.Touch.XMax / 2, Rm2.Touch.YMax / 2, 0, 0, 0, 0, 0)
            ]));

            Assert.InRange(Math.Abs(penPoint.ScreenX - touchFrame.Contacts[0].ScreenX), 0, 2);
            Assert.InRange(Math.Abs(penPoint.ScreenY - touchFrame.Contacts[0].ScreenY), 0, 2);
        }
    }

    [Fact]
    public void PortraitOrigin_MapsToTopLeft()
    {
        // Pen aligned with touch (verified 2026-05-07):
        //   ABS_X is the long axis  (0 = top in portrait, PenXMax = USB/bottom).
        //   ABS_Y is the short axis (0 = right,           PenYMax = left).
        // Physical top-left in portrait = (ABS_X=0, ABS_Y=PenYMax).
        // Formula (1-ny, nx): rx=0, ry=0 → screen (0, 0).
        var mapper = MakeMapper(Orientation.Portrait);
        var frame = MakeFrame(0, Rm2.Pen.YMax);
        var mapped = mapper.Map(frame);
        Assert.Equal(0, mapped.ScreenX);
        Assert.Equal(0, mapped.ScreenY);
    }

    [Fact]
    public void PortraitOppositeCorner_MapsInsideFinalScreenPixel()
    {
        var mapped = MakeMapper(Orientation.Portrait)
            .Map(MakeFrame(Rm2.Pen.XMax, 0));

        Assert.Equal(1919, mapped.ScreenX);
        Assert.Equal(1079, mapped.ScreenY);
    }

    [Fact]
    public void PortraitCenter_MapsToScreenCenter()
    {
        var mapper = MakeMapper(Orientation.Portrait);
        var frame = MakeFrame(Rm2.Pen.XMax / 2, Rm2.Pen.YMax / 2);
        var mapped = mapper.Map(frame);
        // Should be approximately center (±2px tolerance for integer rounding)
        Assert.InRange(mapped.ScreenX, 958, 962);
        Assert.InRange(mapped.ScreenY, 538, 542);
    }

    [Fact]
    public void LandscapeOrientation_MapsDirectly()
    {
        var mapper = MakeMapper(Orientation.Landscape);
        var frame = MakeFrame(Rm2.Pen.XMax / 2, Rm2.Pen.YMax / 2);
        var mapped = mapper.Map(frame);
        Assert.InRange(mapped.ScreenX, 958, 962);
        Assert.InRange(mapped.ScreenY, 538, 542);
    }


    // Orientation corner tests — each orientation maps one physical corner to screen (0,0).
    // Pen aligned with touch: ABS_X long axis (0=top portrait); ABS_Y short axis (0=right portrait).

    [Fact]
    public void LandscapeOrigin_MapsToTopLeft()
    {
        // Landscape: pen slot on the right. Physical top-left = (ABS_X=0, ABS_Y=0).
        // Formula (nx, ny): rx=0, ry=0 → screen (0,0).
        var mapper = MakeMapper(Orientation.Landscape);
        var frame = MakeFrame(0, 0);
        var mapped = mapper.Map(frame);
        Assert.Equal(0, mapped.ScreenX);
        Assert.Equal(0, mapped.ScreenY);
    }

    [Fact]
    public void PortraitFlippedOrigin_MapsToTopLeft()
    {
        // PortraitFlipped = 180° from portrait; USB at top.
        // Physical top-left = (ABS_X=PenXMax, ABS_Y=0).
        // Formula (ny, 1-nx): rx=0, ry=0 → screen (0,0).
        var mapper = MakeMapper(Orientation.PortraitFlipped);
        var frame = MakeFrame(Rm2.Pen.XMax, 0);
        var mapped = mapper.Map(frame);
        Assert.Equal(0, mapped.ScreenX);
        Assert.Equal(0, mapped.ScreenY);
    }

    [Fact]
    public void LandscapeFlippedOrigin_MapsToTopLeft()
    {
        // LandscapeFlipped: pen slot on the left.
        // Physical top-left = (ABS_X=PenXMax, ABS_Y=PenYMax).
        // Formula (1-nx, 1-ny): rx=0, ry=0 → screen (0,0).
        var mapper = MakeMapper(Orientation.LandscapeFlipped);
        var frame = MakeFrame(Rm2.Pen.XMax, Rm2.Pen.YMax);
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
        Assert.Equal(1024u, mapper.Map(MakeFrame(0, 0, Rm2.Pen.PressureMax)).Pressure);

        // Half pressure → ~512
        var half = mapper.Map(MakeFrame(0, 0, Rm2.Pen.PressureMax / 2));
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
        var softMapper = new CoordinateMapper(opts, Rm2, PressureCurve.Soft);
        var linMapper = new CoordinateMapper(opts, Rm2, PressureCurve.Linear);

        // At 25% input, soft curve should produce noticeably higher output than linear.
        // Linear at t=0.25 ≈ 0.25 → ~256/1024.
        // Soft   at t=0.25 (y1=0.40, y2=0.90) ≈ 0.311 → ~318/1024.
        var rawPressure = Rm2.Pen.PressureMax / 4;
        var softMapped = softMapper.Map(MakeFrame(0, 0, rawPressure));
        var linMapped = linMapper.Map(MakeFrame(0, 0, rawPressure));

        Assert.InRange(linMapped.Pressure, 254u, 258u);
        Assert.InRange(softMapped.Pressure, 315u, 322u);
    }

    // ── Tilt rotation: tilt vector must rotate in lockstep with position ───────

    // Raw tilt (TiltXMax, 0) → after ScaleTilt: (90, 0). After RotateTilt
    // (rotated 180° vs. previous convention so the tilt vector follows the
    // position transform that aligns with touch):
    //   Portrait        → (0,   90)
    //   Landscape       → (90,  0)
    //   PortraitFlipped → (0,  -90)
    //   LandscapeFlip   → (-90, 0)
    private static (int X, int Y) TiltAfter(Orientation o)
    {
        var mapper = MakeMapper(o);
        var f = MakeFrame(0, 0, tiltX: Rm2.Pen.TiltXMax, tiltY: 0);
        var m = mapper.Map(f);
        return (m.TiltX, m.TiltY);
    }

    [Fact]
    public void Tilt_PortraitRotates()
    {
        Assert.Equal((0, 90), TiltAfter(Orientation.Portrait));
    }

    [Fact]
    public void Tilt_LandscapeRotates()
    {
        Assert.Equal((90, 0), TiltAfter(Orientation.Landscape));
    }

    [Fact]
    public void Tilt_PortraitFlippedRotates()
    {
        Assert.Equal((0, -90), TiltAfter(Orientation.PortraitFlipped));
    }

    [Fact]
    public void Tilt_LandscapeFlippedPasses()
    {
        Assert.Equal((-90, 0), TiltAfter(Orientation.LandscapeFlipped));
    }

    // ── PressureCurve.FromName ────────────────────────────────────────────────

    [Theory]
    [InlineData("linear")]
    [InlineData("Linear")]
    [InlineData("LINEAR")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("unknown-name")]
    public void PressureCurveFromName_DefaultsToLinearForUnknownValues(string? name)
    {
        var curve = PressureCurve.FromName(name);
        // Linear curve maps t to t exactly (within float precision).
        Assert.Equal(0.5, curve.Apply(0.5), 6);
    }

    [Fact]
    public void PressureCurveFromName_SoftBoostsLowPressure()
    {
        var curve = PressureCurve.FromName("soft");
        Assert.True(curve.Apply(0.25) > 0.30, "soft curve should boost t=0.25 above linear");
    }

    [Fact]
    public void PressureCurveFromName_HardSuppressesLowPressure()
    {
        var curve = PressureCurve.FromName("hard");
        Assert.True(curve.Apply(0.25) < 0.20, "hard curve should suppress t=0.25 below linear");
    }
}
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
        // Pen axes measured 2026-07-25 (samples/hw2-pen.log):
        //   ABS_X is the long axis  (0 = bottom/USB edge, PenXMax = top).
        //   ABS_Y is the short axis (0 = left,            PenYMax = right).
        // Physical top-left in portrait = (ABS_X=PenXMax, ABS_Y=0).
        var mapper = MakeMapper(Orientation.Portrait);
        var mapped = mapper.Map(MakeFrame(Rm2.Pen.XMax, 0));
        Assert.Equal(0, mapped.ScreenX);
        Assert.Equal(0, mapped.ScreenY);
    }

    [Fact]
    public void PortraitOppositeCorner_MapsInsideFinalScreenPixel()
    {
        // Physical bottom-right = (ABS_X=0, ABS_Y=PenYMax).
        var mapped = MakeMapper(Orientation.Portrait)
            .Map(MakeFrame(0, Rm2.Pen.YMax));

        Assert.Equal(1919, mapped.ScreenX);
        Assert.Equal(1079, mapped.ScreenY);
    }

    // Raw samples straight from the hardware captures, so a future "correction"
    // to the axis convention has to argue with the device rather than with a
    // formula. Device held portrait, USB-C edge at the bottom.
    [Theory]
    // pen tip on the top-left corner, then the top-right corner. Both are along the
    // top edge, so only the expected X differs; Y is asserted near 0 either way.
    [InlineData(20258, 672, 0)]
    [InlineData(20584, 15258, 1919)]
    public void MeasuredPenCorners_LandOnTheCorrespondingScreenCorner(
        int rawX, int rawY, int expectX)
    {
        var mapped = MakeMapper(Orientation.Portrait).Map(MakeFrame(rawX, rawY));

        // Within ~7 % of the edge: a tip resting on a corner sits several mm in
        // from the true extreme, so this pins the corner, not the exact pixel.
        Assert.InRange(mapped.ScreenX, expectX == 0 ? 0 : 1790, expectX == 0 ? 130 : 1919);
        Assert.InRange(mapped.ScreenY, 0, 80);
    }

    // The regression that would have caught the original bug. Both devices were
    // captured touching the SAME two corners in the SAME hold, so their mapped
    // pixels must agree — no formula involved, just the hardware. Before the
    // 2026-07-25 correction these disagreed by a full horizontal mirror: a pen
    // stroke on the top-left corner landed bottom-right while a finger on the
    // same spot landed bottom-left.
    [Theory]
    // top-left corner:  pen (X,Y),      touch (X,Y)
    [InlineData(20258, 672, 85, 1837)]
    // top-right corner: pen (X,Y),      touch (X,Y)
    [InlineData(20584, 15258, 1379, 1835)]
    public void MeasuredCorners_PenAndTouchLandInTheSamePlace(
        int penX, int penY, int touchX, int touchY)
    {
        var opts = MappingOptions.ForScreen(1920, 1080, Orientation.Portrait, FitMode.Stretch);
        var pen = new CoordinateMapper(opts, Rm2, PressureCurve.Linear);
        var touch = new TouchCoordinateMapper(opts, Rm2, pen.Transform);

        var p = pen.Map(MakeFrame(penX, penY));
        var t = touch.Map(new TouchFrame([new TouchContact(0, 1, touchX, touchY, 0, 0, 0, 0, 0)]))
            .Contacts[0];

        // 40 px ≈ 3 mm of screen: the gap between where a pen tip and a fingertip
        // sit when both are "on the corner". A mirrored axis shows up as ~1900 px.
        Assert.InRange(Math.Abs(p.ScreenX - t.ScreenX), 0, 40);
        Assert.InRange(Math.Abs(p.ScreenY - t.ScreenY), 0, 40);
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


    // Orientation corner tests — each orientation maps one physical corner to
    // screen (0,0). Measured pen axes: ABS_X long axis (0 = bottom/USB edge in
    // portrait); ABS_Y short axis (0 = left in portrait).

    [Fact]
    public void LandscapeOrigin_MapsToTopLeft()
    {
        // Landscape = portrait rotated 90° CCW, so the device's TOP-RIGHT corner
        // swings round to the screen's top-left: (ABS_X=PenXMax, ABS_Y=PenYMax).
        var mapper = MakeMapper(Orientation.Landscape);
        var mapped = mapper.Map(MakeFrame(Rm2.Pen.XMax, Rm2.Pen.YMax));
        Assert.Equal(0, mapped.ScreenX);
        Assert.Equal(0, mapped.ScreenY);
    }

    [Fact]
    public void PortraitFlippedOrigin_MapsToTopLeft()
    {
        // PortraitFlipped = 180°, USB edge at the top. The device's bottom-right
        // corner is now the screen's top-left: (ABS_X=0, ABS_Y=PenYMax).
        var mapper = MakeMapper(Orientation.PortraitFlipped);
        var mapped = mapper.Map(MakeFrame(0, Rm2.Pen.YMax));
        Assert.Equal(0, mapped.ScreenX);
        Assert.Equal(0, mapped.ScreenY);
    }

    [Fact]
    public void LandscapeFlippedOrigin_MapsToTopLeft()
    {
        // LandscapeFlipped = portrait rotated 90° CW, USB edge on the left. The
        // device's BOTTOM-LEFT corner swings round to the screen's top-left:
        // (ABS_X=0, ABS_Y=0).
        var mapper = MakeMapper(Orientation.LandscapeFlipped);
        var mapped = mapper.Map(MakeFrame(0, 0));
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

    // Raw tilt (TiltXMax, 0) → after ScaleTilt: (90, 0), i.e. the pen leaning
    // fully along +ABS_X, which the hardware captures show points UP the device.
    // Up is screen −Y in portrait, so the rotated vector is:
    //   Portrait        → (0,  -90)
    //   Landscape       → (-90,  0)
    //   PortraitFlipped → (0,   90)
    //   LandscapeFlip   → (90,   0)
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
        Assert.Equal((0, -90), TiltAfter(Orientation.Portrait));
    }

    [Fact]
    public void Tilt_LandscapeRotates()
    {
        Assert.Equal((-90, 0), TiltAfter(Orientation.Landscape));
    }

    [Fact]
    public void Tilt_PortraitFlippedRotates()
    {
        Assert.Equal((0, 90), TiltAfter(Orientation.PortraitFlipped));
    }

    [Fact]
    public void Tilt_LandscapeFlippedPasses()
    {
        Assert.Equal((90, 0), TiltAfter(Orientation.LandscapeFlipped));
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
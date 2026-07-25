using RemarkableTablet.Core.Devices;
using RemarkableTablet.Core.Mapping;
using Xunit;

namespace RemarkableTablet.Core.Tests;

public class ScreenTransformTests
{
    private static readonly DeviceProfile Rm2 = ReMarkable2Profile.Instance;

    private static ScreenTransform Make(FitMode fit, Orientation o = Orientation.Portrait,
        int w = 1920, int h = 1080)
    {
        return new ScreenTransform(MappingOptions.ForScreen(w, h, o, fit), Rm2);
    }

    // ── Rounding ──────────────────────────────────────────────────────────────

    [Fact]
    public void Stretch_MapsFullRangeToFullScreenWithRounding()
    {
        var t = Make(FitMode.Stretch);

        Assert.Equal((0, 0), t.ToScreen(0.0, 0.0));
        Assert.Equal((1919, 1079), t.ToScreen(1.0, 1.0));
        // Rounding, not truncation: the midpoint lands on the nearer pixel.
        Assert.Equal((960, 540), t.ToScreen(0.5, 0.5));
    }

    [Fact]
    public void OutOfRangeInput_ClampsToTheEdge()
    {
        var t = Make(FitMode.Stretch);

        Assert.Equal((0, 0), t.ToScreen(-0.5, -2.0));
        Assert.Equal((1919, 1079), t.ToScreen(1.5, 9.0));
    }

    // ── Aspect fit ────────────────────────────────────────────────────────────

    [Fact]
    public void Crop_KeepsFullScreenReachable()
    {
        var t = Make(FitMode.Crop);

        // Portrait 3:4 surface, 16:9 screen ⇒ the live strip is 0.75/1.7778 =
        // 0.421875 of the tablet's height, centred (top edge at 0.2890625).
        const double liveH = 0.421875;
        const double top = (1.0 - liveH) / 2.0;

        Assert.Equal(1920, t.MonitorW);
        Assert.Equal(1080, t.MonitorH);
        Assert.Equal((0, 0), t.ToScreen(0.0, top));
        Assert.Equal((1919, 1079), t.ToScreen(1.0, top + liveH));
        // Outside the strip clamps to the screen edge, as at any tablet edge.
        Assert.Equal((0, 0), t.ToScreen(0.0, 0.0));
    }

    [Fact]
    public void Crop_IsAspectCorrect()
    {
        // A square on the tablet must land as a square on screen: equal
        // millimetre steps in x and y must produce equal pixel steps.
        var t = Make(FitMode.Crop);
        var mmPerFractionX = Rm2.Surface.WidthMm; // portrait: x = short axis
        var mmPerFractionY = Rm2.Surface.HeightMm;

        var (x0, y0) = t.ToScreen(0.5, 0.5);
        // Move 10 mm along each axis.
        var (x1, _) = t.ToScreen(0.5 + 10.0 / mmPerFractionX, 0.5);
        var (_, y1) = t.ToScreen(0.5, 0.5 + 10.0 / mmPerFractionY);

        Assert.InRange(Math.Abs(x1 - x0) - Math.Abs(y1 - y0), -1, 1);
    }

    [Fact]
    public void Stretch_IsNotAspectCorrect_AndThatIsTheDifference()
    {
        var t = Make(FitMode.Stretch);
        var (x0, y0) = t.ToScreen(0.5, 0.5);
        var (x1, _) = t.ToScreen(0.5 + 10.0 / Rm2.Surface.WidthMm, 0.5);
        var (_, y1) = t.ToScreen(0.5, 0.5 + 10.0 / Rm2.Surface.HeightMm);

        // 3:4 tablet on a 16:9 screen in portrait ⇒ x stretched ~2.37× vs y.
        var ratio = Math.Abs(x1 - x0) / (double)Math.Abs(y1 - y0);
        Assert.InRange(ratio, 2.2, 2.5);
    }

    [Fact]
    public void Letterbox_ShrinksTheTargetRectangleAndCentresIt()
    {
        var t = Make(FitMode.Letterbox);

        // Portrait 3:4 tablet in a 1920×1080 screen ⇒ a centred 810×1080 column.
        Assert.Equal(1080, t.MonitorH);
        Assert.Equal(810, t.MonitorW);
        Assert.Equal((1920 - 810) / 2, t.MonitorX);
        Assert.Equal(0, t.MonitorY);

        // Whole tablet stays usable: the corners reach the column's corners.
        Assert.Equal((555, 0), t.ToScreen(0.0, 0.0));
        Assert.Equal((555 + 809, 1079), t.ToScreen(1.0, 1.0));
    }

    [Fact]
    public void Landscape_UsesTheRotatedSurfaceAspect()
    {
        // 4:3 surface on a 16:9 screen: crop trims the tablet's width, not height.
        var t = Make(FitMode.Crop, Orientation.Landscape);

        var (xLeft, _) = t.ToScreen(0.0, 0.5);
        var (xRight, _) = t.ToScreen(1.0, 0.5);
        Assert.Equal(0, xLeft);
        Assert.Equal(1919, xRight);

        // Vertically, part of the surface is cropped away, so the extremes clamp.
        var (_, yTop) = t.ToScreen(0.5, 0.0);
        Assert.Equal(0, yTop);
    }

    // ── Active tablet area ────────────────────────────────────────────────────

    [Fact]
    public void ActiveArea_MapsOnlyThatSubRectangleToTheWholeScreen()
    {
        // Centre quarter of the tablet drives the whole screen.
        var opts = new MappingOptions
        {
            MonitorW = 1000, MonitorH = 1000,
            TabletAreaX = 0.25, TabletAreaY = 0.25, TabletAreaW = 0.5, TabletAreaH = 0.5,
            Fit = FitMode.Stretch
        };
        var t = new ScreenTransform(opts, Rm2);

        Assert.Equal((0, 0), t.ToScreen(0.25, 0.25));
        Assert.Equal((999, 999), t.ToScreen(0.75, 0.75));
        Assert.Equal((500, 500), t.ToScreen(0.5, 0.5)); // 0.5 × 999 rounds up
        // Outside the active area clamps rather than running off-screen.
        Assert.Equal((0, 0), t.ToScreen(0.0, 0.0));
        Assert.Equal((999, 999), t.ToScreen(1.0, 1.0));
    }

    [Fact]
    public void MonitorOffset_IsAppliedToEveryPoint()
    {
        var opts = new MappingOptions
        {
            MonitorX = 2560, MonitorY = 100, MonitorW = 1000, MonitorH = 1000, Fit = FitMode.Stretch
        };
        var t = new ScreenTransform(opts, Rm2);

        Assert.Equal((2560, 100), t.ToScreen(0.0, 0.0));
        Assert.Equal((3559, 1099), t.ToScreen(1.0, 1.0));
    }

    // ── Axis normalisation ────────────────────────────────────────────────────

    [Fact]
    public void Normalize_HonoursANonZeroMinimum()
    {
        Assert.Equal(0.0, ScreenTransform.Normalize(100, 100, 200));
        Assert.Equal(0.5, ScreenTransform.Normalize(150, 100, 200));
        Assert.Equal(1.0, ScreenTransform.Normalize(200, 100, 200));
    }

    [Fact]
    public void Normalize_ClampsAndSurvivesADegenerateRange()
    {
        Assert.Equal(0.0, ScreenTransform.Normalize(-5, 0, 100));
        Assert.Equal(1.0, ScreenTransform.Normalize(500, 0, 100));
        Assert.Equal(0.0, ScreenTransform.Normalize(7, 5, 5));
    }

    // ── uinput resolution ─────────────────────────────────────────────────────

    [Fact]
    public void Resolution_IsScreenPixelsPerMillimetreOfSurface()
    {
        var t = Make(FitMode.Stretch);

        // Portrait: screen X spans the tablet's 157.5 mm short axis.
        Assert.Equal((int)Math.Round(1920 / 157.5), t.XResolution);
        Assert.Equal((int)Math.Round(1080 / 210.0), t.YResolution);

        // Sanity: nothing like the old 100 ticks/mm, which implied a 19 mm tablet.
        Assert.InRange(t.XResolution, 2, 60);
    }

}

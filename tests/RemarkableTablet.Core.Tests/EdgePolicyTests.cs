using RemarkableTablet.Core.Devices;
using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Tablet;
using Xunit;

namespace RemarkableTablet.Core.Tests;

/// <summary>
///     Active-area cropping and what happens at its boundary. Clamping is right at
///     the physical edge of the surface — there is nowhere further to go — but
///     wrong at the edge of a *crop*, where it draws a line along the boundary the
///     user never made. Hence <see cref="EdgePolicy.Drop" />.
/// </summary>
public class EdgePolicyTests
{
    private static readonly DeviceProfile Rm2 = ReMarkable2Profile.Instance;

    // Centre half of the surface, stretched so the maths stays legible.
    private static MappingOptions Area(EdgePolicy edge)
    {
        return new MappingOptions
        {
            MonitorW = 1000, MonitorH = 1000,
            TabletAreaX = 0.25, TabletAreaY = 0.25, TabletAreaW = 0.5, TabletAreaH = 0.5,
            Fit = FitMode.Stretch,
            Edge = edge
        };
    }

    private static PenFrame Pen(int x, int y)
    {
        return new PenFrame(x, y, 800, 0, 0, 0, true, false, false, false, true);
    }

    // Pen axes: u (left→right) = ny, v (top→bottom) = 1 − nx. So a point at
    // fraction (u, v) of the surface is raw (X = (1−v)·XMax, Y = u·YMax).
    private static PenFrame PenAt(double u, double v)
    {
        return Pen((int)((1 - v) * Rm2.Pen.XMax), (int)(u * Rm2.Pen.YMax));
    }

    private static TouchFrame TouchAt(double u, double v, int id = 1)
    {
        // Touch axes: u = nx, v = 1 − ny.
        return new TouchFrame([
            new TouchContact(0, id,
                (int)(u * Rm2.Touch.XMax), (int)((1 - v) * Rm2.Touch.YMax), 100, 12, 10, 0, 0)
        ]);
    }

    // ── Clamp: the pre-existing behaviour, unchanged ──────────────────────────

    [Fact]
    public void Clamp_PinsOutsidePointsToTheBorderAndKeepsReporting()
    {
        var mapper = new CoordinateMapper(Area(EdgePolicy.Clamp), Rm2);

        var outside = mapper.Map(PenAt(0.05, 0.5)); // left of the active area

        Assert.True(outside.InArea);
        Assert.Equal(0, outside.ScreenX);
        Assert.True(outside.InRange);
    }

    [Fact]
    public void Clamp_IsTheDefault()
    {
        Assert.Equal(EdgePolicy.Clamp, new MappingOptions().Edge);
    }

    // ── Drop ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Drop_MarksOutsidePointsSoTheOutputCanIgnoreThem()
    {
        var mapper = new CoordinateMapper(Area(EdgePolicy.Drop), Rm2);

        Assert.False(mapper.Map(PenAt(0.05, 0.5)).InArea); // outboard of the crop
        Assert.False(mapper.Map(PenAt(0.5, 0.95)).InArea); // below it
        Assert.True(mapper.Map(PenAt(0.5, 0.5)).InArea); // dead centre
    }

    [Fact]
    public void Drop_StillReportsAUsablePositionForTheFinalPenUp()
    {
        // The pipeline sends one out-of-range frame when the pen leaves the area;
        // that frame still needs somewhere sensible to be.
        var mapper = new CoordinateMapper(Area(EdgePolicy.Drop), Rm2);

        var outside = mapper.Map(PenAt(0.05, 0.5));

        Assert.InRange(outside.ScreenX, 0, 999);
        Assert.InRange(outside.ScreenY, 0, 999);
    }

    [Fact]
    public void Drop_KeepsTheEdgeOfTheAreaItself()
    {
        // The boundary must count as inside, or drawing along it flickers.
        var mapper = new CoordinateMapper(Area(EdgePolicy.Drop), Rm2);

        Assert.True(mapper.Map(PenAt(0.25, 0.5)).InArea);
        Assert.True(mapper.Map(PenAt(0.75, 0.5)).InArea);
    }

    [Fact]
    public void Drop_OmitsTouchContactsOutsideTheArea()
    {
        var opts = Area(EdgePolicy.Drop);
        var touch = new TouchCoordinateMapper(opts, Rm2);

        Assert.Empty(touch.Map(TouchAt(0.05, 0.5)).Contacts);
        Assert.Single(touch.Map(TouchAt(0.5, 0.5)).Contacts);
    }

    [Fact]
    public void Clamp_KeepsTouchContactsOutsideTheArea()
    {
        var touch = new TouchCoordinateMapper(Area(EdgePolicy.Clamp), Rm2);

        var mapped = touch.Map(TouchAt(0.05, 0.5));

        Assert.Single(mapped.Contacts);
        Assert.Equal(0, mapped.Contacts[0].ScreenX);
    }

    [Fact]
    public void Drop_WithAFullSurfaceArea_KeepsEverything()
    {
        // No crop means nothing to fall outside of, whatever the policy.
        var opts = new MappingOptions
        {
            MonitorW = 1000, MonitorH = 1000, Fit = FitMode.Stretch, Edge = EdgePolicy.Drop
        };
        var mapper = new CoordinateMapper(opts, Rm2);

        Assert.True(mapper.Map(PenAt(0.0, 0.0)).InArea);
        Assert.True(mapper.Map(PenAt(1.0, 1.0)).InArea);
    }

    // ── The area itself ───────────────────────────────────────────────────────

    [Fact]
    public void ActiveArea_StretchesTheCropToFillTheTarget()
    {
        var mapper = new CoordinateMapper(Area(EdgePolicy.Clamp), Rm2);

        // ±1 px: the helper quantises to raw device units on the way in, so the
        // centre of the crop can't land on an exact half-pixel.
        Assert.Equal(0, mapper.Map(PenAt(0.25, 0.25)).ScreenX);
        Assert.Equal(999, mapper.Map(PenAt(0.75, 0.75)).ScreenX);
        Assert.InRange(mapper.Map(PenAt(0.5, 0.5)).ScreenX, 499, 501);
    }

    [Fact]
    public void ActiveArea_AppliesToPenAndTouchIdentically()
    {
        // The two mappers share one transform; a crop must not desynchronise them.
        var opts = Area(EdgePolicy.Clamp);
        var pen = new CoordinateMapper(opts, Rm2);
        var touch = new TouchCoordinateMapper(opts, Rm2, pen.Transform);

        var p = pen.Map(PenAt(0.4, 0.6));
        var t = touch.Map(TouchAt(0.4, 0.6)).Contacts[0];

        Assert.InRange(Math.Abs(p.ScreenX - t.ScreenX), 0, 2);
        Assert.InRange(Math.Abs(p.ScreenY - t.ScreenY), 0, 2);
    }
}

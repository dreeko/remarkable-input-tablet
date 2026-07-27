using RemarkableTablet.Core.Devices;
using RemarkableTablet.Core.Mapping;
using Xunit;

namespace RemarkableTablet.Core.Tests;

public class TabletAreaTests
{
    private static readonly DeviceProfile Rm2 = ReMarkable2Profile.Instance; // 157.5 × 210 mm

    private static TabletArea Parse(string? spec, Orientation o = Orientation.Portrait)
    {
        Assert.True(TabletArea.TryParse(spec, Rm2, o, out var area, out var error), error);
        return area;
    }

    [Fact]
    public void Blank_MeansTheWholeSurface()
    {
        Assert.Equal(TabletArea.Full, Parse(null));
        Assert.Equal(TabletArea.Full, Parse(""));
        Assert.Equal(TabletArea.Full, Parse("   "));
    }

    [Fact]
    public void Fractions_PassThrough()
    {
        Assert.Equal(new TabletArea(0.25, 0, 0.5, 1), Parse("0.25,0,0.5,1"));
    }

    [Fact]
    public void Millimetres_ConvertAgainstTheSurfaceSize()
    {
        // Portrait: 157.5 mm across, 210 mm down.
        var area = Parse("0mm,105mm,157.5mm,105mm");

        Assert.Equal(0, area.X, 6);
        Assert.Equal(0.5, area.Y, 6);
        Assert.Equal(1.0, area.W, 6);
        Assert.Equal(0.5, area.H, 6);
    }

    [Fact]
    public void Millimetres_UseTheRotatedSurfaceInLandscape()
    {
        // Landscape swaps which physical edge is horizontal, so the same
        // millimetre value is a different fraction.
        var portrait = Parse("0,0,105mm,105mm");
        var landscape = Parse("0,0,105mm,105mm", Orientation.Landscape);

        Assert.Equal(105 / 157.5, portrait.W, 6);
        Assert.Equal(105 / 210.0, landscape.W, 6);
    }

    [Fact]
    public void MixedUnits_AreAllowed()
    {
        // Half the surface down, in fractions; a hand's width across, in mm.
        var area = Parse("0,0.5,100mm,0.5");

        Assert.Equal(0.5, area.Y, 6);
        Assert.Equal(100 / 157.5, area.W, 6);
    }

    [Theory]
    [InlineData("1,2,3", "four comma-separated")]
    [InlineData("a,b,c,d", "non-negative number")]
    [InlineData("0,0,-1,1", "non-negative number")]
    [InlineData("0,0,,1", "non-negative number")]
    public void MalformedSpecs_AreRejectedWithAUsefulMessage(string spec, string expected)
    {
        Assert.False(TabletArea.TryParse(spec, Rm2, Orientation.Portrait, out _, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void ZeroSizedArea_IsRejected()
    {
        Assert.False(TabletArea.TryParse("0,0,0,1", Rm2, Orientation.Portrait, out _, out var error));
        Assert.Contains("positive width and height", error);
    }

    [Fact]
    public void AreaRunningPastTheEdge_IsRejectedAndSaysHowBigTheTabletIs()
    {
        // The likely mistake: millimetres for a bigger tablet, or mm where
        // fractions were meant.
        Assert.False(TabletArea.TryParse("0,0,300mm,300mm", Rm2, Orientation.Portrait, out _, out var error));
        Assert.Contains("past the edge", error);
        Assert.Contains("157.5", error);
    }

    [Fact]
    public void FullSurfaceInMillimetres_IsAccepted()
    {
        // Exactly the surface must not trip the bounds check.
        var area = Parse("0mm,0mm,157.5mm,210mm");

        Assert.Equal(1.0, area.W, 6);
        Assert.Equal(1.0, area.H, 6);
    }

    [Fact]
    public void ValidateSyntax_ChecksShapeWithoutADevice()
    {
        // Used before the device is probed, so it must not need a profile — and
        // must not reject a millimetre area it can't yet bounds-check.
        Assert.Null(TabletArea.ValidateSyntax("0,0,300mm,300mm"));
        Assert.Null(TabletArea.ValidateSyntax(null));
        Assert.NotNull(TabletArea.ValidateSyntax("nonsense"));
    }
}

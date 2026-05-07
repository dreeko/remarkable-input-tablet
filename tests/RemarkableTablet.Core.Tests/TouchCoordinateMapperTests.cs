using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Tablet;
using Xunit;

namespace RemarkableTablet.Core.Tests;

public class TouchCoordinateMapperTests
{
    private static TouchContact Contact(int x, int y, int pressure = 100, int slot = 0, int trackingId = 1) =>
        new(slot, trackingId, x, y, pressure, 0, 0, 0, 0);

    private static TouchFrame Frame(params TouchContact[] contacts) => new(contacts);

    [Fact]
    public void Portrait_TopLeftMapsToScreenTopLeft()
    {
        var opts = MappingOptions.ForScreen(1920, 1080, Orientation.Portrait);
        var mapper = new TouchCoordinateMapper(opts);

        var f = mapper.Map(Frame(Contact(0, 0)));

        Assert.Equal(0, f.Contacts[0].ScreenX);
        Assert.Equal(0, f.Contacts[0].ScreenY);
    }

    [Fact]
    public void Portrait_BottomRightMapsToScreenBottomRight()
    {
        var opts = MappingOptions.ForScreen(1920, 1080, Orientation.Portrait);
        var mapper = new TouchCoordinateMapper(opts);

        var f = mapper.Map(Frame(Contact(
            ReMarkable2Constants.TouchXMax,
            ReMarkable2Constants.TouchYMax)));

        // (int) truncation of 0.999... can land just below max — accept ±1.
        Assert.InRange(f.Contacts[0].ScreenX, 1918, 1920);
        Assert.InRange(f.Contacts[0].ScreenY, 1078, 1080);
    }

    [Fact]
    public void EmptyFrame_PassesThroughEmpty()
    {
        var opts = MappingOptions.ForScreen(1920, 1080);
        var mapper = new TouchCoordinateMapper(opts);

        var f = mapper.Map(TouchFrame.Empty);

        Assert.Empty(f.Contacts);
    }

    [Fact]
    public void Pressure_ScaledTo0To1024()
    {
        var opts = MappingOptions.ForScreen(1920, 1080);
        var mapper = new TouchCoordinateMapper(opts);

        var f = mapper.Map(Frame(Contact(0, 0, pressure: ReMarkable2Constants.TouchPressureMax)));

        Assert.Equal((uint)ReMarkable2Constants.WindowsPressureMax, f.Contacts[0].Pressure);
    }

    [Fact]
    public void SlotAndTrackingId_PassedThroughUnchanged()
    {
        var opts = MappingOptions.ForScreen(1920, 1080);
        var mapper = new TouchCoordinateMapper(opts);

        var f = mapper.Map(Frame(Contact(100, 100, slot: 7, trackingId: 9999)));

        Assert.Equal(7, f.Contacts[0].Slot);
        Assert.Equal(9999, f.Contacts[0].TrackingId);
    }

    [Fact]
    public void LandscapeFlipsAxesAppropriately()
    {
        // In Landscape, top-left of touch panel (X=0, Y=0) should map to
        // bottom-left of the screen (the rotated frame).
        var opts = MappingOptions.ForScreen(1920, 1080, Orientation.Landscape);
        var mapper = new TouchCoordinateMapper(opts);

        var f = mapper.Map(Frame(Contact(0, 0)));

        // Y=0 panel ↦ rx = ny = 0   ↦ ScreenX = 0
        // X=0 panel ↦ ry = 1 - nx = 1 ↦ ScreenY ≈ MonitorH - 1 (truncation)
        Assert.Equal(0, f.Contacts[0].ScreenX);
        Assert.InRange(f.Contacts[0].ScreenY, 1078, 1080);
    }

    [Fact]
    public void MultipleContacts_AllMapped()
    {
        var opts = MappingOptions.ForScreen(1920, 1080, Orientation.Portrait);
        var mapper = new TouchCoordinateMapper(opts);

        var f = mapper.Map(Frame(
            Contact(0, 0, slot: 0, trackingId: 1),
            Contact(ReMarkable2Constants.TouchXMax, ReMarkable2Constants.TouchYMax,
                slot: 1, trackingId: 2)));

        Assert.Equal(2, f.Contacts.Count);
        Assert.Equal(0, f.Contacts[0].ScreenX);
        Assert.InRange(f.Contacts[1].ScreenX, 1918, 1920);
    }
}

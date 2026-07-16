using RemarkableTablet.Core.Devices;
using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Tablet;
using Xunit;

namespace RemarkableTablet.Core.Tests;

public class TouchCoordinateMapperTests
{
    private static readonly DeviceProfile Rm2 = ReMarkable2Profile.Instance;

    private static TouchContact Contact(int x, int y, int pressure = 100, int slot = 0, int trackingId = 1)
    {
        return new TouchContact(slot, trackingId, x, y, pressure, 0, 0, 0, 0);
    }

    private static TouchFrame Frame(params TouchContact[] contacts)
    {
        return new TouchFrame(contacts);
    }

    [Fact]
    public void Portrait_TopLeftMapsToScreenTopLeft()
    {
        var opts = MappingOptions.ForScreen(1920, 1080);
        var mapper = new TouchCoordinateMapper(opts, Rm2);

        var f = mapper.Map(Frame(Contact(0, 0)));

        Assert.Equal(0, f.Contacts[0].ScreenX);
        Assert.Equal(0, f.Contacts[0].ScreenY);
    }

    [Fact]
    public void Portrait_BottomRightMapsToScreenBottomRight()
    {
        var opts = MappingOptions.ForScreen(1920, 1080);
        var mapper = new TouchCoordinateMapper(opts, Rm2);

        var f = mapper.Map(Frame(Contact(
            Rm2.Touch.XMax,
            Rm2.Touch.YMax)));

        Assert.Equal(1919, f.Contacts[0].ScreenX);
        Assert.Equal(1079, f.Contacts[0].ScreenY);
    }

    [Fact]
    public void EmptyFrame_PassesThroughEmpty()
    {
        var opts = MappingOptions.ForScreen(1920, 1080);
        var mapper = new TouchCoordinateMapper(opts, Rm2);

        var f = mapper.Map(TouchFrame.Empty);

        Assert.Empty(f.Contacts);
    }

    [Fact]
    public void Pressure_ScaledTo0To1024()
    {
        var opts = MappingOptions.ForScreen(1920, 1080);
        var mapper = new TouchCoordinateMapper(opts, Rm2);

        var f = mapper.Map(Frame(Contact(0, 0, Rm2.Touch.PressureMax)));

        Assert.Equal((uint)InjectionScale.PressureMax, f.Contacts[0].Pressure);
    }

    [Fact]
    public void SlotAndTrackingId_PassedThroughUnchanged()
    {
        var opts = MappingOptions.ForScreen(1920, 1080);
        var mapper = new TouchCoordinateMapper(opts, Rm2);

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
        var mapper = new TouchCoordinateMapper(opts, Rm2);

        var f = mapper.Map(Frame(Contact(0, 0)));

        // Y=0 panel ↦ rx = ny = 0   ↦ ScreenX = 0
        // X=0 panel ↦ ry = 1 - nx = 1 ↦ ScreenY ≈ MonitorH - 1 (truncation)
        Assert.Equal(0, f.Contacts[0].ScreenX);
        Assert.InRange(f.Contacts[0].ScreenY, 1078, 1080);
    }

    [Fact]
    public void MultipleContacts_AllMapped()
    {
        var opts = MappingOptions.ForScreen(1920, 1080);
        var mapper = new TouchCoordinateMapper(opts, Rm2);

        var f = mapper.Map(Frame(
            Contact(0, 0, slot: 0, trackingId: 1),
            Contact(Rm2.Touch.XMax, Rm2.Touch.YMax,
                slot: 1, trackingId: 2)));

        Assert.Equal(2, f.Contacts.Count);
        Assert.Equal(0, f.Contacts[0].ScreenX);
        Assert.InRange(f.Contacts[1].ScreenX, 1918, 1920);
    }
}
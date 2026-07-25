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

    // Panel axes measured 2026-07-25 (samples/hw2-touch.log): X = 0 at the left,
    // Y = 0 at the BOTTOM. So the panel's raw origin is the screen's bottom-left.
    private static MappingOptions Portrait()
    {
        return MappingOptions.ForScreen(1920, 1080, Orientation.Portrait, FitMode.Stretch);
    }

    [Fact]
    public void Portrait_PanelOriginIsTheScreensBottomLeft()
    {
        var f = new TouchCoordinateMapper(Portrait(), Rm2).Map(Frame(Contact(0, 0)));

        Assert.Equal(0, f.Contacts[0].ScreenX);
        Assert.Equal(1079, f.Contacts[0].ScreenY);
    }

    [Fact]
    public void Portrait_TopLeftMapsToScreenTopLeft()
    {
        // Physical top-left = (X=0, Y=YMax).
        var f = new TouchCoordinateMapper(Portrait(), Rm2).Map(Frame(Contact(0, Rm2.Touch.YMax)));

        Assert.Equal(0, f.Contacts[0].ScreenX);
        Assert.Equal(0, f.Contacts[0].ScreenY);
    }

    [Fact]
    public void Portrait_BottomRightMapsToScreenBottomRight()
    {
        // Physical bottom-right = (X=XMax, Y=0).
        var f = new TouchCoordinateMapper(Portrait(), Rm2).Map(Frame(Contact(Rm2.Touch.XMax, 0)));

        Assert.Equal(1919, f.Contacts[0].ScreenX);
        Assert.Equal(1079, f.Contacts[0].ScreenY);
    }

    // Raw samples from the hardware capture: fingertip on the top-left corner,
    // then the top-right corner, device portrait with the USB-C edge at the bottom.
    [Theory]
    [InlineData(85, 1837, 0)]
    [InlineData(1379, 1835, 1919)]
    public void MeasuredTouchCorners_LandAlongTheTopOfTheScreen(int rawX, int rawY, int expectX)
    {
        var f = new TouchCoordinateMapper(Portrait(), Rm2).Map(Frame(Contact(rawX, rawY)));

        // ~7 % tolerance: a fingertip centre can't sit closer than half a finger
        // width to the edge, so this pins the corner, not the exact pixel.
        Assert.InRange(f.Contacts[0].ScreenX, expectX == 0 ? 0 : 1790, expectX == 0 ? 130 : 1919);
        Assert.InRange(f.Contacts[0].ScreenY, 0, 40);
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
        // Panel origin (X=0, Y=0) is the device's BOTTOM-left corner. Landscape
        // rotates the device 90° CCW, which swings that corner to the screen's
        // bottom-right.
        var opts = MappingOptions.ForScreen(1920, 1080, Orientation.Landscape, FitMode.Stretch);
        var mapper = new TouchCoordinateMapper(opts, Rm2);

        var f = mapper.Map(Frame(Contact(0, 0)));

        Assert.Equal(1919, f.Contacts[0].ScreenX);
        Assert.Equal(1079, f.Contacts[0].ScreenY);
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
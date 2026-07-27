using RemarkableTablet.Core.Devices;
using Xunit;

namespace RemarkableTablet.Core.Tests;

public class InputDeviceMapTests
{
    // Verbatim from an rM2 on stock firmware 1231, trimmed to the lines the
    // parser reads. Node numbering here is the profile's expectation.
    private const string Rm2Table = """
                                    I: Bus=0019 Vendor=0000 Product=0000 Version=0000
                                    N: Name="30370000.snvs:snvs-powerkey"
                                    P: Phys=snvs-pwrkey/input0
                                    S: Sysfs=/devices/platform/soc@0/30000000.bus/30370000.snvs/30370000.snvs:snvs-powerkey/input/input0
                                    H: Handlers=kbd event0
                                    B: EV=3

                                    I: Bus=0018 Vendor=2d1f Product=0095 Version=1231
                                    N: Name="Wacom I2C Digitizer"
                                    P: Phys=
                                    S: Sysfs=/devices/platform/soc@0/30800000.bus/30a30000.i2c/i2c-1/1-0009/input/input1
                                    H: Handlers=event1
                                    B: EV=b

                                    I: Bus=0000 Vendor=0000 Product=0000 Version=0000
                                    N: Name="pt_mt"
                                    P: Phys=
                                    S: Sysfs=/devices/virtual/input/input2
                                    H: Handlers=event2
                                    B: EV=b

                                    """;

    private static readonly DeviceProfile Rm2 = ReMarkable2Profile.Instance;

    [Fact]
    public void Parse_ReadsNamesAndEventNodes()
    {
        var map = InputDeviceMap.Parse(Rm2Table);

        Assert.Equal(3, map.Entries.Count);
        Assert.Equal("/dev/input/event1", map.FindByName("Wacom I2C Digitizer"));
        Assert.Equal("/dev/input/event2", map.FindByName("pt_mt"));
        Assert.Equal("/dev/input/event0", map.FindByName("30370000.snvs:snvs-powerkey"));
    }

    [Fact]
    public void FindByName_IsCaseInsensitiveAndAcceptsAPrefix()
    {
        var map = InputDeviceMap.Parse(Rm2Table);

        Assert.Equal("/dev/input/event2", map.FindByName("PT_MT"));
        Assert.Equal("/dev/input/event1", map.FindByName("Wacom I2C"));
        Assert.Null(map.FindByName("cyttsp5"));
        Assert.Null(map.FindByName(null));
    }

    [Fact]
    public void FindByName_SurvivesAPunctuationChangeInTheDriverName()
    {
        // Guards the case that would otherwise produce a spurious "device not
        // found" warning on every connect: same driver, different spelling.
        var renamed = Rm2Table.Replace("N: Name=\"Wacom I2C Digitizer\"", "N: Name=\"wacom_i2c_digitizer\"");

        Assert.Equal("/dev/input/event1", InputDeviceMap.Parse(renamed).FindByName("Wacom I2C Digitizer"));
    }

    [Fact]
    public void FindByName_PrefersAnExactMatchOverALooseOne()
    {
        var ambiguous = """
                        N: Name="pt_mt helper"
                        H: Handlers=event7

                        N: Name="pt_mt"
                        H: Handlers=event2

                        """;

        Assert.Equal("/dev/input/event2", InputDeviceMap.Parse(ambiguous).FindByName("pt_mt"));
    }

    [Fact]
    public void Resolve_OnStockFirmware_MatchesTheProfileAndSaysNothing()
    {
        var resolved = InputDeviceMap.Parse(Rm2Table).Resolve(Rm2);

        Assert.Equal(Rm2.PenDevicePath, resolved.PenPath);
        Assert.Equal(Rm2.TouchDevicePath, resolved.TouchPath);
        Assert.Empty(resolved.Notes);
    }

    [Fact]
    public void Resolve_WhenNodesHaveMoved_FollowsTheNameAndSaysSo()
    {
        // The failure this exists for: a firmware revision renumbers the nodes.
        // Hard-coded paths would open the wrong device and hang with no events.
        var shuffled = Rm2Table
            .Replace("H: Handlers=event1", "H: Handlers=event5")
            .Replace("H: Handlers=event2", "H: Handlers=event6");

        var resolved = InputDeviceMap.Parse(shuffled).Resolve(Rm2);

        Assert.Equal("/dev/input/event5", resolved.PenPath);
        Assert.Equal("/dev/input/event6", resolved.TouchPath);
        Assert.Contains(resolved.Notes, n => n.Contains("pen device moved"));
        Assert.Contains(resolved.Notes, n => n.Contains("touch device moved"));
    }

    [Fact]
    public void Resolve_OnAnUnrecognisedTouchDriver_WarnsAboutTheMapping()
    {
        // Mainline kernels bind cyttsp5 rather than the stock pt_mt, and its
        // coordinate behavior differed enough to break KOReader (#10012). The
        // axis conventions this tool maps with were measured on stock firmware.
        var mainline = Rm2Table.Replace("N: Name=\"pt_mt\"", "N: Name=\"cyttsp5\"");

        var resolved = InputDeviceMap.Parse(mainline).Resolve(Rm2);

        Assert.Equal(Rm2.TouchDevicePath, resolved.TouchPath); // falls back
        Assert.Contains(resolved.Notes, n => n.Contains("custom or mainline kernel"));
    }

    [Fact]
    public void Parse_ToleratesGarbageAndEmptyInput()
    {
        Assert.Empty(InputDeviceMap.Parse("").Entries);
        Assert.Empty(InputDeviceMap.Parse("not a device table at all\n\n").Entries);

        // A record with a name but no event handler is not usable.
        Assert.Empty(InputDeviceMap.Parse("N: Name=\"thing\"\nH: Handlers=kbd\n\n").Entries);
    }
}

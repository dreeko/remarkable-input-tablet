using RemarkableTablet.Core.Devices;
using Xunit;

namespace RemarkableTablet.Core.Tests;

public class DeviceDetectorTests
{
    [Theory]
    [InlineData("armv7l")]
    [InlineData("ARMV7L")]
    [InlineData(" armv7l\n")]
    public void ResolveProfile_Armv7l_ReturnsRm2(string uname)
    {
        Assert.Same(ReMarkable2Profile.Instance, DeviceDetector.ResolveProfile(uname));
    }

    [Theory]
    [InlineData("aarch64")]
    [InlineData("arm64")]
    [InlineData("AARCH64")]
    [InlineData("  aarch64  ")]
    public void ResolveProfile_Aarch64_ReturnsRmpp(string uname)
    {
        Assert.Same(ReMarkablePaperProProfile.Instance, DeviceDetector.ResolveProfile(uname));
    }

    [Theory]
    [InlineData("x86_64")]
    [InlineData("mips")]
    [InlineData("")]
    public void ResolveProfile_Unknown_Throws(string uname)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DeviceDetector.ResolveProfile(uname));
        Assert.Contains("Unrecognised device architecture", ex.Message);
    }

    [Theory]
    [InlineData("rm2")]
    [InlineData("reMarkable2")]
    [InlineData("reMarkable 2")]
    public void ByName_Rm2_Variants(string name)
    {
        Assert.Same(ReMarkable2Profile.Instance, DeviceDetector.ByName(name));
    }

    [Theory]
    [InlineData("rmpp")]
    [InlineData("PaperPro")]
    [InlineData("reMarkable Paper Pro")]
    public void ByName_Rmpp_Variants(string name)
    {
        Assert.Same(ReMarkablePaperProProfile.Instance, DeviceDetector.ByName(name));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData(null)]
    public void ByName_AutoOrUnknown_ReturnsNull(string? name)
    {
        Assert.Null(DeviceDetector.ByName(name));
    }
}

using RemarkableTablet.Linux.Display;
using Xunit;

namespace RemarkableTablet.Core.Tests;

public class LinuxScreenDetectorTests
{
    [Fact]
    public void Xrandr_ParsesDesktopDimensions()
    {
        const string text = "Screen 0: minimum 16 x 16, current 3440 x 1440, maximum 32767 x 32767";
        Assert.Equal((3440, 1440), LinuxScreenDetector.ParseXrandr(text));
    }

    [Fact]
    public void KScreenDoctor_PrefersPrimaryEnabledOutput()
    {
        const string text = """
            Output: 1 HDMI-A-1 enabled connected
              Geometry: 0,0 1280x720
            Output: 2 eDP-1 enabled connected primary
              Geometry: 1280,0 1920x1080
            """;
        Assert.Equal((1920, 1080), LinuxScreenDetector.ParseKScreenDoctor(text));
    }

    [Fact]
    public void WlrRandr_ParsesCurrentMode()
    {
        const string text = """
            eDP-1
              2560x1440 px, 59.951000 Hz (preferred, current)
            """;
        Assert.Equal((2560, 1440), LinuxScreenDetector.ParseWlrRandr(text));
    }

    [Fact]
    public void InvalidOutput_ReturnsNull()
    {
        Assert.Null(LinuxScreenDetector.ParseXrandr("cannot open display"));
        Assert.Null(LinuxScreenDetector.ParseWlrRandr("no outputs"));
    }
}

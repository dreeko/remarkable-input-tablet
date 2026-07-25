using RemarkableTablet.Core.Output;
using RemarkableTablet.Windows.Output;
using Xunit;

namespace RemarkableTablet.Windows.Tests;

public class WindowsInkOutputTests
{
    [Fact]
    public void Initialize_CreatesDeviceSuccessfully()
    {
        // Verifies CreateSyntheticPointerDevice is available on this Windows version.
        // Requires Windows 10 1809+. Will throw InvalidOperationException on older OS.
        using var output = new WindowsInkOutput();
        var ex = Record.Exception(() => output.Initialize());
        Assert.Null(ex);
    }

    [Fact]
    public void Send_HoverFrame_DoesNotThrow()
    {
        using var output = new WindowsInkOutput();
        output.Initialize();

        var frame = new MappedFrame(
            100, 100,
            0,
            0, 0,
            30,
            false, false,
            false, true);

        var ex = Record.Exception(() => output.Send(frame));
        Assert.Null(ex);
    }

    [Fact]
    public void Send_TouchDownThenUp_DoesNotThrow()
    {
        using var output = new WindowsInkOutput();
        output.Initialize();

        var hover = new MappedFrame(100, 100, 0, 0, 0, 30, false, false, false, true);
        var touch = new MappedFrame(100, 100, 512, 0, 0, 0, true, false, false, true);
        var lift = new MappedFrame(100, 100, 0, 0, 0, 10, false, false, false, true);

        output.Send(hover);
        output.Send(touch);
        output.Send(lift);
    }

    [Fact]
    public void Dispose_WithPenDown_DoesNotThrow()
    {
        var output = new WindowsInkOutput();
        output.Initialize();
        output.Send(new MappedFrame(200, 200, 800, 0, 0, 0, true, false, false, true));
        // Dispose should emit pen-up cleanly
        var ex = Record.Exception(() => output.Dispose());
        Assert.Null(ex);
    }
}
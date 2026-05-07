using RemarkableTablet.Core.Output;
using RemarkableTablet.Windows.Output;
using Xunit;

namespace RemarkableTablet.Windows.Tests;

public class WindowsTouchInjectionOutputTests
{
    private static MappedTouchContact C(int slot, int x, int y, int trackingId = 0, uint pressure = 256) =>
        new(slot, trackingId == 0 ? slot + 1000 : trackingId, x, y, pressure);

    private static MappedTouchFrame F(params MappedTouchContact[] cs) => new(cs);

    [Fact]
    public void Initialize_CreatesDeviceSuccessfully()
    {
        using var output = new WindowsTouchInjectionOutput();
        var ex = Record.Exception(() => output.Initialize());
        Assert.Null(ex);
    }

    [Fact]
    public void Send_TwoContactsDownThenUp_DoesNotThrow()
    {
        using var output = new WindowsTouchInjectionOutput();
        output.Initialize();

        // Two-finger touch down.
        var ex = Record.Exception(() => output.Send(F(C(0, 100, 100), C(1, 300, 100))));
        Assert.Null(ex);

        // Two-finger pan (positions update; no contact-set change).
        ex = Record.Exception(() => output.Send(F(C(0, 110, 100), C(1, 310, 100))));
        Assert.Null(ex);

        // Both lifted.
        ex = Record.Exception(() => output.Send(MappedTouchFrame.Empty));
        Assert.Null(ex);
    }

    [Fact]
    public void Send_OneContactReleasedWhileOtherContinues_DoesNotThrow()
    {
        using var output = new WindowsTouchInjectionOutput();
        output.Initialize();

        output.Send(F(C(0, 100, 100), C(1, 300, 100)));
        // Slot 0 lifts; slot 1 continues.
        var ex = Record.Exception(() => output.Send(F(C(1, 310, 100))));
        Assert.Null(ex);
    }

    [Fact]
    public void ReleaseAll_AfterContactsActive_DoesNotThrow()
    {
        using var output = new WindowsTouchInjectionOutput();
        output.Initialize();

        output.Send(F(C(0, 100, 100), C(1, 300, 100)));
        var ex = Record.Exception(() => output.ReleaseAll());
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_WithContactsActive_DoesNotThrow()
    {
        var output = new WindowsTouchInjectionOutput();
        output.Initialize();
        output.Send(F(C(0, 100, 100)));
        var ex = Record.Exception(() => output.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Send_EmptyFrameWithNoActiveContacts_DoesNotThrow()
    {
        using var output = new WindowsTouchInjectionOutput();
        output.Initialize();
        var ex = Record.Exception(() => output.Send(MappedTouchFrame.Empty));
        Assert.Null(ex);
    }
}

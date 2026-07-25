using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Pipeline;
using Xunit;

namespace RemarkableTablet.Core.Tests;

public class PenProximityGateTests
{
    private long _now;

    private PenProximityGate Make()
    {
        return new PenProximityGate(() => _now);
    }

    private static MappedFrame Pen(bool inRange)
    {
        return new MappedFrame(10, 10, 0, 0, 0, 0, false, false, false, inRange);
    }

    private static MappedTouchFrame Touch(params int[] trackingIds)
    {
        var contacts = new MappedTouchContact[trackingIds.Length];
        for (var i = 0; i < trackingIds.Length; i++)
            contacts[i] = new MappedTouchContact(i, trackingIds[i], 100 + i, 200, 500, 12, 10);
        return new MappedTouchFrame(contacts);
    }

    [Fact]
    public void PenAway_TouchPassesThroughUnchanged()
    {
        var gate = Make();
        var frame = Touch(1, 2);

        Assert.Same(frame, gate.Filter(frame));
        Assert.False(gate.IsClosed);
        Assert.False(gate.TakePendingRelease());
    }

    // ── Closing is driven by the pen, not by touch ────────────────────────────

    [Fact]
    public void PenArriving_RequestsAReleaseWithoutWaitingForATouchFrame()
    {
        // The load-bearing case: on the rM2 the panel goes silent the moment the
        // pen is in proximity, so nothing may depend on another touch frame
        // arriving. Note there is no Filter() call after the pen arrives.
        var gate = Make();
        gate.Filter(Touch(7)); // palm live on the host
        gate.OnPenFrame(Pen(true));

        Assert.True(gate.IsClosed);
        Assert.True(gate.TakePendingRelease());
        Assert.False(gate.TakePendingRelease()); // once per closure
        Assert.Equal(1, gate.CloseCount);
    }

    [Fact]
    public void ContactLiveWhenThePenArrives_StaysSuppressedThoughNoFrameArrivedWhileClosed()
    {
        // The other half of the same silence: the resting palm must not come back
        // as a live contact when the pen lifts.
        var gate = Make();
        gate.Filter(Touch(7));
        gate.OnPenFrame(Pen(true));
        Assert.True(gate.TakePendingRelease());

        _now += PenProximityGate.LingerMs + 1;

        // Panel resumes: palm still down, plus a real finger.
        var result = gate.Filter(Touch(7, 8));

        Assert.NotNull(result);
        Assert.Single(result.Contacts);
        Assert.Equal(8, result.Contacts[0].TrackingId);
    }

    [Fact]
    public void PenAway_NoReleaseIsRequested()
    {
        var gate = Make();
        gate.Filter(Touch(1));
        gate.OnPenFrame(Pen(false));

        Assert.False(gate.TakePendingRelease());
        Assert.False(gate.IsClosed);
    }

    [Fact]
    public void PenStayingInRange_DoesNotRequestRepeatReleases()
    {
        var gate = Make();

        gate.OnPenFrame(Pen(true));
        Assert.True(gate.TakePendingRelease());

        for (var i = 0; i < 20; i++)
        {
            _now += 10;
            gate.OnPenFrame(Pen(true));
            Assert.False(gate.TakePendingRelease());
        }

        Assert.Equal(1, gate.CloseCount);
    }

    [Fact]
    public void EachStroke_CountsAsItsOwnClosure()
    {
        var gate = Make();

        gate.OnPenFrame(Pen(true));
        Assert.True(gate.TakePendingRelease());

        _now += PenProximityGate.LingerMs + 1;
        gate.Filter(MappedTouchFrame.Empty); // gate reopens
        Assert.False(gate.IsClosed);

        gate.OnPenFrame(Pen(true));
        Assert.True(gate.TakePendingRelease());
        Assert.Equal(2, gate.CloseCount);
    }

    // ── Withholding touch while closed ────────────────────────────────────────

    [Fact]
    public void WhileClosed_TouchIsWithheld()
    {
        var gate = Make();
        gate.OnPenFrame(Pen(true));

        Assert.Null(gate.Filter(Touch(1)));
        Assert.True(gate.IsClosed);
    }

    [Fact]
    public void ContactLandingWhileClosed_StaysSuppressedAfterReopening()
    {
        // For a device that keeps reporting touch while the pen is down: contacts
        // that appeared during the stroke must not become live on pen-up.
        var gate = Make();
        gate.OnPenFrame(Pen(true));
        gate.Filter(Touch(7));

        _now += PenProximityGate.LingerMs + 1;
        var result = gate.Filter(Touch(7));

        Assert.NotNull(result);
        Assert.Empty(result.Contacts);
    }

    [Fact]
    public void SuppressedContact_IsForgottenOnceLifted()
    {
        var gate = Make();
        gate.OnPenFrame(Pen(true));
        gate.Filter(Touch(7));
        _now += PenProximityGate.LingerMs + 1;

        gate.Filter(MappedTouchFrame.Empty); // palm lifted
        var frame = Touch(7); // ID reuse would be a firmware bug, but be sure
        Assert.Same(frame, gate.Filter(frame));
    }

    [Fact]
    public void GateStaysClosedForTheLingerWindow()
    {
        var gate = Make();
        gate.OnPenFrame(Pen(true));

        _now += PenProximityGate.LingerMs - 1;
        Assert.Null(gate.Filter(Touch(2)));

        _now += 2;
        Assert.NotNull(gate.Filter(MappedTouchFrame.Empty));
    }
}

using System.Threading.Channels;
using RemarkableTablet.Core.Evdev;
using RemarkableTablet.Core.Tablet;
using Xunit;

namespace RemarkableTablet.Core.Tests;

/// <summary>
///     Host-side touch policy: the bounded output-slot pool, the size filter and
///     the stale-contact sweep. Protocol decoding lives in
///     <see cref="TouchStateMachineTests" />.
/// </summary>
public class TouchPolicyTests
{
    private long _now;

    private static EvdevEvent Abs(ushort code, int value)
    {
        return new EvdevEvent(EvdevTypes.EV_ABS, code, value);
    }

    private static EvdevEvent Syn()
    {
        return new EvdevEvent(EvdevTypes.EV_SYN, EvdevCodes.SYN_REPORT, 0);
    }

    private (TouchStateMachine Sm, ChannelWriter<TouchFrame> Writer, Channel<TouchFrame> Ch) Setup(
        TouchOptions opts)
    {
        var ch = Channel.CreateUnbounded<TouchFrame>();
        var sm = new TouchStateMachine(opts, () => _now);
        return (sm, ch.Writer, ch);
    }

    private static TouchFrame Last(Channel<TouchFrame> ch)
    {
        TouchFrame? last = null;
        while (ch.Reader.TryRead(out var f)) last = f;
        Assert.NotNull(last);
        return last;
    }

    private static void Contact(TouchStateMachine sm, ChannelWriter<TouchFrame> w,
        int slot, int trackingId, int major = 10)
    {
        sm.Process(Abs(EvdevCodes.ABS_MT_SLOT, slot), w);
        sm.Process(Abs(EvdevCodes.ABS_MT_TRACKING_ID, trackingId), w);
        sm.Process(Abs(EvdevCodes.ABS_MT_POSITION_X, 100 + slot), w);
        sm.Process(Abs(EvdevCodes.ABS_MT_POSITION_Y, 200 + slot), w);
        sm.Process(Abs(EvdevCodes.ABS_MT_TOUCH_MAJOR, major), w);
    }

    // ── Slot pool ─────────────────────────────────────────────────────────────

    [Fact]
    public void ContactsBeyondMaxTracked_AreNotEmitted()
    {
        // The sinks silently discard slots >= MaxTracked, so the state machine
        // must never hand out one; the cap belongs here, where it is countable.
        var (sm, w, ch) = Setup(new TouchOptions { MaxTracked = 2 });

        for (var i = 0; i < 5; i++) Contact(sm, w, i, 100 + i);
        sm.Process(Syn(), w);

        var frame = Last(ch);
        Assert.Equal(2, frame.Contacts.Count);
        Assert.Equal([0, 1], frame.Contacts.Select(c => c.Slot));
        Assert.Equal(3, sm.DroppedContacts);
    }

    [Fact]
    public void DroppedContact_ClaimsASlotOnceOneFrees()
    {
        var (sm, w, ch) = Setup(new TouchOptions { MaxTracked = 1 });

        Contact(sm, w, 0, 500);
        Contact(sm, w, 1, 501); // no slot available
        sm.Process(Syn(), w);
        Assert.Single(Last(ch).Contacts);

        // First contact lifts; the queued one takes its place.
        sm.Process(Abs(EvdevCodes.ABS_MT_SLOT, 0), w);
        sm.Process(Abs(EvdevCodes.ABS_MT_TRACKING_ID, -1), w);
        sm.Process(Syn(), w);

        var frame = Last(ch);
        Assert.Single(frame.Contacts);
        Assert.Equal(501, frame.Contacts[0].TrackingId);
    }

    [Fact]
    public void DropCount_CountsContactsNotFrames()
    {
        var (sm, w, _) = Setup(new TouchOptions { MaxTracked = 1 });

        Contact(sm, w, 0, 1);
        Contact(sm, w, 1, 2);
        for (var i = 0; i < 10; i++) sm.Process(Syn(), w);

        Assert.Equal(1, sm.DroppedContacts);
    }

    // ── Size filter ───────────────────────────────────────────────────────────

    [Fact]
    public void SizeFilter_DropsOversizeContactsWhenConfigured()
    {
        var (sm, w, ch) = Setup(new TouchOptions { MaxTracked = 5, MaxTouchMajor = 40 });

        Contact(sm, w, 0, 1, 12); // fingertip
        Contact(sm, w, 1, 2, 200); // palm
        sm.Process(Syn(), w);

        var frame = Last(ch);
        Assert.Single(frame.Contacts);
        Assert.Equal(1, frame.Contacts[0].TrackingId);
        Assert.Equal(1, sm.DroppedContacts);
    }

    [Fact]
    public void SizeFilter_ClassificationIsSticky()
    {
        // libinput's rule: once a touch is a palm it stays one. Contact size
        // fluctuates frame to frame (measured palms 17–79 vs fingertips 8–17), so
        // re-testing downward would let a palm flicker back into a live contact
        // in the middle of a rest.
        var (sm, w, ch) = Setup(new TouchOptions { MaxTracked = 5, MaxTouchMajor = 40 });

        Contact(sm, w, 0, 1, 200); // lands as a palm
        sm.Process(Syn(), w);
        Assert.Empty(Last(ch).Contacts);

        // Same contact now reports a fingertip-sized major — must stay rejected.
        sm.Process(Abs(EvdevCodes.ABS_MT_SLOT, 0), w);
        sm.Process(Abs(EvdevCodes.ABS_MT_TOUCH_MAJOR, 10), w);
        sm.Process(Syn(), w);

        Assert.Empty(Last(ch).Contacts);
        Assert.Equal(1, sm.DroppedContacts);
    }

    [Fact]
    public void SizeFilter_StillPromotesAContactThatGrowsIntoAPalm()
    {
        // The other direction must keep working: a palm that lands lightly and
        // spreads should be dropped once it crosses the threshold.
        var (sm, w, ch) = Setup(new TouchOptions { MaxTracked = 5, MaxTouchMajor = 40 });

        Contact(sm, w, 0, 1, 12);
        sm.Process(Syn(), w);
        Assert.Single(Last(ch).Contacts);

        sm.Process(Abs(EvdevCodes.ABS_MT_SLOT, 0), w);
        sm.Process(Abs(EvdevCodes.ABS_MT_TOUCH_MAJOR, 90), w);
        sm.Process(Syn(), w);

        Assert.Empty(Last(ch).Contacts);
    }

    [Fact]
    public void SizeFilter_AFreshContactAfterAPalmIsNotTainted()
    {
        // Stickiness is per contact, not per slot: the next finger in that slot
        // starts with a clean slate.
        var (sm, w, ch) = Setup(new TouchOptions { MaxTracked = 5, MaxTouchMajor = 40 });

        Contact(sm, w, 0, 1, 200);
        sm.Process(Syn(), w);
        sm.Process(Abs(EvdevCodes.ABS_MT_SLOT, 0), w);
        sm.Process(Abs(EvdevCodes.ABS_MT_TRACKING_ID, -1), w);

        Contact(sm, w, 0, 2, 12);
        sm.Process(Syn(), w);

        var frame = Last(ch);
        Assert.Single(frame.Contacts);
        Assert.Equal(2, frame.Contacts[0].TrackingId);
    }

    [Fact]
    public void SizeFilter_IsOffByDefault()
    {
        // The rM2 can't report MT_TOOL_PALM and no palm capture exists yet, so a
        // default threshold would be a guess. Off means "forward everything".
        var (sm, w, ch) = Setup(new TouchOptions { MaxTracked = 5 });

        Contact(sm, w, 0, 1, 255);
        sm.Process(Syn(), w);

        Assert.Single(Last(ch).Contacts);
        Assert.Equal(0, sm.DroppedContacts);
    }

    [Fact]
    public void ContactSize_IsCarriedOnTheFrame()
    {
        var (sm, w, ch) = Setup(new TouchOptions());

        Contact(sm, w, 0, 1, 33);
        sm.Process(Abs(EvdevCodes.ABS_MT_TOUCH_MINOR, 22), w);
        sm.Process(Syn(), w);

        var c = Last(ch).Contacts[0];
        Assert.Equal(33, c.TouchMajor);
        Assert.Equal(22, c.TouchMinor);
    }

    // ── Stale sweep ───────────────────────────────────────────────────────────

    [Fact]
    public void StrandedContact_IsReleasedAfterTheStaleWindow()
    {
        // Firmware going silent mid-contact (e.g. the pen entering proximity)
        // must not leave the host holding a touch-down forever.
        var (sm, w, ch) = Setup(new TouchOptions { StaleContactMs = 1000 });

        Contact(sm, w, 0, 900);
        sm.Process(Syn(), w);
        Assert.Single(Last(ch).Contacts);

        _now += 999;
        Assert.False(sm.SweepStale(w));

        _now += 2;
        Assert.True(sm.SweepStale(w));
        Assert.Empty(Last(ch).Contacts);
        Assert.Equal(1, sm.StaleReleases);
    }

    [Fact]
    public void HeldContact_SurvivesAsLongAsItKeepsReporting()
    {
        // A motionless contact can go ~1 s between reports on the rM2 (measured
        // in touch-pen.log), so activity — not motion — is what keeps it alive.
        var (sm, w, ch) = Setup(new TouchOptions { StaleContactMs = 3000 });

        Contact(sm, w, 0, 900);
        sm.Process(Syn(), w);

        for (var i = 0; i < 5; i++)
        {
            _now += 1100;
            Assert.False(sm.SweepStale(w));
            sm.Process(Abs(EvdevCodes.ABS_MT_SLOT, 0), w);
            sm.Process(Abs(EvdevCodes.ABS_MT_POSITION_X, 100 + i), w);
            sm.Process(Syn(), w);
        }

        Assert.Single(Last(ch).Contacts);
        Assert.Equal(0, sm.StaleReleases);
    }

    [Fact]
    public void SweptSlot_IsReusableByANewContact()
    {
        // The stale sweep must return the output slot to the pool, or a stranded
        // contact would permanently shrink it — five of those and touch is dead.
        var (sm, w, ch) = Setup(new TouchOptions { MaxTracked = 1, StaleContactMs = 500 });

        Contact(sm, w, 0, 900);
        sm.Process(Syn(), w);

        _now += 501;
        Assert.True(sm.SweepStale(w));

        Contact(sm, w, 3, 901);
        sm.Process(Syn(), w);

        var frame = Last(ch);
        Assert.Single(frame.Contacts);
        Assert.Equal(901, frame.Contacts[0].TrackingId);
        Assert.Equal(0, frame.Contacts[0].Slot);
    }
}

using System.Threading.Channels;
using RemarkableTablet.Core.Evdev;
using RemarkableTablet.Core.Tablet;
using Xunit;

namespace RemarkableTablet.Core.Tests;

public class TouchStateMachineTests
{
    private static async Task<List<TouchFrame>> RunFrames(params EvdevEvent[] events)
    {
        var inputCh = Channel.CreateUnbounded<EvdevEvent>();
        var outputCh = Channel.CreateUnbounded<TouchFrame>();

        foreach (var ev in events)
            await inputCh.Writer.WriteAsync(ev);
        inputCh.Writer.Complete();

        await TouchStateMachine.RunAsync(inputCh.Reader, outputCh.Writer, CancellationToken.None);

        var frames = new List<TouchFrame>();
        await foreach (var f in outputCh.Reader.ReadAllAsync())
            frames.Add(f);
        return frames;
    }

    private static EvdevEvent Abs(ushort code, int value) => new(EvdevTypes.EV_ABS, code, value);
    private static EvdevEvent Syn() => new(EvdevTypes.EV_SYN, EvdevCodes.SYN_REPORT, 0);
    private static EvdevEvent SynDropped() => new(EvdevTypes.EV_SYN, EvdevCodes.SYN_DROPPED, 0);

    [Fact]
    public async Task SingleTap_StartsAndReleasesContactInSlotZero()
    {
        var frames = await RunFrames(
            // Slot 0 implicit at start. Tracking ID landing first per protocol.
            Abs(EvdevCodes.ABS_MT_TRACKING_ID, 1389),
            Abs(EvdevCodes.ABS_MT_POSITION_X, 297),
            Abs(EvdevCodes.ABS_MT_POSITION_Y, 1303),
            Abs(EvdevCodes.ABS_MT_PRESSURE, 65),
            Syn(),
            // Release.
            Abs(EvdevCodes.ABS_MT_TRACKING_ID, -1),
            Syn()
        );

        Assert.Equal(2, frames.Count);

        var f0 = frames[0];
        Assert.Single(f0.Contacts);
        Assert.Equal(0, f0.Contacts[0].Slot);
        Assert.Equal(1389, f0.Contacts[0].TrackingId);
        Assert.Equal(297, f0.Contacts[0].X);
        Assert.Equal(1303, f0.Contacts[0].Y);
        Assert.Equal(65, f0.Contacts[0].Pressure);

        var f1 = frames[1];
        Assert.Empty(f1.Contacts);
    }

    [Fact]
    public async Task TwoFinger_TracksBothSlotsIndependently()
    {
        var frames = await RunFrames(
            // Slot 0 contact lands.
            Abs(EvdevCodes.ABS_MT_TRACKING_ID, 1389),
            Abs(EvdevCodes.ABS_MT_POSITION_X, 100),
            Abs(EvdevCodes.ABS_MT_POSITION_Y, 200),
            Syn(),
            // Slot 1 contact lands.
            Abs(EvdevCodes.ABS_MT_SLOT, 1),
            Abs(EvdevCodes.ABS_MT_TRACKING_ID, 1390),
            Abs(EvdevCodes.ABS_MT_POSITION_X, 700),
            Abs(EvdevCodes.ABS_MT_POSITION_Y, 800),
            Syn(),
            // Slot 0 moves; slot 1 stays the same.
            Abs(EvdevCodes.ABS_MT_SLOT, 0),
            Abs(EvdevCodes.ABS_MT_POSITION_X, 110),
            Syn()
        );

        Assert.Equal(3, frames.Count);

        Assert.Single(frames[0].Contacts);

        Assert.Equal(2, frames[1].Contacts.Count);
        Assert.Equal(0, frames[1].Contacts[0].Slot);
        Assert.Equal(1, frames[1].Contacts[1].Slot);
        Assert.Equal(1389, frames[1].Contacts[0].TrackingId);
        Assert.Equal(1390, frames[1].Contacts[1].TrackingId);

        Assert.Equal(2, frames[2].Contacts.Count);
        Assert.Equal(110, frames[2].Contacts[0].X);
        // Slot 1 carries over its previous Y.
        Assert.Equal(800, frames[2].Contacts[1].Y);
    }

    [Fact]
    public async Task SlotRelease_RemovesContactFromSubsequentFrames()
    {
        var frames = await RunFrames(
            Abs(EvdevCodes.ABS_MT_TRACKING_ID, 1),
            Abs(EvdevCodes.ABS_MT_POSITION_X, 50),
            Abs(EvdevCodes.ABS_MT_POSITION_Y, 60),
            Syn(),
            // Release slot 0.
            Abs(EvdevCodes.ABS_MT_TRACKING_ID, -1),
            Syn(),
            // Empty frame (nothing changes).
            Syn()
        );

        Assert.Equal(3, frames.Count);
        Assert.Single(frames[0].Contacts);
        Assert.Empty(frames[1].Contacts);
        Assert.Empty(frames[2].Contacts);
    }

    [Fact]
    public async Task SynDropped_ClearsAllContactsAndEmitsEmptyFrame()
    {
        var frames = await RunFrames(
            Abs(EvdevCodes.ABS_MT_TRACKING_ID, 1),
            Abs(EvdevCodes.ABS_MT_POSITION_X, 100),
            Syn(),
            // Add second contact.
            Abs(EvdevCodes.ABS_MT_SLOT, 1),
            Abs(EvdevCodes.ABS_MT_TRACKING_ID, 2),
            Abs(EvdevCodes.ABS_MT_POSITION_X, 700),
            Syn(),
            // Kernel ring overflow: state is unknown.
            SynDropped(),
            // Following SYN_REPORT must show empty.
            Syn()
        );

        Assert.Equal(4, frames.Count);
        Assert.Equal(2, frames[1].Contacts.Count);
        Assert.Empty(frames[2].Contacts); // emitted by SYN_DROPPED
        Assert.Empty(frames[3].Contacts);
    }

    [Fact]
    public async Task SlotReuse_AfterReleaseAcceptsNewTrackingId()
    {
        var frames = await RunFrames(
            // First contact in slot 0.
            Abs(EvdevCodes.ABS_MT_TRACKING_ID, 1389),
            Abs(EvdevCodes.ABS_MT_POSITION_X, 100),
            Syn(),
            // Release.
            Abs(EvdevCodes.ABS_MT_TRACKING_ID, -1),
            Syn(),
            // New contact reuses slot 0 with a new tracking ID.
            Abs(EvdevCodes.ABS_MT_TRACKING_ID, 1390),
            Abs(EvdevCodes.ABS_MT_POSITION_X, 500),
            Syn()
        );

        Assert.Equal(3, frames.Count);
        Assert.Equal(1389, frames[0].Contacts[0].TrackingId);
        Assert.Empty(frames[1].Contacts);
        Assert.Equal(1390, frames[2].Contacts[0].TrackingId);
        Assert.Equal(500, frames[2].Contacts[0].X);
    }

    [Fact]
    public async Task ContactsOrderedBySlotAscending()
    {
        var frames = await RunFrames(
            // Land slot 5 first.
            Abs(EvdevCodes.ABS_MT_SLOT, 5),
            Abs(EvdevCodes.ABS_MT_TRACKING_ID, 100),
            Abs(EvdevCodes.ABS_MT_POSITION_X, 1),
            // Then slot 2.
            Abs(EvdevCodes.ABS_MT_SLOT, 2),
            Abs(EvdevCodes.ABS_MT_TRACKING_ID, 200),
            Abs(EvdevCodes.ABS_MT_POSITION_X, 2),
            // Then slot 7.
            Abs(EvdevCodes.ABS_MT_SLOT, 7),
            Abs(EvdevCodes.ABS_MT_TRACKING_ID, 300),
            Abs(EvdevCodes.ABS_MT_POSITION_X, 3),
            Syn()
        );

        var f = Assert.Single(frames);
        Assert.Equal(3, f.Contacts.Count);
        Assert.Equal(2, f.Contacts[0].Slot);
        Assert.Equal(5, f.Contacts[1].Slot);
        Assert.Equal(7, f.Contacts[2].Slot);
    }
}

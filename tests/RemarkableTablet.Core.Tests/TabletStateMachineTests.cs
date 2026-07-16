using System.Threading.Channels;
using RemarkableTablet.Core.Evdev;
using RemarkableTablet.Core.Tablet;
using Xunit;

namespace RemarkableTablet.Core.Tests;

public class TabletStateMachineTests
{
    private static async Task<List<PenFrame>> RunFrames(params EvdevEvent[] events)
    {
        var inputCh = Channel.CreateUnbounded<EvdevEvent>();
        var outputCh = Channel.CreateUnbounded<PenFrame>();

        foreach (var ev in events)
            await inputCh.Writer.WriteAsync(ev);
        inputCh.Writer.Complete();

        await TabletStateMachine.RunAsync(inputCh.Reader, outputCh.Writer, CancellationToken.None);

        var frames = new List<PenFrame>();
        await foreach (var f in outputCh.Reader.ReadAllAsync())
            frames.Add(f);
        return frames;
    }

    [Fact]
    public async Task EmitsFrameOnSynReport()
    {
        var frames = await RunFrames(
            new EvdevEvent(EvdevTypes.EV_KEY, EvdevCodes.BTN_TOOL_PEN, 1), // pen enters range
            new EvdevEvent(EvdevTypes.EV_ABS, EvdevCodes.ABS_X, 10000),
            new EvdevEvent(EvdevTypes.EV_ABS, EvdevCodes.ABS_Y, 8000),
            new EvdevEvent(EvdevTypes.EV_ABS, EvdevCodes.ABS_PRESSURE, 2048),
            new EvdevEvent(EvdevTypes.EV_KEY, EvdevCodes.BTN_TOUCH, 1),
            new EvdevEvent(EvdevTypes.EV_SYN, EvdevCodes.SYN_REPORT, 0)
        );

        Assert.Single(frames);
        var f = frames[0];
        Assert.Equal(10000, f.X);
        Assert.Equal(8000, f.Y);
        Assert.Equal(2048, f.Pressure);
        Assert.True(f.IsTouch);
        Assert.True(f.InRange);
    }

    [Fact]
    public async Task DetectsEraserTool()
    {
        var frames = await RunFrames(
            new EvdevEvent(EvdevTypes.EV_KEY, EvdevCodes.BTN_TOOL_RUBBER, 1),
            new EvdevEvent(EvdevTypes.EV_SYN, EvdevCodes.SYN_REPORT, 0)
        );

        Assert.Single(frames);
        Assert.True(frames[0].IsEraser);
        Assert.False(frames[0].IsTouch); // not touching surface yet
        Assert.True(frames[0].InRange);
    }

    [Fact]
    public async Task EmitsPenUpOnSynDropped()
    {
        // First establish touch, then SYN_DROPPED should force pen-up
        var frames = await RunFrames(
            new EvdevEvent(EvdevTypes.EV_KEY, EvdevCodes.BTN_TOUCH, 1),
            new EvdevEvent(EvdevTypes.EV_SYN, EvdevCodes.SYN_REPORT, 0), // frame 0: touching
            new EvdevEvent(EvdevTypes.EV_SYN, EvdevCodes.SYN_DROPPED, 0) // frame 1: forced pen-up
        );

        Assert.Equal(2, frames.Count);
        Assert.True(frames[0].IsTouch);
        Assert.False(frames[1].IsTouch); // forced pen-up
        Assert.Equal(0, frames[1].Pressure);
    }

    [Fact]
    public async Task SynDropped_ClearsToolEraserAndBarrelState()
    {
        var frames = await RunFrames(
            new EvdevEvent(EvdevTypes.EV_KEY, EvdevCodes.BTN_TOOL_RUBBER, 1),
            new EvdevEvent(EvdevTypes.EV_KEY, EvdevCodes.BTN_STYLUS, 1),
            new EvdevEvent(EvdevTypes.EV_KEY, EvdevCodes.BTN_STYLUS2, 1),
            new EvdevEvent(EvdevTypes.EV_KEY, EvdevCodes.BTN_TOUCH, 1),
            new EvdevEvent(EvdevTypes.EV_SYN, EvdevCodes.SYN_REPORT, 0),
            new EvdevEvent(EvdevTypes.EV_SYN, EvdevCodes.SYN_DROPPED, 0));

        var recovered = frames[1];
        Assert.False(recovered.InRange);
        Assert.False(recovered.IsEraser);
        Assert.False(recovered.IsTouch);
        Assert.False(recovered.BarrelButton1);
        Assert.False(recovered.BarrelButton2);
    }

    [Fact]
    public async Task AccumulatesMultipleAbsEventsPerFrame()
    {
        var frames = await RunFrames(
            new EvdevEvent(EvdevTypes.EV_ABS, EvdevCodes.ABS_X, 5000),
            new EvdevEvent(EvdevTypes.EV_ABS, EvdevCodes.ABS_Y, 3000),
            new EvdevEvent(EvdevTypes.EV_ABS, EvdevCodes.ABS_PRESSURE, 500),
            new EvdevEvent(EvdevTypes.EV_ABS, EvdevCodes.ABS_TILT_X, 100),
            new EvdevEvent(EvdevTypes.EV_ABS, EvdevCodes.ABS_TILT_Y, -200),
            new EvdevEvent(EvdevTypes.EV_ABS, EvdevCodes.ABS_DISTANCE, 10),
            new EvdevEvent(EvdevTypes.EV_SYN, EvdevCodes.SYN_REPORT, 0)
        );

        var f = Assert.Single(frames);
        Assert.Equal(5000, f.X);
        Assert.Equal(3000, f.Y);
        Assert.Equal(500, f.Pressure);
        Assert.Equal(100, f.TiltX);
        Assert.Equal(-200, f.TiltY);
        Assert.Equal(10, f.Distance);
    }

    [Fact]
    public async Task DetectsBarrelButton()
    {
        var frames = await RunFrames(
            new EvdevEvent(EvdevTypes.EV_KEY, EvdevCodes.BTN_TOOL_PEN, 1),
            new EvdevEvent(EvdevTypes.EV_KEY, EvdevCodes.BTN_STYLUS, 1),
            new EvdevEvent(EvdevTypes.EV_SYN, EvdevCodes.SYN_REPORT, 0)
        );

        Assert.True(frames[0].BarrelButton1);
    }
}

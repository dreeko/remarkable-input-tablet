using System.Threading.Channels;
using RemarkableTablet.Core.Evdev;

namespace RemarkableTablet.Core.Tablet;

/// <summary>
///     Accumulates evdev events within a frame (between SYN_REPORT boundaries)
///     and emits a complete PenFrame on each SYN_REPORT.
///     SYN_DROPPED (kernel ring-buffer overflow) causes a pen-up frame to be
///     emitted immediately to avoid stuck button state in the host application.
/// </summary>
public sealed class TabletStateMachine
{
    private bool _isTouch, _isEraser, _isPen, _barrel1, _barrel2;
    private int _x, _y, _pressure, _tiltX, _tiltY, _distance;

    public static async Task RunAsync(
        ChannelReader<EvdevEvent> input,
        ChannelWriter<PenFrame> output,
        CancellationToken ct)
    {
        var sm = new TabletStateMachine();
        try
        {
            await foreach (var ev in input.ReadAllAsync(ct))
                sm.Process(ev, output);
        }
        catch (OperationCanceledException) { }
        finally
        {
            output.TryComplete();
        }
    }

    private void Process(EvdevEvent ev, ChannelWriter<PenFrame> output)
    {
        switch (ev.Type)
        {
            case EvdevTypes.EV_SYN:
                HandleSync(ev.Code, output);
                break;

            case EvdevTypes.EV_ABS:
                HandleAbs(ev.Code, ev.Value);
                break;

            case EvdevTypes.EV_KEY:
                HandleKey(ev.Code, ev.Value);
                break;
        }
    }

    private void HandleSync(ushort code, ChannelWriter<PenFrame> output)
    {
        if (code == EvdevCodes.SYN_REPORT)
            output.TryWrite(Snapshot());
        else if (code == EvdevCodes.SYN_DROPPED)
        {
            // Kernel dropped events — state is unknown; emit pen-up then reset
            _isTouch = false;
            _pressure = 0;
            output.TryWrite(Snapshot());
        }
    }

    private void HandleAbs(ushort code, int value)
    {
        switch (code)
        {
            case EvdevCodes.ABS_X: _x = value; break;
            case EvdevCodes.ABS_Y: _y = value; break;
            case EvdevCodes.ABS_PRESSURE: _pressure = value; break;
            case EvdevCodes.ABS_DISTANCE: _distance = value; break;
            case EvdevCodes.ABS_TILT_X: _tiltX = value; break;
            case EvdevCodes.ABS_TILT_Y: _tiltY = value; break;
        }
    }

    private void HandleKey(ushort code, int value)
    {
        var on = value != 0;
        switch (code)
        {
            case EvdevCodes.BTN_TOOL_PEN: _isPen = on; break;
            case EvdevCodes.BTN_TOOL_RUBBER: _isEraser = on; break;
            case EvdevCodes.BTN_TOUCH: _isTouch = on; break;
            case EvdevCodes.BTN_STYLUS: _barrel1 = on; break;
            case EvdevCodes.BTN_STYLUS2: _barrel2 = on; break;
        }
    }

    private PenFrame Snapshot()
    {
        return new PenFrame(
            _x, _y, _pressure, _tiltX, _tiltY, _distance,
            _isTouch, _isEraser, _barrel1, _barrel2,
            _isPen || _isEraser
        );
    }
}
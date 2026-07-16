using System.Threading.Channels;
using RemarkableTablet.Core.Evdev;

namespace RemarkableTablet.Core.Tablet;

/// <summary>
///     Accumulates MT-B slot-protocol evdev events between SYN_REPORT
///     boundaries and emits a complete TouchFrame on each SYN_REPORT.
///     This device (rM2 capacitive `pt_mt`) does NOT report BTN_TOUCH; contact
///     lifecycle is driven entirely by ABS_MT_TRACKING_ID:
///     value &gt;= 0  → contact starts in the current slot
///     value == -1  → contact in the current slot is released
///     SYN_DROPPED clears all contacts and emits an empty frame.
/// </summary>
public sealed class TouchStateMachine
{
    // Slot indices are sparse (firmware reports up to 32) so a dictionary
    // costs less than a fixed-size array of nullable structs and degrades
    // gracefully if a future firmware expands the slot range.
    private readonly Dictionary<int, MutableContact> _slots = new();
    private readonly HashSet<int> _assignedOutputSlots = new();
    private int _currentSlot;

    public static async Task RunAsync(
        ChannelReader<EvdevEvent> input,
        ChannelWriter<TouchFrame> output,
        CancellationToken ct)
    {
        var sm = new TouchStateMachine();
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

    private void Process(EvdevEvent ev, ChannelWriter<TouchFrame> output)
    {
        switch (ev.Type)
        {
            case EvdevTypes.EV_SYN:
                HandleSync(ev.Code, output);
                break;

            case EvdevTypes.EV_ABS:
                HandleAbs(ev.Code, ev.Value);
                break;
        }
    }

    private void HandleSync(ushort code, ChannelWriter<TouchFrame> output)
    {
        if (code == EvdevCodes.SYN_REPORT)
            output.TryWrite(Snapshot());
        else if (code == EvdevCodes.SYN_DROPPED)
        {
            // Kernel ring overflow: release everything and emit empty so
            // downstream sees a clean "no contacts" state.
            _slots.Clear();
            _assignedOutputSlots.Clear();
            output.TryWrite(TouchFrame.Empty);
        }
    }

    private void HandleAbs(ushort code, int value)
    {
        switch (code)
        {
            case EvdevCodes.ABS_MT_SLOT:
                _currentSlot = value;
                break;

            case EvdevCodes.ABS_MT_TRACKING_ID:
                if (value < 0)
                {
                    if (_slots.Remove(_currentSlot, out var released) && released.OutputSlot >= 0)
                        _assignedOutputSlots.Remove(released.OutputSlot);
                }
                else
                {
                    var contact = EnsureSlot();
                    contact.TrackingId = value;
                    if (contact.OutputSlot < 0)
                        contact.OutputSlot = AllocateOutputSlot();
                }
                break;

            case EvdevCodes.ABS_MT_POSITION_X:
                EnsureSlot().X = value;
                break;

            case EvdevCodes.ABS_MT_POSITION_Y:
                EnsureSlot().Y = value;
                break;

            case EvdevCodes.ABS_MT_PRESSURE:
                EnsureSlot().Pressure = value;
                break;

            case EvdevCodes.ABS_MT_TOUCH_MAJOR:
                EnsureSlot().TouchMajor = value;
                break;

            case EvdevCodes.ABS_MT_TOUCH_MINOR:
                EnsureSlot().TouchMinor = value;
                break;

            case EvdevCodes.ABS_MT_ORIENTATION:
                EnsureSlot().Orientation = value;
                break;

            case EvdevCodes.ABS_MT_TOOL_TYPE:
                EnsureSlot().ToolType = value;
                break;
        }
    }

    private MutableContact EnsureSlot()
    {
        if (!_slots.TryGetValue(_currentSlot, out var c))
        {
            c = new MutableContact { TrackingId = -1, OutputSlot = -1 };
            _slots[_currentSlot] = c;
        }

        return c;
    }

    // Hardware MT-B slot identifiers are sparse (0..31 on rM2), while the
    // synthetic devices expose MaxTracked dense slots (normally 0..4). Keep a
    // stable dense assignment for the lifetime of each contact.
    private int AllocateOutputSlot()
    {
        var slot = 0;
        while (_assignedOutputSlots.Contains(slot)) slot++;
        _assignedOutputSlots.Add(slot);
        return slot;
    }

    private TouchFrame Snapshot()
    {
        if (_slots.Count == 0) return TouchFrame.Empty;

        // Skip slots whose tracking ID hasn't arrived yet (axis updates
        // landed before TRACKING_ID — defensive, shouldn't happen with
        // a well-formed driver).
        var contacts = new List<TouchContact>(_slots.Count);
        foreach (var kv in _slots)
        {
            var c = kv.Value;
            if (c.TrackingId < 0) continue;
            contacts.Add(new TouchContact(
                c.OutputSlot, c.TrackingId, c.X, c.Y, c.Pressure,
                c.TouchMajor, c.TouchMinor, c.Orientation, c.ToolType));
        }

        if (contacts.Count == 0) return TouchFrame.Empty;
        contacts.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));
        return new TouchFrame(contacts);
    }

    private sealed class MutableContact
    {
        public int OutputSlot;
        public int TouchMajor, TouchMinor, Orientation, ToolType;
        public int TrackingId = -1;
        public int X, Y, Pressure;
    }
}

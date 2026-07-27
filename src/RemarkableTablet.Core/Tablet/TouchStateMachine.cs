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
///     <para>
///         Two host-side policies live here (see <see cref="TouchOptions" />):
///         the output-slot pool is bounded by <see cref="TouchOptions.MaxTracked" />
///         so the sinks never have to silently discard contacts, and
///         <see cref="SweepStale" /> releases contacts the firmware abandoned
///         without a tracking-ID reset.
///     </para>
///     Thread-safety: <see cref="Process" /> and <see cref="SweepStale" /> may be
///     driven from different tasks (event loop and timer); both take the same lock.
/// </summary>
public sealed class TouchStateMachine
{
    private readonly HashSet<int> _assignedOutputSlots = new();
    private readonly Func<long> _clock;
    private readonly TouchOptions _opts;

    // Slot indices are sparse (firmware reports up to 32) so a dictionary
    // costs less than a fixed-size array of nullable structs and degrades
    // gracefully if a future firmware expands the slot range.
    private readonly Dictionary<int, MutableContact> _slots = new();
    private readonly object _sync = new();
    private int _currentSlot;

    public TouchStateMachine(TouchOptions? opts = null, Func<long>? clock = null)
    {
        _opts = opts ?? new TouchOptions();
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>Contacts dropped because the output-slot pool was full or the size filter rejected them.</summary>
    public int DroppedContacts { get; private set; }

    /// <summary>Contacts released by <see cref="SweepStale" /> rather than by the firmware.</summary>
    public int StaleReleases { get; private set; }

    public static async Task RunAsync(
        ChannelReader<EvdevEvent> input,
        ChannelWriter<TouchFrame> output,
        CancellationToken ct)
    {
        await RunAsync(input, output, new TouchOptions(), ct);
    }

    public static Task RunAsync(
        ChannelReader<EvdevEvent> input,
        ChannelWriter<TouchFrame> output,
        TouchOptions opts,
        CancellationToken ct)
    {
        return new TouchStateMachine(opts).RunLoopAsync(input, output, ct);
    }

    /// <summary>
    ///     Instance form of <see cref="RunAsync(ChannelReader{EvdevEvent}, ChannelWriter{TouchFrame}, TouchOptions, CancellationToken)" />,
    ///     for callers that want to read <see cref="DroppedContacts" /> /
    ///     <see cref="StaleReleases" /> afterwards.
    /// </summary>
    public async Task RunLoopAsync(
        ChannelReader<EvdevEvent> input,
        ChannelWriter<TouchFrame> output,
        CancellationToken ct)
    {
        // The sweep runs on its own cadence because a stranded contact produces
        // no events at all — there is nothing to react to.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sweeper = SweepLoopAsync(output, stop.Token);

        try
        {
            await foreach (var ev in input.ReadAllAsync(ct))
                Process(ev, output);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await stop.CancelAsync();
            await sweeper;
            output.TryComplete();
        }
    }

    public void Process(EvdevEvent ev, ChannelWriter<TouchFrame> output)
    {
        lock (_sync)
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
    }

    /// <summary>
    ///     Releases contacts idle for longer than <see cref="TouchOptions.StaleContactMs" />
    ///     and emits a frame if anything changed. Exposed for tests; production
    ///     callers get it via <see cref="RunAsync(ChannelReader{EvdevEvent}, ChannelWriter{TouchFrame}, TouchOptions, CancellationToken)" />.
    /// </summary>
    public bool SweepStale(ChannelWriter<TouchFrame> output)
    {
        lock (_sync)
        {
            var cutoff = _clock() - _opts.StaleContactMs;
            List<int>? expired = null;

            foreach (var (slot, contact) in _slots)
            {
                if (contact.LastUpdateMs > cutoff) continue;
                (expired ??= new List<int>()).Add(slot);
            }

            if (expired is null) return false;

            foreach (var slot in expired) ReleaseSlot(slot);
            StaleReleases += expired.Count;
            output.TryWrite(Snapshot());
            return true;
        }
    }

    private async Task SweepLoopAsync(ChannelWriter<TouchFrame> output, CancellationToken ct)
    {
        // Quarter of the threshold: fine enough that a stranded contact clears
        // promptly after the deadline, coarse enough to be free.
        var period = TimeSpan.FromMilliseconds(Math.Max(50, _opts.StaleContactMs / 4));
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                SweepStale(output);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void HandleSync(ushort code, ChannelWriter<TouchFrame> output)
    {
        if (code == EvdevCodes.SYN_REPORT)
        {
            output.TryWrite(Snapshot());
        }
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
                    ReleaseSlot(_currentSlot);
                }
                else
                {
                    EnsureSlot().TrackingId = value;
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

        c.LastUpdateMs = _clock();
        return c;
    }

    private void ReleaseSlot(int slot)
    {
        if (_slots.Remove(slot, out var released) && released.OutputSlot >= 0)
            _assignedOutputSlots.Remove(released.OutputSlot);
    }

    // Hardware MT-B slot identifiers are sparse (0..31 on rM2), while the
    // synthetic devices expose MaxTracked dense slots (normally 0..4). Keep a
    // stable dense assignment for the lifetime of each contact, and refuse to
    // hand out an index the sinks would have to discard.
    private bool TryAssignOutputSlot(MutableContact contact)
    {
        // Size is evaluated on every snapshot, not once at contact start: the
        // panel reports ABS_MT_TRACKING_ID before ABS_MT_TOUCH_MAJOR (see the
        // sample captures), and a palm can also grow past the threshold after
        // landing. An oversize contact gives its slot back so it can't hog the
        // pool.
        //
        // The classification is one-way, following libinput's rule that a touch
        // labelled a palm "will remain so even if the pressure drops below the
        // threshold again". Contact size fluctuates frame to frame — measured
        // palm blobs spanned 17–79 against fingertips at 8–17 — so re-testing
        // both directions would let a palm flicker back into a live contact
        // mid-rest, which is worse than never having filtered it.
        if (_opts.MaxTouchMajor > 0 && contact.TouchMajor > _opts.MaxTouchMajor)
            contact.IsPalm = true;

        if (contact.IsPalm)
        {
            if (contact.OutputSlot >= 0)
            {
                _assignedOutputSlots.Remove(contact.OutputSlot);
                contact.OutputSlot = -1;
            }

            CountDrop(contact);
            return false;
        }

        if (contact.OutputSlot >= 0) return true;

        for (var slot = 0; slot < _opts.MaxTracked; slot++)
        {
            if (_assignedOutputSlots.Contains(slot)) continue;
            _assignedOutputSlots.Add(slot);
            contact.OutputSlot = slot;
            return true;
        }

        CountDrop(contact);
        return false;
    }

    // Count each rejected contact once, not once per frame — the assignment is
    // retried on every snapshot so a contact can claim a slot that frees up.
    private void CountDrop(MutableContact contact)
    {
        if (contact.DropCounted) return;
        contact.DropCounted = true;
        DroppedContacts++;
    }

    private TouchFrame Snapshot()
    {
        if (_slots.Count == 0) return TouchFrame.Empty;

        var contacts = new List<TouchContact>(_slots.Count);
        foreach (var kv in _slots)
        {
            var c = kv.Value;

            // A contact with no tracking ID yet (axis updates landed before
            // TRACKING_ID) or no output slot (pool full, or size-filtered) is
            // not reportable. The assignment is retried here so a contact can
            // claim a slot freed since it landed.
            if (c.TrackingId < 0) continue;
            if (!TryAssignOutputSlot(c)) continue;

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
        public bool DropCounted;

        /// <summary>Latched once the size filter rejects this contact; never cleared before release.</summary>
        public bool IsPalm;

        public long LastUpdateMs;
        public int OutputSlot;
        public int TouchMajor, TouchMinor, Orientation, ToolType;
        public int TrackingId = -1;
        public int X, Y, Pressure;
    }
}

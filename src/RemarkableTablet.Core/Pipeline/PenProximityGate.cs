using RemarkableTablet.Core.Output;

namespace RemarkableTablet.Core.Pipeline;

/// <summary>
///     Host-side palm rejection: while the pen is near the surface, touch is not
///     forwarded to the host.
///     <para>
///         The rM2 firmware already suppresses its touch panel while the pen is in
///         proximity (verified — see <c>tools/EventDiagnostics/samples/README.md</c>),
///         so on that device this gate mostly has nothing to suppress. What it does
///         do, on every device, is handle the two cases firmware suppression cannot:
///     </para>
///     <list type="number">
///         <item>
///             A contact that is already down when the pen arrives. If the panel
///             simply goes quiet without releasing it, the host would hold that
///             contact for the whole stroke. The gate releases everything on entry
///             and then ignores those tracking IDs until they are lifted, so a palm
///             resting through a stroke cannot reappear as a live contact.
///         </item>
///         <item>
///             A device that does not arbitrate in firmware at all. The Paper Pro's
///             behavior here is unverified, and any future device is unknown; the
///             gate means correctness no longer depends on that answer.
///         </item>
///     </list>
///     <para>
///         <b>Why the pen side drives the release.</b> Closing must not depend on a
///         touch frame arriving, because on the rM2 touch frames stop at exactly the
///         moment the gate needs to act. So <see cref="OnPenFrame" /> — fed by the pen
///         loop, which is still running — performs the close: it moves the contacts
///         currently live on the host into the suppression set and raises
///         <see cref="TakePendingRelease" /> for the caller to act on.
///         <see cref="Filter" /> handles the other direction (a device that keeps
///         reporting touch while the pen is down).
///     </para>
///     <para>
///         Pen "near" means <see cref="MappedFrame.InRange" /> — i.e. the digitizer
///         reports the tool, which on the rM2 is roughly a centimetre of hover.
///         The gate stays closed for <see cref="LingerMs" /> after the pen leaves so
///         a hand still settling on the panel doesn't immediately land a contact.
///     </para>
///     Thread-safety: <see cref="OnPenFrame" /> runs on the pen loop and
///     <see cref="Filter" /> on the touch loop, so all state is behind one lock.
/// </summary>
public sealed class PenProximityGate
{
    /// <summary>How long the gate stays closed after the pen goes out of range.</summary>
    public const int LingerMs = 150;

    private readonly Func<long> _clock;

    // Tracking IDs currently live on the host — the ones a close has to disown.
    private readonly HashSet<int> _forwardedIds = new();

    // Tracking IDs seen while the gate was closed, or live when it closed.
    // Monotonic per session, so there is no reuse hazard: an ID leaves this set
    // only once the contact is actually lifted.
    private readonly HashSet<int> _suppressedIds = new();
    private readonly object _sync = new();
    private bool _closed;

    private long _closedUntilMs;
    private bool _pendingRelease;

    public PenProximityGate(Func<long>? clock = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>True while touch is being withheld.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_sync) return _closed;
        }
    }

    /// <summary>Number of times the gate has closed — surfaced by <c>--debug</c>.</summary>
    public int CloseCount { get; private set; }

    /// <summary>
    ///     Feed every mapped pen frame in. Closes the gate as soon as the pen is in
    ///     range, without waiting for a touch frame that may never come.
    /// </summary>
    public void OnPenFrame(MappedFrame frame)
    {
        if (!frame.InRange) return;

        lock (_sync)
        {
            _closedUntilMs = _clock() + LingerMs;
            if (_closed) return;
            Close();
        }
    }

    /// <summary>
    ///     Returns true once per closure, telling the caller to drop whatever the
    ///     touch sink is holding. Called from the pen loop so the release happens
    ///     even when the panel has gone silent.
    /// </summary>
    public bool TakePendingRelease()
    {
        lock (_sync)
        {
            if (!_pendingRelease) return false;
            _pendingRelease = false;
            return true;
        }
    }

    /// <summary>
    ///     Filter a mapped touch frame: returns the frame to forward, or null to
    ///     forward nothing. Releasing what the sink holds is not this method's job —
    ///     that is <see cref="TakePendingRelease" />, so the release has exactly one
    ///     owner and does not depend on a frame arriving.
    /// </summary>
    public MappedTouchFrame? Filter(MappedTouchFrame frame)
    {
        lock (_sync)
        {
            if (_clock() < _closedUntilMs)
            {
                // Everything reported while the pen is down is suspect — this is
                // the path a device that doesn't arbitrate in firmware takes.
                foreach (var c in frame.Contacts) _suppressedIds.Add(c.TrackingId);
                return null;
            }

            _closed = false;

            // Forget suppressed IDs that are gone (contact lifted).
            if (_suppressedIds.Count > 0)
                _suppressedIds.RemoveWhere(id => !Contains(frame, id));

            if (_suppressedIds.Count == 0)
            {
                Remember(frame.Contacts);
                return frame;
            }

            var kept = new List<MappedTouchContact>(frame.Contacts.Count);
            foreach (var c in frame.Contacts)
                if (!_suppressedIds.Contains(c.TrackingId))
                    kept.Add(c);

            Remember(kept);
            return kept.Count == frame.Contacts.Count
                ? frame
                : kept.Count == 0
                    ? MappedTouchFrame.Empty
                    : new MappedTouchFrame(kept);
        }
    }

    private void Close()
    {
        _closed = true;
        CloseCount++;
        _pendingRelease = true;

        // Contacts live on the host are about to be released, so they must not be
        // re-forwarded when the gate reopens — that is the resting palm.
        foreach (var id in _forwardedIds) _suppressedIds.Add(id);
        _forwardedIds.Clear();
    }

    private void Remember(IReadOnlyList<MappedTouchContact> contacts)
    {
        _forwardedIds.Clear();
        foreach (var c in contacts) _forwardedIds.Add(c.TrackingId);
    }

    private static bool Contains(MappedTouchFrame frame, int trackingId)
    {
        foreach (var c in frame.Contacts)
            if (c.TrackingId == trackingId)
                return true;
        return false;
    }
}

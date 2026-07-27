using RemarkableTablet.Core.Output;

namespace RemarkableTablet.Core.Pipeline;

/// <summary>
///     Host-side palm rejection: while the pen is near the surface, touch under
///     the writing hand is not forwarded to the host.
///     <para>
///         The rM2's own arbitration is only half a solution, measured 2026-07-25
///         (<c>tools/EventDiagnostics/samples/README.md</c>): the firmware blocks
///         <i>new</i> contacts while the pen is in proximity, but an already
///         established contact keeps streaming straight through — a fingertip held
///         down for 27 s reported without interruption (max gap 35 ms) across three
///         proximity windows, one of them at <c>ABS_DISTANCE 0</c>.
///     </para>
///     <para>So this gate carries the cases the firmware does not:</para>
///     <list type="number">
///         <item>
///             The common one: a hand already resting on the panel when a stroke
///             begins. Firmware will happily report it for the entire stroke. The
///             gate withholds it and keeps ignoring those tracking IDs until they
///             are lifted, so the resting hand can neither drag during the stroke
///             nor spring back to life on pen-up.
///         </item>
///         <item>
///             A device that does not arbitrate at all. The Paper Pro's behavior is
///             unverified, and any future device is unknown; the gate means
///             correctness no longer depends on that answer.
///         </item>
///     </list>
///     <para>
///         <b>Two shapes of suppression.</b> <see cref="ArbitrationMode.Full" />
///         withholds everything while the pen is in range — safe, and what every
///         other reMarkable driver does. <see cref="ArbitrationMode.Region" />
///         withholds only what falls under the writing hand, after libinput's
///         location-based arbitration, so the off hand can pan or pinch mid-stroke.
///         That is worth having here specifically because the firmware keeps
///         reporting an already-established contact: the off-hand gesture is
///         physically available on this hardware, and only full arbitration throws
///         it away.
///     </para>
///     <para>
///         <b>Why the pen side drives the release.</b> Closing must not depend on a
///         touch frame arriving, because on the rM2 touch frames stop at exactly the
///         moment the gate needs to act. So <see cref="OnPenFrame" /> — fed by the
///         pen loop, which is still running — performs the close.
///     </para>
///     <para>
///         Pen "near" means <see cref="MappedFrame.InRange" /> — the digitizer
///         reports the tool, roughly a centimetre of hover on the rM2. The gate
///         stays closed for <see cref="LingerMs" /> after the pen leaves so a hand
///         still settling on the panel doesn't land.
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
    private readonly ArbitrationOptions _opts;

    // Tracking IDs the gate has withheld. Membership is one-way for the life of a
    // contact: a hand that was under the pen must not become live again just
    // because it drifted out of the region or the pen lifted.
    private readonly HashSet<int> _suppressedIds = new();
    private readonly object _sync = new();
    private readonly double _xPixelsPerMm;
    private readonly double _yPixelsPerMm;
    private bool _closed;

    private long _closedUntilMs;

    // Sign votes from pen tilt: positive leans right, and a right-hander's pen
    // leans toward their own shoulder.
    private int _handednessVotes;
    private int _penX, _penY;
    private bool _pendingRelease;

    /// <param name="opts">Arbitration shape and region geometry.</param>
    /// <param name="xPixelsPerMm">
    ///     Screen pixels per millimetre of tablet surface, from
    ///     <see cref="Mapping.ScreenTransform.XResolution" />. Lets the region be
    ///     specified in millimetres of hand rather than pixels, so it means the
    ///     same thing on any display.
    /// </param>
    /// <param name="yPixelsPerMm">As above, for the vertical screen axis.</param>
    /// <param name="clock">Monotonic milliseconds; injectable for tests.</param>
    public PenProximityGate(
        ArbitrationOptions? opts = null,
        double xPixelsPerMm = 1,
        double yPixelsPerMm = 1,
        Func<long>? clock = null)
    {
        _opts = opts ?? new ArbitrationOptions();
        _xPixelsPerMm = xPixelsPerMm > 0 ? xPixelsPerMm : 1;
        _yPixelsPerMm = yPixelsPerMm > 0 ? yPixelsPerMm : 1;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>True while touch is being withheld (wholly or partly).</summary>
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
    ///     Handedness in force: the configured value, or what tilt has voted for so
    ///     far. <see cref="Handedness.Auto" /> means not yet confident, in which
    ///     case the suppressed region is symmetric.
    /// </summary>
    public Handedness ResolvedHand
    {
        get
        {
            lock (_sync) return CurrentHand();
        }
    }

    /// <summary>
    ///     Feed every mapped pen frame in. Closes the gate as soon as the pen is in
    ///     range, without waiting for a touch frame that may never come, and keeps
    ///     the tip position that <see cref="ArbitrationMode.Region" /> needs.
    /// </summary>
    public void OnPenFrame(MappedFrame frame)
    {
        if (_opts.Mode == ArbitrationMode.Off || !frame.InRange) return;

        lock (_sync)
        {
            _closedUntilMs = _clock() + LingerMs;
            _penX = frame.ScreenX;
            _penY = frame.ScreenY;

            // A right-hander's pen leans toward their own shoulder, i.e. right,
            // which is +TiltX once mapped into screen space (measured convention,
            // see ReMarkable2Profile). Screen space is orientation-corrected, so
            // this reads the same however the tablet is held.
            //
            // Weaker than the position signal in VoteOnHandedness — 70% of writing
            // samples leaned right against 84% of contacts sitting right — and
            // weaker still if the pen is held upright. Its job is to carry the
            // decision before any hand has landed; once contacts exist they
            // outvote it, since every contact in the band votes every frame.
            if (_opts.Hand == Handedness.Auto && frame.TiltX != 0) Bump(Math.Sign(frame.TiltX));

            if (_closed) return;

            _closed = true;
            CloseCount++;

            // Region mode disowns contacts lazily, per position, in Filter; only
            // full arbitration drops everything the sink is holding.
            if (_opts.Mode != ArbitrationMode.Full) return;

            _pendingRelease = true;
            foreach (var id in _forwardedIds) _suppressedIds.Add(id);
            _forwardedIds.Clear();
        }
    }

    /// <summary>
    ///     Returns true once per closure, telling the caller to drop whatever the
    ///     touch sink is holding. Called from the pen loop so the release happens
    ///     even when the panel has gone silent. Never fires in
    ///     <see cref="ArbitrationMode.Region" />, where contacts are dropped
    ///     individually by omission instead.
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
    ///     forward nothing at all. Releasing what the sink already holds is not this
    ///     method's job — that is <see cref="TakePendingRelease" />, so the release
    ///     has exactly one owner and does not depend on a frame arriving.
    /// </summary>
    public MappedTouchFrame? Filter(MappedTouchFrame frame)
    {
        lock (_sync)
        {
            if (_opts.Mode == ArbitrationMode.Off) return frame;

            var penNear = _clock() < _closedUntilMs;

            if (penNear && _opts.Mode == ArbitrationMode.Full)
            {
                // Everything reported while the pen is down is suspect — this is
                // the path a device that doesn't arbitrate in firmware takes.
                foreach (var c in frame.Contacts) _suppressedIds.Add(c.TrackingId);
                return null;
            }

            if (!penNear) _closed = false;

            // Region mode: anything under the writing hand joins the suppressed
            // set, and membership never lapses while the contact lives.
            if (penNear)
                foreach (var c in frame.Contacts)
                {
                    VoteOnHandedness(c.ScreenX, c.ScreenY);
                    if (IsUnderWritingHand(c.ScreenX, c.ScreenY))
                        _suppressedIds.Add(c.TrackingId);
                }

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

    /// <summary>
    ///     A contact near the tip votes for the side it sits on. This is the
    ///     stronger of the two handedness signals — 84% of hand contacts fell to
    ///     the right of the tip for a right-hander in the calibration capture,
    ///     against 70% for tilt direction — because it observes the hand itself
    ///     rather than inferring it from how the pen is held.
    ///     <para>
    ///         Only contacts plausibly belonging to the writing hand vote: within
    ///         the vertical band and a generous horizontal reach. A finger halfway
    ///         across the panel says nothing about which hand holds the pen.
    ///     </para>
    /// </summary>
    private void VoteOnHandedness(int x, int y)
    {
        if (_opts.Hand != Handedness.Auto) return;

        var dy = y - _penY;
        if (dy < -_opts.AheadMm * _yPixelsPerMm || dy > _opts.BehindMm * _yPixelsPerMm) return;

        var dx = x - _penX;
        var reach = Math.Max(_opts.InboardMm, _opts.OutboardMm) * 1.5 * _xPixelsPerMm;
        if (Math.Abs(dx) > reach || dx == 0) return;

        Bump(Math.Sign(dx));
    }

    private void Bump(int vote)
    {
        _handednessVotes = Math.Clamp(
            _handednessVotes + vote, -_opts.HandednessVotes * 2, _opts.HandednessVotes * 2);
    }

    /// <summary>
    ///     Is this screen point inside the rectangle where the writing hand sits?
    ///     Directions are screen-space, which is already orientation-corrected, so
    ///     "behind" is toward the user however the tablet is held.
    /// </summary>
    private bool IsUnderWritingHand(int x, int y)
    {
        var hand = CurrentHand();

        var behind = _opts.BehindMm * _yPixelsPerMm;
        var ahead = _opts.AheadMm * _yPixelsPerMm;
        var inboard = _opts.InboardMm * _xPixelsPerMm;
        var outboard = _opts.OutboardMm * _xPixelsPerMm;

        // Until tilt has voted, cover both sides: a symmetric region is closer to
        // full arbitration, which is the safe direction to be wrong in.
        var (left, right) = hand switch
        {
            Handedness.Right => (outboard, inboard),
            Handedness.Left => (inboard, outboard),
            _ => (inboard, inboard)
        };

        return x >= _penX - left && x <= _penX + right &&
               y >= _penY - ahead && y <= _penY + behind;
    }

    private Handedness CurrentHand()
    {
        if (_opts.Hand != Handedness.Auto) return _opts.Hand;
        if (_handednessVotes >= _opts.HandednessVotes) return Handedness.Right;
        if (_handednessVotes <= -_opts.HandednessVotes) return Handedness.Left;
        return Handedness.Auto;
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

using RemarkableTablet.Core.Output;

namespace RemarkableTablet.Core.Gestures;

/// <summary>
///     Two-finger gesture recognizer. Stateful: feed each MappedTouchFrame
///     in the order it arrives and consume the returned event list.
///     Locks onto two contacts when the gesture begins (identified by tracking
///     ID) and ignores any third+ contact until the screen returns to fewer than
///     two contacts. The gesture ends when either of the locked contacts is
///     released.
///     <para>
///         Not wired into <see cref="Pipeline.TabletPipeline" />: both platforms
///         inject real contacts and let the host application recognise gestures,
///         which is strictly better where it works. This exists for the planned
///         <c>--gestures synth</c> mode, for apps that consume scroll/zoom events
///         but not touch.
///     </para>
///     <para>
///         Anchor choice is deliberately size-based, not arrival-ordered: the two
///         *smallest* contacts are the two most finger-like. Picking by slot index
///         would let a resting palm anchor the gesture and leave the user's actual
///         fingers as the ignored "third" contact.
///     </para>
/// </summary>
public sealed class GestureEngine
{
    private bool _active;
    private int _anchorIdA, _anchorIdB;
    private double _prevAngleDeg;
    private int _prevCenterX, _prevCenterY;

    // Previous-frame state (only valid while _active).
    private double _prevDistance;

    /// <summary>
    ///     Process the next mapped touch frame. Returns zero or more
    ///     gesture events to emit downstream. The list is allocated per
    ///     call — callers can dispatch and drop. (Common case: 0 events
    ///     when fewer than two contacts, or 1–3 events during a gesture.)
    /// </summary>
    public IReadOnlyList<GestureEvent> Process(MappedTouchFrame frame)
    {
        var contacts = frame.Contacts;

        if (!_active)
        {
            if (contacts.Count < 2) return Array.Empty<GestureEvent>();
            var (a0, b0) = PickAnchors(contacts);
            return Begin(a0, b0);
        }

        // Active — find the anchor contacts in this frame.
        MappedTouchContact? a = null, b = null;
        foreach (var c in contacts)
            if (c.TrackingId == _anchorIdA) a = c;
            else if (c.TrackingId == _anchorIdB) b = c;
        if (a is null || b is null)
            return End();

        return Update(a.Value, b.Value);
    }

    /// <summary>
    ///     The two most finger-like contacts: smallest major axis first, ties
    ///     broken by tracking ID so the choice is stable across frames. Contacts
    ///     with no size data (TouchMajor 0, e.g. a device that doesn't report it)
    ///     all tie, which degrades to arrival order — the old behavior, but only
    ///     when there is nothing better to go on.
    /// </summary>
    private static (MappedTouchContact A, MappedTouchContact B) PickAnchors(
        IReadOnlyList<MappedTouchContact> contacts)
    {
        var a = contacts[0];
        var b = contacts[1];
        if (Larger(a, b)) (a, b) = (b, a);

        for (var i = 2; i < contacts.Count; i++)
        {
            var c = contacts[i];
            if (Larger(b, c)) b = c;
            if (Larger(a, b)) (a, b) = (b, a);
        }

        // Report in tracking-ID order so pan/rotate signs don't depend on which
        // contact happened to be smaller this frame.
        return a.TrackingId <= b.TrackingId ? (a, b) : (b, a);
    }

    private static bool Larger(MappedTouchContact x, MappedTouchContact y)
    {
        return x.TouchMajor != y.TouchMajor
            ? x.TouchMajor > y.TouchMajor
            : x.TrackingId > y.TrackingId;
    }

    private List<GestureEvent> Begin(MappedTouchContact a, MappedTouchContact b)
    {
        _active = true;
        _anchorIdA = a.TrackingId;
        _anchorIdB = b.TrackingId;
        _prevDistance = Distance(a, b);
        _prevAngleDeg = AngleDeg(a, b);
        _prevCenterX = (a.ScreenX + b.ScreenX) / 2;
        _prevCenterY = (a.ScreenY + b.ScreenY) / 2;

        return new List<GestureEvent> { new GestureBegin(_prevCenterX, _prevCenterY) };
    }

    private List<GestureEvent> Update(MappedTouchContact a, MappedTouchContact b)
    {
        var distance = Distance(a, b);
        var angle = AngleDeg(a, b);
        var cx = (a.ScreenX + b.ScreenX) / 2;
        var cy = (a.ScreenY + b.ScreenY) / 2;

        var dx = cx - _prevCenterX;
        var dy = cy - _prevCenterY;

        // Guard against zero-distance frame (both fingers at same point).
        var scaleDelta = _prevDistance > 0.5 && distance > 0.5
            ? distance / _prevDistance
            : 1.0;

        var degDelta = NormalizeDegrees(angle - _prevAngleDeg);

        _prevDistance = distance;
        _prevAngleDeg = angle;
        _prevCenterX = cx;
        _prevCenterY = cy;

        var events = new List<GestureEvent>(3);
        if (dx != 0 || dy != 0) events.Add(new GesturePan(dx, dy));
        if (scaleDelta != 1.0) events.Add(new GesturePinch(scaleDelta));
        if (degDelta != 0.0) events.Add(new GestureRotate(degDelta));
        return events;
    }

    private List<GestureEvent> End()
    {
        _active = false;
        return new List<GestureEvent> { new GestureEnd() };
    }

    private static double Distance(MappedTouchContact a, MappedTouchContact b)
    {
        double dx = a.ScreenX - b.ScreenX;
        double dy = a.ScreenY - b.ScreenY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double AngleDeg(MappedTouchContact a, MappedTouchContact b)
    {
        double dx = b.ScreenX - a.ScreenX;
        double dy = b.ScreenY - a.ScreenY;
        return Math.Atan2(dy, dx) * (180.0 / Math.PI);
    }

    private static double NormalizeDegrees(double d)
    {
        while (d > 180.0) d -= 360.0;
        while (d <= -180.0) d += 360.0;
        return d;
    }
}
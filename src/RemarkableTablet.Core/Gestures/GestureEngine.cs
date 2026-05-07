using RemarkableTablet.Core.Output;

namespace RemarkableTablet.Core.Gestures;

/// <summary>
///     Two-finger gesture recognizer. Stateful: feed each MappedTouchFrame
///     in the order it arrives and consume the returned event list.
///     Locks onto the first two contacts present when the gesture begins
///     (identified by tracking ID) and ignores any third+ contact until
///     the screen returns to fewer than two contacts. The gesture ends
///     when either of the locked contacts is released.
/// </summary>
public sealed class GestureEngine
{
    private bool _active;
    private int _anchorIdA, _anchorIdB;

    // Previous-frame state (only valid while _active).
    private double _prevDistance;
    private double _prevAngleDeg;
    private int _prevCenterX, _prevCenterY;

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
            if (contacts.Count >= 2)
                return Begin(contacts[0], contacts[1]);
            return Array.Empty<GestureEvent>();
        }

        // Active — find the anchor contacts in this frame.
        MappedTouchContact? a = null, b = null;
        foreach (var c in contacts)
        {
            if (c.TrackingId == _anchorIdA) a = c;
            else if (c.TrackingId == _anchorIdB) b = c;
        }
        if (a is null || b is null)
            return End();

        return Update(a.Value, b.Value);
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
        if (degDelta != 0.0)   events.Add(new GestureRotate(degDelta));
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
        while (d > 180.0)  d -= 360.0;
        while (d <= -180.0) d += 360.0;
        return d;
    }
}

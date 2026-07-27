using RemarkableTablet.Core.Devices;

namespace RemarkableTablet.Core.Tablet;

/// <summary>
///     Host-side policy for the touch pipeline. Device *capabilities* live in
///     <see cref="DeviceProfile" />; the knobs here are choices about what we do
///     with them.
/// </summary>
public sealed record TouchOptions
{
    /// <summary>
    ///     Maximum concurrent contacts forwarded to the host. The panel reports
    ///     up to 32 slots; the synthetic devices declare this many, so the state
    ///     machine must not emit more (both sinks would silently drop the excess).
    /// </summary>
    public int MaxTracked { get; init; } = 5;

    /// <summary>
    ///     Release a contact that has not been updated for this long. Safety net for
    ///     a contact abandoned without an <c>ABS_MT_TRACKING_ID = -1</c>, which would
    ///     otherwise be held by the host forever.
    ///     <para>
    ///         No such abandonment has been observed on the rM2 — every contact across
    ///         four capture sessions was released cleanly, including through pen
    ///         proximity — so this is precaution, not a fix for a known behavior. It
    ///         stays because the cost is one timer and the failure it prevents (a
    ///         permanently stuck touch-down, plus an output slot never returned to the
    ///         pool) is severe and silent.
    ///     </para>
    ///     <para>
    ///         Do not shorten this without evidence: in
    ///         <c>tools/EventDiagnostics/samples/touch-pen.log</c> a genuinely
    ///         held, motionless contact went 1085 ms between reports, because the
    ///         panel only reports on change. The pen gate
    ///         (<see cref="Pipeline.PenProximityGate" />) is the fast path; this
    ///         is the backstop for everything else.
    ///     </para>
    /// </summary>
    public int StaleContactMs { get; init; } = 3000;

    /// <summary>
    ///     Drop contacts whose major axis exceeds this, in the panel's own
    ///     (unknown-unit) scale — a palm-size filter. 0 disables it. The rM2 cannot
    ///     report <c>MT_TOOL_PALM</c> (its <c>ABS_MT_TOOL_TYPE</c> range is 0–1 and
    ///     the kernel's palm value is 2), so contact size is the only signal
    ///     available.
    ///     <para>
    ///         <b>35, measured 2026-07-27.</b> The panel quantises this axis to
    ///         about ten levels (8, 17, 26, 35, 44, 52, …). Deliberate touches and
    ///         a resting hand separate cleanly:
    ///     </para>
    ///     <code>
    ///     index fingertip, thumb, flat finger pad, pinch, taps   median 17, max 26  (44 contacts)
    ///     writing hand resting while writing                     median 52, max 88  (60 contacts)
    ///     </code>
    ///     <para>
    ///         At 35, no deliberate contact in that corpus is dropped and 92% of
    ///         hand contacts are caught; the rest read fingertip-sized and are left
    ///         to <see cref="Pipeline.PenProximityGate" />, which covers them
    ///         whenever the pen is near. A lower threshold is tempting — 21 catches
    ///         98% — but classification is sticky, so a single frame over the line
    ///         kills a contact for good, and at 21 that killed 27% of real
    ///         fingertip contacts. The gap between "most samples" and "any sample"
    ///         is what sets this value.
    ///     </para>
    ///     <para>
    ///         Caveat: one person's hands. If deliberate touches get ignored, raise
    ///         it or pass <c>--palm-size off</c>.
    ///     </para>
    /// </summary>
    public int MaxTouchMajor { get; init; } = 35;

    public static TouchOptions ForProfile(DeviceProfile profile)
    {
        return new TouchOptions { MaxTracked = profile.Touch.MaxTracked };
    }
}

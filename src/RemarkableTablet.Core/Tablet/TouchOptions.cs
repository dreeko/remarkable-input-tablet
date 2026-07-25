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
    ///     (unknown-unit) scale — a palm-size filter. 0 disables it, which is the
    ///     default: the rM2 cannot report <c>MT_TOOL_PALM</c> (its
    ///     <c>ABS_MT_TOOL_TYPE</c> range is 0–1 and the kernel's palm value is 2),
    ///     so size is the only available signal, and a threshold picked without a
    ///     real palm capture would drop legitimate fingers. Capture a palm rest
    ///     with <c>tools/EventDiagnostics</c>, compare its
    ///     <c>ABS_MT_TOUCH_MAJOR</c> against a fingertip's, then set this.
    /// </summary>
    public int MaxTouchMajor { get; init; }

    public static TouchOptions ForProfile(DeviceProfile profile)
    {
        return new TouchOptions { MaxTracked = profile.Touch.MaxTracked };
    }
}

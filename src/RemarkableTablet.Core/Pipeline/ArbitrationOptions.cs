namespace RemarkableTablet.Core.Pipeline;

/// <summary>How touch is suppressed while the pen is near the surface.</summary>
public enum ArbitrationMode
{
    /// <summary>
    ///     All touch is withheld while the pen is in range. Safe, and what every
    ///     other reMarkable driver does. Default.
    /// </summary>
    Full,

    /// <summary>
    ///     Only contacts under the writing hand are withheld; the rest are
    ///     forwarded, so the off hand can keep panning or pinching mid-stroke.
    ///     Possible on the rM2 because the firmware only blocks contacts that
    ///     *start* during pen proximity — one already established keeps
    ///     reporting. Modelled on libinput's location-based arbitration.
    /// </summary>
    Region,

    /// <summary>No suppression at all. For debugging, or a device that arbitrates well in firmware.</summary>
    Off
}

/// <summary>Which hand holds the pen; decides which side of the tip the palm sits on.</summary>
public enum Handedness
{
    /// <summary>Infer from pen tilt, falling back to a symmetric region until confident.</summary>
    Auto,
    Left,
    Right
}

/// <summary>
///     Geometry of the region suppressed under the writing hand, in millimetres of
///     tablet surface, measured from the pen tip in screen-space directions
///     (the mapping is orientation-corrected, so "behind" is toward the user
///     whichever way the tablet is held).
///     <para>
///         The defaults are a first approximation of where a hand sits relative to
///         the tip — roughly a palm's reach behind and inboard of the nib, with a
///         small margin ahead and outboard. libinput's equivalent is admittedly
///         heuristic too ("I'm not sure we got all of them right" — its author).
///         Refine these against a two-handed capture corpus before making
///         <see cref="ArbitrationMode.Region" /> the default.
///     </para>
/// </summary>
public sealed record ArbitrationOptions
{
    public ArbitrationMode Mode { get; init; } = ArbitrationMode.Full;

    public Handedness Hand { get; init; } = Handedness.Auto;

    /// <summary>Toward the user from the tip — where the heel of the hand rests.</summary>
    public double BehindMm { get; init; } = 150;

    /// <summary>Away from the user from the tip; small, since the hand is rarely ahead of the nib.</summary>
    public double AheadMm { get; init; } = 30;

    /// <summary>Toward the writing hand's side (right of the tip for a right-hander).</summary>
    public double InboardMm { get; init; } = 120;

    /// <summary>Away from the writing hand's side.</summary>
    public double OutboardMm { get; init; } = 30;

    /// <summary>
    ///     Tilt-sign votes needed before Auto commits to a handedness. Each frame
    ///     with the pen in range votes; the count is clamped so a long stroke can't
    ///     make the decision unshakeable. At ~100 Hz this is a fraction of a second
    ///     of consistent lean.
    /// </summary>
    public int HandednessVotes { get; init; } = 25;
}

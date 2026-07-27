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
///         <b>Measured, 2026-07-27</b>, from a minute of continuous writing with the
///         hand resting on the panel throughout — 4904 samples where a hand contact
///         and a pen position coincide in time, capture committed as
///         <c>handrest-*-2026-07-27.bin</c>. Offsets from the tip, in mm:
///     </para>
///     <code>
///     dx (+ = right)   p1 -36   median +54   p99 +103   max +106
///     dy (+ = toward)  p1 -33   median +91   p99 +160   max +168
///     </code>
///     <para>
///         The defaults below cover the p99 of that distribution with a small
///         margin. They are one right-handed person, one grip, one session — the
///         numbers to widen if someone reports their palm getting through, and the
///         reason <see cref="ArbitrationMode.Region" /> is not the default. Note
///         two of these were guessed badly before being measured: the hand reaches
///         further behind, and further to the *outboard* side, than seemed likely.
///     </para>
/// </summary>
public sealed record ArbitrationOptions
{
    public ArbitrationMode Mode { get; init; } = ArbitrationMode.Full;

    public Handedness Hand { get; init; } = Handedness.Auto;

    /// <summary>Toward the user from the tip — where the heel of the hand rests. Measured p99 +160.</summary>
    public double BehindMm { get; init; } = 165;

    /// <summary>Away from the user; the knuckles still reach a little past the nib. Measured p1 −33.</summary>
    public double AheadMm { get; init; } = 40;

    /// <summary>Toward the writing hand's side, right of the tip for a right-hander. Measured p99 +103.</summary>
    public double InboardMm { get; init; } = 110;

    /// <summary>Away from the writing hand's side. Measured p1 −36, max −43.</summary>
    public double OutboardMm { get; init; } = 45;

    /// <summary>
    ///     Net votes needed before Auto commits to a handedness. Contacts vote by
    ///     which side of the tip they sit (84% fell to the right for a right-hander
    ///     in the calibration capture) and each pen frame votes by tilt direction
    ///     (70% leaned right, median +1800 raw). Position is the stronger signal
    ///     and dominates, since every contact in the band votes each frame; tilt
    ///     carries the decision before any hand has landed. The count is clamped so
    ///     a long stroke can't make the decision unshakeable.
    /// </summary>
    public int HandednessVotes { get; init; } = 25;
}

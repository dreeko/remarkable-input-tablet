namespace RemarkableTablet.Core.Mapping;

/// <summary>
///     Parametric cubic curve used to shape pressure response. Treats the input
///     pressure t∈[0,1] as the curve parameter and evaluates the y-component of a
///     cubic Bézier whose endpoints are fixed at (0,0) and (1,1).
///     <para>
///         Note: this is not a geometric Bézier of the form y(x). The two interior
///         control points contribute only their y-values; we deliberately omit the
///         x-component because solving x(t) = input each frame is overkill for a
///         shaping curve. If you need true Bézier semantics, root-find x(t) first.
///     </para>
///     Input and output are both in [0,1].
/// </summary>
public sealed class PressureCurve
{
    private readonly double _y1, _y2;

    /// <param name="y1">y-value of the first interior control point (at t=1/3).</param>
    /// <param name="y2">y-value of the second interior control point (at t=2/3).</param>
    public PressureCurve(double y1, double y2)
    {
        _y1 = y1;
        _y2 = y2;
    }

    /// <summary>Identity curve: output = input.</summary>
    public static PressureCurve Linear => new(1.0 / 3.0, 2.0 / 3.0);

    /// <summary>Boosts low-pressure response (lighter strokes feel firmer).</summary>
    public static PressureCurve Soft => new(0.40, 0.90);

    /// <summary>Suppresses low-pressure response (heavier strokes feel firmer).</summary>
    public static PressureCurve Hard => new(0.10, 0.50);

    /// <summary>
    ///     Resolve a curve by name (case-insensitive). Unknown / null / empty
    ///     values fall back to <see cref="Linear" />.
    ///     Names: <c>linear</c>, <c>soft</c>, <c>hard</c>.
    /// </summary>
    public static PressureCurve FromName(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "soft" => Soft,
        "hard" => Hard,
        _      => Linear
    };

    /// <summary>Maps input pressure t∈[0,1] to output pressure∈[0,1].</summary>
    public double Apply(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        // B(t) = (1-t)³·0 + 3(1-t)²t·_y1 + 3(1-t)t²·_y2 + t³·1
        var u = 1.0 - t;
        return 3.0 * u * u * t * _y1
               + 3.0 * u * t * t * _y2
               + t * t * t;
    }
}

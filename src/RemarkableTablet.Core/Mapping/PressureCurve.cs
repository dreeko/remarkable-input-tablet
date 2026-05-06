namespace RemarkableTablet.Core.Mapping;

/// <summary>
/// Cubic Bézier pressure curve. p0=(0,0) and p3=(1,1) are fixed endpoints.
/// p1 and p2 are the configurable interior control handles.
///
/// Input and output are both in the [0,1] range.
/// The curve maps normalised tablet pressure to normalised output pressure.
/// </summary>
public sealed class PressureCurve
{
    private readonly double _p1x, _p1y, _p2x, _p2y;

    public static PressureCurve Linear => new(0.33, 0.33, 0.67, 0.67);
    public static PressureCurve Soft   => new(0.10, 0.40, 0.50, 0.90);
    public static PressureCurve Hard   => new(0.40, 0.10, 0.90, 0.50);

    public PressureCurve(double p1x, double p1y, double p2x, double p2y)
    {
        _p1x = p1x; _p1y = p1y;
        _p2x = p2x; _p2y = p2y;
    }

    /// <summary>Maps input pressure t∈[0,1] to output pressure∈[0,1].</summary>
    public double Apply(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        // Cubic Bézier y-value at parameter t
        // B(t) = (1-t)³·p0 + 3(1-t)²t·p1 + 3(1-t)t²·p2 + t³·p3
        // p0=(0,0), p3=(1,1), only need y values
        double u = 1.0 - t;
        return 3.0 * u * u * t * _p1y
             + 3.0 * u * t * t * _p2y
             + t * t * t;
    }
}

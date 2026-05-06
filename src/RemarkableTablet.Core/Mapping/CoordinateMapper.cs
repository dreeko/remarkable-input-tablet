using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Tablet;

namespace RemarkableTablet.Core.Mapping;

/// <summary>
/// Maps raw PenFrames (tablet coordinates) to MappedFrames (screen coordinates
/// with normalised pressure/tilt for the Windows Ink API).
/// </summary>
public sealed class CoordinateMapper
{
    private readonly MappingOptions _opts;
    private readonly PressureCurve  _curve;

    public CoordinateMapper(MappingOptions opts, PressureCurve? curve = null)
    {
        _opts  = opts;
        _curve = curve ?? PressureCurve.Linear;
    }

    public MappedFrame Map(PenFrame frame)
    {
        // Normalise raw tablet coords to [0,1]
        double nx = frame.X / (double)ReMarkable2Constants.PenXMax;
        double ny = frame.Y / (double)ReMarkable2Constants.PenYMax;

        // Apply orientation transform
        (double rx, double ry) = _opts.Orientation switch
        {
            Orientation.Portrait         => (ny,       1.0 - nx),
            Orientation.Landscape        => (nx,       ny),
            Orientation.PortraitFlipped  => (1.0 - ny, nx),
            Orientation.LandscapeFlipped => (1.0 - nx, 1.0 - ny),
            _                            => (nx, ny),
        };

        // Apply tablet area crop (user-selected active region)
        rx = _opts.TabletAreaX + rx * _opts.TabletAreaW;
        ry = _opts.TabletAreaY + ry * _opts.TabletAreaH;
        rx = Math.Clamp(rx, 0.0, 1.0);
        ry = Math.Clamp(ry, 0.0, 1.0);

        // Map to screen pixels
        int sx = _opts.MonitorX + (int)(rx * _opts.MonitorW);
        int sy = _opts.MonitorY + (int)(ry * _opts.MonitorH);

        // Pressure: tablet 0–4095 → normalised → Bézier curve → Windows 0–1024
        double normPressure = frame.Pressure / (double)ReMarkable2Constants.PressureMax;
        uint   wPressure    = (uint)(_curve.Apply(normPressure) * ReMarkable2Constants.WindowsPressureMax);

        // Tilt: rM2 units → degrees ±90
        int tiltX = ScaleTilt(frame.TiltX, ReMarkable2Constants.TiltXMin, ReMarkable2Constants.TiltXMax);
        int tiltY = ScaleTilt(frame.TiltY, ReMarkable2Constants.TiltYMin, ReMarkable2Constants.TiltYMax);

        return new MappedFrame(
            ScreenX:      sx,
            ScreenY:      sy,
            Pressure:     wPressure,
            TiltX:        tiltX,
            TiltY:        tiltY,
            IsTouch:      frame.IsTouch,
            IsEraser:     frame.IsEraser,
            BarrelButton: frame.BarrelButton1,
            InRange:      frame.InRange
        );
    }

    private static int ScaleTilt(int raw, int min, int max)
    {
        if (max == min) return 0;
        double norm = (raw - min) / (double)(max - min); // [0,1]
        return (int)(norm * (ReMarkable2Constants.WindowsTiltMax - ReMarkable2Constants.WindowsTiltMin)
                     + ReMarkable2Constants.WindowsTiltMin);
    }
}

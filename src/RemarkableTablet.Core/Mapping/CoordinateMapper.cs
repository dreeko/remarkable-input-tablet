using RemarkableTablet.Core.Devices;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Tablet;

namespace RemarkableTablet.Core.Mapping;

/// <summary>
///     Maps raw PenFrames (tablet coordinates) to MappedFrames (screen coordinates
///     with normalised pressure/tilt for the Windows Ink API).
/// </summary>
public sealed class CoordinateMapper
{
    private readonly PressureCurve _curve;
    private readonly MappingOptions _opts;
    private readonly DeviceProfile _profile;

    public CoordinateMapper(MappingOptions opts, DeviceProfile profile, PressureCurve? curve = null)
    {
        _opts = opts;
        _profile = profile;
        _curve = curve ?? PressureCurve.Linear;
        Transform = new ScreenTransform(opts, profile);
    }

    /// <summary>
    ///     Shared screen-side transform. Exposed so the touch mapper and the
    ///     Linux uinput device can be built from the same fitted geometry.
    /// </summary>
    public ScreenTransform Transform { get; }

    public MappedFrame Map(PenFrame frame)
    {
        // Normalise raw tablet coords to [0,1] against the declared axis range.
        var nx = ScreenTransform.Normalize(frame.X, _profile.Pen.XMin, _profile.Pen.XMax);
        var ny = ScreenTransform.Normalize(frame.Y, _profile.Pen.YMin, _profile.Pen.YMax);

        // Orientation transform. rM2 pen axes, measured 2026-07-25 (see
        // ReMarkable2Profile and samples/hw2-pen.log):
        //   ABS_X is the LONG axis,  0 = bottom (USB edge), max = top.
        //   ABS_Y is the SHORT axis, 0 = left,              max = right.
        // So in the device's own portrait frame, left-to-right u = ny and
        // top-to-bottom v = 1 - nx. Each case below is that (u, v) pair rotated
        // by the orientation — the touch mapper derives its cases the same way
        // from its own axes, which is what keeps pen and touch on the same pixel.
        var (rx, ry) = _opts.Orientation switch
        {
            Orientation.Portrait => (ny, 1.0 - nx),
            Orientation.Landscape => (1.0 - nx, 1.0 - ny),
            Orientation.PortraitFlipped => (1.0 - ny, nx),
            Orientation.LandscapeFlipped => (nx, ny),
            _ => (ny, 1.0 - nx)
        };

        // Active-area crop, aspect fit and screen scaling — shared with touch.
        var (sx, sy) = Transform.ToScreen(rx, ry);

        // Pressure: tablet raw → normalised → curve → Windows 0–1024
        var normPressure = ScreenTransform.Normalize(
            frame.Pressure, _profile.Pen.PressureMin, _profile.Pen.PressureMax);
        var wPressure = (uint)(_curve.Apply(normPressure) * InjectionScale.PressureMax);

        // Tilt: firmware units → degrees ±90, then rotated to match the position transform.
        var tiltX = ScaleTilt(frame.TiltX, _profile.Pen.TiltXMin, _profile.Pen.TiltXMax);
        var tiltY = ScaleTilt(frame.TiltY, _profile.Pen.TiltYMin, _profile.Pen.TiltYMax);
        (tiltX, tiltY) = RotateTilt(tiltX, tiltY, _opts.Orientation);

        return new MappedFrame(
            sx,
            sy,
            wPressure,
            tiltX,
            tiltY,
            frame.Distance,
            frame.IsTouch,
            frame.IsEraser,
            frame.BarrelButton1,
            frame.InRange
        );
    }

    private static int ScaleTilt(int raw, int min, int max)
    {
        if (max == min) return 0;
        var norm = (raw - min) / (double)(max - min); // [0,1]
        return (int)(norm * (InjectionScale.TiltMax - InjectionScale.TiltMin)
                     + InjectionScale.TiltMin);
    }

    /// <summary>
    ///     Rotates the tilt vector in lockstep with the position transform applied above.
    ///     Tilt-X in the tablet's frame is no longer tilt-X in the screen's frame once
    ///     the device is rotated; brushes that key off tilt direction would otherwise
    ///     be wrong in non-Portrait orientations.
    ///     <para>
    ///         Derived from the measured axis directions, not chosen: +ABS_TILT_Y
    ///         leans along +ABS_Y, which points right (screen +X in portrait), and
    ///         +ABS_TILT_X leans along +ABS_X, which points up (screen −Y). Each
    ///         case is that pair rotated with the position transform. Sign
    ///         convention follows Windows Ink (positive = pen leans toward +X / +Y).
    ///     </para>
    /// </summary>
    private static (int X, int Y) RotateTilt(int tx, int ty, Orientation o)
    {
        return o switch
        {
            Orientation.Portrait => (ty, -tx),
            Orientation.Landscape => (-tx, -ty),
            Orientation.PortraitFlipped => (-ty, tx),
            Orientation.LandscapeFlipped => (tx, ty),
            _ => (ty, -tx)
        };
    }
}

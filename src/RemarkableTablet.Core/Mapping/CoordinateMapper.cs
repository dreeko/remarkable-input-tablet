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
    }

    public MappedFrame Map(PenFrame frame)
    {
        // Normalise raw tablet coords to [0,1]
        var nx = frame.X / (double)_profile.Pen.XMax;
        var ny = frame.Y / (double)_profile.Pen.YMax;

        // Apply orientation transform.
        // rM2 pen axis layout (empirically aligned with touch panel 2026-05-07):
        //   ABS_X is the LONG axis,  0 = top of device in portrait.
        //   ABS_Y is the SHORT axis, 0 = right of device in portrait.
        // These are 180° rotated from earlier documented conventions; the
        // formulas below match the touch mapper's behavior so pen and touch
        // agree on screen direction in every orientation.
        var (rx, ry) = _opts.Orientation switch
        {
            Orientation.Portrait         => (1.0 - ny, nx),
            Orientation.Landscape        => (nx,        ny),
            Orientation.PortraitFlipped  => (ny,        1.0 - nx),
            Orientation.LandscapeFlipped => (1.0 - nx,  1.0 - ny),
            _                            => (1.0 - ny, nx)
        };

        // Apply tablet area crop (user-selected active region)
        rx = _opts.TabletAreaX + rx * _opts.TabletAreaW;
        ry = _opts.TabletAreaY + ry * _opts.TabletAreaH;
        rx = Math.Clamp(rx, 0.0, 1.0);
        ry = Math.Clamp(ry, 0.0, 1.0);

        // Map to screen pixels
        var sx = _opts.MonitorX + (int)(rx * _opts.MonitorW);
        var sy = _opts.MonitorY + (int)(ry * _opts.MonitorH);

        // Pressure: tablet raw → normalised → curve → Windows 0–1024
        var normPressure = frame.Pressure / (double)_profile.Pen.PressureMax;
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
    ///     Sign convention follows Windows Ink (positive = pen leans toward +X / +Y axis).
    ///     Convention may need empirical adjustment — see README "Hardware details".
    /// </summary>
    private static (int X, int Y) RotateTilt(int tx, int ty, Orientation o) => o switch
    {
        Orientation.Portrait         => (-ty,  tx),
        Orientation.Landscape        => ( tx,  ty),
        Orientation.PortraitFlipped  => ( ty, -tx),
        Orientation.LandscapeFlipped => (-tx, -ty),
        _                            => (-ty,  tx)
    };
}

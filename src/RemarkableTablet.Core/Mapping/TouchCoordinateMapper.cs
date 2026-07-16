using RemarkableTablet.Core.Devices;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Tablet;

namespace RemarkableTablet.Core.Mapping;

/// <summary>
///     Maps raw TouchFrames (touchscreen coordinates) to MappedTouchFrames
///     (screen pixels). Mirrors <see cref="CoordinateMapper" /> but for the
///     touch coordinate range, with no tilt or pressure-curve handling.
///     Touch coordinate axes on the rM2 (verified pt_mt driver):
///     ABS_MT_POSITION_X is the SHORT axis (0..1403).
///     ABS_MT_POSITION_Y is the LONG axis  (0..1871).
///     This is *opposite* to the pen, where ABS_X is the long axis. The
///     orientation rotation cases below account for that — they are not
///     copy-paste of the pen mapper.
/// </summary>
public sealed class TouchCoordinateMapper
{
    private readonly MappingOptions _opts;
    private readonly DeviceProfile _profile;

    public TouchCoordinateMapper(MappingOptions opts, DeviceProfile profile)
    {
        _opts = opts;
        _profile = profile;
    }

    public MappedTouchFrame Map(TouchFrame frame)
    {
        if (frame.Contacts.Count == 0) return MappedTouchFrame.Empty;

        var mapped = new MappedTouchContact[frame.Contacts.Count];
        for (var i = 0; i < frame.Contacts.Count; i++)
            mapped[i] = MapContact(frame.Contacts[i]);
        return new MappedTouchFrame(mapped);
    }

    private MappedTouchContact MapContact(TouchContact c)
    {
        // Normalise raw touch coords to [0,1] in the panel's own frame.
        var nx = c.X / (double)_profile.Touch.XMax;
        var ny = c.Y / (double)_profile.Touch.YMax;

        // Touch panel native frame: portrait — short axis = X, long axis = Y,
        // pen slot at the bottom. So unlike the pen, no axis swap is needed
        // for portrait.
        var (rx, ry) = _opts.Orientation switch
        {
            Orientation.Portrait => (nx, ny),
            Orientation.Landscape => (ny, 1.0 - nx),
            Orientation.PortraitFlipped => (1.0 - nx, 1.0 - ny),
            Orientation.LandscapeFlipped => (1.0 - ny, nx),
            _ => (nx, ny)
        };

        // Apply tablet area crop, identical to pen mapper.
        rx = _opts.TabletAreaX + rx * _opts.TabletAreaW;
        ry = _opts.TabletAreaY + ry * _opts.TabletAreaH;
        rx = Math.Clamp(rx, 0.0, 1.0);
        ry = Math.Clamp(ry, 0.0, 1.0);

        var sx = _opts.MonitorX + (int)(rx * Math.Max(0, _opts.MonitorW - 1));
        var sy = _opts.MonitorY + (int)(ry * Math.Max(0, _opts.MonitorH - 1));

        // Pressure: device raw → 0..1024 (Windows Ink scale).
        var pressureNorm = c.Pressure / (double)_profile.Touch.PressureMax;
        var pressure = (uint)(pressureNorm * InjectionScale.PressureMax);

        return new MappedTouchContact(c.Slot, c.TrackingId, sx, sy, pressure);
    }
}

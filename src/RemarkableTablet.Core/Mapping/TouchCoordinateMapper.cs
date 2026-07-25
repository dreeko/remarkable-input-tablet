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
///     copy-paste of the pen mapper. Everything after the rotation is shared
///     with the pen via <see cref="ScreenTransform" />.
/// </summary>
public sealed class TouchCoordinateMapper
{
    private readonly MappingOptions _opts;
    private readonly DeviceProfile _profile;
    private readonly ScreenTransform _transform;

    /// <param name="transform">
    ///     Pass the pen mapper's <see cref="CoordinateMapper.Transform" /> so pen
    ///     and touch share one fitted geometry; omit to build an equivalent one.
    /// </param>
    public TouchCoordinateMapper(MappingOptions opts, DeviceProfile profile, ScreenTransform? transform = null)
    {
        _opts = opts;
        _profile = profile;
        _transform = transform ?? new ScreenTransform(opts, profile);
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
        var nx = ScreenTransform.Normalize(c.X, _profile.Touch.XMin, _profile.Touch.XMax);
        var ny = ScreenTransform.Normalize(c.Y, _profile.Touch.YMin, _profile.Touch.YMax);

        // rM2 touch axes, measured 2026-07-25 (see ReMarkable2Profile and
        // samples/hw2-touch.log): X is the short axis with 0 = left, Y is the long
        // axis with 0 = BOTTOM. INPUT_PROP_DIRECT does not mean the origin matches
        // the display's — here it doesn't, and assuming it did put every stroke on
        // the wrong half of the screen vertically.
        // Device portrait frame: u = nx, v = 1 - ny; each case is that pair rotated.
        var (rx, ry) = _opts.Orientation switch
        {
            Orientation.Portrait => (nx, 1.0 - ny),
            Orientation.Landscape => (1.0 - ny, 1.0 - nx),
            Orientation.PortraitFlipped => (1.0 - nx, ny),
            Orientation.LandscapeFlipped => (ny, nx),
            _ => (nx, 1.0 - ny)
        };

        var (sx, sy) = _transform.ToScreen(rx, ry);

        // Pressure: device raw → 0..1024 (Windows Ink scale).
        var pressureNorm = ScreenTransform.Normalize(
            c.Pressure, _profile.Touch.PressureMin, _profile.Touch.PressureMax);
        var pressure = (uint)(pressureNorm * InjectionScale.PressureMax);

        // Contact size passes through in raw device units — see
        // MappedTouchContact for why it is not converted to millimetres.
        return new MappedTouchContact(
            c.Slot, c.TrackingId, sx, sy, pressure, c.TouchMajor, c.TouchMinor);
    }
}

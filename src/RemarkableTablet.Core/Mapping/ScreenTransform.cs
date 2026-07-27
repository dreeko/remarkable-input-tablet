using RemarkableTablet.Core.Devices;

namespace RemarkableTablet.Core.Mapping;

/// <summary>
///     The half of the mapping that is identical for pen and touch: take a
///     normalised, orientation-corrected point in the tablet's screen-aligned
///     frame and turn it into an absolute screen pixel, honouring the active
///     tablet area and the aspect-ratio <see cref="FitMode" />.
///     Both <see cref="CoordinateMapper" /> and <see cref="TouchCoordinateMapper" />
///     share this instance so the two can't drift apart — they are a matched
///     pair by design (pen and touch must land on the same pixel).
/// </summary>
public sealed class ScreenTransform
{
    private readonly double _areaH;
    private readonly double _areaW;

    // Effective active area (after fit), as a fraction of the full surface.
    private readonly double _areaX;
    private readonly double _areaY;
    private readonly EdgePolicy _edge;

    public ScreenTransform(MappingOptions opts, DeviceProfile profile)
    {
        _edge = opts.Edge;

        _areaX = opts.TabletAreaX;
        _areaY = opts.TabletAreaY;
        _areaW = opts.TabletAreaW > 0 ? opts.TabletAreaW : 1.0;
        _areaH = opts.TabletAreaH > 0 ? opts.TabletAreaH : 1.0;

        MonitorX = opts.MonitorX;
        MonitorY = opts.MonitorY;
        MonitorW = Math.Max(1, opts.MonitorW);
        MonitorH = Math.Max(1, opts.MonitorH);

        // Physical extent of the surface in the rotated (screen-aligned) frame.
        var landscape = opts.Orientation is Orientation.Landscape or Orientation.LandscapeFlipped;
        var surfaceW = landscape ? profile.Surface.HeightMm : profile.Surface.WidthMm;
        var surfaceH = landscape ? profile.Surface.WidthMm : profile.Surface.HeightMm;

        var areaAspect = surfaceW * _areaW / (surfaceH * _areaH);
        var screenAspect = MonitorW / (double)MonitorH;

        switch (opts.Fit)
        {
            case FitMode.Crop when areaAspect > screenAspect:
                // Tablet area is wider than the screen — narrow it, keep it centred.
                Shrink(ref _areaX, ref _areaW, screenAspect / areaAspect);
                break;
            case FitMode.Crop:
                Shrink(ref _areaY, ref _areaH, areaAspect / screenAspect);
                break;

            case FitMode.Letterbox when screenAspect > areaAspect:
                // Screen is wider than the tablet area — use a centred column of it.
                var w = (int)Math.Round(MonitorH * areaAspect);
                MonitorX += (MonitorW - w) / 2;
                MonitorW = Math.Max(1, w);
                break;
            case FitMode.Letterbox:
                var h = (int)Math.Round(MonitorW / areaAspect);
                MonitorY += (MonitorH - h) / 2;
                MonitorH = Math.Max(1, h);
                break;

            case FitMode.Stretch:
            default:
                break;
        }

        // Screen pixels per millimetre of pen travel along each screen axis. The
        // uinput pen device declares these on ABS_X/ABS_Y so a consumer's
        // physical-size maths is coherent with the axis ranges it is given (which
        // are in screen pixels, not tablet ticks — mixing those told libinput the
        // tablet was ~19 mm wide).
        //
        // Caveat under FitMode.Letterbox: the declared axis range still spans the
        // whole screen (it must, because injected coordinates are absolute screen
        // pixels and a narrower declared range would be re-stretched to the display
        // by X/libinput), while these resolutions describe the letterboxed
        // sub-rectangle the pen actually reaches. range ÷ resolution therefore
        // overstates the physical width in that mode. Positioning stays correct;
        // only the metadata is approximate.
        //
        // Verified 2026-07-25 on Manjaro/libinput 1.31.3 with a 1920×1080 screen
        // and FitMode.Crop: `libinput list-devices` reports the virtual pen as
        // "Capabilities: tablet, Size: 160x90mm" (true mapped area 157.5 × 88.6 mm
        // — the ~2 % overshoot is input_absinfo.resolution being an integer
        // units-per-mm). Before this changed it reported 19 × 11 mm.
        XResolution = (int)Math.Round(MonitorW / (surfaceW * _areaW));
        YResolution = (int)Math.Round(MonitorH / (surfaceH * _areaH));
    }

    /// <summary>Target rectangle in absolute screen pixels (after letterboxing).</summary>
    public int MonitorX { get; }

    public int MonitorY { get; }
    public int MonitorW { get; }
    public int MonitorH { get; }

    /// <summary>Screen pixels per millimetre of tablet surface, per screen axis.</summary>
    public int XResolution { get; }

    public int YResolution { get; }

    /// <summary>
    ///     Normalised tablet point (0,0 = top-left of the surface in the rotated
    ///     frame) to absolute screen pixels, clamped to the target rectangle.
    /// </summary>
    public (int X, int Y) ToScreen(double rx, double ry)
    {
        TryToScreen(rx, ry, out var point);
        return point;
    }

    /// <summary>
    ///     As <see cref="ToScreen" />, but reports whether the point was actually
    ///     inside the active area. Returns false only under
    ///     <see cref="EdgePolicy.Drop" />; the out parameter is still the clamped
    ///     position, so a caller can use it for a final pen-up before discarding
    ///     the rest.
    /// </summary>
    public bool TryToScreen(double rx, double ry, out (int X, int Y) point)
    {
        var rawU = (rx - _areaX) / _areaW;
        var rawV = (ry - _areaY) / _areaH;

        var u = Math.Clamp(rawU, 0.0, 1.0);
        var v = Math.Clamp(rawV, 0.0, 1.0);

        point = (
            MonitorX + (int)Math.Round(u * (MonitorW - 1)),
            MonitorY + (int)Math.Round(v * (MonitorH - 1)));

        if (_edge == EdgePolicy.Clamp) return true;

        // Tolerance in output pixels, not in surface fractions: if clamping moved
        // the point by less than half a pixel it is indistinguishable from being
        // inside, so call it inside. A fraction-based epsilon would have to know
        // the device's quantisation — one raw unit is ~6e-5 of the surface, which
        // lands just outside a boundary expressed as an exact fraction and makes
        // the edge of the area flicker.
        return Math.Abs(rawU - u) * (MonitorW - 1) <= 0.5 &&
               Math.Abs(rawV - v) * (MonitorH - 1) <= 0.5;
    }

    /// <summary>Normalise a raw axis value against its declared range.</summary>
    public static double Normalize(int value, int min, int max)
    {
        if (max <= min) return 0.0;
        return Math.Clamp((value - min) / (double)(max - min), 0.0, 1.0);
    }

    private static void Shrink(ref double origin, ref double extent, double factor)
    {
        var reduced = extent * factor;
        origin += (extent - reduced) / 2.0;
        extent = reduced;
    }
}

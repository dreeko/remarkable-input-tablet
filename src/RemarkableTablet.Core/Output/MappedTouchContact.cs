namespace RemarkableTablet.Core.Output;

/// <summary>
///     A touch contact after coordinate mapping. ScreenX/ScreenY are absolute
///     pixels on the host display. Pressure is on the 0–1024 scale (matching
///     <see cref="MappedFrame.Pressure" />). Slot and TrackingId carry through
///     unchanged so output sinks can produce stable per-contact pointer IDs.
///     <para>
///         TouchMajor / TouchMinor are the contact's axis lengths in the panel's
///         own units, passed through unconverted. The kernel MT protocol says
///         these should be in surface units (i.e. panel pixels), but the rM2's
///         <c>pt_mt</c> driver declares a 0–255 range on a 1404 × 1872 panel and
///         reports 8–17 for a fingertip — roughly 1–2 px, which is not a
///         fingertip. The real unit is therefore unknown, so nothing here or
///         downstream may convert these to millimetres or screen pixels until a
///         calibration capture exists. They remain useful as a *relative* size
///         signal: a palm reads much larger than a finger on the same device.
///     </para>
/// </summary>
public readonly record struct MappedTouchContact(
    int Slot,
    int TrackingId,
    int ScreenX,
    int ScreenY,
    uint Pressure,
    int TouchMajor = 0,
    int TouchMinor = 0
);

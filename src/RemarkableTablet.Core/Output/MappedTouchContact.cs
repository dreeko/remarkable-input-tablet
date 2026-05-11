namespace RemarkableTablet.Core.Output;

/// <summary>
///     A touch contact after coordinate mapping. ScreenX/ScreenY are absolute
///     pixels on the host display. Pressure is on the 0–1024 scale (matching
///     <see cref="MappedFrame.Pressure" />). Slot and TrackingId carry through
///     unchanged so output sinks can produce stable per-contact pointer IDs.
/// </summary>
public readonly record struct MappedTouchContact(
    int Slot,
    int TrackingId,
    int ScreenX,
    int ScreenY,
    uint Pressure
);
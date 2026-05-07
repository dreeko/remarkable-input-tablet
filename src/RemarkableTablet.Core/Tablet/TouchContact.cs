namespace RemarkableTablet.Core.Tablet;

/// <summary>
///     A single touch contact within a TouchFrame. Coordinates are in raw
///     tablet (touchscreen) units — mapping to screen happens downstream.
///     Slot is the kernel MT-B slot index (stable across the contact's lifetime).
///     TrackingId is monotonically assigned by the firmware and may be reused
///     after release.
/// </summary>
public readonly record struct TouchContact(
    int Slot,
    int TrackingId,
    int X,
    int Y,
    int Pressure,
    int TouchMajor,
    int TouchMinor,
    int Orientation,
    int ToolType
);

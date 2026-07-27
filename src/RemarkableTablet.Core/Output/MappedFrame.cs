namespace RemarkableTablet.Core.Output;

/// <summary>
///     A pen frame after coordinate and pressure mapping, ready to be sent to an output mode.
///     All values are in host-screen / Windows Ink units.
/// </summary>
public readonly record struct MappedFrame(
    int ScreenX,
    int ScreenY,
    uint Pressure, // 0–1024  (Windows Ink scale)
    int TiltX, // −90 to +90 degrees
    int TiltY, // −90 to +90 degrees
    int Distance, // hover height, raw device units (0 = on the surface)
    bool IsTouch,
    bool IsEraser,
    bool BarrelButton,
    bool InRange,
    /// <summary>
    ///     False when the pen is outside the active tablet area and
    ///     <see cref="Mapping.EdgePolicy.Drop" /> is in force. The frame is still
    ///     mapped (and still tells the palm gate the pen is near the surface), but
    ///     the output should not follow it.
    /// </summary>
    bool InArea = true
);
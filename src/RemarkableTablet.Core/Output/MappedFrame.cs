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
    bool IsTouch,
    bool IsEraser,
    bool BarrelButton,
    bool InRange
);
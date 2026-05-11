namespace RemarkableTablet.Core.Output;

/// <summary>
///     Target scale for injected pointer values, independent of source device.
///     Matches Windows Ink expectations (pressure 0–1024, tilt −90 to +90
///     degrees); the Linux uinput outputs declare these same ranges at
///     <c>UI_ABS_SETUP</c> time so a single <see cref="MappedFrame" /> works
///     on both platforms without re-scaling.
/// </summary>
public static class InjectionScale
{
    public const int PressureMax = 1024;
    public const int TiltMin = -90;
    public const int TiltMax = 90;
}
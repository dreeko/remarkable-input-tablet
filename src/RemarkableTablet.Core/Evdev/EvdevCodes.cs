namespace RemarkableTablet.Core.Evdev;

public static class EvdevCodes
{
    // EV_SYN codes
    public const ushort SYN_REPORT  = 0;
    public const ushort SYN_DROPPED = 3;

    // EV_KEY codes — pen tool / buttons
    public const ushort BTN_TOOL_PEN    = 320;
    public const ushort BTN_TOOL_RUBBER = 321;
    public const ushort BTN_TOUCH       = 330;
    public const ushort BTN_STYLUS      = 331;
    public const ushort BTN_STYLUS2     = 332;

    // EV_ABS codes — absolute pen axes
    public const ushort ABS_X        = 0;
    public const ushort ABS_Y        = 1;
    public const ushort ABS_PRESSURE = 24;
    public const ushort ABS_DISTANCE = 25;
    public const ushort ABS_TILT_X   = 26;
    public const ushort ABS_TILT_Y   = 27;
}

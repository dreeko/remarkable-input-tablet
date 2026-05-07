namespace RemarkableTablet.Core.Evdev;

public static class EvdevCodes
{
    // EV_SYN codes
    public const ushort SYN_REPORT = 0;
    public const ushort SYN_DROPPED = 3;

    // EV_KEY codes — pen tool / buttons
    public const ushort BTN_TOOL_PEN = 320;
    public const ushort BTN_TOOL_RUBBER = 321;
    public const ushort BTN_TOUCH = 330;
    public const ushort BTN_STYLUS = 331;
    public const ushort BTN_STYLUS2 = 332;

    // EV_ABS codes — absolute pen axes
    public const ushort ABS_X = 0;
    public const ushort ABS_Y = 1;
    public const ushort ABS_PRESSURE = 24;
    public const ushort ABS_DISTANCE = 25;
    public const ushort ABS_TILT_X = 26;
    public const ushort ABS_TILT_Y = 27;

    // EV_ABS codes — multi-touch (MT-B slot protocol). rM2 touchscreen does
    // NOT report BTN_TOUCH; contact lifecycle is driven entirely by
    // ABS_MT_TRACKING_ID transitions (>=0 starts, -1 releases).
    public const ushort ABS_MT_SLOT = 47;
    public const ushort ABS_MT_TOUCH_MAJOR = 48;
    public const ushort ABS_MT_TOUCH_MINOR = 49;
    public const ushort ABS_MT_ORIENTATION = 52;
    public const ushort ABS_MT_POSITION_X = 53;
    public const ushort ABS_MT_POSITION_Y = 54;
    public const ushort ABS_MT_TOOL_TYPE = 55;
    public const ushort ABS_MT_TRACKING_ID = 57;
    public const ushort ABS_MT_PRESSURE = 58;
}
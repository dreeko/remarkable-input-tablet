namespace RemarkableTablet.Core.Tablet;

/// <summary>
///     Hardware constants for the reMarkable 2 pen digitizer.
///     Verified 2026-05-06 via evtest /dev/input/event1 on firmware with Wacom I2C Digitizer
///     (Bus=0018, Vendor=2d1f, Product=0095, Version=1231). Struct size confirmed 16 bytes (armv7l).
/// </summary>
public static class ReMarkable2Constants
{
    // Pen device path on the tablet
    public const string PenDevicePath = "/dev/input/event1";

    // evdev struct size — 16 bytes on 32-bit ARM (i.MX7D Cortex-A7), confirmed armv7l
    public const int EventStructSize = 16;

    // ABS_X / ABS_Y — portrait orientation (USB at bottom, device held tall).
    // Pen aligned with the touch panel (verified 2026-05-07 against pt_mt
    // touchscreen which produces correct cursor positioning):
    //   ABS_X is the LONG axis,  0 = top of device, PenXMax = USB/bottom.
    //   ABS_Y is the SHORT axis, 0 = right side,    PenYMax = left side.
    public const int PenXMin = 0;
    public const int PenXMax = 20966;

    public const int PenYMin = 0;
    public const int PenYMax = 15725;

    // ABS_PRESSURE — 12-bit, 4096 levels
    public const int PressureMin = 0;
    public const int PressureMax = 4095;

    // ABS_DISTANCE — hover distance from surface
    public const int DistanceMin = 0;
    public const int DistanceMax = 255;

    // ABS_TILT_X / ABS_TILT_Y — range ±9000 (firmware units, not degrees)
    public const int TiltXMin = -9000;
    public const int TiltXMax = 9000;
    public const int TiltYMin = -9000;
    public const int TiltYMax = 9000;

    // Windows Ink pressure scale
    public const int WindowsPressureMax = 1024;

    // Windows Ink tilt range (degrees)
    public const int WindowsTiltMin = -90;
    public const int WindowsTiltMax = 90;

    // ── Touchscreen ─────────────────────────────────────────────────────────
    // Verified 2026-05-07 via evtest /dev/input/event2 (driver: pt_mt).
    // Coordinates are display-aligned (1404 × 1872), MT-B slot protocol.
    // BTN_TOUCH is NOT reported by this device — contact lifecycle uses
    // ABS_MT_TRACKING_ID transitions only.
    public const string TouchDevicePath = "/dev/input/event2";

    public const int TouchXMin = 0;
    public const int TouchXMax = 1403;
    public const int TouchYMin = 0;
    public const int TouchYMax = 1871;

    public const int TouchPressureMin = 0;
    public const int TouchPressureMax = 255;

    // Hardware reports 32 slots; we cap our state machine at 5 (enough for
    // two-finger gestures with margin for transient noise contacts).
    public const int TouchMaxSlots = 32;
    public const int TouchMaxTracked = 5;
}
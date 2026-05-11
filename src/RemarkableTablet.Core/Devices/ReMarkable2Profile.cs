namespace RemarkableTablet.Core.Devices;

/// <summary>
///     reMarkable 2 (Wacom I2C Digitizer, i.MX7D armv7l).
///     Verified 2026-05-06 via evtest /dev/input/event1 on firmware 1231
///     (Bus=0018, Vendor=2d1f, Product=0095, Version=1231); touchscreen
///     verified 2026-05-07 via evtest /dev/input/event2 (driver: pt_mt).
///     Pen axis convention (re-verified 2026-05-07 against the touchscreen,
///     which is INPUT_PROP_DIRECT and indisputable):
///       ABS_X is the LONG axis,  0 = top of device, max = USB/bottom (portrait).
///       ABS_Y is the SHORT axis, 0 = right side,    max = left side  (portrait).
///     Touch panel coordinates are display-aligned (1404 × 1872), MT-B
///     slot protocol. BTN_TOUCH is NOT reported by the touchscreen — contact
///     lifecycle is driven by ABS_MT_TRACKING_ID transitions.
/// </summary>
public static class ReMarkable2Profile
{
    public static DeviceProfile Instance { get; } = new()
    {
        Name = "reMarkable 2",
        EventLayout = EvdevLayout.Bits32,
        PenDevicePath = "/dev/input/event1",
        TouchDevicePath = "/dev/input/event2",
        Pen = new PenAxes(
            XMin: 0,    XMax: 20966, XResolution: 0,
            YMin: 0,    YMax: 15725, YResolution: 0,
            PressureMin: 0, PressureMax: 4095,
            TiltXMin: -9000, TiltXMax: 9000,
            TiltYMin: -9000, TiltYMax: 9000,
            DistanceMin: 0, DistanceMax: 255),
        Touch = new TouchAxes(
            XMin: 0, XMax: 1403,
            YMin: 0, YMax: 1871,
            PressureMin: 0, PressureMax: 255,
            MaxSlots: 32, MaxTracked: 5),
        PenSuppressesTouch = true
    };
}

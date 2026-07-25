namespace RemarkableTablet.Core.Devices;

/// <summary>
///     reMarkable 2 (Wacom I2C Digitizer, i.MX7D armv7l).
///     Verified 2026-05-06 via evtest /dev/input/event1 on firmware 1231
///     (Bus=0018, Vendor=2d1f, Product=0095, Version=1231); touchscreen
///     verified 2026-05-07 via evtest /dev/input/event2 (driver: pt_mt).
///     <para>
///         <b>Axis conventions, measured 2026-07-25</b> by touching two corners of
///         the SAME top edge with the device held portrait, USB-C port at the
///         bottom — raw captures in <c>tools/EventDiagnostics/samples/</c>.
///         Both axes of both devices had been documented backwards; the earlier
///         "verification" inferred the pen axes from an assumption about the touch
///         panel's origin, and that assumption was wrong.
///     </para>
///     <list type="bullet">
///         <item>
///             Pen: ABS_X is the LONG axis, <b>0 = bottom (USB edge), max = top</b>
///             — top-edge samples read X ≈ 20258–20584 of 20966. ABS_Y is the SHORT
///             axis, <b>0 = left, max = right</b> — top-left read Y ≈ 672,
///             top-right ≈ 15258 of 15725.
///         </item>
///         <item>
///             Touch: ABS_MT_POSITION_X is the SHORT axis, <b>0 = left</b>
///             (top-left 85 → top-right 1379 of 1403). ABS_MT_POSITION_Y is the
///             LONG axis, <b>0 = bottom, max = top</b> — Y held ≈ 1836 of 1871
///             along the top edge. The panel is INPUT_PROP_DIRECT but its origin
///             is the bottom-left, not the display's top-left.
///         </item>
///     </list>
///     Touch is MT-B slot protocol. BTN_TOUCH is NOT reported by the touchscreen —
///     contact lifecycle is driven by ABS_MT_TRACKING_ID transitions, and the
///     firmware abandons in-flight contacts without a release when the pen enters
///     proximity (see <see cref="Pipeline.PenProximityGate" />).
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
            // Resolution = 100 ticks/mm follows FreeCap23/reMarkable-tablet-driver;
            // libinput uses this to recognise the virtual uinput device as a
            // tablet on Wayland. Prior to this value being set, Linux Wayland
            // users sometimes saw the device categorised as generic absolute
            // input rather than a tablet.
            0, 20966, 100,
            0, 15725, 100,
            0, 4095,
            -9000, 9000,
            -9000, 9000,
            0, 255),
        Touch = new TouchAxes(
            0, 1403,
            0, 1871,
            0, 255,
            32, 5),

        // 1404 × 1872 px at 226 dpi ⇒ 157.8 × 210.4 mm; the pen digitizer
        // agrees independently (20966 / 15725 ticks at the declared 100
        // ticks/mm ⇒ 209.7 × 157.3 mm), so the two axes are within half a
        // millimetre of each other. Used for aspect-correct screen fitting
        // and for the virtual pen device's declared resolution.
        Surface = new ActiveArea(157.5, 210.0)
    };
}
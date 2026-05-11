namespace RemarkableTablet.Core.Devices;

/// <summary>
///     reMarkable Paper Pro (NXP i.MX 8MM "ferrari", aarch64 / arm64).
///     <para>
///         Values seeded from <c>Evidlo/remarkable_mouse</c>'s <c>rmpro</c>
///         branch (codes.py, captured by the maintainer via Issue #92).
///         Items marked <c>TODO(rmpp-phase0)</c> were not in the public
///         community data and must be confirmed on real hardware before
///         v0.4.0 ships. Grep for that tag before tagging the release.
///     </para>
///     <para>
///         Key fact: aarch64 userspace uses a 24-byte <c>struct input_event</c>
///         (16-byte timeval + HHi), not the 16-byte struct rM2 emits. The
///         <see cref="EvdevLayout.Bits64" /> layout below is what makes the
///         shared <see cref="Evdev.EvdevParser" /> correctly decode this
///         device's stream — running the rM2 (Bits32) layout against an rMPP
///         stream produces silent desync, observable as garbage event codes
///         (e.g. <c>KeyError: 61892</c> in Issue #92's Python decoder).
///     </para>
/// </summary>
public static class ReMarkablePaperProProfile
{
    public static DeviceProfile Instance { get; } = new()
    {
        Name = "reMarkable Paper Pro",
        EventLayout = EvdevLayout.Bits64,

        // Device-node ordering per Issue #92: event0 = power button,
        // event1 = pen attach/detach, event2 = pen, event3 = touch.
        PenDevicePath = "/dev/input/event2",
        TouchDevicePath = "/dev/input/event3",

        Pen = new PenAxes(
            // Pen axis ranges and resolutions from Evidlo `rmpro` constants.
            // Resolution units are ticks per millimetre per the input_absinfo
            // convention; the rMPP's much higher values vs rM2 (100) reflect
            // the higher-density active digitizer geometry.
            XMin: 0,    XMax: 11180, XResolution: 2832,
            YMin: 0,    YMax: 15340, YResolution: 2064,
            PressureMin: 0, PressureMax: 4096,

            // TODO(rmpp-phase0): tilt and distance ranges are not in the
            // public rmpro constants. Capture via evtest /dev/input/event2
            // on real hardware. Placeholders assume rM2 conventions
            // (firmware units ±9000 mapped to ±90°, hover 0–255).
            TiltXMin: -9000, TiltXMax: 9000,
            TiltYMin: -9000, TiltYMax: 9000,
            DistanceMin: 0,  DistanceMax: 255),

        Touch = new TouchAxes(
            // TODO(rmpp-phase0): touch axis ranges have not been published
            // by anyone. The placeholders match the display (1620×2160,
            // INPUT_PROP_DIRECT) on the reasonable assumption the panel is
            // display-aligned like the rM2's pt_mt driver. Confirm via
            // evtest /dev/input/event3 and corner-tap calibration.
            XMin: 0, XMax: 1619,
            YMin: 0, YMax: 2159,
            PressureMin: 0, PressureMax: 255,
            MaxSlots: 32, MaxTracked: 5),

        // TODO(rmpp-phase0): reMarkable markets palm rejection on rMPP but
        // does not specify the mechanism. If touch events flow while the
        // pen is in proximity, a host-side pen-tool gate is required.
        // Verify by streaming event2 + event3 simultaneously and hovering
        // the pen.
        PenSuppressesTouch = true
    };
}

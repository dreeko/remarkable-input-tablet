namespace RemarkableTablet.Core.Devices;

/// <summary>
///     Hardware-specific facts about a reMarkable model. One instance per
///     supported device; the pipeline reads everything device-dependent from
///     here so adding a new device is one file in this folder.
///     Target-side scale constants (Windows Ink pressure 0–1024, tilt ±90°)
///     are NOT device-specific — see <see cref="Output.InjectionScale" />.
/// </summary>
public sealed record DeviceProfile
{
    public required string Name { get; init; }

    /// <summary>Byte layout of <c>struct input_event</c> on this device's userspace ABI.</summary>
    public required EvdevLayout EventLayout { get; init; }

    public required string PenDevicePath { get; init; }
    public required string TouchDevicePath { get; init; }

    public required PenAxes Pen { get; init; }
    public required TouchAxes Touch { get; init; }

    /// <summary>
    ///     True if the firmware suppresses touch events while the pen is in
    ///     proximity (rM2 hardware-level behavior, verified via evtest). When
    ///     true, no host-side pen-tool gate is needed to keep gestures from
    ///     firing during drawing.
    /// </summary>
    public bool PenSuppressesTouch { get; init; }
}

/// <summary>
///     Byte layout of <c>struct input_event</c>. 32-bit ARM userspace uses 16
///     bytes (8-byte timeval + HHi); 64-bit ARM userspace uses 24 bytes (16-byte
///     timeval + HHi). The HHi tail is identical; only the timeval prefix size
///     and therefore the field offsets differ.
/// </summary>
public sealed record EvdevLayout(int StructSize, int TypeOffset, int CodeOffset, int ValueOffset)
{
    public static EvdevLayout Bits32 { get; } = new(16, 8, 10, 12);
    public static EvdevLayout Bits64 { get; } = new(24, 16, 18, 20);
}

/// <summary>
///     Pen digitizer axis ranges (raw firmware units). Resolution is "ticks per
///     mm" as defined by <c>input_absinfo</c> — populated for libinput / Wayland
///     tablet recognition; 0 means "not declared."
/// </summary>
public sealed record PenAxes(
    int XMin,
    int XMax,
    int XResolution,
    int YMin,
    int YMax,
    int YResolution,
    int PressureMin,
    int PressureMax,
    int TiltXMin,
    int TiltXMax,
    int TiltYMin,
    int TiltYMax,
    int DistanceMin,
    int DistanceMax);

/// <summary>
///     Touchscreen axis ranges. <c>MaxSlots</c> is what the kernel reports;
///     <c>MaxTracked</c> is what the host-side state machine actually keeps
///     state for (enough for two-finger gestures plus margin).
/// </summary>
public sealed record TouchAxes(
    int XMin,
    int XMax,
    int YMin,
    int YMax,
    int PressureMin,
    int PressureMax,
    int MaxSlots,
    int MaxTracked);
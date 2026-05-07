using System.Runtime.InteropServices;

namespace RemarkableTablet.Linux.Interop;

// ── event types ──────────────────────────────────────────────────────────────
internal static class EvType
{
    internal const ushort EV_SYN = 0;
    internal const ushort EV_KEY = 1;
    internal const ushort EV_ABS = 3;
}

// ── sync codes ───────────────────────────────────────────────────────────────
internal static class SynCode
{
    internal const ushort SYN_REPORT = 0;
}

// ── key/button codes ─────────────────────────────────────────────────────────
internal static class BtnCode
{
    internal const ushort BTN_TOOL_PEN    = 0x140;
    internal const ushort BTN_TOOL_RUBBER = 0x141;
    internal const ushort BTN_TOUCH       = 0x14a;
    internal const ushort BTN_STYLUS      = 0x14b;
}

// ── absolute axis codes ──────────────────────────────────────────────────────
internal static class AbsCode
{
    internal const ushort ABS_X        = 0;
    internal const ushort ABS_Y        = 1;
    internal const ushort ABS_PRESSURE = 24;
    internal const ushort ABS_TILT_X   = 26;
    internal const ushort ABS_TILT_Y   = 27;

    // Multi-touch (MT-B slot protocol)
    internal const ushort ABS_MT_SLOT        = 47;
    internal const ushort ABS_MT_TOUCH_MAJOR = 48;
    internal const ushort ABS_MT_POSITION_X  = 53;
    internal const ushort ABS_MT_POSITION_Y  = 54;
    internal const ushort ABS_MT_TRACKING_ID = 57;
    internal const ushort ABS_MT_PRESSURE    = 58;
}

// ── input device properties ──────────────────────────────────────────────────
internal static class InputProp
{
    internal const int INPUT_PROP_DIRECT = 1; // coordinates map 1:1 to screen
}

// ── bus types ────────────────────────────────────────────────────────────────
internal static class BusType
{
    internal const ushort BUS_USB = 3;
}

// ── uinput ioctl request codes (x86_64 Linux) ───────────────────────────────
// Computed via _IO/_IOW macros from linux/uinput.h:
//   _IOW('U', nr, T) = (1<<30) | (sizeof(T)<<16) | ('U'<<8) | nr
internal static class UinputIoctl
{
    internal const ulong UI_DEV_CREATE  = 0x0000_5501; // _IO ('U', 1)
    internal const ulong UI_DEV_DESTROY = 0x0000_5502; // _IO ('U', 2)
    internal const ulong UI_DEV_SETUP   = 0x405C_5503; // _IOW('U', 3,  uinput_setup[92])
    internal const ulong UI_ABS_SETUP   = 0x401C_5504; // _IOW('U', 4,  uinput_abs_setup[28])
    internal const ulong UI_SET_EVBIT   = 0x4004_5564; // _IOW('U', 100, int)
    internal const ulong UI_SET_KEYBIT  = 0x4004_5565; // _IOW('U', 101, int)
    internal const ulong UI_SET_ABSBIT  = 0x4004_5567; // _IOW('U', 103, int)
    internal const ulong UI_SET_PROPBIT = 0x4004_556E; // _IOW('U', 110, int)
}

// ── kernel structs ───────────────────────────────────────────────────────────

/// <summary>
///     24-byte input_event for x86_64 Linux.
///     struct timeval = { long tv_sec, long tv_usec } (16 bytes on LP64),
///     followed by __u16 type, __u16 code, __s32 value.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct input_event
{
    public long   tv_sec;
    public long   tv_usec;
    public ushort type;
    public ushort code;
    public int    value;
}

/// <summary>struct input_id (8 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct input_id
{
    public ushort bustype;
    public ushort vendor;
    public ushort product;
    public ushort version;
}

/// <summary>
///     uinput_setup (92 bytes): input_id(8) + name[80] + ff_effects_max(4).
///     Must be exactly this size for the UI_DEV_SETUP ioctl.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct uinput_setup
{
    public input_id id;
    public fixed byte name[80];
    public uint ff_effects_max;
}

/// <summary>struct input_absinfo (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct input_absinfo
{
    public int  value;
    public int  minimum;
    public int  maximum;
    public int  fuzz;
    public int  flat;
    public uint resolution;
}

/// <summary>
///     uinput_abs_setup (28 bytes): code(2) + _pad(2) + input_absinfo(24).
///     The 2-byte pad aligns input_absinfo (which starts with __s32) to 4 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct uinput_abs_setup
{
    public ushort       code;
    private ushort      _pad;
    public input_absinfo absinfo;
}

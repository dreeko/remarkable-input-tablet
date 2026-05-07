using System.Runtime.InteropServices;

namespace RemarkableTablet.Windows.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int left;
    public int top;
    public int right;
    public int bottom;
}

[Flags]
internal enum TouchFlags : uint
{
    None = 0
}

[Flags]
internal enum TouchMask : uint
{
    None = 0,
    ContactArea = 0x00000001,
    Orientation = 0x00000002,
    Pressure = 0x00000004
}

/// <summary>
///     Win32 POINTER_TOUCH_INFO. Used as the touch payload in the
///     POINTER_TYPE_INFO union when injecting touch contacts.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_TOUCH_INFO
{
    public POINTER_INFO pointerInfo;
    public TouchFlags touchFlags;
    public TouchMask touchMask;
    public RECT rcContact;
    public RECT rcContactRaw;
    public uint orientation;
    public uint pressure; // 0–1024 (Windows convention)
}

/// <summary>
///     Tagged-union payload for InjectSyntheticPointerInput when the device
///     was created with PT_TOUCH. Same alignment quirk as
///     <see cref="POINTER_TYPE_INFO" />: on 64-bit Windows the union starts
///     at offset 8 (not 4) because POINTER_INFO contains HANDLE fields that
///     force 8-byte alignment.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct POINTER_TYPE_INFO_TOUCH
{
    [FieldOffset(0)] public uint type;
    [FieldOffset(8)] public POINTER_TOUCH_INFO touchInfo;
}

using System.Runtime.InteropServices;

namespace RemarkableTablet.Windows.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[Flags]
internal enum PointerFlags : uint
{
    None           = 0x00000000,
    New            = 0x00000001,
    InRange        = 0x00000002,
    InContact      = 0x00000004,
    FirstButton    = 0x00000010,
    SecondButton   = 0x00000020,
    ThirdButton    = 0x00000040,
    FourthButton   = 0x00000080,
    FifthButton    = 0x00000100,
    Primary        = 0x00002000,
    Confidence     = 0x00004000,
    Canceled       = 0x00008000,
    Down           = 0x00010000,
    Update         = 0x00020000,
    Up             = 0x00040000,
    Wheel          = 0x00080000,
    HWheel         = 0x00100000,
    CaptureChanged = 0x00200000,
    HasTransform   = 0x00400000,
}

[Flags]
internal enum PenFlags : uint
{
    None     = 0x00000000,
    Barrel   = 0x00000001,
    Inverted = 0x00000002,  // eraser end
    Eraser   = 0x00000004,
}

[Flags]
internal enum PenMask : uint
{
    None     = 0x00000000,
    Pressure = 0x00000001,
    Rotation = 0x00000002,
    TiltX    = 0x00000004,
    TiltY    = 0x00000008,
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_INFO
{
    public uint         pointerType;
    public uint         pointerId;
    public uint         frameId;
    public PointerFlags pointerFlags;
    public IntPtr       sourceDevice;
    public IntPtr       hwndTarget;
    public POINT        ptPixelLocation;
    public POINT        ptHimetricLocation;
    public POINT        ptPixelLocationRaw;
    public POINT        ptHimetricLocationRaw;
    public uint         dwTime;
    public uint         historyCount;
    public int          inputData;
    public uint         dwKeyStates;
    public ulong        PerformanceCount;
    public uint         ButtonChangeType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_PEN_INFO
{
    public POINTER_INFO pointerInfo;
    public PenFlags     penFlags;
    public PenMask      penMask;
    public uint         pressure;  // 0–1024
    public uint         rotation;  // 0–359 (unused for now)
    public int          tiltX;     // −90 to +90
    public int          tiltY;     // −90 to +90
}

/// <summary>
/// Tagged union passed to InjectSyntheticPointerInput.
/// Explicit layout matches the Win32 POINTER_TYPE_INFO union.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct POINTER_TYPE_INFO
{
    [FieldOffset(0)] public uint             type;
    [FieldOffset(4)] public POINTER_PEN_INFO penInfo;
}

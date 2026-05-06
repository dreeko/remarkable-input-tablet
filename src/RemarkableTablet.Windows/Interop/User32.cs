using System.Runtime.InteropServices;

namespace RemarkableTablet.Windows.Interop;

internal static partial class User32
{
    internal const uint PT_PEN                    = 3;
    internal const uint POINTER_FEEDBACK_DEFAULT  = 1;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreateSyntheticPointerDevice(
        uint pointerType,
        uint maxCount,
        uint feedbackMode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InjectSyntheticPointerInput(
        IntPtr device,
        in POINTER_TYPE_INFO pointerInfo,
        uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroySyntheticPointerDevice(IntPtr device);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    internal static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP   = 0x0004;
    internal const uint MOUSEEVENTF_MOVE     = 0x0001;
}

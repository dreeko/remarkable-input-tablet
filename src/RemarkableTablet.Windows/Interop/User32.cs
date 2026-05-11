using System.Runtime.InteropServices;

namespace RemarkableTablet.Windows.Interop;

internal static class User32
{
    internal const uint PT_TOUCH = 2;
    internal const uint PT_PEN = 3;
    internal const uint POINTER_FEEDBACK_DEFAULT = 1;

    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_MOVE = 0x0001;

    // DPI awareness — required for SM_CXSCREEN/SM_CYSCREEN to return native pixels
    // on high-DPI displays. Without this the CLI sees scaled coordinates and the pen
    // lands in the wrong place.
    internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

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

    /// <summary>
    ///     Touch-flavoured overload — separate P/Invoke because the union
    ///     payload differs in size and layout from the pen variant.
    ///     Caller passes a pinned array of POINTER_TYPE_INFO_TOUCH.
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "InjectSyntheticPointerInput", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern unsafe bool InjectSyntheticTouchInput(
        IntPtr device,
        POINTER_TYPE_INFO_TOUCH* pointerInfo,
        uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroySyntheticPointerDevice(IntPtr device);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    internal static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}

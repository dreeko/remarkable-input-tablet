namespace RemarkableTablet.Windows.Interop;

public static class ScreenMetrics
{
    public static (int W, int H) GetPrimarySize()
    {
        return (User32.GetSystemMetrics(0), User32.GetSystemMetrics(1));
        // SM_CXSCREEN, SM_CYSCREEN
    }

    /// <summary>
    ///     Declare per-monitor DPI awareness so <see cref="GetPrimarySize" /> returns
    ///     native pixels on high-DPI displays. Must be called before any UI is drawn
    ///     and before screen metrics are queried.
    /// </summary>
    public static bool EnablePerMonitorDpiAwareness()
    {
        return User32.SetProcessDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
}
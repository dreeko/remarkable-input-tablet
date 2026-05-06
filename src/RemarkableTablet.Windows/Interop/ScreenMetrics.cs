namespace RemarkableTablet.Windows.Interop;

public static class ScreenMetrics
{
    public static (int W, int H) GetPrimarySize() =>
        (User32.GetSystemMetrics(0), User32.GetSystemMetrics(1)); // SM_CXSCREEN, SM_CYSCREEN
}

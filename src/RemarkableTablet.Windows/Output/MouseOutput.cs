using RemarkableTablet.Core.Output;
using RemarkableTablet.Windows.Interop;

namespace RemarkableTablet.Windows.Output;

/// <summary>
///     Phase 1 output: dumb mouse cursor movement.
///     Used to verify the full pipeline before replacing with WindowsInkOutput.
///     No pressure or tilt support.
/// </summary>
public sealed class MouseOutput : IOutputMode
{
    private bool _wasInContact;

    public void Initialize() { }

    public void Send(MappedFrame frame)
    {
        User32.SetCursorPos(frame.ScreenX, frame.ScreenY);

        // BTN_TOUCH does not fire reliably on the rM2; treat any non-zero pressure
        // as contact too, matching WindowsInkOutput.
        var inContact = frame.IsTouch || frame.Pressure > 0;

        if (inContact && !_wasInContact)
            User32.mouse_event(User32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        else if (!inContact && _wasInContact)
            User32.mouse_event(User32.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);

        _wasInContact = inContact;
    }

    public void Dispose()
    {
        // Ensure pen-up on exit
        if (_wasInContact)
            User32.mouse_event(User32.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }
}

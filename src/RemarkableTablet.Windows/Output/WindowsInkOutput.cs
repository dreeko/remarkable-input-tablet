using System.Runtime.InteropServices;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Windows.Interop;

namespace RemarkableTablet.Windows.Output;

/// <summary>
///     Phase 2 output: full Windows Pointer Injection (Windows Ink).
///     Delivers pressure, tilt, hover, barrel button, and eraser to any app
///     that supports the Windows Pointer API (Krita, Photoshop 2018+, Affinity, etc.)
///     App setup notes:
///     Krita:             Settings → Configure Krita → Tablet → Windows 8+ Pointer Input
///     Photoshop 2018+:   Works by default (or set UseSystemStylus 1 in PSUserConfig.txt)
///     Clip Studio Paint: Preferences → Tablet → Tablet PC
/// </summary>
public sealed class WindowsInkOutput : IOutputMode
{
    private IntPtr _device = IntPtr.Zero;
    private uint _frameId;
    private bool _wasInContact;

    public void Initialize()
    {
        _device = User32.CreateSyntheticPointerDevice(
            User32.PT_PEN, 1, User32.POINTER_FEEDBACK_DEFAULT);

        if (_device == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateSyntheticPointerDevice failed: error {Marshal.GetLastWin32Error()}. " +
                "Requires Windows 10 version 1809 or later.");
        }
    }

    public void Send(MappedFrame frame)
    {
        if (_device == IntPtr.Zero) return;

        _frameId++;

        var flags = BuildPointerFlags(frame);
        var pen = BuildPenFlags(frame);

        var typeInfo = new POINTER_TYPE_INFO
        {
            type = User32.PT_PEN,
            penInfo = new POINTER_PEN_INFO
            {
                pointerInfo = new POINTER_INFO
                {
                    pointerType = User32.PT_PEN,
                    pointerId = 0,
                    frameId = _frameId,
                    pointerFlags = flags,
                    ptPixelLocation = new POINT { X = frame.ScreenX, Y = frame.ScreenY },
                    ptPixelLocationRaw = new POINT { X = frame.ScreenX, Y = frame.ScreenY }
                },
                penFlags = pen,
                penMask = PenMask.Pressure | PenMask.TiltX | PenMask.TiltY,
                pressure = frame.Pressure, // 0–1024
                tiltX = frame.TiltX, // −90 to +90
                tiltY = frame.TiltY
            }
        };

        User32.InjectSyntheticPointerInput(_device, in typeInfo, 1);

        _wasInContact = frame.IsTouch || frame.Pressure > 0;
    }

    public void Dispose()
    {
        if (_device == IntPtr.Zero) return;

        // Emit final pen-up to prevent stuck state in drawing apps
        if (_wasInContact)
        {
            var typeInfo = new POINTER_TYPE_INFO
            {
                type = User32.PT_PEN,
                penInfo = new POINTER_PEN_INFO
                {
                    pointerInfo = new POINTER_INFO
                    {
                        pointerType = User32.PT_PEN,
                        pointerFlags = PointerFlags.Up
                    },
                    penMask = PenMask.Pressure,
                    pressure = 0
                }
            };
            User32.InjectSyntheticPointerInput(_device, in typeInfo, 1);
        }

        User32.DestroySyntheticPointerDevice(_device);
        _device = IntPtr.Zero;
    }

    private PointerFlags BuildPointerFlags(MappedFrame frame)
    {
        if (!frame.InRange)
            return _wasInContact ? PointerFlags.Up : PointerFlags.Update;

        var flags = PointerFlags.InRange;

        if (frame.IsTouch || frame.Pressure > 0)
        {
            flags |= PointerFlags.InContact;
            flags |= _wasInContact ? PointerFlags.Update : PointerFlags.Down;
        }
        else if (_wasInContact)
        {
            // Pen lifted — emit UP but keep InRange (still hovering)
            flags = PointerFlags.Up | PointerFlags.InRange;
        }
        else
            flags |= PointerFlags.Update; // hovering

        return flags;
    }

    private static PenFlags BuildPenFlags(MappedFrame frame)
    {
        var flags = PenFlags.None;
        if (frame.IsEraser) flags |= PenFlags.Inverted | PenFlags.Eraser;
        if (frame.BarrelButton) flags |= PenFlags.Barrel;
        return flags;
    }
}
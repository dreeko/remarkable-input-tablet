using System.Diagnostics;
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
    private bool _isFirstFrame = true;
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

        var inContact = frame.IsTouch || frame.Pressure > 0;

        // Skip frames that have nothing to report: pen fully out of range and was
        // already up. Injecting an Update flag with no InRange/InContact/Up/Down
        // accompanying flag is rejected by the OS.
        if (!frame.InRange && !_wasInContact && !_isFirstFrame)
            return;

        _frameId++;

        var flags = BuildPointerFlags(frame, inContact);
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

        if (!User32.InjectSyntheticPointerInput(_device, in typeInfo, 1))
            Trace.WriteLine($"InjectSyntheticPointerInput rejected: flags={flags} err={Marshal.GetLastWin32Error()}");

        _wasInContact = inContact;
        _isFirstFrame = false;
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
                        pointerFlags = PointerFlags.Up | PointerFlags.Primary
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

    private PointerFlags BuildPointerFlags(MappedFrame frame, bool inContact)
    {
        // Primary on every frame; New only on the very first inject after Initialize.
        var baseFlags = PointerFlags.Primary;
        if (_isFirstFrame) baseFlags |= PointerFlags.New;

        if (!frame.InRange)
        {
            // Pen left the digitizer entirely. If we were in contact, emit Up;
            // otherwise the early-return in Send() already handled the no-op case.
            return baseFlags | PointerFlags.Up;
        }

        var flags = baseFlags | PointerFlags.InRange;

        if (inContact)
        {
            flags |= PointerFlags.InContact;
            flags |= _wasInContact ? PointerFlags.Update : PointerFlags.Down;
        }
        else if (_wasInContact)
        {
            // Pen lifted — emit Up but keep InRange (still hovering)
            flags = baseFlags | PointerFlags.Up | PointerFlags.InRange;
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

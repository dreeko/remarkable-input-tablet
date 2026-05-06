using System.Runtime.InteropServices;
using System.Text;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Linux.Interop;

namespace RemarkableTablet.Linux.Output;

/// <summary>
///     Linux output: injects pen events via the uinput kernel module.
///     Creates a virtual tablet device that reports absolute position, pressure,
///     tilt, hover, and eraser to apps that read the Linux input subsystem
///     (Krita, GIMP, Inkscape, MyPaint, etc.).
///
///     Prerequisites:
///       sudo usermod -aG input $USER   (log out and back in)
///     Or create /etc/udev/rules.d/70-uinput.rules:
///       KERNEL=="uinput", GROUP="input", MODE="0660"
/// </summary>
public sealed class UinputOutput : IOutputMode
{
    private readonly int _screenW;
    private readonly int _screenH;
    private int  _fd = -1;
    private bool _wasInRange;

    public UinputOutput(int screenW, int screenH)
    {
        _screenW = screenW;
        _screenH = screenH;
    }

    public void Initialize()
    {
        _fd = Libc.open("/dev/uinput", Libc.O_WRONLY | Libc.O_NONBLOCK);
        if (_fd < 0)
            throw new InvalidOperationException(
                $"Cannot open /dev/uinput (errno {Marshal.GetLastWin32Error()}). " +
                "Add yourself to the 'input' group: sudo usermod -aG input $USER");

        // Declare event types this device produces
        Ioctl(UinputIoctl.UI_SET_EVBIT, EvType.EV_SYN);
        Ioctl(UinputIoctl.UI_SET_EVBIT, EvType.EV_KEY);
        Ioctl(UinputIoctl.UI_SET_EVBIT, EvType.EV_ABS);
        // INPUT_PROP_DIRECT: absolute coordinates map 1:1 to display pixels
        Ioctl(UinputIoctl.UI_SET_PROPBIT, InputProp.INPUT_PROP_DIRECT);

        // Key/button capabilities
        Ioctl(UinputIoctl.UI_SET_KEYBIT, BtnCode.BTN_TOOL_PEN);
        Ioctl(UinputIoctl.UI_SET_KEYBIT, BtnCode.BTN_TOOL_RUBBER);
        Ioctl(UinputIoctl.UI_SET_KEYBIT, BtnCode.BTN_TOUCH);
        Ioctl(UinputIoctl.UI_SET_KEYBIT, BtnCode.BTN_STYLUS);

        // Absolute axis capabilities
        Ioctl(UinputIoctl.UI_SET_ABSBIT, AbsCode.ABS_X);
        Ioctl(UinputIoctl.UI_SET_ABSBIT, AbsCode.ABS_Y);
        Ioctl(UinputIoctl.UI_SET_ABSBIT, AbsCode.ABS_PRESSURE);
        Ioctl(UinputIoctl.UI_SET_ABSBIT, AbsCode.ABS_TILT_X);
        Ioctl(UinputIoctl.UI_SET_ABSBIT, AbsCode.ABS_TILT_Y);

        SetupDevice();

        // Axis ranges match MappedFrame units so values inject directly without re-scaling
        SetAxis(AbsCode.ABS_X,        min: 0,   max: _screenW - 1, fuzz: 0, flat: 0);
        SetAxis(AbsCode.ABS_Y,        min: 0,   max: _screenH - 1, fuzz: 0, flat: 0);
        SetAxis(AbsCode.ABS_PRESSURE, min: 0,   max: 1024,          fuzz: 4, flat: 0);
        SetAxis(AbsCode.ABS_TILT_X,   min: -90, max: 90,            fuzz: 0, flat: 0);
        SetAxis(AbsCode.ABS_TILT_Y,   min: -90, max: 90,            fuzz: 0, flat: 0);

        Ioctl(UinputIoctl.UI_DEV_CREATE);

        // Allow udev to enumerate the new device before the first event arrives
        Thread.Sleep(100);
    }

    public void Send(MappedFrame frame)
    {
        if (_fd < 0) return;

        if (!frame.InRange)
        {
            if (_wasInRange)
            {
                EmitEvent(EvType.EV_KEY, BtnCode.BTN_TOUCH,       0);
                EmitEvent(EvType.EV_KEY, BtnCode.BTN_TOOL_PEN,    0);
                EmitEvent(EvType.EV_KEY, BtnCode.BTN_TOOL_RUBBER, 0);
                EmitSyn();
                _wasInRange = false;
            }
            return;
        }

        _wasInRange = true;

        EmitEvent(EvType.EV_ABS, AbsCode.ABS_X,        frame.ScreenX);
        EmitEvent(EvType.EV_ABS, AbsCode.ABS_Y,        frame.ScreenY);
        EmitEvent(EvType.EV_ABS, AbsCode.ABS_PRESSURE, (int)frame.Pressure);
        EmitEvent(EvType.EV_ABS, AbsCode.ABS_TILT_X,   frame.TiltX);
        EmitEvent(EvType.EV_ABS, AbsCode.ABS_TILT_Y,   frame.TiltY);

        if (frame.IsEraser)
        {
            EmitEvent(EvType.EV_KEY, BtnCode.BTN_TOOL_PEN,    0);
            EmitEvent(EvType.EV_KEY, BtnCode.BTN_TOOL_RUBBER, 1);
        }
        else
        {
            EmitEvent(EvType.EV_KEY, BtnCode.BTN_TOOL_PEN,    1);
            EmitEvent(EvType.EV_KEY, BtnCode.BTN_TOOL_RUBBER, 0);
        }

        EmitEvent(EvType.EV_KEY, BtnCode.BTN_TOUCH, frame.IsTouch || frame.Pressure > 0 ? 1 : 0);
        EmitSyn();
    }

    public void Dispose()
    {
        if (_fd < 0) return;

        if (_wasInRange)
        {
            EmitEvent(EvType.EV_KEY, BtnCode.BTN_TOUCH,       0);
            EmitEvent(EvType.EV_KEY, BtnCode.BTN_TOOL_PEN,    0);
            EmitEvent(EvType.EV_KEY, BtnCode.BTN_TOOL_RUBBER, 0);
            EmitSyn();
        }

        Libc.ioctl_noarg(_fd, UinputIoctl.UI_DEV_DESTROY);
        Libc.close(_fd);
        _fd = -1;
    }

    private unsafe void SetupDevice()
    {
        var setup = new uinput_setup
        {
            id = new input_id { bustype = BusType.BUS_USB, version = 1 },
            ff_effects_max = 0
        };

        var nameBytes = Encoding.ASCII.GetBytes("reMarkable 2 Pen");
        for (var i = 0; i < nameBytes.Length && i < 79; i++)
            setup.name[i] = nameBytes[i];

        IoctlPtr(UinputIoctl.UI_DEV_SETUP, &setup);
    }

    private unsafe void SetAxis(ushort code, int min, int max, int fuzz, int flat)
    {
        var abs = new uinput_abs_setup
        {
            code = code,
            absinfo = new input_absinfo { minimum = min, maximum = max, fuzz = fuzz, flat = flat }
        };
        IoctlPtr(UinputIoctl.UI_ABS_SETUP, &abs);
    }

    private unsafe void EmitEvent(ushort type, ushort code, int value)
    {
        var ev = new input_event { type = type, code = code, value = value };
        Libc.write(_fd, &ev, (nuint)sizeof(input_event));
    }

    private void EmitSyn() => EmitEvent(EvType.EV_SYN, SynCode.SYN_REPORT, 0);

    private void Ioctl(ulong request, int arg = 0)
    {
        if (Libc.ioctl_int(_fd, request, arg) < 0)
            throw new InvalidOperationException(
                $"uinput ioctl 0x{request:X} failed (errno {Marshal.GetLastWin32Error()})");
    }

    private unsafe void IoctlPtr(ulong request, void* arg)
    {
        if (Libc.ioctl_ptr(_fd, request, arg) < 0)
            throw new InvalidOperationException(
                $"uinput ioctl 0x{request:X} failed (errno {Marshal.GetLastWin32Error()})");
    }
}

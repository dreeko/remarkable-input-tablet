using System.Runtime.InteropServices;
using System.Text;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Linux.Interop;

namespace RemarkableTablet.Linux.Output;

/// <summary>
///     Linux output: injects multi-touch contacts via the uinput kernel module
///     using the MT-B (slot) protocol. Creates a virtual touchscreen that
///     reports up to <c>maxTracked</c> concurrent contacts to apps that read
///     the Linux input subsystem.
///     Same uinput permissions apply as <see cref="UinputOutput" />.
/// </summary>
public sealed class UinputTouchOutput : ITouchOutput
{
    // Slots active in the previous frame, keyed by slot index. Value is the
    // tracking ID (cached so we don't re-emit ABS_MT_TRACKING_ID every frame).
    private readonly Dictionary<int, int> _activeSlots = new();
    private readonly int _maxTracked;
    private readonly int _screenH;
    private readonly int _screenW;
    private bool _btnTouchDown;
    private int _currentEmittedSlot = -1;
    private int _fd = -1;

    public UinputTouchOutput(int screenW, int screenH, int maxTracked = 5)
    {
        _screenW = screenW;
        _screenH = screenH;
        _maxTracked = maxTracked;
    }

    public void Initialize()
    {
        _fd = Libc.open("/dev/uinput", Libc.O_WRONLY | Libc.O_NONBLOCK);
        if (_fd < 0)
            throw new InvalidOperationException(
                $"Cannot open /dev/uinput (errno {Marshal.GetLastWin32Error()}). " +
                "Add yourself to the 'input' group: sudo usermod -aG input $USER");

        Ioctl(UinputIoctl.UI_SET_EVBIT);
        Ioctl(UinputIoctl.UI_SET_EVBIT, EvType.EV_KEY);
        Ioctl(UinputIoctl.UI_SET_EVBIT, EvType.EV_ABS);
        Ioctl(UinputIoctl.UI_SET_PROPBIT, InputProp.INPUT_PROP_DIRECT);

        // BTN_TOUCH is required by the kernel for the device to be recognised
        // as a touchscreen even on MT-B devices; emitted from active-contact
        // count transitions.
        Ioctl(UinputIoctl.UI_SET_KEYBIT, BtnCode.BTN_TOUCH);

        Ioctl(UinputIoctl.UI_SET_ABSBIT, AbsCode.ABS_MT_SLOT);
        Ioctl(UinputIoctl.UI_SET_ABSBIT, AbsCode.ABS_MT_POSITION_X);
        Ioctl(UinputIoctl.UI_SET_ABSBIT, AbsCode.ABS_MT_POSITION_Y);
        Ioctl(UinputIoctl.UI_SET_ABSBIT, AbsCode.ABS_MT_TRACKING_ID);
        Ioctl(UinputIoctl.UI_SET_ABSBIT, AbsCode.ABS_MT_PRESSURE);
        Ioctl(UinputIoctl.UI_SET_ABSBIT, AbsCode.ABS_MT_TOUCH_MAJOR);

        SetupDevice();

        SetAxis(AbsCode.ABS_MT_SLOT, 0, _maxTracked - 1, 0, 0);
        SetAxis(AbsCode.ABS_MT_POSITION_X, 0, _screenW - 1, 0, 0);
        SetAxis(AbsCode.ABS_MT_POSITION_Y, 0, _screenH - 1, 0, 0);
        SetAxis(AbsCode.ABS_MT_TRACKING_ID, 0, 65535, 0, 0);
        SetAxis(AbsCode.ABS_MT_PRESSURE, 0, InjectionScale.PressureMax, 0, 0);
        SetAxis(AbsCode.ABS_MT_TOUCH_MAJOR, 0, 255, 0, 0);

        Ioctl(UinputIoctl.UI_DEV_CREATE);

        Thread.Sleep(100);
    }

    public void Send(MappedTouchFrame frame)
    {
        if (_fd < 0) return;

        var contacts = frame.Contacts;
        var seenSlots = new HashSet<int>(contacts.Count);
        var anyEmitted = false;

        foreach (var c in contacts)
        {
            // Cap the slot index to what the virtual device declared. Upstream
            // is supposed to respect TouchMaxTracked, but defend against an
            // out-of-range slot from a misbehaving frame.
            if (c.Slot < 0 || c.Slot >= _maxTracked) continue;

            seenSlots.Add(c.Slot);
            SwitchSlot(c.Slot);

            // New contact in this slot? Emit the tracking ID once.
            if (!_activeSlots.TryGetValue(c.Slot, out var prevId) || prevId != c.TrackingId)
            {
                EmitEvent(EvType.EV_ABS, AbsCode.ABS_MT_TRACKING_ID, c.TrackingId);
                _activeSlots[c.Slot] = c.TrackingId;
            }

            EmitEvent(EvType.EV_ABS, AbsCode.ABS_MT_POSITION_X, c.ScreenX);
            EmitEvent(EvType.EV_ABS, AbsCode.ABS_MT_POSITION_Y, c.ScreenY);
            EmitEvent(EvType.EV_ABS, AbsCode.ABS_MT_PRESSURE, (int)c.Pressure);
            anyEmitted = true;
        }

        // Release slots that were active last frame but aren't anymore.
        if (_activeSlots.Count > seenSlots.Count)
        {
            var toRelease = new List<int>();
            foreach (var slot in _activeSlots.Keys)
                if (!seenSlots.Contains(slot))
                    toRelease.Add(slot);
            foreach (var slot in toRelease)
            {
                SwitchSlot(slot);
                EmitEvent(EvType.EV_ABS, AbsCode.ABS_MT_TRACKING_ID, -1);
                _activeSlots.Remove(slot);
                anyEmitted = true;
            }
        }

        // BTN_TOUCH transition based on whether any contact is currently active.
        var shouldBeDown = _activeSlots.Count > 0;
        if (shouldBeDown != _btnTouchDown)
        {
            EmitEvent(EvType.EV_KEY, BtnCode.BTN_TOUCH, shouldBeDown ? 1 : 0);
            _btnTouchDown = shouldBeDown;
            anyEmitted = true;
        }

        if (anyEmitted) EmitSyn();
    }

    public void ReleaseAll()
    {
        if (_fd < 0 || _activeSlots.Count == 0) return;

        foreach (var slot in _activeSlots.Keys)
        {
            SwitchSlot(slot);
            EmitEvent(EvType.EV_ABS, AbsCode.ABS_MT_TRACKING_ID, -1);
        }

        _activeSlots.Clear();

        if (_btnTouchDown)
        {
            EmitEvent(EvType.EV_KEY, BtnCode.BTN_TOUCH, 0);
            _btnTouchDown = false;
        }

        EmitSyn();
    }

    public void Dispose()
    {
        if (_fd < 0) return;
        ReleaseAll();
        Libc.ioctl_noarg(_fd, UinputIoctl.UI_DEV_DESTROY);
        Libc.close(_fd);
        _fd = -1;
    }

    private void SwitchSlot(int slot)
    {
        if (_currentEmittedSlot == slot) return;
        EmitEvent(EvType.EV_ABS, AbsCode.ABS_MT_SLOT, slot);
        _currentEmittedSlot = slot;
    }

    private unsafe void SetupDevice()
    {
        var setup = new uinput_setup
        {
            id = new input_id { bustype = BusType.BUS_USB, version = 1 },
            ff_effects_max = 0
        };

        var nameBytes = Encoding.ASCII.GetBytes("reMarkable 2 Touch");
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

    private void EmitSyn()
    {
        EmitEvent(EvType.EV_SYN, SynCode.SYN_REPORT, 0);
    }

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
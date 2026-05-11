using System.Diagnostics;
using System.Runtime.InteropServices;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Windows.Interop;

namespace RemarkableTablet.Windows.Output;

/// <summary>
///     Windows touch output: injects multi-touch contacts via the synthetic
///     pointer device API (Windows 10 1809+, no driver required).
///     Apps that handle Windows touch (Krita, Photoshop, Affinity, browsers,
///     Microsoft apps) see real touch points and run their own pinch / pan /
///     rotate gesture recognition.
///     Contact lifecycle:
///       New contact:   Down + InRange + InContact (+ New on first frame ever)
///       Continuing:    Update + InRange + InContact
///       Released:      Up
///     Windows requires that EVERY currently-tracked contact appears in EVERY
///     InjectSyntheticPointerInput call until released — partial frames cause
///     the OS to drop pointers silently.
/// </summary>
public sealed class WindowsTouchInjectionOutput : ITouchOutput
{
    private readonly int _maxTracked;
    private IntPtr _device = IntPtr.Zero;
    private uint _frameId;
    private bool _isFirstFrame = true;

    // Active contacts keyed by slot. Stores tracking ID + last position so
    // we can build Up records for slots that have left without remembering
    // the upstream frame.
    private readonly Dictionary<int, ActiveContact> _active = new();

    private struct ActiveContact
    {
        public int TrackingId;
        public int X, Y;
        public uint Pressure;
    }

    public WindowsTouchInjectionOutput(int maxTracked = 5)
    {
        _maxTracked = maxTracked;
    }

    public void Initialize()
    {
        _device = User32.CreateSyntheticPointerDevice(
            User32.PT_TOUCH,
            (uint)_maxTracked,
            User32.POINTER_FEEDBACK_DEFAULT);

        if (_device == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CreateSyntheticPointerDevice(PT_TOUCH) failed: error {Marshal.GetLastWin32Error()}. " +
                "Requires Windows 10 version 1809 or later.");
    }

    public void Send(MappedTouchFrame frame)
    {
        if (_device == IntPtr.Zero) return;

        var current = frame.Contacts;

        // Build the inject array: every active contact (new, update, or up).
        var totalCount = current.Count + CountReleasedSlots(current);
        if (totalCount == 0) return;

        _frameId++;

        var infos = new POINTER_TYPE_INFO_TOUCH[totalCount];
        var idx = 0;

        // Track which slot is "primary" for this batch — convention: lowest
        // slot index in the current frame. Only one contact may have Primary.
        var primarySlot = -1;
        foreach (var c in current)
            if (primarySlot < 0 || c.Slot < primarySlot) primarySlot = c.Slot;

        // Updates / new contacts.
        foreach (var c in current)
        {
            if (c.Slot < 0 || c.Slot >= _maxTracked) continue;

            var isNew = !_active.ContainsKey(c.Slot);
            var flags = PointerFlags.InRange | PointerFlags.InContact;
            flags |= isNew ? PointerFlags.Down : PointerFlags.Update;
            if (c.Slot == primarySlot) flags |= PointerFlags.Primary;
            if (_isFirstFrame && isNew) flags |= PointerFlags.New;

            infos[idx++] = BuildInfo((uint)c.Slot, c.ScreenX, c.ScreenY, c.Pressure, flags);
            _active[c.Slot] = new ActiveContact
            {
                TrackingId = c.TrackingId, X = c.ScreenX, Y = c.ScreenY, Pressure = c.Pressure
            };
        }

        // Released contacts: present last frame, absent this frame.
        var releasing = new List<int>();
        foreach (var slot in _active.Keys)
        {
            var stillPresent = false;
            foreach (var c in current)
                if (c.Slot == slot) { stillPresent = true; break; }
            if (!stillPresent) releasing.Add(slot);
        }
        foreach (var slot in releasing)
        {
            var prev = _active[slot];
            infos[idx++] = BuildInfo((uint)slot, prev.X, prev.Y, prev.Pressure, PointerFlags.Up);
            _active.Remove(slot);
        }

        InjectBatch(infos, (uint)idx);
        _isFirstFrame = false;
    }

    public void ReleaseAll()
    {
        if (_device == IntPtr.Zero || _active.Count == 0) return;
        _frameId++;

        var infos = new POINTER_TYPE_INFO_TOUCH[_active.Count];
        var idx = 0;
        foreach (var kv in _active)
        {
            var prev = kv.Value;
            infos[idx++] = BuildInfo((uint)kv.Key, prev.X, prev.Y, prev.Pressure, PointerFlags.Up);
        }
        _active.Clear();
        InjectBatch(infos, (uint)idx);
    }

    public void Dispose()
    {
        if (_device == IntPtr.Zero) return;
        ReleaseAll();
        User32.DestroySyntheticPointerDevice(_device);
        _device = IntPtr.Zero;
    }

    private int CountReleasedSlots(IReadOnlyList<MappedTouchContact> current)
    {
        if (_active.Count == 0) return 0;
        var n = 0;
        foreach (var slot in _active.Keys)
        {
            var stillPresent = false;
            foreach (var c in current)
                if (c.Slot == slot) { stillPresent = true; break; }
            if (!stillPresent) n++;
        }
        return n;
    }

    private POINTER_TYPE_INFO_TOUCH BuildInfo(uint pointerId, int x, int y, uint pressure, PointerFlags flags) =>
        new()
        {
            type = User32.PT_TOUCH,
            touchInfo = new POINTER_TOUCH_INFO
            {
                pointerInfo = new POINTER_INFO
                {
                    pointerType = User32.PT_TOUCH,
                    pointerId = pointerId,
                    frameId = _frameId,
                    pointerFlags = flags,
                    ptPixelLocation = new POINT { X = x, Y = y },
                    ptPixelLocationRaw = new POINT { X = x, Y = y }
                },
                touchFlags = TouchFlags.None,
                touchMask = TouchMask.Pressure,
                pressure = pressure
            }
        };

    private unsafe void InjectBatch(POINTER_TYPE_INFO_TOUCH[] infos, uint count)
    {
        if (count == 0) return;
        fixed (POINTER_TYPE_INFO_TOUCH* p = infos)
        {
            if (!User32.InjectSyntheticTouchInput(_device, p, count))
                Trace.WriteLine($"InjectSyntheticPointerInput(touch) rejected: count={count} err={Marshal.GetLastWin32Error()}");
        }
    }
}

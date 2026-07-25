#!/usr/bin/env python3
"""Decode an rM2 evdev capture (evtest text or raw 16-byte structs) and answer:
   - which corner is the touch panel's origin
   - fingertip vs palm ABS_MT_TOUCH_MAJOR
   - whether contacts are released (TRACKING_ID=-1) before the stream goes quiet
Usage: analyse.py <file> [touch|pen]
"""
import re
import struct
import sys

ABS = {
    0: "ABS_X", 1: "ABS_Y", 24: "ABS_PRESSURE", 25: "ABS_DISTANCE",
    26: "ABS_TILT_X", 27: "ABS_TILT_Y", 47: "ABS_MT_SLOT",
    48: "ABS_MT_TOUCH_MAJOR", 49: "ABS_MT_TOUCH_MINOR", 52: "ABS_MT_ORIENTATION",
    53: "ABS_MT_POSITION_X", 54: "ABS_MT_POSITION_Y", 55: "ABS_MT_TOOL_TYPE",
    57: "ABS_MT_TRACKING_ID", 58: "ABS_MT_PRESSURE",
}
KEY = {320: "BTN_TOOL_PEN", 321: "BTN_TOOL_RUBBER", 330: "BTN_TOUCH", 331: "BTN_STYLUS"}


def parse(path):
    """-> [(t, type, code, value)] from either capture format."""
    raw = open(path, "rb").read()
    if b"Event: time" in raw[:4096] or b"Input device name" in raw[:4096]:
        out = []
        for line in raw.decode("utf-8", "replace").splitlines():
            m = re.search(r"time (\d+\.\d+).*type (\d+).*code (\d+).*value (-?\d+)", line)
            if m:
                out.append((float(m.group(1)), int(m.group(2)), int(m.group(3)), int(m.group(4))))
            elif "SYN_REPORT" in line:
                t = re.search(r"time (\d+\.\d+)", line)
                if t:
                    out.append((float(t.group(1)), 0, 0, 0))
        return out
    # raw 16-byte input_event (32-bit ARM): u32 sec, u32 usec, u16 type, u16 code, s32 value
    out = []
    for off in range(0, len(raw) - 15, 16):
        sec, usec, typ, code, val = struct.unpack_from("<IIHHi", raw, off)
        out.append((sec + usec / 1e6, typ, code, val))
    return out


def windows(events, gap=2.0):
    """Split into activity windows separated by >gap seconds of silence."""
    result, cur, last = [], [], None
    for e in events:
        if last is not None and e[0] - last > gap:
            if cur:
                result.append(cur)
            cur = []
        cur.append(e)
        last = e[0]
    if cur:
        result.append(cur)
    return result


def describe_touch(win, idx):
    slot, slots = 0, {}
    xs, ys, majors, minors, tools = [], [], [], [], set()
    ids_started, ids_released, live = [], [], set()
    for t, typ, code, val in win:
        if typ != 3:
            continue
        name = ABS.get(code)
        if name == "ABS_MT_SLOT":
            slot = val
        elif name == "ABS_MT_TRACKING_ID":
            if val >= 0:
                ids_started.append((slot, val, t))
                live.add(val)
            else:
                ids_released.append((slot, t))
                live.discard(slots.get(slot))
            slots[slot] = val if val >= 0 else None
        elif name == "ABS_MT_POSITION_X":
            xs.append(val)
        elif name == "ABS_MT_POSITION_Y":
            ys.append(val)
        elif name == "ABS_MT_TOUCH_MAJOR":
            majors.append(val)
        elif name == "ABS_MT_TOUCH_MINOR":
            minors.append(val)
        elif name == "ABS_MT_TOOL_TYPE":
            tools.add(val)

    print(f"\n── window {idx}  t={win[0][0]:.3f}→{win[-1][0]:.3f} ({win[-1][0]-win[0][0]:.1f}s)")
    print(f"   contacts started: {len(ids_started)}   released(-1): {len(ids_released)}   "
          f"still live at end: {len(live)}")
    if xs:
        print(f"   X: min={min(xs)} max={max(xs)}   Y: min={min(ys)} max={max(ys)}")
    if majors:
        print(f"   TOUCH_MAJOR: min={min(majors)} max={max(majors)} "
              f"median={sorted(majors)[len(majors)//2]}   (n={len(majors)})")
    if minors:
        print(f"   TOUCH_MINOR: min={min(minors)} max={max(minors)}")
    if tools:
        print(f"   TOOL_TYPE values seen: {sorted(tools)}")
    if live:
        print(f"   ⚠ {len(live)} contact(s) never released in this window: {sorted(live)}")
    return live


def describe_pen(win, idx):
    xs, ys, press, dist, keys = [], [], [], [], {}
    for t, typ, code, val in win:
        if typ == 3:
            n = ABS.get(code)
            if n == "ABS_X":
                xs.append(val)
            elif n == "ABS_Y":
                ys.append(val)
            elif n == "ABS_PRESSURE":
                press.append(val)
            elif n == "ABS_DISTANCE":
                dist.append(val)
        elif typ == 1:
            keys.setdefault(KEY.get(code, code), set()).add(val)
    print(f"\n── window {idx}  t={win[0][0]:.3f}→{win[-1][0]:.3f} ({win[-1][0]-win[0][0]:.1f}s)")
    if xs:
        print(f"   ABS_X: min={min(xs)} max={max(xs)}   ABS_Y: min={min(ys)} max={max(ys)}")
        # Contact samples only (pressure > 0) tell us where the tip actually was.
        print(f"   pressure: max={max(press) if press else 0}   distance range="
              f"{(min(dist), max(dist)) if dist else '—'}")
    if keys:
        print(f"   keys: { ({k: sorted(v) for k, v in keys.items()}) }")


def main():
    path = sys.argv[1]
    kind = sys.argv[2] if len(sys.argv) > 2 else ("pen" if "pen" in path else "touch")
    events = parse(path)
    if not events:
        print(f"{path}: no events decoded ({len(open(path,'rb').read())} bytes)")
        return
    print(f"{path}: {len(events)} events, {events[-1][0]-events[0][0]:.1f}s span, kind={kind}")
    for i, win in enumerate(windows(events), 1):
        (describe_pen if kind == "pen" else describe_touch)(win, i)


if __name__ == "__main__":
    main()

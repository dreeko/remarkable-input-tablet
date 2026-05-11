# Touchscreen evtest captures — Phase 0 verification

Source: `evtest --grab /dev/input/event2` on rM2 firmware (capture date 2026-05-07).
Raw log: [`touch-header.log`](touch-header.log) (single ~10 s capture, despite filename — full session.log was not
produced separately).

## Confirmed device facts

| Property                  | Value                                                                       | Source                                                    |
|---------------------------|-----------------------------------------------------------------------------|-----------------------------------------------------------|
| Device path               | `/dev/input/event2`                                                         | header line 3 (`Input device name: "pt_mt"`)              |
| Driver                    | Parade Technologies multi-touch (`pt_mt`)                                   | header                                                    |
| Bus / vendor / product    | `0x0 / 0x0 / 0x0` (firmware exposes no identifiers)                         | header line 2                                             |
| MT protocol               | **MT-B (Slot protocol)**                                                    | `ABS_MT_SLOT` present                                     |
| Coordinate range X        | **0 – 1403** (1404 px)                                                      | `ABS_MT_POSITION_X` axis info                             |
| Coordinate range Y        | **0 – 1871** (1872 px)                                                      | `ABS_MT_POSITION_Y` axis info                             |
| Pressure range            | 0 – 255                                                                     | `ABS_MT_PRESSURE` axis info                               |
| Slot range                | **0 – 31** (32 slots reported, far more than realistic concurrent contacts) | `ABS_MT_SLOT` axis info                                   |
| Tracking ID range         | 0 – 65535, monotonically increasing                                         | observed: 1389→1399 in 10 s                               |
| Touch-major / touch-minor | 0 – 255 each                                                                | available, useful for contact-size palm rejection         |
| Orientation               | `ABS_MT_ORIENTATION` -127 – 127                                             | available                                                 |
| Tool type                 | `ABS_MT_TOOL_TYPE` 0 – 1                                                    | likely finger vs palm — to confirm                        |
| INPUT_PROP_DIRECT         | yes                                                                         | display-aligned coordinates, no offset transform needed   |
| Sample rate               | **~85 Hz** (≈11.8 ms between SYN_REPORTs during continuous motion)          | measured from timestamps                                  |
| evdev struct size         | 16 bytes                                                                    | implicit from working evtest output (consistent with pen) |

## ⚠ Surprises that affect the plan

1. **`BTN_TOUCH` is NOT reported by this device.** The only `EV_KEY` codes exposed are `KEY_F1`–`KEY_F8` (
   gesture-shortcut keys, irrelevant to pointer logic). Contact lifecycle must therefore be derived **purely
   from `ABS_MT_TRACKING_ID` transitions**: a positive value starts a contact in the current slot; `-1` releases it. The
   pen state machine had a fallback (`pressure > 0` ⇒ contact) because `BTN_TOUCH` was unreliable there too — for touch
   we have no fallback at all, so the parser must be strict about tracking IDs.

2. **`ABS_MT_TOOL_TYPE` is reported.** Range 0–1. `MT_TOOL_FINGER = 0`, `MT_TOOL_PEN = 1` per the kernel — though for an
   `pt_mt` capacitive sensor "pen" likely means "stylus-like contact size" rather than the actual Wacom pen (which is on
   event1). Worth a follow-up evtest: rest a palm and watch whether tool type flips to 1.

3. **32 slots** is far more than necessary. We will cap our `TouchMaxContacts` at a sensible value (5 is plenty for
   two-finger gestures plus palm contacts). The slot index space is sparse; we don't allocate per-slot storage for all
   32.

4. **Sample rate ~85 Hz is faster than the 60 Hz typical of capacitive touch.** Good news for latency; transport
   bandwidth is not a concern.

## Observed gesture pattern (sanity check on parser plan)

Excerpt — two-finger contact begins at t = 742.889911:

```
ABS_MT_TRACKING_ID = 1389        ← slot 0 starts (slot is implicit = 0 at session start)
ABS_MT_POSITION_X  = 297
ABS_MT_POSITION_Y  = 1303
ABS_MT_PRESSURE    = 65
ABS_MT_TOUCH_MINOR = 17
ABS_MT_ORIENTATION = 2
SYN_REPORT
                                  ← 70 ms later (probably second finger landing)
ABS_MT_SLOT        = 1            ← switch to slot 1
ABS_MT_TRACKING_ID = 1390         ← slot 1 starts
ABS_MT_POSITION_X  = 737
ABS_MT_POSITION_Y  = 817
ABS_MT_PRESSURE    = 108
ABS_MT_TOUCH_MAJOR = 8
ABS_MT_TOUCH_MINOR = 17
SYN_REPORT
```

This is exactly the protocol shape `TouchStateMachine` is planned for in the M1 milestone: track current slot via
`ABS_MT_SLOT`, accumulate `_x/_y/_pressure` into `_slots[currentSlot]`, mark active on `TRACKING_ID >= 0`, mark released
on `TRACKING_ID = -1`, snapshot all active slots on `SYN_REPORT`. No surprises in protocol shape.

Slot release on second finger lift:

```
ABS_MT_TRACKING_ID = -1     (slot 1 — under previous SLOT context)
SYN_REPORT
ABS_MT_TRACKING_ID = -1     (slot 0 needed an explicit SLOT switch first in some traces)
SYN_REPORT
```

## Coverage gaps in this capture

The first session (`touch-header.log`) is ~10 s and only covers two-finger gestures. A second session (`touch-pen.log`)
was captured for motions 11 / 13 / 15 / 17 to answer the pen-proximity question.

## Pen-proximity findings — `touch-pen.log`

Motions captured (in order requested): 11 palm rest → pause → 13 pen hover → pause → 15 pen draw + finger rest → pause →
17 pen draw alone.

| Time window (epoch s)      | Duration              | Activity                                                                            |
|----------------------------|-----------------------|-------------------------------------------------------------------------------------|
| 838.087 → 841.028          | ~3.0 s                | Multiple touch contacts including one long (~1.7 s) — palm rest.                    |
| **841.028 → 851.727**      | **10.7 s of silence** | Covers motions 13 (pen hover, 3 s), pause, 15 (pen draw + finger rest, 3 s), pause. |
| 851.727 → 856.7 (file end) | ~5 s                  | Multiple touch contacts again — incidental touches after the pen was set aside.     |

**Conclusion (high confidence):** rM2 firmware suppresses the capacitive touch panel while the pen is in proximity. This
is consistent with Wacom's standard behavior on hybrid devices.

Specifically:

- Motion 13 (pen hovering 5 mm above screen, no touch contact) produced **zero events** on `event2`.
- Motion 15 (pen drawing while a finger rested in the corner of the screen) produced **zero events** on `event2`. If the
  firmware did not suppress, the resting finger would have generated a multi-second contact. It did not.
- Touch events resumed only after the pen was set aside.

**Implication for the implementation plan:** the host-side `PenToolGate` is a defense-in-depth feature, not a
load-bearing requirement. The firmware already enforces "pen takes priority over touch" at the source. Users will *not*
be able to "draw + pinch with off-hand simultaneously" — that's a hardware-level UX constraint, not a software gap. The
workflow is "lift pen → pinch/pan → resume drawing."

## Plan adjustments based on what we learned

To be folded into `docs/IMPLEMENTATION_PLAN_TOUCH.md` after the missing motions land:

- `ReMarkable2Constants.cs` additions (final values now confirmed):
  ```csharp
  public const string TouchDevicePath = "/dev/input/event2";
  public const int TouchXMin = 0, TouchXMax = 1403;
  public const int TouchYMin = 0, TouchYMax = 1871;
  public const int TouchPressureMin = 0, TouchPressureMax = 255;
  public const int TouchMaxSlots  = 32;     // hardware reports
  public const int TouchMaxTracked = 5;     // we cap our state machine here
  ```
- `TouchStateMachine` MUST NOT depend on `BTN_TOUCH` at all (it does not exist on this device). Update its design
  accordingly.
- `TouchStateMachine` should record `ABS_MT_TOUCH_MAJOR`/`MINOR` alongside position so future palm-rejection logic has
  the data. Initial implementation can ignore it.
- Slot allocation: use a `Dictionary<int slot, Contact>` rather than a fixed array sized to 32 — avoids wasted memory
  and accommodates sparse slot indices.

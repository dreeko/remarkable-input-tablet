# Touchscreen evtest captures — Phase 0 verification

> **2026-07-25: the axis conventions in the older sections below were wrong.** Corner captures on real
> hardware ([`corners-touch-2026-07-25.bin`](corners-touch-2026-07-25.bin),
> [`corners-pen-2026-07-25.bin`](corners-pen-2026-07-25.bin)) settled it — see
> [Corner calibration](#corner-calibration-2026-07-25) at the bottom. Read that first.

Source: `evtest --grab /dev/input/event2` on rM2 firmware (capture date 2026-05-07). Raw log: [
`touch-header.log`](touch-header.log) (single ~10 s capture, despite filename — full session.log was not produced
separately).

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

1. **`BTN_TOUCH` is NOT reported by this device.** The only `EV_KEY` codes exposed are `KEY_F1`–`KEY_F8`
   (gesture-shortcut keys, irrelevant to pointer logic). Contact lifecycle must therefore be derived **purely from
   `ABS_MT_TRACKING_ID` transitions**: a positive value starts a contact in the current slot; `-1` releases it. The pen
   state machine had a fallback (`pressure > 0` ⇒ contact) because `BTN_TOUCH` was unreliable there too — for touch we
   have no fallback at all, so the parser must be strict about tracking IDs.

2. **`ABS_MT_TOOL_TYPE` is reported.** Range 0–1. `MT_TOOL_FINGER = 0`, `MT_TOOL_PEN = 1` per the kernel — though for an
   `pt_mt` capacitive sensor "pen" likely means "stylus-like contact size" rather than the actual Wacom pen (which is on
   event1).
   **Resolved (2026-07-25, from the header above + kernel `linux/input.h`):** this panel *cannot* report a palm. The
   kernel values are `MT_TOOL_FINGER 0x00`, `MT_TOOL_PEN 0x01`, `MT_TOOL_PALM 0x02`, and the declared axis maximum here
   is 1 — so 2 is out of range and the "rest a palm and watch for a flip" follow-up has no possible positive outcome.
   Contact size (`ABS_MT_TOUCH_MAJOR` / `MINOR`) is the only palm signal this device offers.

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

**Implication for the implementation plan:** the host-side pen gate is a defense-in-depth feature, not a
load-bearing requirement. The firmware already enforces "pen takes priority over touch" at the source. Users will *not*
be able to "draw + pinch with off-hand simultaneously" — that's a hardware-level UX constraint, not a software gap. The
workflow is "lift pen → pinch/pan → resume drawing."

### Gap in this conclusion (noted 2026-07-25)

The suppression is inferred from silence, and the capture does **not** cover the case that matters most for palm
rejection: a contact already down when the pen *arrives*. Every contact in this log is explicitly released (`tid=-1` at
`1778125841.028023`) *before* the 10.7 s quiet window — the palm was lifted first, then the pen approached. So we still
don't know whether the firmware releases in-flight contacts or just stops reporting them. If it just stops, the host
would hold that contact indefinitely without the stale-contact sweep.

**To close it:** `evtest /dev/input/event2`, rest a palm, then bring the pen into proximity, and watch whether
`ABS_MT_TRACKING_ID = -1` appears at the moment the stream goes quiet.

### Report cadence while a contact is held (measured 2026-07-25)

Inter-`SYN_REPORT` gaps with at least one contact active — this bounds how aggressive any staleness timeout can be:

| Capture            | p50     | p95     | max         |
|--------------------|---------|---------|-------------|
| `touch-header.log` (active two-finger motion) | 11.8 ms | 11.9 ms | 70 ms       |
| `touch-pen.log` (palm rest, mostly still)     | 11.8 ms | 86 ms   | **1085 ms** |

The panel reports on change, so a motionless contact can be quiet for over a second. Any per-contact timeout must sit
well above that — `TouchOptions.StaleContactMs` defaults to 3 s for this reason, with the pen gate as the fast path.

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

---

## Corner calibration (2026-07-25)

Raw captures: [`corners-touch-2026-07-25.bin`](corners-touch-2026-07-25.bin) (`/dev/input/event2`),
[`corners-pen-2026-07-25.bin`](corners-pen-2026-07-25.bin) (`/dev/input/event1`), taken simultaneously
over SSH on firmware 1231. Decode either with [`decode.py`](decode.py):

```bash
python3 decode.py corners-touch-2026-07-25.bin touch
python3 decode.py corners-pen-2026-07-25.bin pen
```

These are raw 16-byte `struct input_event` streams (`cat /dev/input/eventN`), not `evtest` text.
`evtest`'s stdout is block-buffered when piped, and the SIGTERM that ends a capture discards whatever
is still in the buffer — which is the last motion, usually the interesting one. `cat` uses plain
read/write, so nothing is lost. That every capture is an exact multiple of 16 bytes also re-confirms
the 32-bit ARM struct layout the parser assumes.

### Method

Earlier sessions tapped two **diagonal** corners, which cannot settle orientation: opposite corners
look identical under a rotation *and* under a mirror. This capture used two corners of the **same
edge**, so the vector between them identifies which raw axis runs horizontally and in which direction.
Device held portrait, USB-C edge along the bottom; fingertip on top-left then top-right, then the pen
tip on the same two corners in the same hold.

### Result — both devices had been documented backwards

| Corner | Touch (`event2`) | Pen (`event1`) |
|---|---|---|
| top-left | X ≈ 85, Y ≈ 1837 | X ≈ 20258, Y ≈ 672 |
| top-right | X ≈ 1379, Y ≈ 1835 | X ≈ 20584, Y ≈ 15258 |

- **Touch**: X is the short axis, 0 = **left** (as assumed). Y is the long axis, but **0 = bottom** —
  Y stays at ≈ 1836 of 1871 all along the top edge. `INPUT_PROP_DIRECT` says the digitizer overlays a
  display; it says nothing about which corner is the origin, and here the origin is the bottom-left.
- **Pen**: ABS_X is the long axis with **0 = bottom** (USB edge) and max = top; ABS_Y is the short axis
  with **0 = left** and max = right. Both axes are inverted relative to the old documentation, i.e.
  the pen convention was 180° out.
- Consequently pen and touch disagreed with each other by a horizontal mirror — a pen stroke on the
  physical top-left corner landed at the screen's bottom-right while a finger on the same spot landed
  bottom-left. The claim that the pen axes had been "re-verified against the touchscreen" was circular:
  it inferred the pen from an unverified assumption about the panel's origin.

Both mappers were corrected against these numbers, and the values above are pinned as test data in
`CoordinateMapperTests` / `TouchCoordinateMapperTests`, including a cross-device test asserting that
pen and touch land within 40 px of each other on the same physical corner.

### Pen arbitration: firmware blocks new contacts, not established ones

Three sessions bear on this, and the middle one was misread at first.

**Session 3 (2026-07-25, `hw3` captures — the decisive one).** A fingertip was rested mid-screen and
kept *moving* in small circles for 27 s while the pen was brought into proximity three times, once
reaching `ABS_DISTANCE 0`. The contact reported continuously throughout:

| Pen in range | Duration | Position samples from the held contact | Max gap |
|---|---|---|---|
| 13.10 – 13.18 | 0.08 s | 12 | 12 ms |
| 15.53 – 17.36 | 1.83 s | 269 | 24 ms |
| 18.90 – 20.85 | 1.95 s | 293 | 35 ms |

Uninterrupted ~85 Hz cadence, and the contact was released normally at the end. **An established
contact is not suppressed.** (An accidental brush produced eight brief contacts at t = 11.49–12.77;
they end before the first proximity window and do not affect the above.)

**Session 1 (2026-05-07, `touch-pen.log`).** A finger resting down *during* a pen stroke produced zero
events, and the stream was demonstrably alive because unrelated touches resumed afterwards. So **new
contacts are suppressed** while the pen is in proximity.

**Session 2 (2026-07-25, `hw2` captures) — inconclusive, and an earlier reading of it was wrong.** A
motionless fingertip's contact was never released and the touch stream fell silent at t = 42.53, which
was first written up here as "the firmware abandons live contacts". Session 3 contradicts that. The
silence is equally explained by the capture's SSH stream ending there, and a motionless contact
generates no events anyway because the panel reports on change. Treat that session as evidence of
nothing.

### What this means for the host

- **Host-side palm rejection is load-bearing**, for the most common case rather than an exotic one: a
  hand already resting on the panel when a stroke begins keeps injecting touch for the entire stroke,
  because firmware only blocks contacts that *start* during proximity. `PenProximityGate` exists for
  exactly this.
- **The stale-contact sweep is precaution, not a fix for observed behavior.** No contact was ever
  abandoned across four sessions. It stays because it costs one timer and the failure it prevents — a
  permanently stuck touch-down holding an output slot — is severe and silent.
- "Simultaneous draw + pinch is impossible" is still true for *starting* a gesture mid-stroke, but not
  because touch stops entirely: an already-down finger keeps being tracked.

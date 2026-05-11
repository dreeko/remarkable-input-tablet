# Feasibility — reMarkable Paper Pro (rMPP) support

**Date:** 2026-05-11
**Verdict:** Feasible. The architecture of this tool (SSH → evdev stream → state machine → host-side pointer injection) carries over cleanly. The pen, touchscreen, and root SSH all exist on the rMPP. What changes is mostly numbers — a different CPU bitness, a different display size, different axis ranges — and the community has already mapped most of those numbers in `Evidlo/remarkable_mouse`'s `rmpro` branch (see §2 below), so empirical bring-up is verification, not discovery.

No host-side change is needed (Win32 / uinput injection is device-agnostic). All the work lives in `RemarkableTablet.Core`, gated behind a device-profile abstraction.

The polish work that usually dominates a port — reconnect with exponential backoff, pre-reconnect synthetic pen-up / touch-release, pump-task teardown ordering, bounded channels with `DropOldest` backpressure, real touch contact injection on both platforms, pressure-curve shaping — is **already in place** for rM2 and is not pen-or-tablet-specific. The rMPP scope is essentially: parameterize the three things that are currently hard-coded to rM2 (event-struct size, device-node paths, axis ranges) plus one small uinput correctness fix (§3.4) that improves rM2 as well.

## Prior art (peer projects from `reHackable/awesome-reMarkable`)

| Project | License | Relevance |
|---|---|---|
| [`Evidlo/remarkable_mouse`](https://github.com/Evidlo/remarkable_mouse) | GPL-3.0 | **Highest value.** The `rmpro` branch (Issue #92) parameterises event-struct size per model and has captured rMPP pen/touch device paths and axis ranges. We use these as a starting baseline. |
| [`DCS-87/remarkable_mouse_winpress`](https://github.com/DCS-87/remarkable_mouse_winpress) | GPL-3.0 | Our README's named predecessor. The Windows Ink injection call sequence — `CreateSyntheticPointerDevice(PT_PEN,1,1)` + `InjectSyntheticPointerInput`, with `PEN_MASK_PRESSURE\|TILT_X\|TILT_Y` — matches what we already do. No rMPP support. |
| [`FreeCap23/reMarkable-tablet-driver`](https://github.com/FreeCap23/reMarkable-tablet-driver) | GPL-2.0, archived 2026 | C/`libssh` rewrite of `LinusCDE/rmTabletDriver`. Hard-codes 16-byte event struct — useful as a counterexample of what *not* to do, plus a clean uinput axis/resolution tuple for the rM2 baseline. |
| [`asivery/rm-appload`](https://github.com/asivery/rm-appload) + XOVI | GPL-3.0 | Architecturally different — runs **on the device**. A v2 alternative if SSH-based streaming becomes maintenance-painful (see §7). |

**License implication:** the most useful peer projects are GPL-3.0; this project is MIT. We **cannot lift code**. But constants (axis ranges, device paths, struct layouts) and documented Windows API call sequences are facts, not copyrightable expression. We re-implement using these facts as a guide.

---

## 1. What's the same

| Concern | rM2 | rMPP | Impact |
|---|---|---|---|
| OS | Codex Linux | Codex Linux | None |
| USB-Ethernet address | `10.11.99.1` | `10.11.99.1` | None |
| Root SSH | Yes, password from Settings → Help → Copyrights | Yes, but requires **Developer Mode** toggled on first (Settings → General → Paper Tablet → Software → Advanced) | Doc change + UX hint in the GUI |
| Input subsystem | Linux evdev, `/dev/input/eventN` | Linux evdev | None — same `EV_ABS` / `EV_KEY` / `EV_SYN` semantics |
| Pen reports | Pressure, tilt, distance, hover, eraser, barrel buttons | Same set advertised (12-bit / 4096 pressure levels confirmed) | None at the state-machine level |
| Touchscreen protocol | MT-B (slots + `ABS_MT_TRACKING_ID`) | MT-B (community pattern; unverified on rMPP) | Likely none |
| Pipeline shape | SSH stream → parser → SM → mapper → output | Same | Reuse `RemarkableTablet.Core` end-to-end |

`RemarkableTablet.Windows` and `RemarkableTablet.Linux` need **zero changes** — they inject pointer/touch events to the host OS and do not care which tablet produced them.

---

## 2. What's different — the breaking changes

### 2.1 CPU is 64-bit (the single biggest landmine)

rM2 is armv7l (32-bit, i.MX7D). rMPP is aarch64 (NXP i.MX 8MM "ferrari", Cortex-A53 ×4, kernel `linux-imx-rm` built `ARCH=arm64`).

**Consequence:** `struct input_event` is **24 bytes** on 64-bit Linux, not 16. The layout becomes:

```
[0..7]   long  tv_sec       (was uint32)
[8..15]  long  tv_usec      (was uint32)
[16..17] uint16 type
[18..19] uint16 code
[20..23] int32  value
```

This breaks **every** assumption in `EvdevParser` and `ReMarkable2Constants.EventStructSize`. `src/RemarkableTablet.Core/Evdev/EvdevParser.cs:21` hard-codes `EventSize = 16` and reads `type`/`code`/`value` from offsets 8/10/12. None of those offsets are right on the rMPP.

If you stream a 64-bit event log through the rM2 parser, every frame is interpreted as garbage — but it doesn't crash; it just produces nonsensical absolute coordinates and pressure. A reader expecting 16-byte frames will desync silently and look like "the pen jitters wildly."

This is **empirically confirmed** in `Evidlo/remarkable_mouse` Issue #92: users running the rM2 codepath on rMPP report `KeyError: 61892` from the evdev decoder, which is exactly what happens when 16-byte slicing misaligns over a 24-byte stream — high bytes of the next frame's seconds field land in the `code` slot. Evidlo's `rmpro` branch fixes it by switching the unpack format from `'2IHHi'` (16 B) to `'I4xI4xHHi'` (24 B with pad-skip). Our equivalent fix is to make `EvdevParser.EventSize` and its field offsets parameters of the `DeviceProfile`.

### 2.2 Pen technology changed — community-mapped, not yet locally verified

The rMPP's "Marker Plus" is an **active, battery-powered** stylus (inductively charged). It is **not** Wacom EMR — old reMarkable / LAMY EMR pens do not work on the rMPP.

Constants from Evidlo's `rmpro` branch (`remarkable_mouse/codes.py`, captured by the maintainer with community help via Issue #92):

| | rM2 | rMPP |
|---|---|---|
| Pen device path | `/dev/input/event1` | **`/dev/input/event2`** |
| Touch device path | `/dev/input/event2` | **`/dev/input/event3`** |
| Buttons device path | n/a in current pipeline | `/dev/input/event0` (power) + `event1` (pen attach/detach) |
| ABS_X range | 0 – 20966 | **0 – 11180**, resolution 2832 |
| ABS_Y range | 0 – 15725 | **0 – 15340**, resolution 2064 |
| Pen pressure | 0 – 4095 | **0 – 4096** (off-by-one) |

The very different resolution values on rMPP (`2832` / `2064` vs. rM2's `100`) imply the axes are reported in higher-density units; the geometry rotation that bit us on rM2 must be re-verified empirically. **Tilt range and distance range are not in the public `rmpro` constants** — these still need an on-device `evtest` dump.

A robust device-node discovery should not hard-code these paths. Evidlo's trick — `readlink -f /dev/input/touchscreen0` over SSH — works on rM1, rM2, and rMPP and survives firmware reshuffles. The pen and button nodes can be resolved similarly by walking `/proc/bus/input/devices` and matching by `Name=` line.

### 2.3 Display geometry

| | rM2 | rMPP |
|---|---|---|
| Display | 1404 × 1872 mono | **1620 × 2160** color (E Ink Gallery 3) |
| Aspect | 3:4 | 3:4 (same) |
| PPI | 226 | 229 |

The touchscreen coordinate range will almost certainly match the display (rM2 `pt_mt` does), so `TouchXMax`/`TouchYMax` must move. Pen-digitizer coordinates may or may not align to display pixels — rM2's pen uses its own much larger raw range (20966 × 15725), independent of display.

### 2.4 Pen-suppression behavior

Whether the rMPP suppresses touch while pen is in proximity (the rM2 hardware-level behavior the README and memory call out) is **unverified**. reMarkable markets "palm rejection" but doesn't specify mechanism. If the rMPP filters palm in software *but still emits touch events when the pen is hovering*, the touch pipeline needs a host-side `PenToolGate` after all — exactly the thing we deleted as unnecessary for the rM2.

### 2.5 Security posture (informational)

- Disk encryption stays on even in Developer Mode.
- Secure boot enforces signed bootloader/kernel/rootfs. You cannot drop in custom kernel modules without reMarkable's signing key.
- New overlay filesystem layout in OS 3.x.

None of this blocks an input-streaming tool — we only `cat` files we don't own — but it kills any future idea that would have required custom kernel modules or rootfs writes.

---

## 3. Required code changes

Everything fits in `RemarkableTablet.Core`. Estimated scope below.

### 3.1 Introduce a `DeviceProfile` abstraction (mandatory)

Replace `ReMarkable2Constants` static class with a `DeviceProfile` record carrying:

- Event struct size (16 vs. 24)
- Field offsets for type/code/value (8/10/12 vs. 16/18/20)
- Pen device path, touch device path
- Pen X/Y/pressure/tilt/distance ranges
- Touch X/Y/pressure ranges, max slots
- Display dimensions (for touch mapping)
- Optional pen-priority quirk flag

Two concrete profiles: `ReMarkable2Profile`, `ReMarkablePaperProProfile`. Selection by:

1. CLI flag `--device rm2|rmpp` (explicit)
2. Auto-detect by SSH'ing `uname -m` and `cat /proc/bus/input/devices` once at connect time (preferred — zero user friction)

**Files that change:**
- `src/RemarkableTablet.Core/Tablet/ReMarkable2Constants.cs` → split into profile + interface (rename or keep as `RM2` profile impl)
- `src/RemarkableTablet.Core/Evdev/EvdevParser.cs` — accept event size + offsets from the profile, drop the `const int EventSize = 16`
- `src/RemarkableTablet.Core/Mapping/CoordinateMapper.cs` — read `PenXMax`/`PenYMax` from profile instead of `ReMarkable2Constants`
- `src/RemarkableTablet.Core/Mapping/TouchCoordinateMapper.cs` — same for touch
- `src/RemarkableTablet.Core/Pipeline/TabletPipeline.cs` — plumb the profile through
- `src/RemarkableTablet.Core/Transport/SshTransport.cs:70` — device path lookup via profile instead of `ReMarkable2Constants.PenDevicePath`
- CLI flag in `src/RemarkableTablet.Cli/Program.cs`
- Settings dropdown in `src/RemarkableTablet.App/SettingsWindow.xaml.cs` + persistence in `AppSettings.cs`

### 3.2 Variable-size evdev parsing

`EvdevParser.RunAsync` currently slices `EventSize` (= 16) at a time. Make `EventSize` a parameter and adjust the three `BinaryPrimitives.Read*` offsets accordingly. Add a unit test that feeds a hand-crafted 24-byte aarch64 frame and asserts the decoded `(type, code, value)`.

### 3.4 Set `input_absinfo.resolution` on the uinput pen device (rM2 fix, applies to rMPP too)

`UinputOutput.SetAxis` (`src/RemarkableTablet.Linux/Output/UinputOutput.cs:63-67`) currently passes `min, max, fuzz, flat` only — `input_absinfo.resolution` defaults to 0. `FreeCap23/reMarkable-tablet-driver` exists as a fork of `LinusCDE/rmTabletDriver` specifically to set `resolution = 100` on ABS_X/Y so `libinput` (and therefore every Wayland compositor) recognizes the virtual device as a tablet rather than a generic absolute-axis device.

Fix: add a `resolution` parameter to `SetAxis`, extend `uinput_abs_setup` / `input_absinfo` interop to include the field, and populate it from the device profile (rM2 = 100 per FreeCap23; rMPP would use the per-axis values from Evidlo's `rmpro` constants — 2832 on X, 2064 on Y). This is a one-line interop change plus profile data; it benefits rM2 Wayland users immediately and is required for rMPP from day one.

### 3.5 Auto-detection helper (optional but worth the small cost)

A one-shot SSH command on connect:

```sh
uname -m && ls /dev/input/ && cat /proc/bus/input/devices
```

`armv7l` → rM2 profile. `aarch64` → rMPP profile, then walk `/proc/bus/input/devices` to map the pen and touch device nodes by driver name rather than hard-coding `event1`/`event2` (rMPP node indices are not guaranteed to match rM2's).

This also future-proofs against firmware updates that reshuffle device-node ordering on either device.

### 3.4 Documentation

- README quickstart: note Developer Mode requirement on rMPP, link to reMarkable's official article.
- New hardware-details section for rMPP mirroring the existing rM2 one, populated from the on-device `evtest` capture.
- `tools/EventDiagnostics` already does what we need to capture that data — just point it at the rMPP.

---

## 4. Empirical work required before coding

Most baseline numbers exist in Evidlo's `rmpro` branch (§2.2). The remaining work on real hardware is verification + filling tilt/distance gaps:

1. **Enable Developer Mode**, obtain root password, confirm SSH at `10.11.99.1`.
2. **Run `evtest` on `/dev/input/event2` (pen) and `event3` (touch)**. Save dumps in `tools/EventDiagnostics/samples/rmpp/`. Cross-check axis ranges against the Evidlo constants in §2.2 — any divergence means our profile values need adjustment, or there are firmware variants to handle.
3. **Capture tilt + distance ranges** for the rMPP pen — not in the community data. Tilt the pen end-to-end across both axes while `evtest` is running; the absmin/absmax reported by `evtest -i` in the device header is what we want.
4. **Confirm event-struct size** with `dd if=/dev/input/event2 bs=24 count=1 | xxd` — the first 8 bytes should be a plausible Unix timestamp (`tv_sec` as a 64-bit little-endian value). If those bytes look like a timestamp the layout is 24-byte; if they look like one timestamp followed by another value, layout differs.
5. **Test pen-suppression**: stream pen + touch simultaneously, hover the pen, watch whether touch events keep flowing. This determines whether we need to bring back the deleted host-side `PenToolGate`.
6. **Confirm touchscreen orientation** matches expectations using the same "touch panel is `INPUT_PROP_DIRECT` ground truth" method we used on rM2.

Steps 2–6 together are ~30 minutes of hands-on work.

---

## 5. Effort estimate

Assuming step-4 hardware data in hand:

| Work | Estimate |
|---|---|
| `DeviceProfile` abstraction + plumb-through | 0.5 day |
| Variable-size evdev parsing + unit tests | 0.5 day |
| `input_absinfo.resolution` field + per-profile values | 0.25 day |
| Auto-detection by `uname -m` + `/proc/bus/input/devices` walk | 0.5 day |
| CLI flag + GUI dropdown + settings persistence | 0.5 day |
| README + rMPP hardware-details doc | 0.5 day |
| Real-hardware bring-up, calibration, regression-test rM2 | 1 day |
| **Total** | **~3.75 days** |

No work in `RemarkableTablet.Windows` or `RemarkableTablet.Linux`. No new dependencies.

---

## 6. Risks

- **Touchscreen coordinate orientation may differ from rM2.** The rM2 axis-rotation bug was a 2-day debug; budget contingency for rediscovering the same on rMPP. `tools/EventDiagnostics` plus the already-validated touchscreen-is-ground-truth method (the touch panel is `INPUT_PROP_DIRECT`, so its mapping is empirically known) makes this tractable.
- **Pen-suppression might not hold.** If the rMPP emits touch while the pen hovers, we need a host-side `PenToolGate` (deleted on rM2 as unnecessary). Implementable but adds a day.
- **Pen barrel-button / eraser semantics.** The Marker Plus has different physical inputs than the rM2 Marker Plus. The state machine handles `BTN_TOOL_PEN/RUBBER/STYLUS/STYLUS2` — if the rMPP uses a different code (e.g. `BTN_TOOL_PENCIL`), trivial to add, but worth checking in the `evtest` capture.
- **Firmware drift.** reMarkable has changed axis conventions across firmware versions on the rM2 already. Hard-coded ranges per device profile are a maintenance trap; using values pulled from `/proc/bus/input/devices` at connect time would be more robust but is meaningfully more code. Profile-with-constants is the right tradeoff for v1; add runtime discovery later if it bites.

---

## 7. Recommendation

Proceed in two phases:

**Phase 0 (no code):** Acquire / borrow a Paper Pro, run the six empirical steps above. Most numbers are already drafted in §2.2 from community work; bring-up reduces to confirming them, capturing tilt/distance, and validating orientation.

**Phase 1 (the 3.5-day build):** `DeviceProfile` abstraction + variable-size parser + auto-detection + UX, regression-test rM2, ship a v0.4.0.

**Future option — on-device daemon (v2 architecture).** `asivery/rm-appload` + XOVI on the rMPP runs arbitrary userland code, including a Unix-socket / TCP sender that reads `/dev/input/event*` natively and ships a fixed normalised wire format to the host. This sidesteps both the `sizeof(time_t)` ambiguity (the daemon, compiled for the device, sees the real `struct input_event` natively) and SSH handshake latency, and gets cleaner reconnect via TCP keepalive. Downsides: sideloading via XOVI is a moving target each firmware update, and we'd be coupled to asivery's toolchain. Right call is to keep SSH as primary, prototype on-device only if SSH overhead or maintenance pain shows up. Don't pre-build it.

The codebase is already factored well for this — `ReMarkable2Constants` is the only place hardware specifics live, the host outputs are device-agnostic, and `tools/EventDiagnostics` is exactly the bring-up tool we need. The single highest-leverage change is making the evdev parser size-parameterized; everything else is straight mechanical work behind it.

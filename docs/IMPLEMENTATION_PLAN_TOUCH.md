# Implementation Plan — Touchscreen Gestures (Pinch / Zoom / Pan / Rotate)

**Goal:** Add support for the rM2 capacitive touchscreen so users can pinch-zoom, two-finger pan, and two-finger rotate on the host PC, on both Windows and Linux, alongside existing pen input.

**Companion doc:** `docs/FEASIBILITY_PINCH_PAN.md` (the feasibility analysis this plan operationalizes).

**Non-goals (this iteration):**
- Three+ finger gestures (swipe, four-finger task switch).
- Per-app gesture profiles.
- Custom user-bindable gestures.
- Touchpad-style cursor mode (touch ≠ mouse).

---

## 1. Architectural overview

### 1.1 Pipeline today

```
SshTransport (event1)
  └─ EvdevParser → Channel<EvdevEvent>
       └─ TabletStateMachine → Channel<PenFrame>
            └─ CoordinateMapper → MappedFrame
                 └─ IOutputMode (WindowsInk | Mouse | Uinput)
```

### 1.2 Pipeline after this work

```
SshTransport (multiplexed)
  ├─ event1 → EvdevParser → Channel<EvdevEvent> ──► PenStateMachine ──► Channel<PenFrame> ──► PenMapper ──► IPenOutput
  └─ event2 → EvdevParser → Channel<EvdevEvent> ──► TouchStateMachine ──► Channel<TouchFrame>
                                                          │
                                                          ▼
                                                    GestureEngine  (recognises pinch / pan / rotate)
                                                          │
                                                          ▼
                                                    ITouchOutput
                                                          ├─ WindowsTouchInjectionOutput   (Win32 InjectTouchInput)
                                                          ├─ LinuxUinputTouchOutput        (uinput MT-B device)
                                                          └─ SynthesizedScrollOutput       (Ctrl+wheel / wheel / middle-drag)

PenToolGate (cross-stream coordination — palm rejection, optional pen-priority)
```

Two evdev streams over a single SSH client. Two parallel state machines feeding two parallel output sinks. A small **PenToolGate** is the only place the two streams share state (so that, e.g., touch is suppressed while the pen is in range, *if* the user opts in).

### 1.3 Output strategy — committed decisions

| Decision | Choice | Rationale |
|---|---|---|
| Default Windows output | Real touch injection (`InitializeTouchInjection` + `InjectTouchInput`) | App sees real contacts; OS/app does gesture recognition. Highest fidelity. |
| Default Linux output | uinput MT-B touch device with `INPUT_PROP_DIRECT` | Standard kernel pattern; sibling to existing `UinputOutput`. |
| Fallback both platforms | Host-side gesture recognizer → synthesized `Ctrl+MouseWheel` (zoom), wheel (pan), middle-drag (pan), no rotate | Works in apps that don't accept injected touch. |
| User selection | New flag `--gestures <touch\|synth\|off>` (default `touch`) | Lets user pick when an app misbehaves. |
| Rotate | **Only in `touch` mode.** | No universal desktop shortcut for canvas rotate. |
| Pen / touch interaction | **Firmware already suppresses touch when pen is in proximity** (verified 2026-05-07 — see `tools/EventDiagnostics/samples/README.md`). `PenToolGate` is therefore demoted from required to optional defense-in-depth, defaulting to `coexist` (no host-side gating). The two non-default modes (`pen-priority`, `palm-reject`) are kept as escape valves but should rarely be needed. |

### 1.4 Transport strategy — committed decision

**Single `SshClient`, two `SshCommand` channels.** Reasons:
- One auth handshake, one TCP connection, one reconnect loop to surface in the GUI.
- SSH.NET supports multiple concurrent commands on a single client (each is a separate channel under the SSH session).
- The lifecycle bug already solved in `SshTransport.CleanupConnectionAsync` (dispose `_command` first to unblock the blocking read) generalizes cleanly to multiple commands.

We refactor `SshTransport` into a thin owner of the `SshClient` and a per-stream `SshDeviceStream` that wraps one `SshCommand` + one `Pipe` + one pump task.

---

## 2. Phase 0 — On-device verification (gate)

**No code lands before this completes.** Outcomes feed directly into Phase 1 constants.

### 2.1 Tasks

1. SSH into the rM2: `ssh root@10.11.99.1`.
2. `evtest /dev/input/event2` — capture full output for:
   - Header (device name, vendor/product, axes, MT slot count, resolution).
   - Single-finger tap: full sequence including `ABS_MT_SLOT`, `ABS_MT_TRACKING_ID`, `ABS_MT_POSITION_X/Y`, `BTN_TOUCH`, `SYN_REPORT`.
   - Two-finger pinch: confirm two slots fire concurrently, capture sample rate.
   - Two-finger rotate: same.
   - Pen hovering above touchscreen (no touch contact): does `event2` go silent? **This is the dominant unknown.**
   - Pen drawing while finger rests on screen: same question.
3. Run `cat /dev/input/event2 | wc -c` for ~5 s during active touch — validates struct size (should be `16 × event_count`; if not, our 16-byte assumption is wrong for this device).
4. Confirm `/dev/input/event2` is the touch device (could be `event3` or differently numbered on alternate firmware).

### 2.2 Deliverable

A captured-evtest log committed to `tools/EventDiagnostics/samples/event2-touch.log` plus an updated `ReMarkable2Constants.cs` section adding:

```csharp
// Touchscreen — verified 2026-MM-DD via evtest /dev/input/event2
public const string TouchDevicePath = "/dev/input/event2";
public const int TouchXMin = 0;
public const int TouchXMax = ???;        // from evtest
public const int TouchYMin = 0;
public const int TouchYMax = ???;        // from evtest
public const int TouchMaxContacts = ???; // from evtest (typical: 5–10)
public const int TouchPressureMax = ???; // 0 if not reported
```

### 2.3 Decision gate

After Phase 0, lock answers to:
- Touch device path (variable in code, not assumed).
- Coordinate range (different from pen).
- Whether touch fires while pen is in proximity. **If suppressed by firmware, `PenToolGate` defaults are simpler — no host-side suppression needed.** If concurrent, host-side gating is mandatory.

---

## 3. Phase 1 — Core: types, parser, state machine

All work in `src/RemarkableTablet.Core/`.

### 3.1 Evdev MT codes

Extend `EvdevCodes.cs`:

```csharp
// ABS multi-touch slot protocol (Type B)
public const ushort ABS_MT_SLOT          = 47;
public const ushort ABS_MT_POSITION_X    = 53;
public const ushort ABS_MT_POSITION_Y    = 54;
public const ushort ABS_MT_TRACKING_ID   = 57;
public const ushort ABS_MT_PRESSURE      = 58;   // optional; presence verified in Phase 0
public const ushort ABS_MT_TOUCH_MAJOR   = 48;   // optional
```

`EvdevParser` and `EvdevTypes` are already protocol-agnostic — no change.

### 3.2 Touch frame & state machine

New files in `src/RemarkableTablet.Core/Tablet/`:

- `TouchContact.cs` — record `(int Slot, int TrackingId, int X, int Y, int Pressure, bool Active)`.
- `TouchFrame.cs` — record `(IReadOnlyList<TouchContact> Contacts, long FrameTicks)`.
- `TouchStateMachine.cs` — mirrors `TabletStateMachine.cs`. Maintains an array of slots (size = `TouchMaxContacts`). On each evdev event:
  - `ABS_MT_SLOT n` → set `_currentSlot = n`.
  - `ABS_MT_TRACKING_ID -1` → release `_slots[_currentSlot]`.
  - `ABS_MT_TRACKING_ID >= 0` → start tracking in `_slots[_currentSlot]`.
  - `ABS_MT_POSITION_X/Y` → update slot.
  - `SYN_REPORT` → emit immutable `TouchFrame` of currently-active slots.

Tests in `tests/RemarkableTablet.Core.Tests/TouchStateMachineTests.cs`: synthetic byte streams covering single tap, two-finger touch, slot release, mid-frame `SYN_DROPPED` recovery.

### 3.3 Coordinate mapping

`TouchCoordinateMapper.cs` — separate class (not generalized over pen) because:
- Coordinate range differs (touch ≈ display-aligned, pen ≈ digitizer-aligned).
- Pressure mapping not needed.
- Output is geometry, not a single frame.

Reuses the orientation rotation block from `CoordinateMapper.cs` (extract a small `RotationTransform.Apply(double nx, double ny, Orientation)` helper to share). Each contact in a `TouchFrame` is mapped independently to screen pixels.

### 3.4 Gesture engine

New file: `src/RemarkableTablet.Core/Gestures/GestureEngine.cs`.

**Responsibility:** consume `TouchFrame`s, produce semantic `GestureEvent`s for the synthesized-output fallback. Real-touch-injection bypasses this entirely (the OS/app does recognition).

**Output events:**

```csharp
public abstract record GestureEvent;
public record GestureBegin(int CenterX, int CenterY) : GestureEvent;
public record GesturePinch(double ScaleDelta) : GestureEvent;     // 1.0 = no change
public record GesturePan(int DeltaX, int DeltaY) : GestureEvent;  // pixels, screen space
public record GestureRotate(double DegreesDelta) : GestureEvent;  // signed
public record GestureEnd : GestureEvent;
```

**Recognizer (two-finger only, this iteration):**
- Begin when contact count transitions 0/1 → 2.
- Each frame, compute centroid and the vector between the two contacts:
  - `pan = centroid_now - centroid_prev`
  - `pinch = distance_now / distance_prev` (multiplicative)
  - `rotate = angle_now - angle_prev` (degrees, normalized to ±180)
- Emit a single combined event (or split, depending on dominant component) per frame.
- End when contact count drops below 2.
- Hysteresis / dead-zones to avoid jitter — start values picked from analogous touchpad recognizers, tuned in Phase 5.

Tests: synthetic `TouchFrame` sequences covering pure pinch, pure pan, pure rotate, mixed; assert the emitted gesture stream.

---

## 4. Phase 2 — Transport: dual-stream SSH

All work in `src/RemarkableTablet.Core/Transport/`.

### 4.1 Refactor `SshTransport`

Split into:

- `SshTransport` — owns the `SshClient`, exposes `OpenStream(devicePath)` returning an `SshDeviceStream`. Owns reconnect logic.
- `SshDeviceStream` — wraps one `SshCommand`, one `Pipe`, one pump task; exposes a `PipeReader`. New file.

**Lifecycle (critical — generalization of the bug fix already in `CleanupConnectionAsync`):**
- Cleanup order: dispose **all** `SshCommand`s first (this closes their `OutputStream`s and unblocks the blocking `Read` in each pump), then disconnect the client, then await all pump tasks.
- Reconnect = teardown all streams + reconnect client + re-open all streams. Single source of truth for "connection up/down."

### 4.2 Update `TabletPipeline`

`TabletPipeline` becomes parameterized by a list of stream descriptors and per-stream consumers, or — simpler — a fixed pen + touch pair:

```csharp
public sealed class TabletPipeline(
    SshTransport transport,
    PenStageWiring pen,
    TouchStageWiring? touch,   // null = touch disabled
    PenToolGate gate)
```

`RunOnceAsync` opens both streams, spawns parsers + state machines + output loops in parallel under one `Task.WhenAll`, and the first failure cancels the lot. This already mirrors the existing `Task.WhenAll(parser, state, output)` pattern.

### 4.3 PenToolGate

```csharp
public sealed class PenToolGate
{
    public PenToolGateMode Mode { get; init; }   // Coexist | PenPriority | PalmReject
    public bool ShouldSuppressTouch(PenSnapshot pen) => Mode switch { ... };
}
```

Pen pipeline writes its latest snapshot (in-range, in-contact) into the gate after each frame; touch output checks the gate before emitting.

---

## 5. Phase 3 — Output: per-platform sinks

### 5.1 Windows — real touch injection

New file: `src/RemarkableTablet.Windows/Output/WindowsTouchInjectionOutput.cs`.

**Win32 surface (P/Invoke into `user32.dll`):**
- `InitializeTouchInjection(uint maxCount, uint dwMode)` — call once on `Initialize()` with `maxCount = TouchMaxContacts` and `TOUCH_FEEDBACK_DEFAULT`.
- `InjectTouchInput(uint count, POINTER_TOUCH_INFO[] info)` — called per `TouchFrame`.

**`POINTER_TOUCH_INFO` layout:** depends on `POINTER_INFO` which already has the same `[FieldOffset(8)]` 64-bit-alignment quirk noted in the existing project memory for `POINTER_TYPE_INFO` — apply the same fix here.

**Contact lifecycle (the hard part):**
- Each contact has a stable `pointerId` (we use the slot index 0–9 plus a process-wide base).
- First frame per contact: flag `POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT`.
- Subsequent frames: `POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT`.
- Released contact: `POINTER_FLAG_UP` (one frame), then drop.
- All currently-active contacts must be in every `InjectTouchInput` call until released — Windows requires the full set, not deltas.

Add `tests/RemarkableTablet.Windows.Tests/WindowsTouchInjectionOutputTests.cs` exercising contact-state transitions against a fake P/Invoke wrapper.

### 5.2 Linux — uinput MT-B touch device

New file: `src/RemarkableTablet.Linux/Output/UinputTouchOutput.cs`. Mirror structure of `UinputOutput.cs` but configure a touch device:

- `EVIOC_SET_ABSBIT` for `ABS_MT_SLOT`, `ABS_MT_POSITION_X/Y`, `ABS_MT_TRACKING_ID`, `ABS_MT_PRESSURE`.
- `EVIOC_SET_PROPBIT(INPUT_PROP_DIRECT)`.
- `UI_SET_KEYBIT` for `BTN_TOUCH`.
- Per-frame: write slot updates + tracking IDs + positions + `SYN_REPORT`.

Reuses the `Libc` P/Invoke surface that already exists.

### 5.3 Synthesized scroll/keys (both OSes)

New file: `src/RemarkableTablet.Core/Output/SynthesizedScrollOutput.cs` (lives in Core because the gesture-to-input mapping is OS-agnostic; the actual injection delegates to `IInputBackend`).

Backend per OS:
- **Windows:** extend `MouseOutput` (or new sibling) with `SendInput`-based wheel + key-down/up for `VK_CONTROL`.
- **Linux:** new uinput relative-mouse device with `REL_WHEEL`, `REL_X/Y`, `BTN_MIDDLE`, plus `KEY_LEFTCTRL`.

Mapping:
- `GesturePinch(scaleDelta)` → press Ctrl, emit wheel ticks proportional to `log(scaleDelta)`, release Ctrl when gesture ends.
- `GesturePan(dx, dy)` → either two-direction wheel (most apps) or middle-mouse-button drag (configurable; some apps prefer one over the other).
- `GestureRotate` → ignored (no universal mapping; see §1.3).

### 5.4 Dispatching

New `ITouchOutput` interface (parallel to `IOutputMode`, kept distinct because the data shape differs). The pipeline picks the implementation based on `--gestures`:

| `--gestures` value | Touch output |
|---|---|
| `touch` (default) | `WindowsTouchInjectionOutput` / `UinputTouchOutput` |
| `synth` | `SynthesizedScrollOutput` (driven by `GestureEngine` output) |
| `off` | none — `event2` not opened |

---

## 6. Phase 4 — Pipeline integration

### 6.1 `TabletPipeline` wiring

Update `RunOnceAsync` to run, in parallel under one `Task.WhenAll`:

```
PenStream pump
TouchStream pump (skipped if --gestures off)
Pen EvdevParser → PenStateMachine → CoordinateMapper → IPenOutput
Touch EvdevParser → TouchStateMachine → (TouchCoordinateMapper → ITouchOutput)
                                         OR (GestureEngine → ITouchOutput) for synth
PenToolGate update from PenStateMachine
```

Cancellation propagates the same way it does today.

### 6.2 Reconnection

`TabletPipeline` already emits a synthetic pen-up before each reconnect. Add: on reconnect, emit a synthetic "all contacts released" via `ITouchOutput` to avoid stuck contacts. Same code path triggered by the same lifecycle event.

### 6.3 Disposal ordering

`TabletPipeline.DisposeAsync`:
1. Cancel CTS.
2. Dispose `SshTransport` (closes both streams; pump tasks exit).
3. Dispose `IPenOutput`, `ITouchOutput`.
4. Dispose CTS.

---

## 7. Phase 5 — User-facing surface

### 7.1 CLI

Add to `src/RemarkableTablet.Cli/Program.cs`:

```
--gestures <touch|synth|off>     default: touch
--pen-priority <coexist|pen-priority|palm-reject>   default: pen-priority
--touch-area <x,y,w,h>           reserved for later; no-op for now
```

Refresh `--help` and the README "CLI options" table.

### 7.2 GUI app (Windows-only)

In `src/RemarkableTablet.App/SettingsWindow.xaml`:
- Group box "Touch gestures":
  - Mode dropdown (Touch injection / Scroll fallback / Off).
  - Pen interaction dropdown (Coexist / Pen priority / Palm reject only).
- Persisted in `AppSettings` JSON.
- Tray icon menu shows current gesture mode at a glance.

### 7.3 Documentation

- README: new "Touch gestures" section explaining what works in which apps + the `--gestures` flag.
- README: app-compatibility table updated. Common cases: Krita / Photoshop / Affinity Photo / browsers / Inkscape — note pinch-zoom expected to work in `touch` mode; add results from on-device testing.
- README: explicit caveat that touch may be suppressed during pen use depending on Phase 0 findings.

---

## 8. Testing strategy

### 8.1 Unit tests (deterministic, no hardware)

| Component | Test cases |
|---|---|
| `TouchStateMachine` | Single-tap, two-finger touch, slot release via `TRACKING_ID = -1`, mid-frame `SYN_DROPPED`, slot reuse with new tracking ID. |
| `TouchCoordinateMapper` | All four orientations, edge contacts (corners), aspect-ratio mismatches with screen. |
| `GestureEngine` | Pure pinch (in & out), pure pan, pure rotate, mixed, dead-zone respected, gesture begins/ends correctly on contact-count transitions, two-finger-down-then-third-finger-down does not break the active gesture. |
| `WindowsTouchInjectionOutput` | Contact-state transitions emit correct `POINTER_FLAG_*` sequences (against a fake injection backend). |
| `UinputTouchOutput` | Correct evdev byte stream emitted (against an in-memory `Libc` fake). |
| `SynthesizedScrollOutput` | Pinch scale delta → wheel-tick count, Ctrl pressed only during pinch, pan dx/dy → wheel direction. |
| `PenToolGate` | All three modes correctly gate touch output. |
| `SshDeviceStream` lifecycle | Two streams open/close cleanly under cancellation; reconnect fires both stream-down then both stream-up; no deadlock when blocked `Read` is interrupted by command dispose. |

### 8.2 Integration tests (with hardware)

Manual test matrix run before tagging the release:

| App | Pinch zoom | Pan | Rotate | Notes |
|---|---|---|---|---|
| Krita | ✓ | ✓ | ✓ | Real touch + Windows Ink simultaneously |
| Photoshop | ✓ | ✓ | ✓ | |
| Affinity Photo | ✓ | ✓ | ✓ | |
| Clip Studio Paint | ✓ | ✓ | ✓ | |
| Microsoft Edge / Chrome | ✓ | ✓ | n/a | Browser pinch-zoom of pages |
| Windows Photos | ✓ | ✓ | n/a | Windows-native |
| GIMP (Linux) | ✓ | ✓ | ✓ | |
| Inkscape (Linux) | ✓ | ✓ | n/a | |
| Blender (Linux) | ✓ | ✓ | n/a | |

For each app, also test in `synth` mode (expected: pinch + pan work, rotate doesn't).

### 8.3 Regression tests

Existing pen tests must continue to pass unchanged. The pen pipeline shape must not regress (no extra latency, no dropped pen frames when touch is also active).

---

## 9. Risk register & mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| `event2` is suppressed while pen is in proximity (hardware behavior). | **Confirmed 2026-05-07** | Forces "lift pen → pinch" UX; can't draw + pinch simultaneously. | Documented in README; not a code problem. Removes the need for a load-bearing `PenToolGate`. |
| SSH.NET multi-channel concurrency has unexpected limits. | Low | Have to fall back to two `SshClient`s. | Spike during Phase 2 day 1; if multi-channel works, proceed; else swap. |
| Touch sample rate over Wi-Fi causes jitter. | Medium | Pinch feels notchy on Wi-Fi. | Measure during Phase 0; document USB recommendation; consider a small jitter buffer in `TouchStateMachine` if needed. |
| `InjectTouchInput` works in Windows but specific app rejects synthetic touch. | Medium | App-by-app inconsistency. | `--gestures synth` fallback gives users an out; document in README compat table. |
| Stuck contacts on disconnect (host thinks fingers are still down after SSH drops). | Medium | Cursor jammed; users hit Ctrl-C. | Synthetic "all contacts released" emitted on reconnect (§6.2). Same shape as the existing pen-up-on-reconnect. |
| 64-bit struct alignment bugs in P/Invoke (already burned us once). | Medium | Crash or silent garbage. | Mirror the existing `[FieldOffset(8)]` fix; cover with unit tests using `Marshal.SizeOf`/`Marshal.OffsetOf`. |
| Slot reuse with new tracking ID confuses the gesture engine. | Low | Gesture stutter mid-pinch. | `TouchStateMachine` keys frame contacts by tracking ID, not slot, when emitting; tested in §8.1. |
| Two-finger gesture starts mid-pinch with three fingers down. | Low | Recognizer misbehavior. | Lock the recognizer to the first two contacts that began the gesture; ignore additional contacts until count drops back to 0. Tested. |

---

## 10. Sequencing & milestones

Each phase is gated on the previous; no parallel speculative work.

| Milestone | Status | Definition of done | Estimated effort |
|---|---|---|---|
| **M0 — Verification** | ✅ DONE | Phase 0 evtest logs committed; constants populated; pen-proximity touch suppression confirmed (firmware-level). | 0.5–1 day |
| **M1 — Core types** | ✅ DONE | `TouchContact`, `TouchFrame`, `TouchStateMachine`, `TouchCoordinateMapper`, `GestureEngine` + 22 unit tests green. | 2–3 days |
| **M2 — Transport** | ✅ DONE | `SshTransport` refactored to multi-stream, `SshDeviceStream` extracted, backwards-compat `GetReader()` retained, all 43 tests green. | 1–2 days |
| **M3 — Linux output** | ✅ DONE (code) | `UinputTouchOutput` + `ITouchOutput` interface + pipeline integration + CLI `--gestures` flag. End-to-end Krita / GIMP validation pending real hardware. | 1–2 days |
| **M4 — Windows output** | ✅ DONE (code) | `WindowsTouchInjectionOutput` (PT_TOUCH synthetic pointer device, multi-contact lifecycle tracked, [FieldOffset(8)] alignment matches the existing pen quirk). 6 smoke tests green (instantiate real Win32 API). End-to-end app testing pending real hardware. | 3–5 days |
| **M5 — Synth fallback** | DROPPED | Real touch injection covers all current use cases (Krita confirmed working on real hardware 2026-05-07). Out of scope unless a specific app proves incompatible. | — |
| **M6 — UX surface** | ✅ DONE | CLI `--gestures` flag wired; GUI Settings → "Touch Gestures" group box with persisted `Gestures` field in `AppSettings`; README "Touch gestures" section + app compatibility matrix added. | 1 day |
| **M7 — Hardening** | ✅ DONE (code) | Reconnect emits all-contacts-released (`TabletPipeline.EmitTouchReleaseAll`); 6 Windows smoke tests cover Initialize / Send / ReleaseAll / Dispose lifecycle. Remaining work is hands-on app compatibility validation by user. | 1–2 days |

**Total:** ~10–18 working days, single engineer, hardware in hand. The wide range tracks unknowns from M0 — particularly app-compatibility surprises in M4 and M7.

A reasonable shippable mid-point is **M0 + M1 + M2 + M3 + M5 + M6** (Linux only, both modes). Windows can ship in a follow-up release.

---

## 11. Out of scope (explicit non-goals for this iteration)

- Three+ finger gestures.
- Rotate in `synth` mode (no universal target).
- Per-app gesture profiles or remappings.
- Touch as a cursor (point with finger). Touch is gestures only; pointing remains the pen's job.
- iPadOS-style "Scribble" (handwriting → text). Out of scope; different problem.
- macOS support. The project is Windows + Linux today; touch doesn't change that.

---

## 12. M0 verification — answers (resolved 2026-05-07)

1. **Device path:** `/dev/input/event2`. Driver name `pt_mt`. ✓
2. **Protocol:** MT-B (Slot protocol). ✓
3. **Coordinate range:** X 0–1403, Y 0–1871 (display-aligned). ✓
4. **Max slots reported:** 32 (we cap our state machine at 5 — `TouchMaxTracked = 5`).
5. **Pressure:** reported, range 0–255. ✓
6. **Sample rate:** ~85 Hz over USB SSH (faster than typical 60 Hz capacitive touch).
7. **Pen-proximity behavior:** **firmware suppresses touch while the pen is in proximity.** No host-side `PenToolGate` is required for correctness — see §1.3.
8. **`BTN_TOUCH`:** **NOT reported on this device.** The only `EV_KEY` codes are `KEY_F1`–`F8`. Contact lifecycle must be derived purely from `ABS_MT_TRACKING_ID` transitions (positive ⇒ start, `-1` ⇒ release). The state machine has no fallback signal — be strict about tracking IDs.
9. **`ABS_MT_TOOL_TYPE`:** reported (range 0–1). May enable future palm rejection via tool-type discrimination — capture in `TouchContact` for forward compatibility, but don't act on it in v1.
10. **Other axes:** `ABS_MT_TOUCH_MAJOR/MINOR` (0–255 each) and `ABS_MT_ORIENTATION` (-127–127) are reported. Capture but don't use yet.

**No remaining blockers.** Implementation can proceed against M1.

### Locked-in constants (to add to `ReMarkable2Constants.cs` in M1)

```csharp
// Touchscreen — verified 2026-05-07 via evtest /dev/input/event2 (driver: pt_mt)
public const string TouchDevicePath = "/dev/input/event2";
public const int TouchXMin = 0,        TouchXMax = 1403;
public const int TouchYMin = 0,        TouchYMax = 1871;
public const int TouchPressureMin = 0, TouchPressureMax = 255;
public const int TouchMaxSlots   = 32;   // hardware reports
public const int TouchMaxTracked = 5;    // we cap our state machine here
// BTN_TOUCH is NOT reported — contact lifecycle uses ABS_MT_TRACKING_ID only.
```

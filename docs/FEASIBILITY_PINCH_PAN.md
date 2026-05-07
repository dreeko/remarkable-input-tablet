# Feasibility — Pinch-Zoom and Pan via the rM2 Touchscreen

**Status:** research, no code changes.
**Scope:** Can `remarkable-input-tablet` use the rM2's capacitive multi-touch panel (in addition to the pen digitizer it already uses) to drive pinch-to-zoom and two-finger pan on the host PC?
**TL;DR:** Mechanically feasible on both Windows and Linux, but the dominant UX question is a hardware/firmware behavior on the rM2 (does touch fire while the pen is in proximity?) that must be verified on-device before committing to any path. There are three host-side design forks with very different reach, fidelity, and effort profiles. None of them is small.

---

## 1. What the project does today (baseline)

A single SSH stream, `cat /dev/input/event1`, pumps 16-byte evdev structs from the **pen** digitizer into a parser → state machine → coordinate mapper → output (Windows Ink injection, Win32 mouse, or Linux uinput). See `src/RemarkableTablet.Core/Pipeline/TabletPipeline.cs` and `src/RemarkableTablet.Core/Transport/SshTransport.cs`.

The pen device is verified hardware (firmware Wacom I2C Digitizer, version 1231; see `ReMarkable2Constants.cs`). The pipeline is single-source and single-output.

## 2. What the rM2 provides at the kernel level

The rM2 exposes three input devices on `/dev/input/event*`:

| Device | Conventional path | Confirmed in this codebase | Notes |
|---|---|---|---|
| Hardware buttons | `event0` | No | Power / pen-pairing button. Not relevant here. |
| Pen (Wacom digitizer) | `event1` | **Yes** | Already consumed by this project. |
| Capacitive touchscreen | `event2` | **No — unverified** | The candidate input for pinch/pan. |

> **Verification step before any work begins:** SSH into the device and run `evtest /dev/input/event2`. We need to capture (1) the real device path on this firmware, (2) the multi-touch protocol variant, (3) coordinate ranges, (4) reported max contacts, and (5) whether touch events arrive while the pen is hovering or in contact. The rest of this report assumes the modern Linux MT-B "slot" protocol with `ABS_MT_SLOT`, `ABS_MT_TRACKING_ID`, `ABS_MT_POSITION_X/Y`. If the firmware reports MT-A (no slots) the parser is different, but feasibility doesn't change.

### Likely-but-unverified facts

These are reasonable defaults from rM-community knowledge that should be confirmed, not built on:

- Touch reports a different (lower) coordinate range than the pen — typically aligned to the 1404×1872 display pixels rather than the 20966×15725 pen-digitizer space. This is a separate `CoordinateMapper` calibration.
- The kernel evdev struct on `event2` is 16 bytes (same 32-bit ARM `timeval` layout the pen uses), so `EvdevParser.cs` should bind without changes.
- The rM firmware **palm-rejects touch while the pen is in proximity.** This is the convention on Wacom-equipped Android/Windows tablets and matches reMarkable's drawing-app behavior. **If true, simultaneous "draw with pen + pinch with off-hand" is impossible at the source — no host-side workaround can recover it.** Workflow then becomes "lift pen → pan/zoom → resume drawing," which is acceptable but worth setting expectations on.

## 3. Architectural impact on the existing pipeline

### 3.1 Transport — the real lift

`SshTransport` currently runs **one** `SshCommand`: `cat /dev/input/event1`. To consume `event2` you have three options, each with a real cost:

| Option | Sketch | Cost | Risks |
|---|---|---|---|
| **A. Second `SshCommand` on the same `SshClient`** | Open a second `cat event2` command on the existing connection, second `PumpBlocking` thread, second `Pipe`. | Smallest delta. Probably one new class `SshTouchTransport` parallel to `SshTransport`, or a refactor that shares an `SshClient`. | The codebase has not exercised concurrent `SshCommand`s. SSH.NET supports multiple channels per session, but the existing `CleanupConnectionAsync` ordering (dispose command → disconnect client) needs careful re-work to avoid the same `OutputStream.Read()` deadlock the pen path already had to fix. |
| **B. Second independent `SshClient`** | A whole second SSH session for touch. | Simple isolation; the existing `SshTransport` is reused verbatim with a different device path. | Doubles SSH overhead (two TCP connections, two auth handshakes, two reconnect loops). Two independent reconnection state machines complicate the App's connection-state UI. |
| **C. Single combined shell with tagging** | One `cat event1 event2 \| awk` … or a small helper binary deployed to the device that interleaves and tags the streams. | Lowest network overhead. | Requires a deployed helper or fragile shell pipework; previous design intentionally avoids touching the device's filesystem. Not recommended unless A and B both prove problematic. |

A is the natural choice if SSH.NET's multi-channel support is solid; B is the safe fallback. Either way, **transport, not gesture logic, is where this project's existing code structure has to flex the most.**

### 3.2 Parsing

A new `MtEvdevParser` (or a generalization of `EvdevParser`) is needed because the touch protocol uses MT-specific codes (`ABS_MT_SLOT`, `ABS_MT_TRACKING_ID`, `ABS_MT_POSITION_X`, `ABS_MT_POSITION_Y`, possibly `ABS_MT_PRESSURE`/`ABS_MT_TOUCH_MAJOR`). The frame boundary is still `SYN_REPORT`. The structural pattern of `TabletStateMachine` (accumulate → snapshot on SYN_REPORT) carries over cleanly; the snapshot type changes from a single `PenFrame` to a multi-contact `TouchFrame { Contact[] }`.

Ballpark: ~200–400 LOC + tests, comparable to the existing pen state machine.

### 3.3 Coordinate mapping

A second `CoordinateMapper` instance with its own scale factors. The rotation/orientation logic from `CoordinateMapper.cs` is reusable. New unit tests required because touch coordinate ranges differ from pen.

## 4. Output — the central design fork

This is the decision the project actually has to make. Each of the three options is feasible; they trade reach against fidelity.

### Option 1 — Inject real multi-touch contacts

- **Windows:** `InitializeTouchInjection` + `InjectTouchInput` (user32.dll, no driver, available since Windows 8). Multiple `POINTER_TOUCH_INFO` contacts per frame. Apps see genuine touch points and run their own gesture recognition.
- **Linux:** Extend the existing uinput device — or add a sibling — with MT slots and `INPUT_PROP_DIRECT`. Standard kernel pattern; well-trodden in `libevdev`/`uinput` examples.
- **Pros:** Highest fidelity. Works correctly with apps that have real multi-touch gestures (canvas pan, rotate, zoom in Krita / Photoshop / Affinity / Inkscape via xdotool-style multi-touch, browser pinch zoom). No host-side gesture recognizer required — the OS or app does it.
- **Cons:**
  - Many Windows desktop apps still don't support touch gestures even when they support touch (or their support is patchy). What works on a Surface for pinch-zoom doesn't always work the same way through synthetic injection.
  - Touch injection on Windows must be initialized once per process and contacts must be tracked with stable IDs across frames — non-trivial state to keep right.
  - Apps that handle pen via Windows Ink and touch separately may double-process (e.g., the pen injects through one path, touch through another, and the app sees them as different devices — usually fine, occasionally not).

### Option 2 — Recognize gestures on the host, emit synthesized input

- Host runs a small recognizer: two-finger horizontal/vertical movement → mouse wheel scroll; two-finger pinch/spread → `Ctrl + mouse wheel` (the de facto zoom gesture in nearly every desktop app); two-finger drag → middle-mouse-button drag (pan in many apps).
- **Windows:** `SendInput` for wheel deltas and modifier keys. Already adjacent to `MouseOutput.cs`.
- **Linux:** uinput relative-mouse device emitting wheel events.
- **Pros:** Works in **every** desktop app, including non-touch-aware ones. Predictable. The recognizer is small and testable.
- **Cons:** Lower fidelity. No native pan inertia. The "pinch" gesture becomes discrete wheel ticks, which feels notchy compared to true touch. Custom gestures (rotate, three-finger swipe) don't map cleanly to keyboard/wheel events without per-app shortcuts.

### Option 3 — Hybrid

- Inject real touch where it works, expose a "fallback to synthesized scroll" mode (CLI flag / settings checkbox) for apps that don't behave with injected touch. Likely the eventual production answer; double the implementation surface to get there.

A reasonable phasing if the project commits: Option 2 first (smaller scope, broadest immediate value), Option 1 as a follow-on once the touch transport and parser are stable.

## 5. UX questions that don't have a software answer

These need user-testing on real hardware to settle, not more code reading:

1. **Does the rM2 firmware suppress touch while the pen hovers?** If yes, the experience is "lift pen, then pinch." If no, off-hand pinch while drawing becomes possible — a notably better workflow.
2. **Latency.** Touch sample rate and SSH transport jitter combined will determine whether pinch feels responsive. Pen at ~100 Hz over USB SSH is already fine; touch is typically 60–80 Hz. Should be fine but is unmeasured.
3. **Palm rejection on the host side.** If the user rests their hand on the screen while drawing and the firmware does *not* suppress, the pipeline must drop touch contacts whose `BTN_TOOL_PEN` companion is active. Cheap to implement; needs the cross-device state to flow into one place.
4. **Wi-Fi vs USB.** Two parallel evdev streams over Wi-Fi may be where the SSH bandwidth budget actually starts to matter. Worth a back-of-envelope check (touch packets are small but bursty during gestures).

## 6. Effort estimate (rough, for relative sizing only)

| Slice | Estimate | Comment |
|---|---|---|
| On-device verification (`evtest`, capture sample streams) | 0.5–1 day | Required before any of the below has firm shape. |
| Transport extension (Option A: shared `SshClient`) | 1–2 days | Including reconnect-loop integration and tests. |
| MT parser + touch state machine + tests | 1–2 days | Mirrors the existing pen pipeline. |
| Coordinate mapping + calibration | 0.5–1 day | New scale factors; reuse rotation logic. |
| **Output: synthesized scroll/zoom (Option 2)** | 1–2 days | Includes a small gesture recognizer with unit tests. |
| **Output: real touch injection (Option 1, Windows)** | 3–5 days | `InitializeTouchInjection` + multi-contact tracking + per-app testing matrix. |
| **Output: real touch injection (Option 1, Linux uinput-MT)** | 1–2 days | Standard kernel pattern. |
| App + CLI surface (settings, flags, status UI) | 0.5–1 day | Minor. |

**Lower bound** (Option 2 only, Windows + Linux): ~5–7 working days.
**Upper bound** (Option 1 + Option 2 hybrid, both platforms): ~10–14 working days.

These numbers assume one engineer familiar with the codebase, exclude polish and per-app compatibility hunting, and are wide on purpose — the verification step in §2 can move them.

## 7. Recommended next step (process, not implementation)

1. Run `evtest /dev/input/event2` on the rM2 with a representative session: tap, two-finger pinch, two-finger drag, palm rest, and pinch-while-pen-hovering. Capture the output.
2. Confirm or refute the four "likely-but-unverified" facts in §2.
3. Decide on the design fork in §4 with the latency/palm-rejection answers in hand.
4. Then plan implementation against the chosen fork.

No code changes should land before step 1.

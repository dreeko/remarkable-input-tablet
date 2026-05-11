# Implementation Plan — reMarkable Paper Pro Support

**Status:** Draft, ready to execute
**Companion docs:** `docs/FEASIBILITY_REMARKABLE_PRO.md` (verdict, prior art, constants); project memory `reference_rmpp_prior_art.md` (Evidlo `rmpro` source-of-truth)
**Target release:** v0.4.0
**Total effort estimate:** ~3.75 days of code + hardware bring-up time

---

## Goals

1. The tool runs on a reMarkable Paper Pro with the same UX as on an rM2 (CLI + GUI, pen + touch, all output modes).
2. rM2 behavior is bit-for-bit unchanged. No regressions.
3. Hardware specifics live in **one** place (`DeviceProfile`); adding a hypothetical rM3 / Paper Pro Move later is a single new profile file.
4. The Linux uinput device gets `input_absinfo.resolution` populated correctly — fixes a latent Wayland/libinput issue on rM2 too.

## Non-goals

- On-device daemon architecture (XOVI / rm-appload). Future v2 option.
- Pen attach/detach event handling (rMPP `event1`). Not needed for input streaming; defer.
- Power button event handling (rMPP `event0`). Out of scope.
- Encryption-at-rest concerns. We only `cat` files, we don't write to the device.

---

## Phase 0 — Hardware bring-up (~30 min, gates Phase 5)

Run on a real rMPP. Capture data into `tools/EventDiagnostics/samples/rmpp/`. Does **not** block code work in Phases 1–4.

1. Enable Developer Mode (Settings → General → Paper Tablet → Software → Advanced); confirm SSH at `10.11.99.1` works with the password from Help → Copyrights.
2. Run `evtest -i /dev/input/event2` (pen) and `event3` (touch). Save device headers to disk.
3. Capture pen axis ranges including **tilt and distance** (not in Evidlo's public constants). Tilt the pen end-to-end on both axes while `evtest` is running.
4. Confirm event-struct size: `dd if=/dev/input/event2 bs=24 count=1 | xxd`. First 8 bytes should be a plausible 64-bit Unix timestamp.
5. Pen-suppression test: open `evtest` on event2 and event3 simultaneously, hover the pen, watch whether touch events keep flowing. Records whether `PenToolGate` needs resurrecting (deleted as unnecessary on rM2; may be required on rMPP).
6. Touchscreen orientation test: press top-left corner, confirm reported coordinates are near (0,0). Mirrors the rM2 verification method.

**Gate:** Phase 5 cannot ship until Phase 0 data either confirms the Evidlo constants in `ReMarkablePaperProProfile` or replaces them with locally-captured values.

---

## Phase 1 — `DeviceProfile` foundation (~0.5 day, no behavior change)

Pure refactor. The rM2 profile is extracted from existing constants; nothing about behavior changes. Verifiable purely by running the existing test suite + manual rM2 smoke test.

### New types

`src/RemarkableTablet.Core/Devices/DeviceProfile.cs` (new):

```csharp
public sealed record DeviceProfile
{
    public required string Name { get; init; }              // "reMarkable 2", "reMarkable Paper Pro"
    public required EvdevLayout EventLayout { get; init; }  // 16- or 24-byte struct
    public required string PenDevicePath { get; init; }
    public required string TouchDevicePath { get; init; }
    public required PenAxes Pen { get; init; }
    public required TouchAxes Touch { get; init; }
    public bool PenSuppressesTouch { get; init; }           // rM2 = true; rMPP = TBD
}

public sealed record EvdevLayout(int StructSize, int TypeOffset, int CodeOffset, int ValueOffset)
{
    public static EvdevLayout Bits32 => new(16,  8, 10, 12);  // armv7l: 4+4+2+2+4
    public static EvdevLayout Bits64 => new(24, 16, 18, 20);  // aarch64: 8+8+2+2+4
}

public sealed record PenAxes(
    int XMin, int XMax,            int XResolution,   // resolution: ticks per mm; 0 = not set
    int YMin, int YMax,            int YResolution,
    int PressureMin, int PressureMax,
    int TiltXMin, int TiltXMax,
    int TiltYMin, int TiltYMax,
    int DistanceMin, int DistanceMax);

public sealed record TouchAxes(
    int XMin, int XMax,
    int YMin, int YMax,
    int PressureMin, int PressureMax,
    int MaxSlots, int MaxTracked);
```

`src/RemarkableTablet.Core/Devices/ReMarkable2Profile.cs` (new): single static instance populated verbatim from current `ReMarkable2Constants`. **No new values, no behavior change.**

### Refactors

- `src/RemarkableTablet.Core/Evdev/EvdevParser.cs:21` — drop `const int EventSize = 16`; accept `EvdevLayout` as a parameter. Read at the profile's offsets.
- `src/RemarkableTablet.Core/Mapping/CoordinateMapper.cs:23-25,54-60` — read pen ranges from a `DeviceProfile` reference held by the mapper.
- `src/RemarkableTablet.Core/Mapping/TouchCoordinateMapper.cs` — same for touch.
- `src/RemarkableTablet.Core/Pipeline/TabletPipeline.cs:125,151` — replace `ReMarkable2Constants.{Pen,Touch}DevicePath` with profile lookups.
- `src/RemarkableTablet.Core/Tablet/ReMarkable2Constants.cs` — delete, or thin to `[Obsolete]` re-exports during the refactor and remove in the same PR. Prefer outright delete.

### Tests

- `tests/RemarkableTablet.Core.Tests/Evdev/EvdevParserTests.cs` — add a test that parses a hand-crafted 16-byte rM2 frame using `EvdevLayout.Bits32` and asserts the existing expected event. Existing tests must still pass without change once they thread the profile through.
- All existing tests pass unchanged.

### Acceptance

`dotnet build && dotnet test` green on both target frameworks. Manual rM2 smoke test: pen draws pressure curves identically to v0.3.x.

---

## Phase 2 — rMPP profile + variable-size parsing (~0.5 day)

`src/RemarkableTablet.Core/Devices/ReMarkablePaperProProfile.cs` (new):

```csharp
public static class ReMarkablePaperProProfile
{
    public static DeviceProfile Instance { get; } = new()
    {
        Name = "reMarkable Paper Pro",
        EventLayout = EvdevLayout.Bits64,
        // Draft values from Evidlo/remarkable_mouse `rmpro` branch (Issue #92).
        // Must be confirmed by Phase 0 evtest before v0.4.0 ships.
        PenDevicePath = "/dev/input/event2",
        TouchDevicePath = "/dev/input/event3",
        Pen = new PenAxes(
            XMin: 0, XMax: 11180, XResolution: 2832,
            YMin: 0, YMax: 15340, YResolution: 2064,
            PressureMin: 0, PressureMax: 4096,
            TiltXMin: -9000, TiltXMax: 9000,   // PLACEHOLDER — capture in Phase 0
            TiltYMin: -9000, TiltYMax: 9000,   // PLACEHOLDER — capture in Phase 0
            DistanceMin: 0, DistanceMax: 255), // PLACEHOLDER — capture in Phase 0
        Touch = new TouchAxes(
            XMin: 0, XMax: 1619,               // PLACEHOLDER — display is 1620×2160
            YMin: 0, YMax: 2159,               // PLACEHOLDER
            PressureMin: 0, PressureMax: 255,
            MaxSlots: 32, MaxTracked: 5),
        PenSuppressesTouch = true              // ASSUMPTION — verify in Phase 0
    };
}
```

Mark placeholders with `// TODO(rmpp-phase0)` so a grep before release surfaces any unverified value.

### Tests

`tests/RemarkableTablet.Core.Tests/Evdev/EvdevParser_Rmpp_Tests.cs` (new): feed a hand-crafted 24-byte aarch64 frame, assert `(type, code, value)` decode correctly using `EvdevLayout.Bits64`. Also feed a 24-byte stream of two concatenated frames and assert no desync.

### Acceptance

Tests green. No live-hardware acceptance possible until Phase 0 is done.

---

## Phase 3 — `input_absinfo.resolution` (~0.25 day, applies to both profiles)

This is the FreeCap23 fix: Wayland/libinput recognizes the virtual uinput device as a tablet only when `resolution > 0`. Currently silently 0 on rM2 — known correctness gap, fixed alongside the rMPP work because both profiles need it.

- `src/RemarkableTablet.Linux/Interop/UinputStructs.cs` — confirm `input_absinfo` already has a `resolution` field (Linux struct definition includes it); expose it through the managed type if missing.
- `src/RemarkableTablet.Linux/Output/UinputOutput.cs:63-67,150-158` — extend `SetAxis(..., int resolution)` and `uinput_abs_setup` initialization. Read per-axis resolution from the active `DeviceProfile.Pen`. rM2 gets `100` (per FreeCap23); rMPP gets `2832` / `2064` from the Evidlo constants.
- `src/RemarkableTablet.Linux/Output/UinputTouchOutput.cs` — same treatment if it also uses `SetAxis`. Touch resolution is less critical (touchscreens default to `INPUT_PROP_DIRECT`) but populate it for completeness.

### Acceptance

On Linux: `udevadm info --query=property --name=/dev/input/<our-virtual-device>` shows `ID_INPUT_TABLET=1`. Manual smoke in Krita on a Wayland session.

---

## Phase 4 — UX (~0.5 day)

### CLI

`src/RemarkableTablet.Cli/Program.cs`:

- New flag `--device <rm2|rmpp|auto>` defaulting to `auto`.
- New flag `--no-detect` to force a specific profile (for diagnostics).

### Auto-detection (~0.5 day shared with Phase 3.5)

`src/RemarkableTablet.Core/Devices/DeviceDetector.cs` (new):

1. Open SSH using existing `SshTransport.ConnectAsync`.
2. Execute `uname -m`. `armv7l` → rM2; `aarch64` → rMPP; anything else → throw with a clear "unsupported device" error.
3. (Optional resilience, defer if time-pressed) Walk `/proc/bus/input/devices` and match pen/touch by `Name=` substring rather than trusting hard-coded `event2`/`event3` indices. Evidlo's `readlink -f /dev/input/touchscreen0` is the simpler proven approach; use that.

### GUI

`src/RemarkableTablet.App/SettingsWindow.xaml.cs` + `SettingsWindow.xaml`:

- New "Device" dropdown: `Auto-detect (recommended)`, `reMarkable 2`, `reMarkable Paper Pro`.
- Persist in `AppSettings.cs` (new `DeviceProfile` string field).
- On `Connect`, if `Auto`, run `DeviceDetector` before building the pipeline. Show the detected device name in the tray tooltip.

### Acceptance

Manual: CLI with `--device auto` connects to rM2 and reports "Detected: reMarkable 2" in startup logs. CLI with `--device rmpp` against an rM2 should fail fast with a "wrong profile — expected aarch64, got armv7l" error rather than producing garbage events.

---

## Phase 5 — Docs + release (~0.5 day, gated on Phase 0)

- README quickstart: Developer Mode note for rMPP, link to reMarkable's official article.
- README compatibility table: add rMPP row.
- New section `## Hardware details — Paper Pro` in README mirroring the existing rM2 one, populated from Phase 0 captures.
- `docs/FEASIBILITY_REMARKABLE_PRO.md` — append a "Verified" note with Phase 0 capture date.
- CHANGELOG: v0.4.0 entry.
- Tag `v0.4.0`, GitHub Actions publishes per-OS artifacts as usual.

**Ship gate:** All `// TODO(rmpp-phase0)` placeholders replaced with verified values, or the rMPP profile is marked `[Experimental]` in the docs with the known unknowns called out.

---

## Sequencing and parallelism

```
Phase 0 (hardware bring-up)  ━━━━━━━━━━━━━━━━━━━━━┓
                                                  ▼
Phase 1 (DeviceProfile)  ━━━┓                  Phase 5 (release)
                            ▼                     ▲
Phase 2 (rMPP profile)   ━━━┫                     │
                            ▼                     │
Phase 3 (resolution fix) ━━━┫                     │
                            ▼                     │
Phase 4 (UX)             ━━━┻━━━━━━━━━━━━━━━━━━━━━┛
```

Phase 0 is async with Phases 1–4. Phases 1→2→3→4 must be sequential (each depends on the previous). Phase 5 needs all of them.

---

## Risk register

| Risk | Likelihood | Mitigation |
|---|---|---|
| Evidlo's rmpp axis values are wrong for our firmware build | Medium | Phase 0 verifies before ship; placeholder TODOs grep-able |
| `PenSuppressesTouch = true` assumption fails | Medium | Phase 0 verifies; if false, resurrect `PenToolGate` (+1 day) |
| Touchscreen axis rotation differs (we hit this on rM2 once) | Medium | Phase 0 corner-tap test; `INPUT_PROP_DIRECT` is ground truth |
| Pen barrel buttons use different evdev codes on rMPP (`BTN_TOOL_PENCIL`?) | Low | `evtest` capture in Phase 0 surfaces this; trivial state-machine extension |
| `uname -m` returns something unexpected (e.g. Buildroot variant) | Low | Detector throws with clear message; user falls back to `--device rmpp` |
| Wayland users on rM2 were relying on resolution-0 behavior | Very low | resolution=100 is the libinput-correct value; no plausible regression |
| dotnet test hangs (per project memory) | Low | Known issue; investigate stale testhost.exe / Pipe deadlocks before retrying |

---

## What this plan deliberately doesn't do

- **No abstract "future devices" interface.** Two profiles, both concrete, in one folder. If a third device appears, we generalize then — not preemptively.
- **No runtime discovery of axis ranges from `/proc/bus/input/devices` ABS bitmaps.** Possible, but constants-per-profile is simpler and the existing test surface stays small. Add discovery later if firmware drift forces it.
- **No on-device daemon.** Documented as a v2 option in the feasibility doc; not part of this scope.
- **No support for the rMPP power button or pen-attach event.** Out of scope; input-streaming tool doesn't need them.

---

## Done means

- `dotnet build && dotnet test` green on Windows and Linux.
- Manual: rM2 user upgrading to v0.4.0 sees identical behavior to v0.3.1.
- Manual: rMPP user runs `remtablet --password <pw>` or the GUI, sees pen pressure and tilt in Krita on Windows and Linux.
- Touch gestures work on rMPP if `--gestures touch` is passed (or break loudly with a clear message — touch is optional).
- README documents both devices; rMPP hardware-details section populated from Phase 0 captures.
- No `// TODO(rmpp-phase0)` strings remain.

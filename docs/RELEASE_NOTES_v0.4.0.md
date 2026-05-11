# v0.4.0 — reMarkable Paper Pro support (experimental)

This release adds the plumbing for the reMarkable Paper Pro alongside the existing reMarkable 2 support, plus a Wayland-correctness fix for rM2 Linux users.

**Paper Pro support is experimental.** Most device constants are seeded from the [`Evidlo/remarkable_mouse`](https://github.com/Evidlo/remarkable_mouse) `rmpro` branch and have not been verified against real hardware. Search the source for `TODO(rmpp-phase0)` for the gaps — pen tilt range, hover distance, touchscreen axes, and the pen-suppresses-touch assumption. If you have a Paper Pro and want to help close those, run `tools/EventDiagnostics` and open an issue.

## Highlights

### reMarkable Paper Pro support

- New `DeviceProfile` abstraction; one profile per supported model.
- `EvdevParser` reads `struct input_event` at runtime-supplied offsets — 16-byte (armv7l, rM2) and 24-byte (aarch64, rMPP) layouts coexist in one parser. Avoids the desync that bit users of forks running the rM2 code path against a Paper Pro stream (see [`Evidlo/remarkable_mouse` Issue #92](https://github.com/Evidlo/remarkable_mouse/issues/92)).
- Auto-detection via `uname -m` over SSH. Pass `--device rm2|rmpp|auto` to override; the GUI gains a Device dropdown.
- Developer Mode must be toggled on the Paper Pro before the root password appears (Settings → General → Paper Tablet → Software → Advanced).

### Wayland tablet recognition on rM2 (Linux)

`input_absinfo.resolution` is now populated for the virtual uinput pen device — `100` ticks/mm on rM2 (matching `FreeCap23/reMarkable-tablet-driver`), `2832` / `2064` on rMPP. libinput uses this field to categorise the device as a tablet rather than a generic absolute-axis input; without it some Wayland compositors fell back to mouse semantics.

This is a strict correctness fix for rM2 — no regression, just better recognition under Wayland.

## What's not in this release

- **Live verification on a Paper Pro.** The build is verified, unit tests are green (including new 64-bit parser regression tests), but no on-device smoke test has been done. Expect the `TODO(rmpp-phase0)` items to be adjusted in a follow-up patch release once hardware data is in hand.

## Upgrade notes

- rM2 users: no behaviour changes besides Wayland tablet recognition (Linux). Existing CLI flags and settings carry over unchanged.
- rMPP users: enable Developer Mode first, then connect normally; auto-detect should select the right profile.

## Detailed change log

- DeviceProfile / EvdevLayout / PenAxes / TouchAxes records (`src/RemarkableTablet.Core/Devices/`).
- `ReMarkable2Profile` and `ReMarkablePaperProProfile` populate hardware constants per model.
- `InjectionScale` extracts host-target Windows Ink scale (1024 / ±90) out of the device profile.
- `DeviceDetector` probes `uname -m` and maps to a profile; `--device` CLI flag + GUI dropdown wire it up.
- `SshTransport.RunCommandAsync` for one-shot SSH probes.
- `SshTransport.GetReader` removed (`EventDiagnostics` opens its own stream).
- Two new evdev parser regression tests guarding the 24-byte path.
- 19 new `DeviceDetector` unit tests.
- `ReMarkable2Constants` deleted; all axis/path lookups go through the profile.

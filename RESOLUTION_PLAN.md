# Code-review resolution plan

Source: thorough review on 2026-05-06. Items grouped by impact, ordered for implementation.

Each item lists: **scope** (files), **fix**, and **verification** (how we'll know it worked).

---

## Phase 1 — Distribution / build risks (ship-blockers)

### 1.1 `AppSettings` reflection JSON under `PublishTrimmed`
- **Scope:** `src/RemarkableTablet.App/AppSettings.cs`
- **Fix:** add `[JsonSerializable(typeof(AppSettings))] partial class AppSettingsJsonContext : JsonSerializerContext`. Replace `JsonSerializer.Deserialize<AppSettings>(json, options)` and `Serialize(this, options)` with the source-generated overloads. Mark trimmer-friendly attributes on the class.
- **Verify:** `dotnet build src/RemarkableTablet.App` succeeds with no IL2026 / IL3050 trim warnings; round-trip test (write → read) preserves all fields.

### 1.2 CLI `csproj` selects target framework from build host instead of RID
- **Scope:** `src/RemarkableTablet.Cli/RemarkableTablet.Cli.csproj`
- **Fix:** switch to `<TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>`. Drive the `DefineConstants` and the platform-specific `<ProjectReference>` off `'$(TargetFramework)' == 'net10.0-windows'`. When publishing with `-r linux-x64`, MSBuild will pick `net10.0`; with `-r win-x64`, `net10.0-windows`.
- **Verify:** `dotnet build src/RemarkableTablet.Cli -f net10.0` succeeds on Windows (compiles only Linux platform code); `dotnet build -f net10.0-windows` succeeds. `dotnet test` still works.

### 1.3 No DPI awareness on the CLI
- **Scope:** `src/RemarkableTablet.Cli/Program.cs`, optionally an `app.manifest`
- **Fix:** at the top of `Main` on Windows, call `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)` via P/Invoke. Add the function to `User32.cs`. Manifest is harder to wire under PublishAot; the runtime call is sufficient for our needs.
- **Verify:** at 150% scaling on a 4K screen, `ScreenMetrics.GetPrimarySize` returns the native resolution. Manual check; document the expectation in `README.md`.

---

## Phase 2 — Correctness bugs that affect output

### 2.1 `MouseOutput` ignores BTN_TOUCH-unreliability workaround
- **Scope:** `src/RemarkableTablet.Windows/Output/MouseOutput.cs`
- **Fix:** treat `frame.IsTouch || frame.Pressure > 0` as the contact predicate, mirroring `WindowsInkOutput`.
- **Verify:** unit test driving `MouseOutput` via a P/Invoke seam is overkill for a 4-line fix; instead leave a comment pointing to the rM2 BTN_TOUCH quirk.

### 2.2 `UinputOutput` never emits `BTN_STYLUS`
- **Scope:** `src/RemarkableTablet.Linux/Output/UinputOutput.cs`
- **Fix:** add `EmitEvent(EvType.EV_KEY, BtnCode.BTN_STYLUS, frame.BarrelButton ? 1 : 0)` to the in-range branch and `0` to the out-of-range branch.
- **Verify:** code inspection — the field is now used end-to-end.

### 2.3 `WindowsInkOutput` emits `Update` alone when out-of-range
- **Scope:** `src/RemarkableTablet.Windows/Output/WindowsInkOutput.cs`
- **Fix:** if `!InRange && !_wasInContact`, return early from `Send` (no inject). The pen is fully gone and was already up — there's nothing to report. Also: log when `InjectSyntheticPointerInput` returns false (use `Trace.WriteLine` to stay AOT-safe).
- **Verify:** test added: hovering frame after out-of-range frame produces no inject calls (we'll add a tiny seam — see test additions below).

### 2.4 Tilt axes not rotated by orientation
- **Scope:** `src/RemarkableTablet.Core/Mapping/CoordinateMapper.cs`
- **Fix:** apply the same orientation transform to `(tiltX, tiltY)` as to `(x, y)`. Each rotation is a rotation in the screen plane, so 90° / 180° / 270° rotations of the tilt vector (with sign flips). Concretely:
  - `Portrait`: screen tilt = (origTiltY, -origTiltX)
  - `Landscape`: (-origTiltX, -origTiltY)
  - `PortraitFlipped`: (-origTiltY, origTiltX)
  - `LandscapeFlipped`: (origTiltX, origTiltY)
  These mirror the position rotations in the mapper. Add tests.
- **Verify:** new orientation-tilt unit tests in `CoordinateMapperTests`.

### 2.5 `PressureCurve` ignores X control points
- **Scope:** `src/RemarkableTablet.Core/Mapping/PressureCurve.cs`
- **Fix:** pick the simpler path — drop the `_p1x`/`_p2x` fields and rename the constructor params to be clear it's a parametric polynomial: `new PressureCurve(double yAt33Pct, double yAt67Pct)`. Update factory presets to pass only the y-values. Update the doc comment.
- **Verify:** tests pass with the same numeric outputs (since the math is unchanged — only the API drops the unused params). Tighten `PressureCurveSoft_BooststLowPressure` with a numeric range so future changes can't silently regress.

### 2.6 Tilt sign convention untested
- **Scope:** documentation only
- **Fix:** add a TODO note in `README.md` "Hardware details" section that tilt sign convention is empirical and may need a flip per orientation. Defer to manual verification.
- **Verify:** doc-only.

---

## Phase 3 — Reliability

### 3.1 `TabletPipeline` swallows all exceptions silently
- **Scope:** `src/RemarkableTablet.Core/Pipeline/TabletPipeline.cs`, callers
- **Fix:** add `public event Action<Exception>? Error;` raised from inside the catch block. Wire CLI to print to stderr; wire App to `WriteLog`.
- **Verify:** kill the device mid-stream (manual) — error appears in stderr / app.log instead of vanishing.

### 3.2 Synchronous `Dispatcher.Invoke` from background pipeline thread
- **Scope:** `src/RemarkableTablet.App/App.xaml.cs:104,109`, `TrayIcon.cs:96`, `SettingsWindow.xaml.cs:153`
- **Fix:** replace `Dispatcher.Invoke(...)` with `Dispatcher.BeginInvoke(...)` for fire-and-forget state notifications.
- **Verify:** code inspection.

### 3.3 `TestConnection_Click` blocks UI thread
- **Scope:** `src/RemarkableTablet.App/SettingsWindow.xaml.cs`
- **Fix:** wrap the entire SSH sequence (Connect, RunCommand, Disconnect) in a single `Task.Run`. Add an explicit timeout: `client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10)`. Disable the button while the test is in flight to prevent re-entry.
- **Verify:** with a bad password the dialog stays responsive and reports failure within ~10 s.

### 3.4 `Channel` `DropOldest` can corrupt mid-frame state
- **Scope:** `src/RemarkableTablet.Core/Pipeline/TabletPipeline.cs`
- **Fix:** make the evdev channel unbounded (events are 6 bytes each — even a 1-second backup is ~600 bytes). Keep the frame channel bounded with `DropOldest` since dropping a complete frame is fine.
- **Verify:** sustained stream test (existing fixture replay) shows no event loss; review `RunOnceAsync`.

### 3.5 `_pipeline.Stop()` racing with `DisposeAsync`
- **Scope:** `src/RemarkableTablet.App/App.xaml.cs`
- **Fix:** capture `_pipeline` into a local before calling `Stop()`. Inside `RunPipelineAsync` capture the pipeline reference, dispose, then null the field.
- **Verify:** code inspection.

---

## Phase 4 — Half-implemented features

### 4.1 `AutoConnect` is a no-op
- **Scope:** `src/RemarkableTablet.App/TrayIcon.cs`, `SettingsWindow.xaml.cs`, `App.xaml.cs`, `AppSettings.cs`, `SettingsWindow.xaml`
- **Fix:** keep the checkbox but make it work: when `_autoConnect` is true, after `PasswordBox.PasswordChanged` and the user pressing Enter (or after a brief debounce), trigger `Connect_Click`. Document that "auto-connect" means "auto-click Connect once you type your password" — it can't bypass the password prompt because we deliberately don't store passwords.
- **Verify:** manual test — enable AutoConnect, restart app, type password, hit Enter — connection starts without clicking.

### 4.2 `WindowsInkOutput` never sets `PointerFlags.Primary` or `New`
- **Scope:** `src/RemarkableTablet.Windows/Output/WindowsInkOutput.cs`, `Interop/PointerStructs.cs` (verify `Primary` enum value)
- **Fix:** always include `PointerFlags.Primary` in non-zero flag sets. Set `PointerFlags.New` on the first inject after `Initialize()` (track via a `_isFirstFrame` bool). Apply to dispose-time pen-up too.
- **Verify:** existing `WindowsInkOutputTests` continue to pass; add an assertion-light test that walks the lifecycle.

---

## Phase 5 — Test coverage and nits

### 5.1 Tighten `PressureCurveSoft` test
- Replace `Assert.True(soft > linear)` with a numeric range so the test fails when the math changes.

### 5.2 Tilt rotation tests
- Add four tests (one per orientation) verifying tilt rotation matches the position rotation.

### 5.3 Fixture-present test silently skips
- **Scope:** `tests/RemarkableTablet.Core.Tests/EvdevParserTests.cs`
- **Fix:** keep the early-return for now (it's documented as Phase-0 dependent), but emit `Console.WriteLine` so test output makes the skip visible.

### 5.4 Fix typo `BooststLowPressure`
- Rename to `BoostsLowPressure`.

### 5.5 Selected nits
- `EvdevParser.Parse`: fast-path single-segment with `slice.FirstSpan`.
- `TabletStateMachine.RunAsync` becomes an instance method.
- Centralize `"mouse"` / `"ink"` strings into a constants class (`OutputModes`).
- `SettingsWindow.LoadSettings`: clamp `SelectedIndex` with `Math.Max(0, ...)`.
- Skip `EvdevCodes`/`EvdevTypes` merge — file count change with no benefit.
- Skip `mouse_event` → `SendInput` — works, deprecated only on paper, mouse mode is a fallback.

---

## Sequencing

Work order chosen so that earlier fixes don't break later verification:

1. **Phase 1.2** first — without it, building from a Linux box can't even produce a Linux artifact, so we can't verify Linux fixes.
2. **Phase 1.1** and **1.3** next — settings load and DPI are quick.
3. **Phase 2.5** (PressureCurve) before **5.1**/**5.2** test changes — the API rename touches tests.
4. **Phase 2.4** + **5.2** together — tilt rotation + its tests.
5. Remaining Phase 2 items.
6. Phase 3 reliability fixes.
7. Phase 4 features.
8. Test polish and nits.

Each phase ends with `dotnet build` and `dotnet test` clean.

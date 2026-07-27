using System.IO.Pipelines;
using System.Threading.Channels;
using RemarkableTablet.Core.Devices;
using RemarkableTablet.Core.Evdev;
using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Tablet;
using Xunit;

namespace RemarkableTablet.Core.Tests;

/// <summary>
///     Replays raw evdev captures from real hardware through the whole pipeline —
///     parser, state machine, mapper — and asserts where the strokes land.
///     <para>
///         This is the test class the axis bug needed. Every other test asserts the
///         mapping against the same convention the mapper implements, so when that
///         convention was wrong (both axes, for two years) the suite stayed green.
///         These captures are of a person touching known corners of a physical
///         device; they cannot agree with a wrong formula.
///     </para>
///     Captures live in <c>tools/EventDiagnostics/samples</c> and are copied
///     alongside the test binary. Each is a raw 16-byte <c>input_event</c> stream
///     from <c>cat /dev/input/eventN</c> on firmware 1231.
/// </summary>
public class ReplayTests
{
    private static readonly DeviceProfile Rm2 = ReMarkable2Profile.Instance;

    private static string Capture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", name);
        Assert.True(File.Exists(path), $"missing capture {path} — check the csproj copies samples/*.bin");
        return path;
    }

    /// <summary>Parser → state machine, for the pen stream.</summary>
    private static async Task<List<PenFrame>> ReplayPenAsync(string file)
    {
        var evdev = Channel.CreateUnbounded<EvdevEvent>();
        var frames = Channel.CreateUnbounded<PenFrame>();

        await using var stream = File.OpenRead(file);
        await EvdevParser.RunAsync(PipeReader.Create(stream), evdev.Writer, Rm2.EventLayout, CancellationToken.None);
        await TabletStateMachine.RunAsync(evdev.Reader, frames.Writer, CancellationToken.None);

        var result = new List<PenFrame>();
        await foreach (var f in frames.Reader.ReadAllAsync()) result.Add(f);
        return result;
    }

    /// <summary>Parser → state machine, for the touch stream.</summary>
    private static async Task<List<TouchFrame>> ReplayTouchAsync(string file)
    {
        var evdev = Channel.CreateUnbounded<EvdevEvent>();
        var frames = Channel.CreateUnbounded<TouchFrame>();

        await using var stream = File.OpenRead(file);
        await EvdevParser.RunAsync(PipeReader.Create(stream), evdev.Writer, Rm2.EventLayout, CancellationToken.None);
        await TouchStateMachine.RunAsync(evdev.Reader, frames.Writer, new TouchOptions(), CancellationToken.None);

        var result = new List<TouchFrame>();
        await foreach (var f in frames.Reader.ReadAllAsync()) result.Add(f);
        return result;
    }

    private static (CoordinateMapper Pen, TouchCoordinateMapper Touch) Mappers(
        Orientation o = Orientation.Portrait)
    {
        var opts = MappingOptions.ForScreen(1920, 1080, o, FitMode.Stretch);
        var pen = new CoordinateMapper(opts, Rm2, PressureCurve.Linear);
        return (pen, new TouchCoordinateMapper(opts, Rm2, pen.Transform));
    }

    // ── The capture: phase A is a fingertip then the pen tip on the top-left and
    //    top-right corners (portrait, USB-C edge at the bottom); phase B is a
    //    held mid-screen contact with the pen approaching. Only phase A locates
    //    corners, so both helpers below take the FIRST TWO deliberate contacts
    //    rather than every sample — the file also contains phase B's mid-screen
    //    taps, and asserting over all of them would be asserting the wrong thing.

    /// <summary>Median mapped position of each of the first two sustained pen strokes.</summary>
    private static List<(int X, int Y)> FirstTwoPenHolds(List<PenFrame> frames, CoordinateMapper pen)
    {
        var holds = new List<List<(int X, int Y)>>();
        List<(int X, int Y)>? current = null;

        foreach (var f in frames)
            if (f.Pressure > 0)
            {
                var m = pen.Map(f);
                (current ??= []).Add((m.ScreenX, m.ScreenY));
            }
            else if (current is not null)
            {
                if (current.Count > 200) holds.Add(current); // a deliberate hold, not a tap
                current = null;
            }

        if (current is { Count: > 200 }) holds.Add(current);
        Assert.True(holds.Count >= 2, $"expected two sustained pen holds, found {holds.Count}");
        return holds.Take(2).Select(Median).ToList();
    }

    /// <summary>Median mapped position of each of the first two touch contacts, by tracking ID.</summary>
    private static List<(int X, int Y)> FirstTwoTouchContacts(
        List<TouchFrame> frames, TouchCoordinateMapper touch)
    {
        var order = new List<int>();
        var points = new Dictionary<int, List<(int X, int Y)>>();

        foreach (var mapped in frames.Select(touch.Map))
        foreach (var c in mapped.Contacts)
        {
            if (!points.TryGetValue(c.TrackingId, out var list))
            {
                points[c.TrackingId] = list = [];
                order.Add(c.TrackingId);
            }

            list.Add((c.ScreenX, c.ScreenY));
        }

        Assert.True(order.Count >= 2, $"expected at least two contacts, found {order.Count}");
        return order.Take(2).Select(id => Median(points[id])).ToList();
    }

    private static (int X, int Y) Median(List<(int X, int Y)> pts)
    {
        var xs = pts.Select(p => p.X).Order().ToList();
        var ys = pts.Select(p => p.Y).Order().ToList();
        return (xs[xs.Count / 2], ys[ys.Count / 2]);
    }

    [Fact]
    public async Task PenCorners_LandOnTheTopLeftAndTopRightOfTheScreen()
    {
        var (pen, _) = Mappers();
        var holds = FirstTwoPenHolds(await ReplayPenAsync(Capture("corners-pen-2026-07-25.bin")), pen);

        // Both were on the top edge; a Y flip would put them near 1079.
        Assert.All(holds, h => Assert.InRange(h.Y, 0, 150));

        // First hold was the left corner, second the right. A mirrored X swaps them.
        Assert.InRange(holds[0].X, 0, 200);
        Assert.InRange(holds[1].X, 1700, 1919);
    }

    [Fact]
    public async Task TouchCorners_LandOnTheTopLeftAndTopRightOfTheScreen()
    {
        var (_, touch) = Mappers();
        var contacts = FirstTwoTouchContacts(
            await ReplayTouchAsync(Capture("corners-touch-2026-07-25.bin")), touch);

        // This is the assertion the pre-2026-07-25 mapping failed: with Y taken as
        // top-origin, a touch on the top edge landed at the bottom of the screen.
        Assert.All(contacts, c => Assert.InRange(c.Y, 0, 150));

        Assert.InRange(contacts[0].X, 0, 250);
        Assert.InRange(contacts[1].X, 1650, 1919);
    }

    [Fact]
    public async Task PenAndTouchCaptures_AgreeOnWhereTheCornersAre()
    {
        // Captured seconds apart in the same hold, touching the same two corners,
        // so the mapped positions must coincide. Driven by hardware data rather
        // than by sample values transcribed into a test.
        var (pen, touch) = Mappers();

        var penHolds = FirstTwoPenHolds(
            await ReplayPenAsync(Capture("corners-pen-2026-07-25.bin")), pen);
        var touchContacts = FirstTwoTouchContacts(
            await ReplayTouchAsync(Capture("corners-touch-2026-07-25.bin")), touch);

        // A finger pad and a pen tip on "the same corner" differ by a few mm; a
        // mirrored axis differs by ~1900 px.
        for (var i = 0; i < 2; i++)
        {
            Assert.InRange(Math.Abs(penHolds[i].X - touchContacts[i].X), 0, 150);
            Assert.InRange(Math.Abs(penHolds[i].Y - touchContacts[i].Y), 0, 150);
        }
    }

    // ── The capture: fingertip circling while the pen enters proximity three
    //    times. Documents firmware arbitration and exercises the gate. ─────────

    [Fact]
    public async Task ProximityCapture_ContactSurvivesPenProximityAndIsReleased()
    {
        var frames = await ReplayTouchAsync(Capture("proximity-touch-2026-07-25.bin"));

        // The held contact reported continuously through three pen-proximity
        // windows — the measurement that reclassified the stale sweep as
        // precaution rather than a fix for observed behavior.
        var withContacts = frames.Count(f => f.Contacts.Count > 0);
        Assert.True(withContacts > 500, $"expected a long continuous contact, saw {withContacts} frames");

        // And the stream ends clean: no contact left live at the end of the file.
        Assert.Empty(frames[^1].Contacts);
    }

    [Fact]
    public async Task EveryCapture_ParsesAsWholeEventsWithNoDesync()
    {
        // A 16-byte stream misread as 24 (the rMPP layout) desyncs into garbage
        // codes rather than failing outright — the failure mode behind
        // Evidlo/remarkable_mouse#92. Replaying real bytes guards the layout.
        foreach (var name in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples"), "*.bin"))
        {
            var length = new FileInfo(name).Length;
            Assert.True(length % 16 == 0, $"{Path.GetFileName(name)} is not a whole number of 16-byte events");

            var evdev = Channel.CreateUnbounded<EvdevEvent>();
            await using var stream = File.OpenRead(name);
            await EvdevParser.RunAsync(
                PipeReader.Create(stream), evdev.Writer, EvdevLayout.Bits32, CancellationToken.None);

            var count = 0;
            var known = 0;
            await foreach (var ev in evdev.Reader.ReadAllAsync())
            {
                count++;
                // EV_SYN/EV_KEY/EV_ABS are the only types these devices emit.
                if (ev.Type is EvdevTypes.EV_SYN or EvdevTypes.EV_KEY or EvdevTypes.EV_ABS) known++;
            }

            Assert.Equal(length / 16, count);
            Assert.True(known == count,
                $"{Path.GetFileName(name)}: {count - known} events had unexpected types — layout desync");
        }
    }
}

using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Pipeline;
using Xunit;

namespace RemarkableTablet.Core.Tests;

/// <summary>
///     Location-based touch arbitration: only what sits under the writing hand is
///     withheld, so the off hand can keep gesturing mid-stroke. Worth having on
///     this hardware specifically because the rM2 firmware keeps reporting a
///     contact that was already established when the pen arrived — the off-hand
///     gesture is physically available, and only full arbitration discards it.
/// </summary>
public class RegionArbitrationTests
{
    // 10 px per mm keeps the arithmetic legible: the default region is then
    // 1500 px behind the tip, 300 ahead, 1200 inboard, 300 outboard.
    private const double PxPerMm = 10;

    private long _now;

    private PenProximityGate Make(Handedness hand = Handedness.Right, ArbitrationOptions? opts = null)
    {
        return new PenProximityGate(
            opts ?? new ArbitrationOptions { Mode = ArbitrationMode.Region, Hand = hand },
            PxPerMm, PxPerMm, () => _now);
    }

    private static MappedFrame Pen(int x, int y, bool inRange = true, int tiltX = 0)
    {
        return new MappedFrame(x, y, 0, tiltX, 0, 0, false, false, false, inRange);
    }

    private static MappedTouchFrame Touch(params (int Id, int X, int Y)[] contacts)
    {
        var mapped = new MappedTouchContact[contacts.Length];
        for (var i = 0; i < contacts.Length; i++)
            mapped[i] = new MappedTouchContact(i, contacts[i].Id, contacts[i].X, contacts[i].Y, 500, 12, 10);
        return new MappedTouchFrame(mapped);
    }

    private static int[] Ids(MappedTouchFrame? frame)
    {
        return frame?.Contacts.Select(c => c.TrackingId).ToArray() ?? [];
    }

    // ── The point of the feature ──────────────────────────────────────────────

    [Fact]
    public void OffHandContact_SurvivesWhileTheWritingHandIsSuppressed()
    {
        var gate = Make();
        gate.OnPenFrame(Pen(1000, 500));

        // id 1 sits just below-right of the tip — under the writing hand.
        // id 2 is far to the left, where the other hand would be.
        var result = gate.Filter(Touch((1, 1100, 700), (2, 200, 500)));

        Assert.Equal([2], Ids(result));
    }

    [Fact]
    public void FullArbitration_StillDropsEverything()
    {
        // The default must not change: region mode is opt-in until validated.
        var gate = new PenProximityGate(
            new ArbitrationOptions { Mode = ArbitrationMode.Full }, PxPerMm, PxPerMm, () => _now);
        gate.OnPenFrame(Pen(1000, 500));

        Assert.Null(gate.Filter(Touch((1, 1100, 700), (2, 200, 500))));
        Assert.True(gate.TakePendingRelease());
    }

    [Fact]
    public void RegionMode_NeverAsksForABlanketRelease()
    {
        // A blanket ReleaseAll would kill the off-hand contact we are trying to
        // preserve; region mode drops contacts individually, by omission.
        var gate = Make();
        gate.OnPenFrame(Pen(1000, 500));
        gate.Filter(Touch((1, 1100, 700)));

        Assert.False(gate.TakePendingRelease());
    }

    [Fact]
    public void OffMode_ForwardsEverythingEvenWithThePenDown()
    {
        var gate = Make(opts: new ArbitrationOptions { Mode = ArbitrationMode.Off });
        gate.OnPenFrame(Pen(1000, 500));

        Assert.Equal([1, 2], Ids(gate.Filter(Touch((1, 1000, 520), (2, 200, 500)))));
    }

    // ── Region geometry ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(1000, 600, false, "just behind the tip — heel of the hand")]
    [InlineData(1500, 900, false, "behind and inboard — the palm")]
    [InlineData(1000, -100, true, "well ahead of the tip (60mm)")]
    [InlineData(400, 500, true, "outboard, past the measured 43mm reach")]
    [InlineData(1000, 2400, true, "190mm behind — further than a hand reaches")]
    [InlineData(2300, 500, true, "130mm inboard — further than a hand reaches")]
    public void RightHanded_SuppressesBehindAndToTheRightOfTheTip(
        int x, int y, bool expectForwarded, string why)
    {
        var gate = Make();
        gate.OnPenFrame(Pen(1000, 500));

        var result = gate.Filter(Touch((7, x, y)));

        Assert.True(expectForwarded == (Ids(result).Length == 1), why);
    }

    [Fact]
    public void LeftHanded_MirrorsTheRegion()
    {
        var right = Make();
        var left = Make(Handedness.Left);
        var toTheLeftOfTheTip = Touch((7, 400, 700));

        right.OnPenFrame(Pen(1000, 500));
        left.OnPenFrame(Pen(1000, 500));

        Assert.Equal([7], Ids(right.Filter(toTheLeftOfTheTip))); // outboard for a right-hander
        Assert.Empty(Ids(left.Filter(toTheLeftOfTheTip))); // under a left-hander's palm
    }

    [Fact]
    public void RegionFollowsThePen()
    {
        var gate = Make();
        var contact = Touch((7, 1500, 900));

        gate.OnPenFrame(Pen(200, 200)); // pen far away — contact is clear of it
        Assert.Equal([7], Ids(gate.Filter(contact)));

        gate.OnPenFrame(Pen(1400, 800)); // pen moves next to it
        Assert.Empty(Ids(gate.Filter(Touch((8, 1500, 900)))));
    }

    // ── Latching ──────────────────────────────────────────────────────────────

    [Fact]
    public void ASuppressedContact_StaysSuppressedAfterDriftingOutOfTheRegion()
    {
        // Same one-way rule as palm classification: a hand that was under the pen
        // must not become live again because it shifted, or because the pen moved.
        var gate = Make();
        gate.OnPenFrame(Pen(1000, 500));
        Assert.Empty(Ids(gate.Filter(Touch((7, 1100, 700)))));

        // The pen moves away; the same contact is now geometrically clear.
        gate.OnPenFrame(Pen(200, 200));
        Assert.Empty(Ids(gate.Filter(Touch((7, 1100, 700)))));
    }

    [Fact]
    public void ASuppressedContact_StaysSuppressedAfterThePenLifts()
    {
        var gate = Make();
        gate.OnPenFrame(Pen(1000, 500));
        gate.Filter(Touch((7, 1100, 700)));

        _now += PenProximityGate.LingerMs + 1; // pen gone, linger expired

        Assert.Empty(Ids(gate.Filter(Touch((7, 1100, 700)))));
    }

    [Fact]
    public void OnceLifted_TheSameSpotIsUsableAgain()
    {
        var gate = Make();
        gate.OnPenFrame(Pen(1000, 500));
        gate.Filter(Touch((7, 1100, 700)));
        _now += PenProximityGate.LingerMs + 1;

        gate.Filter(MappedTouchFrame.Empty); // hand lifted
        Assert.Equal([8], Ids(gate.Filter(Touch((8, 1100, 700))))); // new contact, same place
    }

    // ── The measured distribution ─────────────────────────────────────────────
    //
    // Offsets in mm from the pen tip, from handrest-*-2026-07-27.bin: a minute of
    // writing with the hand resting, 4904 samples where a contact and a pen
    // position coincide. The region must cover the hand's spread without
    // swallowing the whole panel, so both directions are asserted.

    [Theory]
    [InlineData(+54, +91, "median hand position")]
    [InlineData(+103, +160, "p99 — the far corner of the palm")]
    [InlineData(-36, -33, "p1 — knuckles outboard and slightly ahead of the nib")]
    [InlineData(-43, +20, "the furthest-left contact observed")]
    public void MeasuredHandPositions_AreAllSuppressed(int dxMm, int dyMm, string what)
    {
        var gate = Make();
        gate.OnPenFrame(Pen(1000, 500));

        var result = gate.Filter(Touch((7, 1000 + (int)(dxMm * PxPerMm), 500 + (int)(dyMm * PxPerMm))));

        Assert.True(Ids(result).Length == 0, $"{what} ({dxMm:+0;-0}, {dyMm:+0;-0} mm) should be suppressed");
    }

    [Theory]
    [InlineData(-80, +40, "off hand, well outboard")]
    [InlineData(+40, -80, "a finger well above the nib")]
    [InlineData(+40, +200, "beyond the heel of the hand")]
    [InlineData(+140, +60, "further inboard than the hand reaches")]
    public void PositionsBeyondTheMeasuredSpread_AreForwarded(int dxMm, int dyMm, string what)
    {
        // The complement of the assertion above: the region has to end somewhere,
        // or region mode is just full arbitration with extra arithmetic.
        var gate = Make();
        gate.OnPenFrame(Pen(1000, 500));

        var result = gate.Filter(Touch((7, 1000 + (int)(dxMm * PxPerMm), 500 + (int)(dyMm * PxPerMm))));

        Assert.True(Ids(result).Length == 1, $"{what} ({dxMm:+0;-0}, {dyMm:+0;-0} mm) should be forwarded");
    }

    // ── Handedness from contact position and tilt ─────────────────────────────

    [Fact]
    public void Auto_LearnsHandednessFromWhereContactsSit()
    {
        // The stronger signal: 84% of hand contacts sat right of the tip for a
        // right-hander, against 70% for tilt. Contacts vote even while suppressed.
        var gate = Make(Handedness.Auto);

        for (var i = 0; i < 30; i++)
        {
            gate.OnPenFrame(Pen(1000, 500)); // no tilt at all
            gate.Filter(Touch((7, 1000 + (int)(54 * PxPerMm), 500 + (int)(91 * PxPerMm))));
        }

        Assert.Equal(Handedness.Right, gate.ResolvedHand);
    }

    [Fact]
    public void Auto_IgnoresAContactTooFarAwayToBeTheWritingHand()
    {
        // A finger halfway across the panel says nothing about which hand holds
        // the pen, and must not vote.
        var gate = Make(Handedness.Auto);

        for (var i = 0; i < 40; i++)
        {
            gate.OnPenFrame(Pen(1000, 500));
            gate.Filter(Touch((7, 1000 - (int)(300 * PxPerMm), 500 + (int)(40 * PxPerMm))));
        }

        Assert.Equal(Handedness.Auto, gate.ResolvedHand);
    }

    [Fact]
    public void Auto_StartsSymmetricSoItErrsTowardSuppressing()
    {
        // Before tilt has voted, cover both sides: over-suppressing is the safe
        // direction to be wrong in, and it degrades toward full arbitration.
        var gate = Make(Handedness.Auto);
        gate.OnPenFrame(Pen(1000, 500, tiltX: 0));

        Assert.Equal(Handedness.Auto, gate.ResolvedHand);
        Assert.Empty(Ids(gate.Filter(Touch((7, 400, 700))))); // left of tip: suppressed
        Assert.Empty(Ids(gate.Filter(Touch((8, 1600, 700))))); // right of tip: suppressed
    }

    [Fact]
    public void Auto_SettlesOnRightWhenThePenLeansRight()
    {
        // Measured convention: +TiltX on screen is a lean to the right, which is
        // how a right-hander holds the pen.
        var gate = Make(Handedness.Auto);
        for (var i = 0; i < 30; i++) gate.OnPenFrame(Pen(1000, 500, tiltX: 40));

        Assert.Equal(Handedness.Right, gate.ResolvedHand);
        Assert.Equal([7], Ids(gate.Filter(Touch((7, 400, 700))))); // now outboard → forwarded
    }

    [Fact]
    public void Auto_SettlesOnLeftWhenThePenLeansLeft()
    {
        var gate = Make(Handedness.Auto);
        for (var i = 0; i < 30; i++) gate.OnPenFrame(Pen(1000, 500, tiltX: -40));

        Assert.Equal(Handedness.Left, gate.ResolvedHand);
        Assert.Equal([7], Ids(gate.Filter(Touch((7, 1600, 700)))));
    }

    [Fact]
    public void Auto_IsNotSwayedByAFewFramesOfTheOtherLean()
    {
        // Wrist rotation mid-stroke should not flip the region under the hand.
        var gate = Make(Handedness.Auto);
        for (var i = 0; i < 40; i++) gate.OnPenFrame(Pen(1000, 500, tiltX: 40));
        for (var i = 0; i < 10; i++) gate.OnPenFrame(Pen(1000, 500, tiltX: -40));

        Assert.Equal(Handedness.Right, gate.ResolvedHand);
    }

    [Fact]
    public void ExplicitHandedness_IgnoresTilt()
    {
        var gate = Make(Handedness.Left);
        for (var i = 0; i < 50; i++) gate.OnPenFrame(Pen(1000, 500, tiltX: 40));

        Assert.Equal(Handedness.Left, gate.ResolvedHand);
    }
}

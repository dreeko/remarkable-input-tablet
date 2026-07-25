using RemarkableTablet.Core.Gestures;
using RemarkableTablet.Core.Output;
using Xunit;

namespace RemarkableTablet.Core.Tests;

public class GestureEngineTests
{
    private static MappedTouchContact C(int trackingId, int x, int y, int slot = 0, int major = 0)
    {
        return new MappedTouchContact(slot, trackingId, x, y, 0, major);
    }

    [Fact]
    public void AnchorsAreTheTwoSmallestContacts_NotTheFirstTwoSlots()
    {
        // A resting palm arrives in a low slot and would otherwise anchor the
        // gesture, leaving the user's actual fingers as the ignored third contact.
        var engine = new GestureEngine();
        var palm = C(1, 0, 0, 0, 200);
        var fingerA = C(2, 100, 100, 1, 12);
        var fingerB = C(3, 300, 500, 2, 14);

        var begin = Assert.IsType<GestureBegin>(
            Assert.Single(engine.Process(Frame(palm, fingerA, fingerB))));

        // Centroid of the two fingers, with the palm ignored.
        Assert.Equal(200, begin.CenterX);
        Assert.Equal(300, begin.CenterY);

        // And the gesture tracks those two: dropping the palm changes nothing.
        var next = engine.Process(Frame(fingerA, fingerB));
        Assert.DoesNotContain(next, e => e is GestureEnd);
    }

    [Fact]
    public void WithoutSizeData_FallsBackToArrivalOrder()
    {
        // Devices that don't report contact size all tie at TouchMajor 0, which
        // must degrade to the old behavior rather than picking arbitrarily.
        var engine = new GestureEngine();
        var begin = Assert.IsType<GestureBegin>(
            Assert.Single(engine.Process(Frame(C(1, 100, 100), C(2, 300, 500, 1), C(3, 900, 900, 2)))));

        Assert.Equal(200, begin.CenterX);
        Assert.Equal(300, begin.CenterY);
    }

    private static MappedTouchFrame Frame(params MappedTouchContact[] contacts)
    {
        return new MappedTouchFrame(contacts);
    }

    [Fact]
    public void NoGestureUntilTwoContactsArrive()
    {
        var engine = new GestureEngine();

        Assert.Empty(engine.Process(MappedTouchFrame.Empty));
        Assert.Empty(engine.Process(Frame(C(1, 100, 100))));
    }

    [Fact]
    public void TwoContacts_EmitsBeginWithCentroid()
    {
        var engine = new GestureEngine();
        var ev = engine.Process(Frame(C(1, 100, 100), C(2, 300, 500, 1)));

        var begin = Assert.IsType<GestureBegin>(Assert.Single(ev));
        Assert.Equal(200, begin.CenterX);
        Assert.Equal(300, begin.CenterY);
    }

    [Fact]
    public void PurePan_EmitsPanWithCorrectDelta()
    {
        var engine = new GestureEngine();
        engine.Process(Frame(C(1, 100, 100), C(2, 300, 100)));

        // Both contacts shift +50 in X; centroid moves +50 in X, 0 in Y.
        var ev = engine.Process(Frame(C(1, 150, 100), C(2, 350, 100)));

        var pan = ev.OfType<GesturePan>().Single();
        Assert.Equal(50, pan.DeltaX);
        Assert.Equal(0, pan.DeltaY);

        // No pinch (distance unchanged) or rotate (angle unchanged).
        Assert.DoesNotContain(ev, e => e is GesturePinch);
        Assert.DoesNotContain(ev, e => e is GestureRotate);
    }

    [Fact]
    public void PurePinchOut_EmitsPinchWithScaleAbove1()
    {
        var engine = new GestureEngine();
        engine.Process(Frame(C(1, 100, 100), C(2, 200, 100))); // distance 100

        // Spread: contact 1 → 50, contact 2 → 250 → distance 200
        var ev = engine.Process(Frame(C(1, 50, 100), C(2, 250, 100)));

        var pinch = ev.OfType<GesturePinch>().Single();
        Assert.True(pinch.ScaleDelta > 1.0, $"expected >1.0 got {pinch.ScaleDelta}");
        Assert.Equal(2.0, pinch.ScaleDelta, 6);
    }

    [Fact]
    public void PurePinchIn_EmitsPinchWithScaleBelow1()
    {
        var engine = new GestureEngine();
        engine.Process(Frame(C(1, 0, 100), C(2, 200, 100))); // distance 200
        var ev = engine.Process(Frame(C(1, 50, 100), C(2, 150, 100))); // distance 100

        var pinch = ev.OfType<GesturePinch>().Single();
        Assert.Equal(0.5, pinch.ScaleDelta, 6);
    }

    [Fact]
    public void PureRotate_EmitsRotateWithDegreeDelta()
    {
        var engine = new GestureEngine();
        engine.Process(Frame(C(1, 0, 0), C(2, 100, 0))); // angle 0°

        // Rotate the pair 90° around their centroid:
        //   centroid was (50, 0); now contacts are at (50, -50) and (50, 50).
        //   angle from a(50,-50) to b(50,50) is +90° (atan2(100, 0)).
        var ev = engine.Process(Frame(C(1, 50, -50), C(2, 50, 50)));

        var rotate = ev.OfType<GestureRotate>().Single();
        Assert.Equal(90.0, rotate.DegreesDelta, 4);
    }

    [Fact]
    public void ContactReleased_EmitsEndAndResetsState()
    {
        var engine = new GestureEngine();
        engine.Process(Frame(C(1, 100, 100), C(2, 300, 100)));
        var ev = engine.Process(Frame(C(1, 100, 100))); // contact 2 lifted

        Assert.IsType<GestureEnd>(Assert.Single(ev));

        // After end, a single contact alone must NOT start a new gesture.
        Assert.Empty(engine.Process(Frame(C(1, 100, 100))));
    }

    [Fact]
    public void NewGestureCanStartAfterPreviousEnded()
    {
        var engine = new GestureEngine();
        engine.Process(Frame(C(1, 0, 0), C(2, 100, 0)));
        engine.Process(MappedTouchFrame.Empty); // end

        // New two-finger contact with fresh tracking IDs starts a new gesture.
        var ev = engine.Process(Frame(C(10, 200, 200), C(11, 400, 200)));
        Assert.Single(ev.OfType<GestureBegin>());
    }

    [Fact]
    public void ThirdContact_IsIgnoredDuringActiveGesture()
    {
        var engine = new GestureEngine();
        engine.Process(Frame(C(1, 0, 0), C(2, 100, 0)));

        // Third finger lands; both anchors still present — gesture continues.
        var ev = engine.Process(Frame(C(1, 0, 0), C(2, 100, 0), C(3, 50, 50, 2)));

        // Anchors haven't moved, so no pan/pinch/rotate.
        Assert.Empty(ev);
    }
}
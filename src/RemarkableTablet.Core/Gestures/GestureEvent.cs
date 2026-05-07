namespace RemarkableTablet.Core.Gestures;

/// <summary>
///     Semantic gesture events emitted by <see cref="GestureEngine" />.
///     Consumed by host-side synthesized output (Ctrl+wheel for zoom, wheel
///     or middle-drag for pan) when real touch injection is unavailable.
///     Real touch injection bypasses this layer entirely — the OS or app
///     does its own gesture recognition on the raw injected contacts.
/// </summary>
public abstract record GestureEvent;

/// <summary>
///     Two-finger gesture started. Centroid is in screen pixels.
/// </summary>
public sealed record GestureBegin(int CenterX, int CenterY) : GestureEvent;

/// <summary>
///     Per-frame pinch delta. ScaleDelta is multiplicative — 1.0 means no
///     change, &gt;1.0 means fingers spread (zoom in), &lt;1.0 means fingers
///     pinch together (zoom out).
/// </summary>
public sealed record GesturePinch(double ScaleDelta) : GestureEvent;

/// <summary>
///     Per-frame pan delta of the centroid in screen pixels.
/// </summary>
public sealed record GesturePan(int DeltaX, int DeltaY) : GestureEvent;

/// <summary>
///     Per-frame rotate delta in degrees, normalized to (-180, 180].
///     Positive is counter-clockwise (matching the math convention).
/// </summary>
public sealed record GestureRotate(double DegreesDelta) : GestureEvent;

/// <summary>
///     Two-finger gesture ended. Always paired 1:1 with a previous Begin.
/// </summary>
public sealed record GestureEnd : GestureEvent;

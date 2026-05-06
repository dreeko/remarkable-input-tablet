namespace RemarkableTablet.Core.Tablet;

/// <summary>
/// A complete pen state snapshot emitted once per EV_SYN SYN_REPORT frame.
/// All values are in raw tablet units — coordinate mapping happens downstream.
/// </summary>
public readonly record struct PenFrame(
    int  X,
    int  Y,
    int  Pressure,
    int  TiltX,
    int  TiltY,
    int  Distance,
    bool IsTouch,
    bool IsEraser,
    bool BarrelButton1,
    bool BarrelButton2,
    bool InRange
);

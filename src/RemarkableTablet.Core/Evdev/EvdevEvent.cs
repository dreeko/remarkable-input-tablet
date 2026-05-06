namespace RemarkableTablet.Core.Evdev;

/// <summary>
/// A single decoded Linux input_event from the rM2 pen device.
/// The raw 16-byte struct layout (32-bit ARM):
///   uint32 sec, uint32 usec, uint16 type, uint16 code, int32 value
/// </summary>
public readonly record struct EvdevEvent(ushort Type, ushort Code, int Value);

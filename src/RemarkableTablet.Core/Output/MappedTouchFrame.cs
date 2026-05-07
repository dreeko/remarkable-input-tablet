namespace RemarkableTablet.Core.Output;

/// <summary>
///     A touch frame after coordinate mapping. Contacts are in screen pixels,
///     ordered by slot index. May be empty (zero active contacts).
/// </summary>
public sealed record MappedTouchFrame(IReadOnlyList<MappedTouchContact> Contacts)
{
    public static readonly MappedTouchFrame Empty = new(Array.Empty<MappedTouchContact>());
}

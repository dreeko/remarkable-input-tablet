namespace RemarkableTablet.Core.Tablet;

/// <summary>
///     A snapshot of all currently-active touch contacts at one SYN_REPORT.
///     The list is ordered by slot index ascending and may be empty (zero
///     active contacts).
/// </summary>
public sealed record TouchFrame(IReadOnlyList<TouchContact> Contacts)
{
    public static readonly TouchFrame Empty = new(Array.Empty<TouchContact>());
}
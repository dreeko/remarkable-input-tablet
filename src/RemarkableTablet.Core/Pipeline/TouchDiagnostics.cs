namespace RemarkableTablet.Core.Pipeline;

/// <summary>
///     Observability for the touch path's silent decisions. Without these, a
///     dropped contact and a contact that was never there look identical.
/// </summary>
/// <param name="DroppedContacts">
///     Contacts never forwarded to the host: the output-slot pool was full, or the
///     contact-size filter rejected them. Counted once per contact.
/// </param>
/// <param name="StaleReleases">
///     Contacts released by the staleness sweep instead of by the device. Non-zero
///     means the firmware stopped reporting a live contact — the failure mode the
///     sweep exists for.
/// </param>
/// <param name="PenGateClosures">Times touch was withheld because the pen came into range.</param>
public readonly record struct TouchDiagnostics(
    int DroppedContacts,
    int StaleReleases,
    int PenGateClosures);

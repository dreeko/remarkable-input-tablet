namespace RemarkableTablet.Core.Output;

/// <summary>
///     Sink for mapped multi-touch frames. Parallel to <see cref="IOutputMode" />
///     for the pen pipeline. Implementations either inject real touch contacts
///     (Win32 InjectTouchInput, Linux uinput-MT) or feed a gesture recognizer
///     for synthesized scroll output.
/// </summary>
public interface ITouchOutput : IDisposable
{
    void Initialize();
    void Send(MappedTouchFrame frame);

    /// <summary>
    ///     Emit a synthetic "all contacts released" so downstream apps don't
    ///     get stuck contacts after a transport drop. Called by the pipeline
    ///     before each reconnect attempt.
    /// </summary>
    void ReleaseAll();
}
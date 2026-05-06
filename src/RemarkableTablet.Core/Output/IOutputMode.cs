namespace RemarkableTablet.Core.Output;

public interface IOutputMode : IDisposable
{
    void Initialize();
    void Send(MappedFrame frame);
}

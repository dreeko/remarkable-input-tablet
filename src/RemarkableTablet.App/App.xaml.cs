using System.Windows;
using System.Windows.Forms;
using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Pipeline;
using RemarkableTablet.Core.Transport;
using RemarkableTablet.Windows.Output;

namespace RemarkableTablet.App;

public partial class App : System.Windows.Application
{
    private TrayIcon?       _trayIcon;
    private TabletPipeline? _pipeline;

    public bool IsConnected => _pipeline is not null;

    public event Action<ConnectionState>? PipelineStateChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _trayIcon = new TrayIcon(this);
        _trayIcon.Initialize();

        var settings = AppSettings.Load();
        if (settings.AutoConnect)
            _trayIcon.TriggerAutoConnect(settings);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopPipeline();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    public void StartPipeline(ConnectionOptions connOpts, MappingOptions mappingOpts, string outputMode)
    {
        if (_pipeline is not null) return;

        var mapper    = new CoordinateMapper(mappingOpts);
        IOutputMode output = outputMode == "mouse"
            ? (IOutputMode)new MouseOutput()
            : new WindowsInkOutput();
        var transport = new SshTransport(connOpts);

        _pipeline = new TabletPipeline(transport, mapper, output);
        _pipeline.ConnectionStateChanged += OnPipelineStateChanged;

        _ = RunPipelineAsync();
    }

    public void StopPipeline()
    {
        _pipeline?.Stop();
        // _pipeline is nulled inside RunPipelineAsync after the task completes
    }

    private async Task RunPipelineAsync()
    {
        try
        {
            await _pipeline!.RunAsync();
        }
        catch { }

        await _pipeline!.DisposeAsync();
        _pipeline = null;
        Dispatcher.Invoke(() => PipelineStateChanged?.Invoke(ConnectionState.Disconnected));
    }

    private void OnPipelineStateChanged(ConnectionState state)
    {
        Dispatcher.Invoke(() => PipelineStateChanged?.Invoke(state));
    }
}

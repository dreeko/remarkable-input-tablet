using System.IO;
using System.Windows;
using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Pipeline;
using RemarkableTablet.Core.Transport;
using RemarkableTablet.Windows.Interop;
using RemarkableTablet.Windows.Output;
using Application = System.Windows.Application;

namespace RemarkableTablet.App;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "remarkable-input-tablet", "app.log");

    private TabletPipeline? _pipeline;
    private TrayIcon? _trayIcon;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            WriteLog($"UnhandledException: {ex.ExceptionObject}");
        DispatcherUnhandledException += (_, ex) =>
        {
            WriteLog($"DispatcherUnhandledException: {ex.Exception}");
            ex.Handled = true;
        };
    }

    public bool IsConnected => _pipeline is not null;

    public event Action<ConnectionState>? PipelineStateChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WriteLog("OnStartup begin");
        ScreenMetrics.EnablePerMonitorDpiAwareness();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _trayIcon = new TrayIcon(this);
        _trayIcon.Initialize();
        WriteLog("TrayIcon initialized");

        var settings = AppSettings.Load();
        if (settings.AutoConnect)
            _trayIcon.TriggerAutoConnect(settings);
        WriteLog("OnStartup complete");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        WriteLog("OnExit");
        StopPipeline();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    internal static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { }
    }

    public void StartPipeline(ConnectionOptions connOpts, MappingOptions mappingOpts, string outputMode)
    {
        if (_pipeline is not null) return;

        var mapper = new CoordinateMapper(mappingOpts);
        var output = outputMode == OutputModes.Mouse
            ? (IOutputMode)new MouseOutput()
            : new WindowsInkOutput();
        var transport = new SshTransport(connOpts);

        var pipeline = new TabletPipeline(transport, mapper, output);
        pipeline.ConnectionStateChanged += OnPipelineStateChanged;
        pipeline.Error += ex => WriteLog($"Pipeline error: {ex}");

        _pipeline = pipeline;
        _ = RunPipelineAsync(pipeline);
    }

    public void StopPipeline()
    {
        // Capture into a local so we can't race with RunPipelineAsync nulling the field.
        var p = _pipeline;
        p?.Stop();
    }

    private async Task RunPipelineAsync(TabletPipeline pipeline)
    {
        try
        {
            await pipeline.RunAsync();
        }
        catch (Exception ex)
        {
            WriteLog($"Pipeline error: {ex}");
        }

        await pipeline.DisposeAsync();
        _pipeline = null;
        await Dispatcher.BeginInvoke(() => PipelineStateChanged?.Invoke(ConnectionState.Disconnected));
    }

    private void OnPipelineStateChanged(ConnectionState state)
    {
        Dispatcher.BeginInvoke(() => PipelineStateChanged?.Invoke(state));
    }
}

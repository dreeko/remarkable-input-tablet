using System.IO;
using System.Windows;
using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Pipeline;
using RemarkableTablet.Core.Transport;
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
        var output = outputMode == "mouse"
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
        catch (Exception ex)
        {
            WriteLog($"Pipeline error: {ex}");
        }

        await _pipeline!.DisposeAsync();
        _pipeline = null;
        Dispatcher.Invoke(() => PipelineStateChanged?.Invoke(ConnectionState.Disconnected));
    }

    private void OnPipelineStateChanged(ConnectionState state)
    {
        Dispatcher.Invoke(() => PipelineStateChanged?.Invoke(state));
    }
}
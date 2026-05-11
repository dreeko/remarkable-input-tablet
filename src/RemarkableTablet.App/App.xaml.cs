using System.IO;
using System.Windows;
using RemarkableTablet.Core.Devices;
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

    public void StartPipeline(ConnectionOptions connOpts, MappingOptions mappingOpts, string outputMode,
        bool gestures = false, string? pressureCurve = null, string device = "auto")
    {
        if (_pipeline is not null) return;
        _ = StartPipelineAsync(connOpts, mappingOpts, outputMode, gestures, pressureCurve, device);
    }

    private async Task StartPipelineAsync(ConnectionOptions connOpts, MappingOptions mappingOpts,
        string outputMode, bool gestures, string? pressureCurve, string device)
    {
        try
        {
            var profile = await ResolveProfileAsync(connOpts, device);
            WriteLog($"Using profile: {profile.Name}");

            var mapper = new CoordinateMapper(mappingOpts, profile, PressureCurve.FromName(pressureCurve));
            var output = outputMode == OutputModes.Mouse
                ? (IOutputMode)new MouseOutput()
                : new WindowsInkOutput();

            TouchCoordinateMapper? touchMapper = null;
            ITouchOutput? touchOutput = null;
            if (gestures)
            {
                touchMapper = new TouchCoordinateMapper(mappingOpts, profile);
                touchOutput = new WindowsTouchInjectionOutput(profile.Touch.MaxTracked);
            }

            var transport = new SshTransport(connOpts);
            var pipeline = new TabletPipeline(transport, profile, mapper, output, touchMapper, touchOutput);
            pipeline.ConnectionStateChanged += OnPipelineStateChanged;
            pipeline.Error += ex => WriteLog($"Pipeline error: {ex}");

            _pipeline = pipeline;
            await RunPipelineAsync(pipeline);
        }
        catch (Exception ex)
        {
            WriteLog($"StartPipeline failed: {ex}");
            _pipeline = null;
            await Dispatcher.BeginInvoke(() => PipelineStateChanged?.Invoke(ConnectionState.Disconnected));
        }
    }

    private static async Task<DeviceProfile> ResolveProfileAsync(ConnectionOptions connOpts, string device)
    {
        var named = DeviceDetector.ByName(device);
        if (named is not null) return named;

        // "auto" or unknown — probe over a short-lived SSH session. The
        // streaming pipeline opens its own session afterwards.
        await using var probe = new SshTransport(connOpts);
        await probe.ConnectAsync(CancellationToken.None);
        return await DeviceDetector.DetectAsync(probe, CancellationToken.None);
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

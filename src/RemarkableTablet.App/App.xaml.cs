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
    private bool _pipelineStarting;
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
    public bool IsConnecting => _pipelineStarting;

    public event Action<ConnectionState>? PipelineStateChanged;

    /// <summary>
    ///     Raised on the UI thread when StartPipeline fails before reaching
    ///     the streaming loop — typically a probe timeout or auth failure.
    ///     Gives SettingsWindow something to display instead of a silent
    ///     transition to Disconnected.
    /// </summary>
    public event Action<string>? ConnectionErrorOccurred;

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
        bool gestures = false, string? pressureCurve = null, string device = "auto", string? areaSpec = null)
    {
        if (_pipeline is not null || _pipelineStarting) return;
        // Set this before the async method reaches its first await so repeated
        // Connect clicks during device probing cannot start parallel pipelines.
        _pipelineStarting = true;
        _ = StartPipelineAsync(connOpts, mappingOpts, outputMode, gestures, pressureCurve, device, areaSpec);
    }

    private async Task StartPipelineAsync(ConnectionOptions connOpts, MappingOptions mappingOpts,
        string outputMode, bool gestures, string? pressureCurve, string device, string? areaSpec)
    {
        SshTransport? transport = null;
        var transferred = false;
        try
        {
            // Resolve the profile. For "auto", we connect once for the probe
            // and then hand the same SshTransport to the pipeline — its first
            // ConnectAsync is a no-op (idempotent on already-connected
            // clients), which avoids paying for two SSH handshakes back-to-
            // back and halves the chance of a transient USB-Ethernet flake
            // stalling startup. For explicit rm2/rmpp the pipeline does its
            // own connect on the empty transport, same as before.
            transport = new SshTransport(connOpts);

            DeviceProfile profile;
            var named = DeviceDetector.ByName(device);
            if (named is not null)
            {
                profile = named;
            }
            else
            {
                await transport.ConnectAsync(CancellationToken.None);
                profile = await DeviceDetector.DetectAsync(transport, CancellationToken.None);
                WriteLog($"Detected: {profile.Name}");
            }

            // Millimetre areas can only be resolved once the profile is known,
            // which is after the device has been probed.
            if (TabletArea.TryParse(areaSpec, profile, mappingOpts.Orientation, out var area, out var areaError))
                mappingOpts = mappingOpts with
                {
                    TabletAreaX = area.X, TabletAreaY = area.Y, TabletAreaW = area.W, TabletAreaH = area.H
                };
            else
                WriteLog($"Ignoring active area '{areaSpec}': {areaError}");

            var mapper = new CoordinateMapper(mappingOpts, profile, PressureCurve.FromName(pressureCurve));
            var output = outputMode == OutputModes.Mouse
                ? (IOutputMode)new MouseOutput()
                : new WindowsInkOutput();

            TouchCoordinateMapper? touchMapper = null;
            ITouchOutput? touchOutput = null;
            if (gestures)
            {
                // Share the pen's fitted geometry so pen and touch agree on pixels.
                touchMapper = new TouchCoordinateMapper(mappingOpts, profile, mapper.Transform);
                touchOutput = new WindowsTouchInjectionOutput(profile.Touch.MaxTracked);
            }

            var pipeline = new TabletPipeline(transport, profile, mapper, output, touchMapper, touchOutput);
            pipeline.ConnectionStateChanged += OnPipelineStateChanged;
            // Node moved, or an unrecognised touch driver: goes to the log rather
            // than a dialog, but it is the first thing to check when someone
            // reports that the mapping is off.
            pipeline.DeviceNoticed += note => WriteLog($"Device note: {note}");
            pipeline.Error += ex =>
            {
                WriteLog($"Pipeline error: {ex}");
                // Forward to the UI so reconnect-loop failures (e.g. wrong IP,
                // device asleep) show a useful message instead of a silent
                // "Disconnected." Run on the dispatcher because the pipeline
                // raises Error from a thread-pool thread.
                var msg = ex.Message;
                _ = Dispatcher.BeginInvoke(() => ConnectionErrorOccurred?.Invoke(msg));
            };

            _pipeline = pipeline;
            _pipelineStarting = false;
            transferred = true; // pipeline owns the transport from here
            await RunPipelineAsync(pipeline);
        }
        catch (Exception ex)
        {
            WriteLog($"StartPipeline failed: {ex}");
            if (!transferred && transport is not null)
            {
                try { await transport.DisposeAsync(); } catch { /* best-effort */ }
            }
            _pipeline = null;
            _pipelineStarting = false;
            var msg = ex.Message;
            await Dispatcher.BeginInvoke(() =>
            {
                // Order matters: fire Disconnected first so the SettingsWindow
                // updates its status text, then fire the error so the more
                // useful message overwrites the generic "Disconnected." line.
                PipelineStateChanged?.Invoke(ConnectionState.Disconnected);
                ConnectionErrorOccurred?.Invoke(msg);
            });
        }
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

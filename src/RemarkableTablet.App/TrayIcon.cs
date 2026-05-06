using RemarkableTablet.Core.Transport;
using Application = System.Windows.Application;

namespace RemarkableTablet.App;

/// <summary>
///     System tray icon. Reflects pipeline connection state and exposes
///     Connect / Disconnect / Open Settings / Exit menu items.
///     Pipeline lifecycle is owned by App.xaml.cs; TrayIcon calls into it.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Application _app;
    private ToolStripMenuItem? _connectItem;
    private ToolStripMenuItem? _disconnectItem;
    private ContextMenuStrip? _menu;
    private NotifyIcon? _notify;
    private SettingsWindow? _settingsWindow;
    private ToolStripMenuItem? _statusItem;

    public TrayIcon(Application app)
    {
        _app = app;
    }

    private App AppInstance => (App)_app;

    public void Dispose()
    {
        if (AppInstance is { } a)
            a.PipelineStateChanged -= OnPipelineStateChanged;
        _notify?.Dispose();
        _menu?.Dispose();
    }

    public void Initialize()
    {
        _menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("Disconnected") { Enabled = false };
        _connectItem = new ToolStripMenuItem("Connect...", null, (_, _) => OpenSettings());
        _disconnectItem = new ToolStripMenuItem("Disconnect", null, (_, _) => Disconnect())
            { Enabled = false };

        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_connectItem);
        _menu.Items.Add(_disconnectItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Open Settings...", null, (_, _) => OpenSettings());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _notify = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "reMarkable Tablet",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notify.DoubleClick += (_, _) => OpenSettings();

        AppInstance.PipelineStateChanged += OnPipelineStateChanged;
    }

    public void TriggerAutoConnect(AppSettings settings)
    {
        // Open the settings window in auto-connect mode. We can't bypass the
        // password prompt (we deliberately don't store passwords), but the
        // window will auto-click Connect once the user types one and presses Enter.
        _ = settings;
        OpenSettings(true);
    }

    private void OpenSettings(bool autoConnect = false)
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(autoConnect);
        _settingsWindow.Show();
    }

    private void Disconnect()
    {
        AppInstance.StopPipeline();
    }

    private void OnPipelineStateChanged(ConnectionState state)
    {
        if (_notify is null || _statusItem is null) return;

        // BeginInvoke (fire-and-forget) avoids deadlocking the pipeline thread if
        // the UI thread is itself awaiting on related work.
        _app.Dispatcher.BeginInvoke(() =>
        {
            var (statusText, _) = state switch
            {
                ConnectionState.Connected => ("● Connected", false),
                ConnectionState.Connecting => ("○ Connecting…", true),
                _ => ("○ Disconnected", false)
            };

            _statusItem.Text = statusText;
            _notify.Text = $"reMarkable Tablet — {statusText}";
            _connectItem!.Enabled = state == ConnectionState.Disconnected;
            _disconnectItem!.Enabled = state != ConnectionState.Disconnected;
        });
    }

    private void ExitApp()
    {
        _app.Dispatcher.BeginInvoke(() => _app.Shutdown());
    }
}

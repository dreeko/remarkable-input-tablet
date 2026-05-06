using System.Drawing;
using System.Windows.Forms;
using RemarkableTablet.Core.Transport;

namespace RemarkableTablet.App;

/// <summary>
/// System tray icon. Reflects pipeline connection state and exposes
/// Connect / Disconnect / Open Settings / Exit menu items.
/// Pipeline lifecycle is owned by App.xaml.cs; TrayIcon calls into it.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Application _app;
    private NotifyIcon?       _notify;
    private ContextMenuStrip? _menu;
    private ToolStripMenuItem? _statusItem;
    private ToolStripMenuItem? _connectItem;
    private ToolStripMenuItem? _disconnectItem;
    private SettingsWindow?   _settingsWindow;

    public TrayIcon(System.Windows.Application app) => _app = app;

    public void Initialize()
    {
        _menu = new ContextMenuStrip();

        _statusItem     = new ToolStripMenuItem("Disconnected") { Enabled = false };
        _connectItem    = new ToolStripMenuItem("Connect...",    null, (_, _) => OpenSettings());
        _disconnectItem = new ToolStripMenuItem("Disconnect",    null, (_, _) => Disconnect())
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
            Icon             = SystemIcons.Application,
            Text             = "reMarkable Tablet",
            ContextMenuStrip = _menu,
            Visible          = true,
        };
        _notify.DoubleClick += (_, _) => OpenSettings();

        AppInstance.PipelineStateChanged += OnPipelineStateChanged;
    }

    public void TriggerAutoConnect(AppSettings settings)
    {
        // Open the settings window so the user can supply their password,
        // then auto-connect once it's provided.
        OpenSettings(autoConnect: true);
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

        // Must update WinForms controls — they're thread-safe for these properties,
        // but dispatch anyway to be safe.
        _app.Dispatcher.Invoke(() =>
        {
            (string statusText, bool connecting) = state switch
            {
                ConnectionState.Connected    => ("● Connected",    false),
                ConnectionState.Connecting   => ("○ Connecting…",  true),
                _                            => ("○ Disconnected", false),
            };

            _statusItem.Text         = statusText;
            _notify.Text             = $"reMarkable Tablet — {statusText}";
            _connectItem!.Enabled    = state == ConnectionState.Disconnected;
            _disconnectItem!.Enabled = state != ConnectionState.Disconnected;
        });
    }

    private App AppInstance => (App)_app;

    private void ExitApp()
    {
        _app.Dispatcher.Invoke(() => _app.Shutdown());
    }

    public void Dispose()
    {
        if (AppInstance is { } a)
            a.PipelineStateChanged -= OnPipelineStateChanged;
        _notify?.Dispose();
        _menu?.Dispose();
    }
}

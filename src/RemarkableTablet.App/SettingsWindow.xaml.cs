using System.Windows;
using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Transport;
using Renci.SshNet;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using TabletOrientation = RemarkableTablet.Core.Mapping.Orientation;

namespace RemarkableTablet.App;

public partial class SettingsWindow : Window
{
    private readonly bool _autoConnect;

    public SettingsWindow(bool autoConnect = false)
    {
        InitializeComponent();
        _autoConnect = autoConnect;
        PopulateMonitors();
        LoadSettings();

        AppInstance.PipelineStateChanged += OnPipelineStateChanged;
        Closed += (_, _) => AppInstance.PipelineStateChanged -= OnPipelineStateChanged;
    }

    private App AppInstance => (App)Application.Current;

    private void PopulateMonitors()
    {
        MonitorBox.Items.Clear();
        var screens = Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            MonitorBox.Items.Add($"Monitor {i + 1}{(s.Primary ? " (Primary)" : "")} — {s.Bounds.Width}×{s.Bounds.Height}");
        }

        MonitorBox.SelectedIndex = 0;
    }

    private void LoadSettings()
    {
        var s = AppSettings.Load();
        AddressBox.Text = s.Host;
        MonitorBox.SelectedIndex = Math.Min(s.MonitorIndex, MonitorBox.Items.Count - 1);
        OrientationBox.SelectedIndex = s.Orientation switch
        {
            "Landscape" => 1,
            "PortraitFlipped" => 2,
            "LandscapeFlipped" => 3,
            _ => 0
        };
        InkRadio.IsChecked = s.OutputMode != "mouse";
        MouseRadio.IsChecked = s.OutputMode == "mouse";
        AutoConnectBox.IsChecked = s.AutoConnect;

        if (_autoConnect)
            SetStatus("Enter password to auto-connect.");
    }

    private void SaveSettings()
    {
        var s = AppSettings.Load();
        s.Host = AddressBox.Text.Trim();
        s.MonitorIndex = MonitorBox.SelectedIndex;
        s.Orientation = OrientationBox.SelectedIndex switch
        {
            1 => "Landscape",
            2 => "PortraitFlipped",
            3 => "LandscapeFlipped",
            _ => "Portrait"
        };
        s.OutputMode = MouseRadio.IsChecked == true ? "mouse" : "ink";
        s.AutoConnect = AutoConnectBox.IsChecked == true;
        s.Save();
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (AppInstance.IsConnected)
        {
            SetStatus("Already connected.");
            return;
        }

        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            SetStatus("Enter a password.", true);
            return;
        }

        SaveSettings();

        var address = AddressBox.Text.Trim();
        var screen = Screen.AllScreens[MonitorBox.SelectedIndex];
        var orient = OrientationBox.SelectedIndex switch
        {
            1 => TabletOrientation.Landscape,
            2 => TabletOrientation.PortraitFlipped,
            3 => TabletOrientation.LandscapeFlipped,
            _ => TabletOrientation.Portrait
        };
        var outputMode = MouseRadio.IsChecked == true ? "mouse" : "ink";

        var connOpts = ConnectionOptions.WithPassword(password, address);
        var mappingOpts = new MappingOptions
        {
            MonitorX = screen.Bounds.Left,
            MonitorY = screen.Bounds.Top,
            MonitorW = screen.Bounds.Width,
            MonitorH = screen.Bounds.Height,
            Orientation = orient
        };

        SetStatus("Connecting…");
        AppInstance.StartPipeline(connOpts, mappingOpts, outputMode);
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        AppInstance.StopPipeline();
        SetStatus("Disconnecting…");
    }


    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Password;
        var address = AddressBox.Text.Trim();
        SetStatus("Testing…");
        try
        {
            using var client = new SshClient(address, 22, "root", password);
            await Task.Run(() => client.Connect());
            var result = client.RunCommand("echo ok").Result;
            client.Disconnect();
            SetStatus(result.Trim() == "ok" ? "Connection OK." : $"Unexpected: {result}");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed: {ex.Message}", true);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void OnPipelineStateChanged(ConnectionState state)
    {
        Dispatcher.Invoke(() =>
        {
            SetStatus(state switch
            {
                ConnectionState.Connected => "Connected.",
                ConnectionState.Connecting => "Connecting…",
                ConnectionState.Disconnected => "Disconnected.",
                _ => state.ToString()
            });
        });
    }

    private void SetStatus(string msg, bool isError = false)
    {
        StatusText.Text = msg;
        StatusText.Foreground = isError
            ? Brushes.Red
            : Brushes.DimGray;
    }
}
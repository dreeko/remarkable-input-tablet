using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemarkableTablet.App;

/// <summary>
///     User-facing settings persisted to %APPDATA%\remarkable-input-tablet\settings.json.
///     Passwords are never stored here — the user is prompted each session.
/// </summary>
public sealed class AppSettings
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "remarkable-input-tablet");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    public string Host { get; set; } = "10.11.99.1";
    public string Orientation { get; set; } = "Portrait";
    public int MonitorIndex { get; set; }
    public string OutputMode { get; set; } = "ink";
    public bool AutoConnect { get; set; }

    /// <summary>"off" or "touch". Defaults to off so existing users see no behavior change on upgrade.</summary>
    public string Gestures { get; set; } = "off";

    /// <summary>"linear" (default), "soft" (boost light strokes), or "hard" (suppress light strokes).</summary>
    public string PressureCurve { get; set; } = "linear";

    /// <summary>"auto" (probe via uname -m), "rm2", or "rmpp".</summary>
    public string Device { get; set; } = "auto";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)
                       ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            App.WriteLog($"AppSettings.Load failed: {ex.Message}");
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings));
        }
        catch (Exception ex)
        {
            App.WriteLog($"AppSettings.Save failed: {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;

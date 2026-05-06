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
    // ── Persistence ──────────────────────────────────────────────────────────
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "remarkable-input-tablet");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Host { get; set; } = "10.11.99.1";
    public string Orientation { get; set; } = "Portrait";
    public int MonitorIndex { get; set; }
    public string OutputMode { get; set; } = "ink";
    public bool AutoConnect { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions)
                       ?? new AppSettings();
            }
        }
        catch { }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch { }
    }
}
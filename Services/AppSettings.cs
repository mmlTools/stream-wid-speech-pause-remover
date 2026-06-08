using System.Text.Json;

namespace StreamWID.Services;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StreamWID");
    private static readonly string SettingsPath = Path.Combine(SettingsFolder, "settings.json");

    public string? LastKnownVersion { get; set; }
    public string? LastSeenUpdateVersion { get; set; }
    public double ThresholdDb { get; set; } = -35;
    public double MinSilenceSeconds { get; set; } = 0.45;
    public double KeepPaddingSeconds { get; set; } = 0.08;
    public double ResolveFps { get; set; } = 25;
    public bool UseAdaptiveThreshold { get; set; } = true;
    public bool ReencodeExports { get; set; } = true;

    public static string Folder => SettingsFolder;
    public static string FilePath => SettingsPath;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsFolder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}

using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace StormDsDecryptor;

public class UserSettings
{
    public string OutputDirectory { get; set; } = string.Empty;
    public bool IsInPlace { get; set; } = false;
    public int ModeIndex { get => IsInPlace ? 1 : 0; set => IsInPlace = value == 1; }
    public bool VerifyCrc { get; set; } = true;
    public bool CompatibilityMode { get; set; } = true;
    public bool CreateBackup { get; set; } = false;
    public string ThemeName { get; set; } = "MIDNIGHT";
    public int AccentIndex { get; set; } = 0;
    public int FontIndex { get; set; } = 0;
    public string LanguageCode { get; set; } = string.Empty;
}

public static class SettingsManager
{
    private static readonly string AppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LegacyDir = Path.Combine(AppDataRoot, "STORM DSI DECRYPTOR");
    private static readonly string SettingsDir = Path.Combine(AppDataRoot, "STORM DS DECRYPTOR");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static UserSettings Current { get; private set; } = new();

    private static void MigrateIfNeeded()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                string legacyFile = Path.Combine(LegacyDir, "settings.json");
                if (File.Exists(legacyFile))
                {
                    Directory.CreateDirectory(SettingsDir);
                    File.Copy(legacyFile, SettingsFile, overwrite: false);
                }
            }
        }
        catch { }
    }

    public static UserSettings Load()
    {
        try
        {
            MigrateIfNeeded();
            if (File.Exists(SettingsFile))
            {
                string json = File.ReadAllText(SettingsFile);
                var loaded = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    Current = loaded;
                }
            }
        }
        catch { }
        return Current;
    }

    public static void Save(UserSettings? settings = null)
    {
        try
        {
            if (settings != null)
                Current = settings;
            Directory.CreateDirectory(SettingsDir);
            string json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }
}

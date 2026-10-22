using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace StormDsiDecryptor;

public class AppSettings
{
    public string OutputDirectory { get; set; } = string.Empty;
    public bool IsInPlace { get; set; } = false;
    public bool CreateBackup { get; set; } = false;
    public string ThemeName { get; set; } = "MIDNIGHT";
    public int AccentIndex { get; set; } = 0;
    public int FontIndex { get; set; } = 0;
    public string LanguageCode { get; set; } = "";
    public bool CheckUpdatesOnStartup { get; set; } = true;
}

public static class SettingsManager
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "STORM DSI DECRYPTOR");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly object _syncLock = new();

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        lock (_syncLock)
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string json = File.ReadAllText(SettingsFile);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (loaded != null)
                    {
                        Current = loaded;
                    }
                }
            }
            catch { }
        }
    }

    public static void Save()
    {
        lock (_syncLock)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string json = JsonSerializer.Serialize(Current, JsonOptions);
                File.WriteAllText(SettingsFile, json);
            }
            catch { }
        }
    }
}

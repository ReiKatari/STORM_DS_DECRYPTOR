using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;

namespace StormDsDecryptor;

public static class HistoryManager
{
    private static readonly string AppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LegacyDir = Path.Combine(AppDataRoot, "STORM DSI DECRYPTOR");
    private static readonly string HistoryDir = Path.Combine(AppDataRoot, "STORM DS DECRYPTOR");
    private static readonly string HistoryFile = Path.Combine(HistoryDir, "history.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly object _syncLock = new();

    public static ObservableCollection<HistoryItem> Items { get; } = new();

    private static void MigrateIfNeeded()
    {
        try
        {
            if (!File.Exists(HistoryFile))
            {
                string legacyFile = Path.Combine(LegacyDir, "history.json");
                if (File.Exists(legacyFile))
                {
                    Directory.CreateDirectory(HistoryDir);
                    File.Copy(legacyFile, HistoryFile, overwrite: false);
                }
            }
        }
        catch { }
    }

    public static void Initialize(Microsoft.UI.Dispatching.DispatcherQueue? dq = null) => Load();
    public static void AddEntry(HistoryItem item) => Add(item);
    public static void RemoveEntry(HistoryItem item) => Remove(item);
    public static void ClearAll() => Clear();

    public static void Load()
    {
        lock (_syncLock)
        {
            try
            {
                MigrateIfNeeded();
                Items.Clear();
                if (File.Exists(HistoryFile))
                {
                    string json = File.ReadAllText(HistoryFile);
                    var list = JsonSerializer.Deserialize<List<HistoryItem>>(json, JsonOptions);
                    if (list != null)
                    {
                        bool updatedAny = false;
                        foreach (var item in list.OrderByDescending(x => x.ProcessedAt))
                        {
                            if (item.BannerIconPng == null || item.BannerIconPng.Length == 0)
                            {
                                try
                                {
                                    string probePath = File.Exists(item.OutPath) ? item.OutPath : (File.Exists(item.FilePath) ? item.FilePath : "");
                                    if (!string.IsNullOrEmpty(probePath))
                                    {
                                        var rom = DsiDecryptorEngine.InspectFile(probePath);
                                        if (rom.BannerIconPng != null && rom.BannerIconPng.Length > 0)
                                        {
                                            item.BannerIconPng = rom.BannerIconPng;
                                            if (!string.IsNullOrWhiteSpace(rom.GameTitle))
                                                item.GameTitle = rom.GameTitle;
                                            updatedAny = true;
                                        }
                                    }
                                }
                                catch { }
                            }
                            Items.Add(item);
                        }
                        if (updatedAny)
                        {
                            Save();
                        }
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
                Directory.CreateDirectory(HistoryDir);
                string json = JsonSerializer.Serialize(Items.ToList(), JsonOptions);
                File.WriteAllText(HistoryFile, json);
            }
            catch { }
        }
    }

    public static void Add(HistoryItem item)
    {
        var dq = MainWindow.Instance?.DispatcherQueue;
        if (dq != null && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(() => Add(item));
            return;
        }

        lock (_syncLock)
        {
            try
            {
                var existing = Items.FirstOrDefault(x =>
                    (!string.IsNullOrEmpty(x.FilePath) && x.FilePath.Equals(item.FilePath, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(x.FileName) && x.FileName.Equals(item.FileName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(x.GameCode) && x.GameCode.Equals(item.GameCode, StringComparison.OrdinalIgnoreCase)));
                if (existing != null)
                {
                    Items.Remove(existing);
                }

                Items.Insert(0, item);
                Save();
            }
            catch { }
        }
    }

    public static void Remove(HistoryItem item)
    {
        var dq = MainWindow.Instance?.DispatcherQueue;
        if (dq != null && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(() => Remove(item));
            return;
        }

        lock (_syncLock)
        {
            try
            {
                if (Items.Remove(item))
                {
                    Save();
                }
            }
            catch { }
        }
    }

    public static void Clear()
    {
        var dq = MainWindow.Instance?.DispatcherQueue;
        if (dq != null && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(Clear);
            return;
        }

        lock (_syncLock)
        {
            try
            {
                Items.Clear();
                Save();
            }
            catch { }
        }
    }

    public static HistoryItem? Find(string filePath)
    {
        lock (_syncLock)
        {
            return Items.FirstOrDefault(x => x.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
        }
    }
}

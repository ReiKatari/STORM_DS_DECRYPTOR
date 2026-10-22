using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace StormDsiDecryptor;

public static class UpdateManager
{
    public static readonly Version CurrentVersion = new(1, 1, 9);
    public const string CurrentVersionString = "1.1.9";
    public const string GitHubRepo = "ReiKatari/STORM_DS_DESCRYPTOR";

    public static async Task CheckForUpdatesAsync(Window? owner, bool silent = true)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.Add("User-Agent", "STORM-DSi-Decryptor");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

            string apiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
            HttpResponseMessage response = await client.GetAsync(apiUrl);

            if (!response.IsSuccessStatusCode)
            {
                // Fallback to releases list if 'latest' is not yet indexed
                string fallbackUrl = $"https://api.github.com/repos/{GitHubRepo}/releases";
                response = await client.GetAsync(fallbackUrl);
            }

            if (!response.IsSuccessStatusCode)
            {
                if (!silent)
                {
                    ShowError(owner);
                }
                return;
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            JsonElement releaseEl;

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                if (doc.RootElement.GetArrayLength() == 0)
                {
                    if (!silent) ShowUpToDate(owner);
                    return;
                }
                releaseEl = doc.RootElement[0];
            }
            else
            {
                releaseEl = doc.RootElement;
            }

            string tagName = releaseEl.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
            string releaseName = releaseEl.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            string body = releaseEl.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
            string htmlUrl = releaseEl.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? "" : "";

            if (!TryParseVersion(tagName, out var remoteVersion))
            {
                if (!silent) ShowUpToDate(owner);
                return;
            }

            if (remoteVersion > CurrentVersion)
            {
                var info = new UpdateInfo
                {
                    Version = remoteVersion,
                    VersionString = CleanVersionString(tagName),
                    Title = string.IsNullOrWhiteSpace(releaseName) ? $"STORM DSi DECRYPTOR {tagName}" : releaseName,
                    Changelog = body,
                    ReleaseUrl = htmlUrl
                };

                // Find the best installer asset
                if (releaseEl.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                {
                    string bestUrl = "";
                    string bestName = "";
                    long bestSize = 0;

                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        string aName = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                        string aUrl = asset.TryGetProperty("browser_download_url", out var au) ? au.GetString() ?? "" : "";
                        long aSize = asset.TryGetProperty("size", out var asz) ? asz.GetInt64() : 0;

                        if (aName.EndsWith("_Setup.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            bestUrl = aUrl;
                            bestName = aName;
                            bestSize = aSize;
                            break; // Highest priority
                        }
                        if (aName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(bestUrl))
                        {
                            bestUrl = aUrl;
                            bestName = aName;
                            bestSize = aSize;
                        }
                        else if (aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(bestUrl))
                        {
                            bestUrl = aUrl;
                            bestName = aName;
                            bestSize = aSize;
                        }
                    }

                    info.SetupDownloadUrl = bestUrl;
                    info.SetupFileName = bestName;
                    info.SetupFileSize = bestSize;
                }

                // Show Update Dialog on UI Thread
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var dlg = new UpdateDialog(info)
                    {
                        Owner = owner ?? Application.Current.MainWindow
                    };
                    dlg.ShowDialog();
                });
            }
            else
            {
                if (!silent)
                {
                    ShowUpToDate(owner);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateManager Check Error]: {ex.Message}");
            if (!silent)
            {
                ShowError(owner);
            }
        }
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;

        string clean = CleanVersionString(tag);
        var parts = clean.Split('.');
        if (parts.Length == 1 && int.TryParse(parts[0], out int maj))
        {
            version = new Version(maj, 0, 0);
            return true;
        }
        if (parts.Length == 2 && int.TryParse(parts[0], out maj) && int.TryParse(parts[1], out int min))
        {
            version = new Version(maj, min, 0);
            return true;
        }
        return Version.TryParse(clean, out version!);
    }

    private static string CleanVersionString(string raw)
    {
        string s = raw.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(1).Trim();
        return s;
    }

    private static void ShowUpToDate(Window? owner)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StormMessageBox.Show(owner ?? Application.Current.MainWindow,
                LocalizationManager.Get("UpdateUpToDate") ?? "У вас установлена самая актуальная версия программы.",
                LocalizationManager.Get("UpdateAvailableTitle") ?? "Обновление");
        });
    }

    private static void ShowError(Window? owner)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StormMessageBox.Show(owner ?? Application.Current.MainWindow,
                LocalizationManager.Get("UpdateCheckError") ?? "Не удалось проверить наличие обновлений. Проверьте подключение к сети.",
                LocalizationManager.Get("UpdateAvailableTitle") ?? "Обновление");
        });
    }
}

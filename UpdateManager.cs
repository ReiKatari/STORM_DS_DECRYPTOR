using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace StormDsDecryptor;

public static class UpdateManager
{
    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateManager).Assembly.GetName().Version;
            return v != null ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 4, 0);
        }
    }

    public static string CurrentVersionString
    {
        get
        {
            var v = CurrentVersion;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public const string GitHubRepo = "ReiKatari/STORM_DS_DECRYPTOR";
    public const string GitHubRepoFallback = "ReiKatari/STORM_DS_DESCRYPTOR";

    public static async Task CheckForUpdatesAsync(bool silent = true)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.Add("User-Agent", "STORM-DS-Decryptor");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

            HttpResponseMessage response = await client.GetAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
            if (!response.IsSuccessStatusCode)
            {
                response = await client.GetAsync($"https://api.github.com/repos/{GitHubRepoFallback}/releases/latest");
            }
            if (!response.IsSuccessStatusCode)
            {
                response = await client.GetAsync($"https://api.github.com/repos/{GitHubRepo}/releases");
            }

            if (!response.IsSuccessStatusCode)
            {
                if (!silent) ShowError();
                return;
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            JsonElement releaseEl;

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                if (doc.RootElement.GetArrayLength() == 0)
                {
                    if (!silent) ShowUpToDate();
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
                if (!silent) ShowUpToDate();
                return;
            }

            if (remoteVersion > CurrentVersion)
            {
                var info = new UpdateInfo
                {
                    Version = remoteVersion,
                    VersionString = CleanVersionString(tagName),
                    Title = string.IsNullOrWhiteSpace(releaseName) ? $"STORM DS DECRYPTOR {tagName}" : releaseName,
                    Changelog = body,
                    ReleaseUrl = htmlUrl
                };

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
                            break;
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

                MainWindow.Instance?.DispatcherQueue.TryEnqueue(() =>
                {
                    MainWindow.Instance?.ShowUpdateModal(info);
                });
            }
            else
            {
                if (!silent) ShowUpToDate();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateManager Check Error]: {ex.Message}");
            if (!silent) ShowError();
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

    private static void ShowUpToDate()
    {
        MainWindow.Instance?.DispatcherQueue.TryEnqueue(() =>
        {
            MainWindow.Instance?.ShowMessageModal(
                LocalizationManager.Get("UpdateAvailableTitle"),
                LocalizationManager.Get("UpdateUpToDate"));
        });
    }

    private static void ShowError()
    {
        MainWindow.Instance?.DispatcherQueue.TryEnqueue(() =>
        {
            MainWindow.Instance?.ShowMessageModal(
                LocalizationManager.Get("UpdateAvailableTitle"),
                LocalizationManager.Get("UpdateCheckError"));
        });
    }

    public static async Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(info.SetupDownloadUrl))
            {
                if (!string.IsNullOrWhiteSpace(info.ReleaseUrl))
                {
                    Process.Start(new ProcessStartInfo(info.ReleaseUrl) { UseShellExecute = true });
                    return true;
                }
                return false;
            }

            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StormDsDecryptor_Update");
            System.IO.Directory.CreateDirectory(tempDir);
            string fileName = !string.IsNullOrWhiteSpace(info.SetupFileName) ? info.SetupFileName : $"STORM_DS_DECRYPTOR_{info.VersionString}_Setup.exe";
            string destPath = System.IO.Path.Combine(tempDir, fileName);

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.Add("User-Agent", "STORM-DS-Decryptor");

            using var resp = await client.GetAsync(info.SetupDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            long totalBytes = resp.Content.Headers.ContentLength ?? info.SetupFileSize;
            await using var netStream = await resp.Content.ReadAsStreamAsync();
            await using (var fs = new System.IO.FileStream(destPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None, 65536, true))
            {
                byte[] buf = new byte[65536];
                long readTotal = 0;
                int read;
                while ((read = await netStream.ReadAsync(buf)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, read));
                    readTotal += read;
                    if (totalBytes > 0)
                        progress?.Report(Math.Min(100.0, (readTotal * 100.0) / totalBytes));
                }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = destPath,
                UseShellExecute = true,
                Verb = "runas"
            });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Update Download Error]: {ex.Message}");
            return false;
        }
    }
}

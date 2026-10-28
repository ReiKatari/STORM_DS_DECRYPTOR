using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace StormDsDecryptor;

public static class UpdateManager
{
    public static Version NormalizeVersion(Version? v)
    {
        if (v == null) return new Version(1, 5, 0);
        return new Version(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateManager).Assembly.GetName().Version;
            return v != null ? NormalizeVersion(v) : new Version(1, 5, 0);
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
    public const string GitHubReleasesWebUrl = "https://github.com/ReiKatari/STORM_DS_DECRYPTOR/releases";

    public const string MirrorRepoSlug = "STORM_DS_DECRYPTOR";
    public const string MirrorWebUrl = "https://stormgamesworld.ru/programs?repo=STORM_DS_DECRYPTOR";
    public const string MirrorApiUrl = "https://stormgamesworld.ru/api/programs?repo=STORM_DS_DECRYPTOR";
    public const string MirrorApiFallbackUrl = "https://stormgamesworld.ru/api/programs?id=storm_ds_decryptor";
    public const string MirrorUploadsBaseUrl = "https://stormgamesworld.ru/uploads/programs/STORM_DS_DECRYPTOR";

    public static async Task CheckForUpdatesAsync(bool silent = true)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(7) };
            client.DefaultRequestHeaders.Add("User-Agent", "STORM-DS-Decryptor");
            client.DefaultRequestHeaders.Add("Accept", "application/json");

            var ghTask = CheckGitHubLatestAsync(client);
            var mirrorTask = CheckStormMirrorLatestAsync(client);
            await Task.WhenAll(ghTask, mirrorTask);

            UpdateInfo? ghInfo = await ghTask;
            UpdateInfo? mirrorInfo = await mirrorTask;

            if (ghInfo == null && mirrorInfo == null)
            {
                if (!silent) ShowError();
                return;
            }

            UpdateInfo bestInfo;
            if (ghInfo != null && mirrorInfo != null)
            {
                Version ghVer = NormalizeVersion(ghInfo.Version);
                Version mirVer = NormalizeVersion(mirrorInfo.Version);
                if (mirVer > ghVer)
                {
                    bestInfo = mirrorInfo;
                    if (!string.IsNullOrWhiteSpace(ghInfo.SetupDownloadUrl))
                        bestInfo.MirrorSetupDownloadUrl = ghInfo.SetupDownloadUrl;
                }
                else if (ghVer > mirVer)
                {
                    bestInfo = ghInfo;
                    if (!string.IsNullOrWhiteSpace(mirrorInfo.SetupDownloadUrl))
                        bestInfo.MirrorSetupDownloadUrl = mirrorInfo.SetupDownloadUrl;
                }
                else
                {
                    bestInfo = ghInfo;
                    bestInfo.SourceName = "GitHub + STORM GAMES WORLD";
                    if (!string.IsNullOrWhiteSpace(mirrorInfo.SetupDownloadUrl))
                        bestInfo.MirrorSetupDownloadUrl = mirrorInfo.SetupDownloadUrl;
                    if (string.IsNullOrWhiteSpace(bestInfo.SetupDownloadUrl))
                        bestInfo.SetupDownloadUrl = mirrorInfo.SetupDownloadUrl;
                    if (string.IsNullOrWhiteSpace(bestInfo.Changelog))
                        bestInfo.Changelog = mirrorInfo.Changelog;
                }
            }
            else
            {
                bestInfo = mirrorInfo ?? ghInfo!;
            }

            if (string.IsNullOrWhiteSpace(bestInfo.MirrorSetupDownloadUrl))
            {
                string fallbackFile = !string.IsNullOrWhiteSpace(bestInfo.SetupFileName)
                    ? bestInfo.SetupFileName
                    : $"STORM_DS_DECRYPTOR_{bestInfo.VersionString}_Setup.exe";
                bestInfo.MirrorSetupDownloadUrl = $"{MirrorUploadsBaseUrl}/{fallbackFile}";
            }

            Version remoteNorm = NormalizeVersion(bestInfo.Version);
            Version currentNorm = NormalizeVersion(CurrentVersion);

            if (remoteNorm > currentNorm)
            {
                MainWindow.Instance?.DispatcherQueue.TryEnqueue(() =>
                {
                    MainWindow.Instance?.ShowUpdateModal(bestInfo);
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

    private static async Task<UpdateInfo?> CheckGitHubLatestAsync(HttpClient client)
    {
        try
        {
            HttpResponseMessage response = await client.GetAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
            if (!response.IsSuccessStatusCode)
                response = await client.GetAsync($"https://api.github.com/repos/{GitHubRepoFallback}/releases/latest");
            if (!response.IsSuccessStatusCode)
                response = await client.GetAsync($"https://api.github.com/repos/{GitHubRepo}/releases");

            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            JsonElement releaseEl;

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                if (doc.RootElement.GetArrayLength() == 0) return null;
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

            if (!TryParseVersion(tagName, out var remoteVersion)) return null;

            string verStr = CleanVersionString(tagName);
            var info = new UpdateInfo
            {
                Version = NormalizeVersion(remoteVersion),
                VersionString = verStr,
                Title = string.IsNullOrWhiteSpace(releaseName) ? $"STORM DS DECRYPTOR {verStr}" : releaseName,
                Changelog = body,
                ReleaseUrl = string.IsNullOrWhiteSpace(htmlUrl) ? GitHubReleasesWebUrl : htmlUrl,
                MirrorWebUrl = MirrorWebUrl,
                SourceName = "GitHub"
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

            return info;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateManager GitHub Error]: {ex.Message}");
            return null;
        }
    }

    private static async Task<UpdateInfo?> CheckStormMirrorLatestAsync(HttpClient client)
    {
        try
        {
            HttpResponseMessage response = await client.GetAsync(MirrorApiUrl);
            if (!response.IsSuccessStatusCode)
                response = await client.GetAsync(MirrorApiFallbackUrl);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("repository", out var repoWrap) && repoWrap.ValueKind == JsonValueKind.Object)
                root = repoWrap;

            if (!root.TryGetProperty("releases", out var releasesProp) || releasesProp.ValueKind != JsonValueKind.Array || releasesProp.GetArrayLength() == 0)
                return null;

            JsonElement latestRel = releasesProp[0];
            foreach (var rel in releasesProp.EnumerateArray())
            {
                if (rel.TryGetProperty("isLatest", out var il) && il.ValueKind == JsonValueKind.True)
                {
                    latestRel = rel;
                    break;
                }
            }

            string rawVer = latestRel.TryGetProperty("version", out var vp) ? vp.GetString() ?? "" : "";
            string title = latestRel.TryGetProperty("title", out var tp) ? tp.GetString() ?? "" : "";
            string notes = latestRel.TryGetProperty("notes", out var np) ? np.GetString() ?? "" : "";

            if (!TryParseVersion(rawVer, out var remoteVersion)) return null;

            string verStr = CleanVersionString(rawVer);
            string defaultSetupName = $"STORM_DS_DECRYPTOR_{verStr}_Setup.exe";
            string bestUrl = $"{MirrorUploadsBaseUrl}/{defaultSetupName}";
            string bestName = defaultSetupName;
            long bestSize = 0;

            if (latestRel.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    string aName = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                    string aUrl = asset.TryGetProperty("downloadUrl", out var du) ? du.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(aUrl) && aUrl.StartsWith("/"))
                        aUrl = "https://stormgamesworld.ru" + aUrl;
                    long aSize = asset.TryGetProperty("size", out var asz) && asz.ValueKind == JsonValueKind.Number ? asz.GetInt64() : 0;

                    if (aName.EndsWith("_Setup.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        bestName = aName;
                        bestUrl = !string.IsNullOrWhiteSpace(aUrl) ? aUrl : $"{MirrorUploadsBaseUrl}/{aName}";
                        bestSize = aSize;
                        break;
                    }
                }
            }

            return new UpdateInfo
            {
                Version = NormalizeVersion(remoteVersion),
                VersionString = verStr,
                Title = string.IsNullOrWhiteSpace(title) ? $"STORM DS DECRYPTOR {verStr}" : title,
                Changelog = notes,
                SetupDownloadUrl = bestUrl,
                MirrorSetupDownloadUrl = bestUrl,
                SetupFileName = bestName,
                SetupFileSize = bestSize,
                ReleaseUrl = MirrorWebUrl,
                MirrorWebUrl = MirrorWebUrl,
                SourceName = "STORM GAMES WORLD (stormgamesworld.ru)"
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateManager Mirror Error]: {ex.Message}");
            return null;
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
        if (Version.TryParse(clean, out var parsed) && parsed != null)
        {
            version = NormalizeVersion(parsed);
            return true;
        }
        return false;
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
                $"{LocalizationManager.Get("UpdateUpToDate")}\n({CurrentVersionString})");
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
        var candidateUrls = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.SetupDownloadUrl))
            candidateUrls.Add(info.SetupDownloadUrl);
        if (!string.IsNullOrWhiteSpace(info.MirrorSetupDownloadUrl) && !candidateUrls.Contains(info.MirrorSetupDownloadUrl))
            candidateUrls.Add(info.MirrorSetupDownloadUrl);

        string fallbackMirrorUrl = $"{MirrorUploadsBaseUrl}/STORM_DS_DECRYPTOR_{info.VersionString}_Setup.exe";
        if (!candidateUrls.Contains(fallbackMirrorUrl))
            candidateUrls.Add(fallbackMirrorUrl);

        string tempDir = Path.Combine(Path.GetTempPath(), "StormDsDecryptor_Update");
        Directory.CreateDirectory(tempDir);
        string fileName = !string.IsNullOrWhiteSpace(info.SetupFileName) ? info.SetupFileName : $"STORM_DS_DECRYPTOR_{info.VersionString}_Setup.exe";
        string destPath = Path.Combine(tempDir, fileName);

        foreach (string downloadUrl in candidateUrls)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                client.DefaultRequestHeaders.Add("User-Agent", "STORM-DS-Decryptor");

                using var resp = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) continue;

                long totalBytes = resp.Content.Headers.ContentLength ?? info.SetupFileSize;
                await using var netStream = await resp.Content.ReadAsStreamAsync();
                await using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
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

                // Rule 9: Verify PE (MZ) signature before launching installer
                var fi = new FileInfo(destPath);
                if (!fi.Exists || fi.Length < 4096) continue;
                byte[] mz = new byte[2];
                await using (var checkFs = new FileStream(destPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (await checkFs.ReadAsync(mz.AsMemory(0, 2)) < 2 || mz[0] != 0x4D || mz[1] != 0x5A)
                        continue;
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
                Debug.WriteLine($"[Update Download Attempt Error ({downloadUrl})]: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(info.MirrorWebUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo(info.MirrorWebUrl) { UseShellExecute = true });
                return false;
            }
            catch { }
        }
        return false;
    }
}

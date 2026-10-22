using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace StormDsiDecryptor;

public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _info;
    private CancellationTokenSource? _cts;
    private bool _isDownloading = false;

    public UpdateDialog(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;

        ApplyLocalization();
        PopulateData();
    }

    private void ApplyLocalization()
    {
        Title = $"{LocalizationManager.Get("AppTitle")} - {LocalizationManager.Get("UpdateAvailableTitle")}";
        TxtHeaderBanner.Text = LocalizationManager.Get("UpdateAvailableHeader") ?? "Найдена новая версия программы!";
        LblCurrent.Text = LocalizationManager.Get("UpdateCurrentVer") ?? "Текущая версия:";
        LblNew.Text = LocalizationManager.Get("UpdateNewVer") ?? "Новая версия:";
        LblChangelog.Text = LocalizationManager.Get("UpdateChangelog") ?? "Список изменений:";
        BtnUpdate.Content = LocalizationManager.Get("UpdateNow") ?? "⚡ Обновить сейчас";
        BtnLater.Content = LocalizationManager.Get("UpdateLater") ?? "Позже";
        TxtProgressStatus.Text = LocalizationManager.Get("UpdateDownloading") ?? "Загрузка обновления...";
    }

    private void PopulateData()
    {
        TxtCurrentVer.Text = UpdateManager.CurrentVersionString;
        TxtNewVer.Text = _info.VersionString;

        if (_info.SetupFileSize > 0)
        {
            double mb = _info.SetupFileSize / (1024.0 * 1024.0);
            TxtFileSize.Text = $" (~{mb:F1} МБ)";
        }
        else
        {
            TxtFileSize.Text = "";
        }

        TxtChangelog.Text = CleanMarkdownForDisplay(_info.Changelog);
    }

    private static string CleanMarkdownForDisplay(string md)
    {
        if (string.IsNullOrWhiteSpace(md))
            return "Новые улучшения и исправления стабильности.";

        // Strip HTML tags like <details>, <summary>, <b>, etc.
        string clean = Regex.Replace(md, @"<[^>]+>", "");
        // Remove markdown headers #, ##, ###
        clean = Regex.Replace(clean, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        // Replace blockquote markers
        clean = Regex.Replace(clean, @"^>\s*", "", RegexOptions.Multiline);
        // Strip bold/italic markdown delimiters
        clean = clean.Replace("**", "").Replace("__", "").Replace("`", "");

        return clean.Trim();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            BtnClose_Click(sender, e);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading)
        {
            _cts?.Cancel();
        }
        Close();
    }

    private void BtnLater_Click(object sender, RoutedEventArgs e)
    {
        BtnClose_Click(sender, e);
    }

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_info.SetupDownloadUrl))
        {
            // If direct setup asset not found, open GitHub release page in browser
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = !string.IsNullOrEmpty(_info.ReleaseUrl) ? _info.ReleaseUrl : "https://github.com/ReiKatari/STORM_DS_DESCRYPTOR/releases",
                    UseShellExecute = true
                });
            }
            catch { }
            Close();
            return;
        }

        _isDownloading = true;
        BtnUpdate.IsEnabled = false;
        BtnLater.Content = LocalizationManager.Get("ClearList") ?? "Отмена";
        PanelProgress.Visibility = Visibility.Visible;
        BarDownload.Value = 0;
        TxtProgressPercent.Text = "0%";
        TxtProgressStatus.Text = LocalizationManager.Get("UpdateDownloading") ?? "Загрузка обновления...";

        _cts = new CancellationTokenSource();
        string tempFileName = !string.IsNullOrWhiteSpace(_info.SetupFileName) ? _info.SetupFileName : "STORM_DSI_DECRYPTOR_Setup.exe";
        string tempFilePath = Path.Combine(Path.GetTempPath(), tempFileName);

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.Add("User-Agent", "STORM-DSi-Decryptor");

            using var response = await client.GetAsync(_info.SetupDownloadUrl, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? _info.SetupFileSize;
            using var stream = await response.Content.ReadAsStreamAsync(_cts.Token);
            using var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int read;
            DateTime lastUpdate = DateTime.MinValue;

            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
                totalRead += read;

                if (totalBytes > 0 && (DateTime.Now - lastUpdate).TotalMilliseconds > 100)
                {
                    lastUpdate = DateTime.Now;
                    double percent = (double)totalRead / totalBytes * 100.0;
                    double readMb = totalRead / (1024.0 * 1024.0);
                    double totalMb = totalBytes / (1024.0 * 1024.0);

                    Dispatcher.Invoke(() =>
                    {
                        BarDownload.Value = percent;
                        TxtProgressPercent.Text = $"{percent:F0}%";
                        TxtProgressStatus.Text = $"Загрузка: {readMb:F1} / {totalMb:F1} МБ";
                    });
                }
            }

            Dispatcher.Invoke(() =>
            {
                BarDownload.Value = 100;
                TxtProgressPercent.Text = "100%";
                TxtProgressStatus.Text = LocalizationManager.Get("UpdateInstalling") ?? "Запуск установщика...";
            });

            await Task.Delay(500);

            // Launch the downloaded installer
            var psi = new ProcessStartInfo
            {
                FileName = tempFilePath,
                UseShellExecute = true
            };
            Process.Start(psi);

            // Shutdown the current application gracefully
            if (Owner is MainWindow mw)
            {
                mw.QuitApplication();
            }
            else
            {
                Application.Current.Shutdown();
            }
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
            PanelProgress.Visibility = Visibility.Collapsed;
            BtnUpdate.IsEnabled = true;
            BtnLater.Content = LocalizationManager.Get("UpdateLater") ?? "Позже";
            _isDownloading = false;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
            TxtProgressStatus.Text = $"Ошибка: {ex.Message}";
            BtnUpdate.IsEnabled = true;
            BtnLater.Content = LocalizationManager.Get("UpdateLater") ?? "Позже";
            _isDownloading = false;
        }
    }
}

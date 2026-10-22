using System;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;

namespace StormDsDecryptor;

public class HistoryItem
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string GameTitle { get; set; } = string.Empty;
    public string GameCode { get; set; } = string.Empty;
    public string FormatType { get; set; } = "NDS / DSi";
    public long FileSize { get; set; }
    public DateTime ProcessedAt { get; set; } = DateTime.Now;
    public string Status { get; set; } = string.Empty;
    public long ElapsedMs { get; set; }
    public string OutPath { get; set; } = string.Empty;
    public bool IsSuccess { get; set; } = true;
    public byte[]? BannerIconPng { get; set; }

    [JsonIgnore]
    private ImageSource? _bannerIcon;

    [JsonIgnore]
    public ImageSource BannerIcon
    {
        get => _bannerIcon ??= NdsBannerParser.FromPngBytes(BannerIconPng);
        set => _bannerIcon = value;
    }

    [JsonIgnore]
    public ImageSource IconSource
    {
        get => BannerIcon;
        set => BannerIcon = value;
    }

    [JsonIgnore]
    public byte[]? BannerPngBytes
    {
        get => BannerIconPng;
        set => BannerIconPng = value;
    }

    [JsonIgnore]
    public string Timestamp
    {
        get => ProcessedAtFormatted;
        set
        {
            if (DateTime.TryParse(value, out var dt))
                ProcessedAt = dt;
        }
    }

    [JsonIgnore]
    public string ResultStatus
    {
        get => Status;
        set => Status = value;
    }

    [JsonIgnore]
    public string OutputPath
    {
        get => OutPath;
        set => OutPath = value;
    }

    [JsonIgnore]
    public string Region => DsiDecryptorEngine.GetRegionFromCode(GameCode);

    [JsonIgnore]
    public string CodeAndRegion => string.IsNullOrWhiteSpace(GameCode) || GameCode == "-" ? "-" : $"{GameCode} [{Region}]";

    private string? _customSizeFormatted;
    [JsonIgnore]
    public string FileSizeFormatted
    {
        get => !string.IsNullOrEmpty(_customSizeFormatted) ? _customSizeFormatted : $"{(double)FileSize / (1024 * 1024):F2} МБ";
        set => _customSizeFormatted = value;
    }

    [JsonIgnore]
    public string ProcessedAtFormatted => ProcessedAt.ToString("dd.MM.yyyy HH:mm");
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace StormDsDecryptor;

public class RomItem : INotifyPropertyChanged
{
    private string _status = "";
    private bool _isProcessing;
    private bool _isSuccess;
    private bool _isEncrypted;
    private bool _isDsiRom = true;
    private bool _needsCompatibilityPatch;
    private double _progressValue;
    private bool _hasError;
    private string _gameTitle = string.Empty;
    private string _gameCode = string.Empty;
    private long _fileSize;

    public string FilePath { get; set; } = string.Empty;
    public string FullPath { get => FilePath; set => FilePath = value; }
    public string FileName { get; set; } = string.Empty;

    public string GameTitle
    {
        get => _gameTitle;
        set { _gameTitle = value; OnPropertyChanged(); OnPropertyChanged(nameof(InternalTitle)); }
    }
    public string InternalTitle { get => GameTitle; set => GameTitle = value; }

    public string GameCode
    {
        get => _gameCode;
        set { _gameCode = value; OnPropertyChanged(); OnPropertyChanged(nameof(Region)); OnPropertyChanged(nameof(RegionCode)); OnPropertyChanged(nameof(CodeAndRegion)); }
    }

    public string Region => DsiDecryptorEngine.GetRegionFromCode(GameCode);
    public string RegionCode => Region;
    public string CodeAndRegion => string.IsNullOrWhiteSpace(GameCode) || GameCode == "-" ? "-" : $"{GameCode} [{Region}]";

    public long FileSize
    {
        get => _fileSize;
        set { _fileSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(FileSizeBytes)); OnPropertyChanged(nameof(FileSizeFormatted)); }
    }
    public long FileSizeBytes { get => FileSize; set => FileSize = value; }
    public string FileSizeFormatted => $"{(double)FileSize / (1024 * 1024):F2} МБ";

    public string FormatType => IsSpecialFormat && !string.IsNullOrEmpty(SpecialFormatType)
        ? SpecialFormatType
        : (IsDsiRom ? (IsEncrypted ? "DSi Modcrypt" : "DSi / TWL") : "NDS Classic");

    public bool IsFromArchive { get; set; }
    public string ArchivePath { get; set; } = string.Empty;
    public string ArchiveEntryName { get; set; } = string.Empty;
    public string SuggestedFileName { get; set; } = string.Empty;

    private ImageSource? _bannerIcon;
    public ImageSource BannerIcon
    {
        get => _bannerIcon ??= NdsBannerParser.FromPngBytes(BannerIconPng);
        set { _bannerIcon = value; OnPropertyChanged(); OnPropertyChanged(nameof(IconSource)); }
    }
    public ImageSource IconSource
    {
        get => BannerIcon;
        set => BannerIcon = value;
    }

    public byte[]? BannerIconPng { get; set; }
    public byte[]? BannerPngBytes
    {
        get => BannerIconPng;
        set => BannerIconPng = value;
    }

    // DSi Animated Icon Frames (0x1240..0x23C0)
    public List<BannerAnimFrame>? AnimFrames { get; set; }
    private List<ImageSource>? _decodedAnimSources;
    private int _animIndex;
    private long _lastFrameTickMs;

    public bool HasAnimatedBanner => AnimFrames != null && AnimFrames.Count > 1;

    public void AdvanceAnimationTick(long nowMs)
    {
        if (AnimFrames == null || AnimFrames.Count <= 1)
            return;

        if (_decodedAnimSources == null || _decodedAnimSources.Count != AnimFrames.Count)
        {
            _decodedAnimSources = new List<ImageSource>(AnimFrames.Count);
            foreach (var f in AnimFrames)
                _decodedAnimSources.Add(NdsBannerParser.FromPngBytes(f.PngBytes));
            _animIndex = 0;
            _lastFrameTickMs = nowMs;
        }

        int curDur = AnimFrames[_animIndex].DurationMs;
        if (nowMs - _lastFrameTickMs >= curDur)
        {
            _animIndex = (_animIndex + 1) % AnimFrames.Count;
            _lastFrameTickMs = nowMs;
            BannerIcon = _decodedAnimSources[_animIndex];
        }
    }

    public void ResetAnimationCache()
    {
        _decodedAnimSources = null;
        _animIndex = 0;
    }

    public bool IsSpecialFormat { get; set; }
    public string SpecialFormatType { get; set; } = string.Empty;
    public long SpecialContentOffset { get; set; }
    public long SpecialContentSize { get; set; }
    public string TitleId { get; set; } = string.Empty;

    public bool IsEncrypted
    {
        get => _isEncrypted;
        set { _isEncrypted = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormatType)); }
    }

    public bool IsDsiRom
    {
        get => _isDsiRom;
        set { _isDsiRom = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormatType)); }
    }

    public bool NeedsCompatibilityPatch
    {
        get => _needsCompatibilityPatch;
        set { _needsCompatibilityPatch = value; OnPropertyChanged(); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    public string StatusText
    {
        get => Status;
        set => Status = value;
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set { _isProcessing = value; OnPropertyChanged(); }
    }

    public bool IsSuccess
    {
        get => _isSuccess;
        set { _isSuccess = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsCompleted)); }
    }

    public bool IsCompleted
    {
        get => IsSuccess;
        set => IsSuccess = value;
    }

    public bool HasError
    {
        get => _hasError;
        set { _hasError = value; OnPropertyChanged(); }
    }

    public double ProgressValue
    {
        get => _progressValue;
        set { _progressValue = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

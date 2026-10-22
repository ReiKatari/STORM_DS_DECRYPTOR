using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace StormDsDecryptor;

public enum DuplicateChoice
{
    Overwrite,
    Skip,
    Cancel
}

public sealed partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }
    private readonly ObservableCollection<RomItem> _allGames = new();
    private readonly ObservableCollection<RomItem> _filteredGames = new();
    private StormTrayService? _trayService;
    private IntPtr _hwnd;
    private AppWindow? _appWindow;
    private bool _isLoaded;
    private bool _isProcessingBatch;
    private bool _sortAscending = true;
    private string _currentSortField = "Name";

    private UpdateInfo? _pendingUpdate;
    private TaskCompletionSource<(DuplicateChoice Choice, bool ApplyToAll)>? _dupTcs;

    // Native Win32 Drag & Drop (WM_DROPFILES) & Subclassing for Elevated/Standard Drag & Drop
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private WndProcDelegate? _wndProcDelegate;
    private IntPtr _origWndProc;

    private const int GWLP_WNDPROC = -4;
    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint MSGFLT_ALLOW = 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, int cch);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr pChangeFilterStruct);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint WM_SETICON = 0x0080;

    // Taskbar Progress Bar (ITaskbarList3)
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, int tbpFlags);
    }

    [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarInstance { }

    private ITaskbarList3? _taskbarList;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        InitializeWindowAndChrome();
        InitializeControlsAndSettings();
    }

    private void InitializeWindowAndChrome()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        WindowId wndId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(wndId);

        Title = $"STORM DS DECRYPTOR {UpdateManager.CurrentVersion}";
        TxtWindowTitle.Text = $"STORM DS DECRYPTOR {UpdateManager.CurrentVersion}";

        // Hide system title bar and extend content into title bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBarDragArea);

        if (_appWindow != null)
        {
            _appWindow.Resize(new SizeInt32(1320, 820));
            CenterWindowOnScreen();
            _appWindow.Closing += AppWindow_Closing;

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = true;
            }
        }

        // Load high-res app icons everywhere (Rule 13)
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string icoPath = Path.Combine(baseDir, "app_icon.ico");
            string pngPath = Path.Combine(baseDir, "icon.png");

            if (File.Exists(icoPath))
            {
                _appWindow?.SetIcon(icoPath);
                IntPtr hIconSmall = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                IntPtr hIconBig = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 48, 48, LR_LOADFROMFILE);
                if (hIconSmall != IntPtr.Zero) SendMessage(_hwnd, WM_SETICON, (IntPtr)0, hIconSmall);
                if (hIconBig != IntPtr.Zero) SendMessage(_hwnd, WM_SETICON, (IntPtr)1, hIconBig);
            }

            if (File.Exists(pngPath))
            {
                var bmp = new BitmapImage(new Uri(pngPath));
                ImgTitleBarIcon.Source = bmp;
                ImgHeaderLogo.Source = bmp;
                ImgModalLogo.Source = bmp;
            }
        }
        catch { }

        // Enable Win32 Drag & Drop (even when elevated via UAC)
        try
        {
            DragAcceptFiles(_hwnd, true);
            ChangeWindowMessageFilterEx(_hwnd, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(_hwnd, 0x0049 /* WM_COPYGLOBALDATA */, MSGFLT_ALLOW, IntPtr.Zero);
            _wndProcDelegate = CustomWndProc;
            _origWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
        }
        catch { }

        // Initialize Taskbar Progress
        try
        {
            _taskbarList = (ITaskbarList3)new TaskbarInstance();
            _taskbarList.HrInit();
        }
        catch { }

        // Initialize Pure Win32 System Tray Service (Rule 1)
        _trayService = new StormTrayService(_hwnd)
        {
            OnRestoreRequested = RestoreFromTray,
            OnCheckUpdatesRequested = () => DispatcherQueue.TryEnqueue(() => _ = UpdateManager.CheckForUpdatesAsync(silent: false)),
            OnAboutRequested = () => DispatcherQueue.TryEnqueue(ShowAboutModal),
            OnExitRequested = ExitApplicationFromTray,
            OnFilesDropped = files => DispatcherQueue.TryEnqueue(() => _ = AddPathsAsync(files))
        };
    }

    private void CenterWindowOnScreen()
    {
        try
        {
            if (_appWindow == null) return;
            var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            if (area != null)
            {
                int x = area.WorkArea.X + (area.WorkArea.Width - _appWindow.Size.Width) / 2;
                int y = area.WorkArea.Y + (area.WorkArea.Height - _appWindow.Size.Height) / 2;
                _appWindow.Move(new PointInt32(Math.Max(0, x), Math.Max(0, y)));
            }
        }
        catch { }
    }

    private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DROPFILES)
        {
            IntPtr hDrop = wParam;
            uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            var paths = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                var sb = new StringBuilder(1024);
                if (DragQueryFile(hDrop, i, sb, sb.Capacity) > 0)
                {
                    paths.Add(sb.ToString());
                }
            }
            DragFinish(hDrop);
            if (paths.Count > 0)
            {
                DispatcherQueue.TryEnqueue(() => _ = AddPathsAsync(paths));
            }
            return IntPtr.Zero;
        }
        return CallWindowProc(_origWndProc, hWnd, msg, wParam, lParam);
    }

    private void InitializeControlsAndSettings()
    {
        HistoryManager.Initialize(DispatcherQueue);
        ListGames.ItemsSource = _filteredGames;
        ListHistory.ItemsSource = HistoryManager.Items;

        var settings = SettingsManager.Load();

        // Populate Themes
        CmbTheme.Items.Clear();
        int themeIdx = 0;
        for (int i = 0; i < ThemeManager.Themes.Count; i++)
        {
            CmbTheme.Items.Add(ThemeManager.Themes[i].Name);
            if (ThemeManager.Themes[i].Name.Equals(settings.ThemeName, StringComparison.OrdinalIgnoreCase))
                themeIdx = i;
        }
        CmbTheme.SelectedIndex = themeIdx;

        // Populate Accents
        CmbAccent.Items.Clear();
        foreach (var acc in ThemeManager.Accents)
            CmbAccent.Items.Add(acc.Name);
        CmbAccent.SelectedIndex = Math.Clamp(settings.AccentIndex, 0, ThemeManager.Accents.Length - 1);

        // Populate Fonts
        CmbFont.Items.Clear();
        foreach (var f in ThemeManager.Fonts)
            CmbFont.Items.Add(f.Name);
        CmbFont.SelectedIndex = Math.Clamp(settings.FontIndex, 0, ThemeManager.Fonts.Length - 1);

        // Populate Languages
        CmbLang.Items.Clear();
        int langIdx = 0;
        for (int i = 0; i < LocalizationManager.SupportedLanguages.Length; i++)
        {
            CmbLang.Items.Add(LocalizationManager.SupportedLanguages[i].DisplayName);
            if (LocalizationManager.SupportedLanguages[i].Code.Equals(LocalizationManager.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                langIdx = i;
        }
        CmbLang.SelectedIndex = langIdx;

        // Restore checkboxes & output folder (Rule 15)
        ChkVerifyCrc.IsChecked = settings.VerifyCrc;
        ChkCompatibility.IsChecked = settings.CompatibilityMode;
        ChkBackup.IsChecked = settings.CreateBackup;

        string defaultOut = !string.IsNullOrWhiteSpace(settings.OutputDirectory)
            ? settings.OutputDirectory
            : Path.Combine(AppContext.BaseDirectory, "Decrypted_ROMs");
        TxtOutputDir.Text = defaultOut;

        ApplyLocalization();
        CmbMode.SelectedIndex = Math.Clamp(settings.ModeIndex, 0, 1);
        UpdateOutputPanelState();

        ThemeManager.ThemeChanged += OnThemeChanged;
        OnThemeChanged();

        _isLoaded = true;

        // Load initial command-line arguments if any
        if (App.FirstLaunchArgs.Length > 0)
        {
            _ = AddPathsAsync(App.FirstLaunchArgs);
        }

        // Silent background update check
        _ = CheckForUpdatesAsync(silent: true);
    }

    private void OnThemeChanged()
    {
        RootPage.RequestedTheme = ThemeManager.CurrentPalette.IsLight ? ElementTheme.Light : ElementTheme.Dark;
        RootPage.FontFamily = new FontFamily(ThemeManager.CurrentFont.FamilyName);
    }

    private void SaveCurrentSettings()
    {
        if (!_isLoaded) return;
        string themeName = CmbTheme.SelectedItem as string ?? "MIDNIGHT";
        int accentIdx = Math.Max(0, CmbAccent.SelectedIndex);
        int fontIdx = Math.Max(0, CmbFont.SelectedIndex);
        int langIdx = Math.Clamp(CmbLang.SelectedIndex, 0, LocalizationManager.SupportedLanguages.Length - 1);
        string langCode = LocalizationManager.SupportedLanguages[langIdx].Code;

        SettingsManager.Save(new UserSettings
        {
            OutputDirectory = TxtOutputDir.Text,
            ModeIndex = Math.Max(0, CmbMode.SelectedIndex),
            VerifyCrc = ChkVerifyCrc.IsChecked == true,
            CompatibilityMode = ChkCompatibility.IsChecked == true,
            CreateBackup = ChkBackup.IsChecked == true,
            ThemeName = themeName,
            AccentIndex = accentIdx,
            FontIndex = fontIdx,
            LanguageCode = langCode
        });
    }

    // ==================== LOCALIZATION & TOOLTIPS (Rule 3 & 14) ====================

    private void ApplyLocalization()
    {
        string G(string k) => LocalizationManager.Get(k);

        TxtSubHeader.Text = G("Subtitle");
        LblTheme.Text = G("ThemeLabel");
        LblAccent.Text = G("AccentLabel");
        LblFont.Text = G("FontLabel");
        LblLang.Text = G("LangLabel");

        TxtBtnAddFiles.Text = G("AddFiles");
        TxtBtnAddFolder.Text = G("AddFolder");
        TxtBtnClearQueue.Text = G("ClearList");

        LblStatTotal.Text = G("TotalGames") + ":";
        LblStatEnc.Text = G("EncryptedCount") + ":";
        LblStatDone.Text = G("DecryptedCount") + ":";

        UpdateTabHeaders();

        HdrColFile.Text = $"{G("ColFileName")} / {G("ColTitle")} ↕";
        HdrColCode.Text = $"{G("ColCode")} ↕";
        HdrColFormat.Text = "FORMAT ↕";
        HdrColSize.Text = $"{G("ColSize")} ↕";
        HdrColStatus.Text = $"{G("ColStatus")} ↕";
        HdrColActions.Text = "ACTIONS";

        HdrHistDate.Text = G("ColProcessedAt");
        HdrHistFile.Text = G("ColFileName");
        HdrHistCode.Text = G("ColCode");
        HdrHistFormat.Text = "FORMAT";
        HdrHistSize.Text = G("ColSize");
        HdrHistResult.Text = G("ColStatus");
        HdrHistActions.Text = G("OpenFolder");
        BtnClearHistory.Content = $"🗑 {G("ClearHistory")}";

        TxtDropTitle.Text = G("DragDropHint");
        TxtDropSub.Text = G("Subtitle");

        int savedMode = Math.Max(0, CmbMode.SelectedIndex);
        CmbMode.Items.Clear();
        CmbMode.Items.Add(G("ModeCopy"));
        CmbMode.Items.Add(G("ModeInPlace"));
        CmbMode.SelectedIndex = savedMode;

        LblOutputFolder.Text = G("OutputFolderLabel");
        BtnBrowseOut.Content = G("BrowseFolder");
        TxtBtnStartBatch.Text = G("DecryptAll").ToUpperInvariant();

        if (!_isProcessingBatch)
            TxtStatusFooter.Text = G("DragDropHint");

        // Styled Tooltips on ALL controls (Rule 14)
        ToolTipService.SetToolTip(BtnWinMinimize, G("ToolTipMinimize"));
        ToolTipService.SetToolTip(BtnWinMaximize, G("ToolTipMaximize"));
        ToolTipService.SetToolTip(BtnWinClose, G("ToolTipClose"));
        ToolTipService.SetToolTip(CmbTheme, G("ToolTipTheme"));
        ToolTipService.SetToolTip(CmbAccent, G("ToolTipAccent"));
        ToolTipService.SetToolTip(CmbFont, G("ToolTipFont"));
        ToolTipService.SetToolTip(CmbLang, G("ToolTipLang"));
        ToolTipService.SetToolTip(BtnCheckUpdate, G("ToolTipCheckUpdates"));
        ToolTipService.SetToolTip(BtnAbout, G("ToolTipAbout"));

        ToolTipService.SetToolTip(BtnAddFiles, G("ToolTipAddFiles"));
        ToolTipService.SetToolTip(BtnAddFolder, G("ToolTipAddFolder"));
        ToolTipService.SetToolTip(BtnClearQueue, G("ToolTipClearList"));
        ToolTipService.SetToolTip(BtnExportReport, "CSV / JSON Report");
        ToolTipService.SetToolTip(TxtSearch, "Search / Filter Queue");
        ToolTipService.SetToolTip(BtnTabQueue, G("ToolTipTabQueue"));
        ToolTipService.SetToolTip(BtnTabHistory, G("ToolTipTabHistory"));

        ToolTipService.SetToolTip(CmbMode, G("ToolTipModeCopy"));
        ToolTipService.SetToolTip(ChkVerifyCrc, "CRC16 Header & Secure Area Validation");
        ToolTipService.SetToolTip(ChkCompatibility, "NDS / TWiLight Menu++ Compatibility Patch");
        ToolTipService.SetToolTip(ChkBackup, G("ToolTipBackup"));
        ToolTipService.SetToolTip(TxtOutputDir, G("ToolTipOutFolder"));
        ToolTipService.SetToolTip(BtnBrowseOut, G("ToolTipBrowseFolder"));
        ToolTipService.SetToolTip(BtnOpenOut, G("ToolTipOpenFolder"));
        ToolTipService.SetToolTip(BtnStartBatch, G("ToolTipDecryptAll"));
        ToolTipService.SetToolTip(BtnClearHistory, G("ToolTipClearHistory"));
    }

    private void UpdateTabHeaders()
    {
        BtnTabQueue.Content = $"{LocalizationManager.Get("TabQueue")} ({_allGames.Count})";
        BtnTabHistory.Content = $"{LocalizationManager.Get("TabHistory")} ({HistoryManager.Items.Count})";
    }

    private void UpdateStats()
    {
        TxtStatTotal.Text = _allGames.Count.ToString();
        TxtStatEnc.Text = _allGames.Count(g => g.IsEncrypted).ToString();
        TxtStatDone.Text = _allGames.Count(g => g.IsCompleted).ToString();
        EmptyStateOverlay.Visibility = _filteredGames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTabHeaders();
    }

    // ==================== WINDOW CAPTION & TRAY HANDLERS (Rule 1) ====================

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_trayService == null || !_trayService.IsQuittingFromTray)
        {
            args.Cancel = true;
            ShowWindow(_hwnd, 0 /* SW_HIDE */);
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow?.Presenter is OverlappedPresenter p)
            p.Minimize();
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow?.Presenter is OverlappedPresenter p)
        {
            if (p.State == OverlappedPresenterState.Maximized)
                p.Restore();
            else
                p.Maximize();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        // Close-to-Tray (Rule 1)
        ShowWindow(_hwnd, 0 /* SW_HIDE */);
    }

    private void BtnWinClose_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        BtnWinClose.Background = ThemeManager.BrushFromHex("#E11D48");
    }

    private void BtnWinClose_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        BtnWinClose.Background = new SolidColorBrush(Colors.Transparent);
    }

    public void RestoreFromTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ShowWindow(_hwnd, 9 /* SW_RESTORE */);
            SetForegroundWindow(_hwnd);
            Activate();
        });
    }

    private void ExitApplicationFromTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_trayService != null) { _trayService.IsQuittingFromTray = true; _trayService.Dispose(); }
            Application.Current.Exit();
        });
    }

    // ==================== APPEARANCE COMBOBOX HANDLERS ====================

    private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || CmbTheme.SelectedItem is not string themeName) return;
        ThemeManager.ApplyTheme(themeName);
        SaveCurrentSettings();
    }

    private void CmbAccent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || CmbAccent.SelectedIndex < 0) return;
        ThemeManager.ApplyAccent(CmbAccent.SelectedIndex);
        SaveCurrentSettings();
    }

    private void CmbFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || CmbFont.SelectedIndex < 0) return;
        ThemeManager.ApplyFont(CmbFont.SelectedIndex);
        SaveCurrentSettings();
    }

    private void CmbLang_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || CmbLang.SelectedIndex < 0) return;
        string code = LocalizationManager.SupportedLanguages[CmbLang.SelectedIndex].Code;
        LocalizationManager.SetLanguage(code);
        ApplyLocalization();
        SaveCurrentSettings();
    }

    private void CmbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        UpdateOutputPanelState();
        SaveCurrentSettings();
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        SaveCurrentSettings();
    }

    private void UpdateOutputPanelState()
    {
        bool isSeparate = CmbMode.SelectedIndex == 0;
        PanelOutputFolder.Opacity = isSeparate ? 1.0 : 0.45;
        BtnBrowseOut.IsEnabled = isSeparate;
    }

    // ==================== TAB SWITCHING, SEARCH & SORTING ====================

    private void BtnTabQueue_Click(object sender, RoutedEventArgs e)
    {
        PanelQueueView.Visibility = Visibility.Visible;
        PanelHistoryView.Visibility = Visibility.Collapsed;
        BtnTabQueue.Background = (Brush)Application.Current.Resources["AccentPrimaryBrush"];
        BtnTabQueue.Foreground = new SolidColorBrush(Colors.White);
        BtnTabHistory.Background = (Brush)Application.Current.Resources["BgSecondaryBrush"];
        BtnTabHistory.Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"];
    }

    private void BtnTabHistory_Click(object sender, RoutedEventArgs e)
    {
        PanelQueueView.Visibility = Visibility.Collapsed;
        PanelHistoryView.Visibility = Visibility.Visible;
        BtnTabHistory.Background = (Brush)Application.Current.Resources["AccentPrimaryBrush"];
        BtnTabHistory.Foreground = new SolidColorBrush(Colors.White);
        BtnTabQueue.Background = (Brush)Application.Current.Resources["BgSecondaryBrush"];
        BtnTabQueue.Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"];
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshFilteredGames();
    }

    private void RefreshFilteredGames()
    {
        string q = TxtSearch.Text?.Trim() ?? string.Empty;
        IEnumerable<RomItem> query = _allGames;

        if (!string.IsNullOrEmpty(q))
        {
            query = query.Where(g =>
                g.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                g.InternalTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                g.GameCode.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                g.RegionCode.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                g.FormatType.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        query = _currentSortField switch
        {
            "Code" => _sortAscending ? query.OrderBy(g => g.GameCode) : query.OrderByDescending(g => g.GameCode),
            "Format" => _sortAscending ? query.OrderBy(g => g.FormatType) : query.OrderByDescending(g => g.FormatType),
            "Size" => _sortAscending ? query.OrderBy(g => g.FileSizeBytes) : query.OrderByDescending(g => g.FileSizeBytes),
            "Status" => _sortAscending ? query.OrderBy(g => g.StatusText) : query.OrderByDescending(g => g.StatusText),
            _ => _sortAscending ? query.OrderBy(g => g.FileName) : query.OrderByDescending(g => g.FileName)
        };

        _filteredGames.Clear();
        foreach (var item in query)
            _filteredGames.Add(item);

        UpdateStats();
    }

    private void ToggleSort(string field)
    {
        if (_currentSortField == field)
            _sortAscending = !_sortAscending;
        else
        {
            _currentSortField = field;
            _sortAscending = true;
        }
        RefreshFilteredGames();
    }

    private void BtnSortName_Click(object sender, RoutedEventArgs e) => ToggleSort("Name");
    private void BtnSortCode_Click(object sender, RoutedEventArgs e) => ToggleSort("Code");
    private void BtnSortFormat_Click(object sender, RoutedEventArgs e) => ToggleSort("Format");
    private void BtnSortSize_Click(object sender, RoutedEventArgs e) => ToggleSort("Size");
    private void BtnSortStatus_Click(object sender, RoutedEventArgs e) => ToggleSort("Status");

    // ==================== ADDING FILES, FOLDERS & DRAG-AND-DROP ====================

    private async void BtnAddFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.ViewMode = PickerViewMode.List;
            picker.FileTypeFilter.Add(".nds");
            picker.FileTypeFilter.Add(".dsi");
            picker.FileTypeFilter.Add(".srl");
            picker.FileTypeFilter.Add(".app");
            picker.FileTypeFilter.Add(".cia");
            picker.FileTypeFilter.Add(".tad");
            picker.FileTypeFilter.Add(".tmd");
            picker.FileTypeFilter.Add(".zip");
            picker.FileTypeFilter.Add(".7z");
            picker.FileTypeFilter.Add(".rar");

            var files = await picker.PickMultipleFilesAsync();
            if (files != null && files.Count > 0)
            {
                await AddPathsAsync(files.Select(f => f.Path));
            }
        }
        catch { }
    }

    private async void BtnAddFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null && !string.IsNullOrEmpty(folder.Path))
            {
                await AddPathsAsync(new[] { folder.Path });
            }
        }
        catch { }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                if (paths.Count > 0)
                {
                    await AddPathsAsync(paths);
                }
            }
        }
        catch { }
    }

    public async Task AddPathsAsync(IEnumerable<string> inputPaths)
    {
        string[] validExts = { ".nds", ".dsi", ".srl", ".app", ".cia", ".tad", ".tmd", ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz" };
        var discoveredFiles = await Task.Run(() =>
        {
            var list = new List<string>();
            foreach (var p in inputPaths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (File.Exists(p))
                {
                    list.Add(p);
                }
                else if (Directory.Exists(p))
                {
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories))
                        {
                            string ext = Path.GetExtension(f).ToLowerInvariant();
                            if (validExts.Contains(ext))
                                list.Add(f);
                        }
                    }
                    catch { }
                }
            }
            return list;
        });

        var existingSet = new HashSet<string>(_allGames.Select(g => g.FullPath), StringComparer.OrdinalIgnoreCase);
        var toInspect = discoveredFiles.Where(f => !existingSet.Contains(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (toInspect.Count == 0) return;

        var inspectedBag = new ConcurrentBag<RomItem>();
        await Task.Run(() =>
        {
            Parallel.ForEach(toInspect, path =>
            {
                var rom = DsiDecryptorEngine.InspectFile(path);
                inspectedBag.Add(rom);
            });
        });

        foreach (var item in inspectedBag.OrderBy(x => x.FileName))
        {
            item.IconSource = NdsBannerParser.GetOrCreateBitmapImage(item.BannerPngBytes);
            _allGames.Add(item);
        }

        RefreshFilteredGames();
        TxtStatusFooter.Text = LocalizationManager.Get("TotalGames") + ": " + _allGames.Count + " (+" + inspectedBag.Count + ")";
    }

    private void BtnClearQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessingBatch) return;
        _allGames.Clear();
        RefreshFilteredGames();
        PbTotal.Value = 0;
        TxtStatusFooter.Text = LocalizationManager.Get("DragDropHint");
    }

    // ==================== OUTPUT DIRECTORY PICKER ====================

    private async void BtnBrowseOut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null && !string.IsNullOrEmpty(folder.Path))
            {
                TxtOutputDir.Text = folder.Path;
                SaveCurrentSettings();
            }
        }
        catch { }
    }

    private void BtnOpenOut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = TxtOutputDir.Text;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // ==================== MULTI-CORE PARALLEL DECRYPTION & SINGLE PROCESSING ====================

    private async void BtnProcessSingle_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessingBatch) return;
        if (sender is Button btn && btn.Tag is RomItem item)
        {
            await ProcessQueueItemsAsync(new List<RomItem> { item });
        }
    }

    private async void BtnStartBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessingBatch) return;
        if (_allGames.Count == 0)
        {
            ShowMessageModal("STORM DS DECRYPTOR", LocalizationManager.Get("DragDropHint"));
            return;
        }
        await ProcessQueueItemsAsync(_allGames.ToList());
    }

    private async Task ProcessQueueItemsAsync(List<RomItem> items)
    {
        _isProcessingBatch = true;
        BtnStartBatch.IsEnabled = false;
        BtnAddFiles.IsEnabled = false;
        BtnAddFolder.IsEnabled = false;
        BtnClearQueue.IsEnabled = false;

        bool isSeparateFolder = CmbMode.SelectedIndex == 0;
        string outDir = TxtOutputDir.Text;
        bool verifyCrc = ChkVerifyCrc.IsChecked == true;
        bool compat = ChkCompatibility.IsChecked == true;
        bool createBackup = ChkBackup.IsChecked == true;

        if (isSeparateFolder && !Directory.Exists(outDir))
        {
            try { Directory.CreateDirectory(outDir); } catch { }
        }

        // Pre-resolve target paths & duplicate prompts on UI thread
        var workPlan = new List<(RomItem Item, string TargetPath)>();
        DuplicateChoice? rememberedChoice = null;

        foreach (var item in items)
        {
            string targetPath;
            if (isSeparateFolder)
            {
                string baseName = Path.GetFileNameWithoutExtension(item.FileName);
                if (!baseName.EndsWith("_Decrypted", StringComparison.OrdinalIgnoreCase))
                    baseName += "_Decrypted";
                targetPath = Path.Combine(outDir, baseName + ".nds");

                if (File.Exists(targetPath))
                {
                    DuplicateChoice choice;
                    if (rememberedChoice.HasValue)
                    {
                        choice = rememberedChoice.Value;
                    }
                    else
                    {
                        var promptRes = await PromptDuplicateAsync(Path.GetFileName(targetPath));
                        choice = promptRes.Choice;
                        if (promptRes.ApplyToAll)
                            rememberedChoice = choice;
                    }

                    if (choice == DuplicateChoice.Cancel)
                    {
                        _isProcessingBatch = false;
                        RestoreButtonsAfterBatch();
                        return;
                    }
                    if (choice == DuplicateChoice.Skip)
                    {
                        item.StatusText = "Пропущен (дубликат)";
                        continue;
                    }
                }
            }
            else
            {
                targetPath = item.FullPath;
            }

            workPlan.Add((item, targetPath));
        }

        int total = workPlan.Count;
        int completedCount = 0;
        int successCount = 0;
        var sw = Stopwatch.StartNew();

        SetTaskbarProgress(0, (ulong)Math.Max(1, total), isIndeterminate: false);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
        };

        await Parallel.ForEachAsync(workPlan, parallelOptions, async (entry, ct) =>
        {
            var (rom, target) = entry;
            DispatcherQueue.TryEnqueue(() =>
            {
                rom.IsProcessing = true;
                rom.ProgressValue = 10;
                rom.StatusText = LocalizationManager.Get("StatusProcessing");
            });

            var progress = new Progress<double>(p =>
            {
                DispatcherQueue.TryEnqueue(() => rom.ProgressValue = p);
            });

            var result = await Task.Run(() => DsiDecryptorEngine.ProcessRom(
                rom,
                target,
                verifyCrc,
                compat,
                createBackup && !isSeparateFolder,
                progress), ct);

            int doneNow = Interlocked.Increment(ref completedCount);
            if (result.Success)
                Interlocked.Increment(ref successCount);

            DispatcherQueue.TryEnqueue(() =>
            {
                rom.IsProcessing = false;
                rom.IsCompleted = result.Success;
                rom.HasError = !result.Success;
                if (result.Success) rom.IsEncrypted = false;
                rom.ProgressValue = result.Success ? 100 : 0;
                rom.StatusText = result.Message;

                double pct = total > 0 ? (doneNow * 100.0) / total : 100;
                PbTotal.Value = pct;
                TxtStatusFooter.Text = LocalizationManager.Get("StatusProcessing") + " [" + doneNow + "/" + total + "] " + rom.FileName;
                SetTaskbarProgress((ulong)doneNow, (ulong)Math.Max(1, total), false);

                HistoryManager.AddEntry(new HistoryItem
                {
                    Timestamp = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"),
                    FileName = rom.FileName,
                    GameCode = rom.GameCode,
                    FormatType = rom.FormatType,
                    FileSizeFormatted = rom.FileSizeFormatted,
                    ResultStatus = result.Message,
                    OutputPath = result.OutputPath,
                    IsSuccess = result.Success,
                    BannerPngBytes = rom.BannerPngBytes,
                    IconSource = rom.IconSource
                });
            });
        });

        sw.Stop();
        ClearTaskbarProgress();
        PbTotal.Value = 100;
        UpdateStats();

        TxtStatusFooter.Text = LocalizationManager.Get("AllDone") + " [" + successCount + "/" + total + "]" + $" ({sw.Elapsed.TotalSeconds:F2} s)";
        _isProcessingBatch = false;
        RestoreButtonsAfterBatch();

        if (total > 1)
        {
            ShowMessageModal(
                "STORM DS DECRYPTOR",
                LocalizationManager.Get("AllDone") + " (" + successCount + "/" + total + ")");
        }
    }

    private void RestoreButtonsAfterBatch()
    {
        BtnStartBatch.IsEnabled = true;
        BtnAddFiles.IsEnabled = true;
        BtnAddFolder.IsEnabled = true;
        BtnClearQueue.IsEnabled = true;
    }

    private void SetTaskbarProgress(ulong current, ulong total, bool isIndeterminate)
    {
        try
        {
            if (_taskbarList == null) return;
            _taskbarList.SetProgressState(_hwnd, isIndeterminate ? 1 : 2 /* TBPF_NORMAL */);
            _taskbarList.SetProgressValue(_hwnd, current, total);
        }
        catch { }
    }

    private void ClearTaskbarProgress()
    {
        try
        {
            _taskbarList?.SetProgressState(_hwnd, 0 /* TBPF_NOPROGRESS */);
        }
        catch { }
    }

    // ==================== ROW ACTION BUTTONS & ROM INSPECTOR ====================

    private async void BtnInspectRom_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not RomItem item) return;

        ImgInspectorBanner.Source = item.IconSource ?? ImgHeaderLogo.Source;
        TxtInspectorTitle.Text = item.FileName;
        TxtInspectorSubtitle.Text = $"{item.FormatType} • {item.GameCode} ({item.RegionCode}) • {item.FileSizeFormatted}";
        TxtInspectorDetails.Text = "Вычисление криптографических хэшей (CRC16 / CRC32 / MD5 / SHA-1 / SHA-256) и чтение заголовка TWL/NTR...";
        OverlayInspector.Visibility = Visibility.Visible;

        var info = await Task.Run(() => DsiDecryptorEngine.InspectRomDetailed(item));

        var sb = new StringBuilder();
        sb.AppendLine($"=== STORM DS DECRYPTOR {UpdateManager.CurrentVersion} — ROM & BANNER INSPECTOR ===");
        sb.AppendLine($"Файл:               {info.FileName}");
        sb.AppendLine($"Полный путь:        {info.FullPath}");
        sb.AppendLine($"Размер:             {info.FileSizeBytes:N0} байт ({item.FileSizeFormatted})");
        sb.AppendLine($"Формат и защита:    {info.FormatType}");
        sb.AppendLine($"Внутр. заголовок:   {info.InternalTitle}");
        sb.AppendLine($"Код игры / Регион:  {info.GameCode} / {info.Region}");
        sb.AppendLine($"Код издателя:       {info.MakerCode}");
        sb.AppendLine($"Тип картриджа:      {info.UnitCodeDescription}");
        sb.AppendLine($"TWL Title ID:       {info.TwlTitleId}");
        sb.AppendLine();
        sb.AppendLine("--- КАРТА СЕКЦИЙ ОБРАЗА (ARM9 / ARM7 / FNT / FAT / MODCRYPT) ---");
        sb.AppendLine($"ARM9 Offset / Size: {info.Arm9Offset} ({info.Arm9Size})");
        sb.AppendLine($"ARM7 Offset / Size: {info.Arm7Offset} ({info.Arm7Size})");
        sb.AppendLine($"FNT Offset / Size:  {info.FntOffset} ({info.FntSize})");
        sb.AppendLine($"FAT Offset / Size:  {info.FatOffset} ({info.FatSize})");
        sb.AppendLine($"Banner Offset:      {info.BannerOffset} (Animated DSi Icon: {(info.HasAnimatedBanner ? "YES" : "NO")})");
        sb.AppendLine($"Modcrypt Area 1:    {info.Modcrypt1Offset} ({info.Modcrypt1Size})");
        sb.AppendLine($"Modcrypt Area 2:    {info.Modcrypt2Offset} ({info.Modcrypt2Size})");
        sb.AppendLine();
        sb.AppendLine("--- КОНТРОЛЬНЫЕ СУММЫ И ХЭШИ ---");
        sb.AppendLine($"Header CRC16:       {info.HeaderCrc16}");
        sb.AppendLine($"Secure Area CRC16:  {info.SecureAreaCrc16}");
        sb.AppendLine($"CRC32:              {info.Crc32}");
        sb.AppendLine($"MD5:                {info.Md5}");
        sb.AppendLine($"SHA-1:              {info.Sha1}");
        sb.AppendLine($"SHA-256:            {info.Sha256}");

        if (info.BannerTitles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- МУЛЬТИЯЗЫЧНЫЕ НАЗВАНИЯ ИЗ БАННЕРА КАРТРИДЖА ---");
            foreach (var kv in info.BannerTitles)
            {
                sb.AppendLine($"[{kv.Key}]: {kv.Value}");
            }
        }

        TxtInspectorDetails.Text = sb.ToString();
    }

    private void BtnCopyInspector_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(TxtInspectorDetails.Text);
            Clipboard.SetContent(pkg);
            TxtStatusFooter.Text = "Отчёт инспектора ROM скопирован в буфер обмена.";
        }
        catch { }
    }

    private void BtnCloseInspector_Click(object sender, RoutedEventArgs e)
    {
        OverlayInspector.Visibility = Visibility.Collapsed;
    }

    private void BtnOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is RomItem item && File.Exists(item.FullPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"") { UseShellExecute = true });
            }
            catch { }
        }
    }

    private void BtnRemoveSingle_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessingBatch) return;
        if (sender is Button btn && btn.Tag is RomItem item)
        {
            _allGames.Remove(item);
            RefreshFilteredGames();
        }
    }

    private void BtnHistoryOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HistoryItem item && !string.IsNullOrEmpty(item.OutputPath))
        {
            try
            {
                if (File.Exists(item.OutputPath))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.OutputPath}\"") { UseShellExecute = true });
                else if (Directory.Exists(Path.GetDirectoryName(item.OutputPath)))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(item.OutputPath)}\"") { UseShellExecute = true });
            }
            catch { }
        }
    }

    private void BtnHistoryRemoveSingle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HistoryItem item)
        {
            HistoryManager.RemoveEntry(item);
            UpdateTabHeaders();
        }
    }

    private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
    {
        HistoryManager.ClearAll();
        UpdateTabHeaders();
    }

    // ==================== EXPORT CSV / JSON REPORT ====================

    private void BtnExportReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string outDir = TxtOutputDir.Text;
            if (!Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string csvPath = Path.Combine(outDir, $"STORM_DS_DECRYPTOR_Report_{stamp}.csv");
            string jsonPath = Path.Combine(outDir, $"STORM_DS_DECRYPTOR_Report_{stamp}.json");

            var sb = new StringBuilder();
            sb.AppendLine("FileName;InternalTitle;GameCode;Region;Format;Size;Status;FullPath");
            foreach (var g in _allGames)
            {
                sb.AppendLine($"\"{g.FileName}\";\"{g.InternalTitle}\";\"{g.GameCode}\";\"{g.RegionCode}\";\"{g.FormatType}\";\"{g.FileSizeFormatted}\";\"{g.StatusText}\";\"{g.FullPath}\"");
            }
            File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);

            var jsonItems = _allGames.Select(g => new
            {
                g.FileName,
                g.InternalTitle,
                g.GameCode,
                g.RegionCode,
                g.FormatType,
                g.FileSizeBytes,
                g.FileSizeFormatted,
                g.IsEncrypted,
                g.IsCompleted,
                g.StatusText,
                g.FullPath
            }).ToList();

            File.WriteAllText(jsonPath, JsonSerializer.Serialize(jsonItems, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            ShowMessageModal("Экспорт отчёта завершён", $"Отчёты успешно сохранены:\n• {csvPath}\n• {jsonPath}");
        }
        catch (Exception ex)
        {
            ShowMessageModal("Ошибка экспорта", ex.Message);
        }
    }

    // ==================== MODAL OVERLAYS & ESC KEY HANDLER (Rule 9 & 16) ====================

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (OverlayInspector.Visibility == Visibility.Visible)
            {
                OverlayInspector.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            else if (OverlayMessage.Visibility == Visibility.Visible)
            {
                OverlayMessage.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            else if (OverlayUpdate.Visibility == Visibility.Visible)
            {
                OverlayUpdate.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            else if (OverlayDuplicate.Visibility == Visibility.Visible)
            {
                ResolveDuplicatePrompt(DuplicateChoice.Cancel);
                e.Handled = true;
            }
        }
    }

    public void ShowMessageModal(string title, string message)
    {
        TxtModalTitle.Text = title;
        TxtModalMessage.Text = message;
        BtnModalOk.Content = "OK";
        OverlayMessage.Visibility = Visibility.Visible;
    }

    private void showAboutInternal()
    {
        ShowMessageModal(
            LocalizationManager.Get("AboutTitle"),
            LocalizationManager.Get("AboutMsg"));
    }

    private void ShowAboutModal() => showAboutInternal();

    private void BtnAbout_Click(object sender, RoutedEventArgs e) => ShowAboutModal();

    private void BtnModalOk_Click(object sender, RoutedEventArgs e)
    {
        OverlayMessage.Visibility = Visibility.Collapsed;
    }

    private Task<(DuplicateChoice Choice, bool ApplyToAll)> PromptDuplicateAsync(string fileName)
    {
        _dupTcs = new TaskCompletionSource<(DuplicateChoice, bool)>();
        TxtDupTitle.Text = LocalizationManager.Get("DuplicateTitle");
        TxtDupMessage.Text = LocalizationManager.Get("DuplicateMsg") + " (" + fileName + ")";
        ChkDupApplyAll.Content = LocalizationManager.Get("ChkApplyToAllDuplicates");
        ChkDupApplyAll.IsChecked = false;
        BtnDupOverwrite.Content = LocalizationManager.Get("BtnReprocess");
        BtnDupSkip.Content = LocalizationManager.Get("BtnSkip");
        BtnDupCancel.Content = "Cancel";
        OverlayDuplicate.Visibility = Visibility.Visible;
        return _dupTcs.Task;
    }

    private void ResolveDuplicatePrompt(DuplicateChoice choice)
    {
        OverlayDuplicate.Visibility = Visibility.Collapsed;
        bool applyAll = ChkDupApplyAll.IsChecked == true;
        _dupTcs?.TrySetResult((choice, applyAll));
    }

    private void BtnDupOverwrite_Click(object sender, RoutedEventArgs e) => ResolveDuplicatePrompt(DuplicateChoice.Overwrite);
    private void BtnDupSkip_Click(object sender, RoutedEventArgs e) => ResolveDuplicatePrompt(DuplicateChoice.Skip);
    private void BtnDupCancel_Click(object sender, RoutedEventArgs e) => ResolveDuplicatePrompt(DuplicateChoice.Cancel);

    // ==================== AUTO-UPDATER (Rule 9) ====================

    private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(silent: false);
    }

    private async Task CheckForUpdatesAsync(bool silent)
    {
        await UpdateManager.CheckForUpdatesAsync(silent);
    }

    public void ShowUpdateModal(UpdateInfo info)
    {
        _pendingUpdate = info;
        TxtUpdateHeader.Text = LocalizationManager.Get("UpdateAvailableTitle");
        TxtUpdateSub.Text = $"{info.Title} ({LocalizationManager.Get("UpdateCurrentVer")}: {UpdateManager.CurrentVersionString} -> {info.VersionString})";
        TxtUpdateNotes.Text = string.IsNullOrWhiteSpace(info.Changelog) ? $"STORM DS DECRYPTOR {info.VersionString}" : info.Changelog;
        BtnUpdateConfirm.Content = LocalizationManager.Get("UpdateNow"); // Concise "Обновить" / "Update" (Rule 9)
        BtnUpdateLater.Content = LocalizationManager.Get("UpdateLater");
        PbUpdateDownload.Visibility = Visibility.Collapsed;
        BtnUpdateConfirm.IsEnabled = true;
        OverlayUpdate.Visibility = Visibility.Visible;
    }

    private void BtnUpdateLater_Click(object sender, RoutedEventArgs e)
    {
        OverlayUpdate.Visibility = Visibility.Collapsed;
    }

    private async void BtnUpdateConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null) return;
        BtnUpdateConfirm.IsEnabled = false;
        PbUpdateDownload.Visibility = Visibility.Visible;
        PbUpdateDownload.Value = 0;

        var progress = new Progress<double>(p =>
        {
            DispatcherQueue.TryEnqueue(() => PbUpdateDownload.Value = p);
        });

        bool ok = await UpdateManager.DownloadAndInstallUpdateAsync(_pendingUpdate, progress);
        if (ok)
        {
            ExitApplicationFromTray();
        }
        else
        {
            BtnUpdateConfirm.IsEnabled = true;
            PbUpdateDownload.Visibility = Visibility.Collapsed;
        }
    }
}

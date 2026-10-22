using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;

namespace StormDsDecryptor;

public partial class App : Application
{
    public static MainWindow? MainWindowInstance { get; private set; }
    public static string[] FirstLaunchArgs { get; private set; } = Array.Empty<string>();
    private static Mutex? _singleInstanceMutex;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    public App()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID("STORM.DS.DECRYPTOR");
        }
        catch { }

        string[] cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (cmdArgs.Length > 0 && cmdArgs.Any(a => a.Equals("--cli", StringComparison.OrdinalIgnoreCase) || a.Equals("-c", StringComparison.OrdinalIgnoreCase)))
        {
            RunCliBatchMode(cmdArgs);
            Environment.Exit(0);
            return;
        }

        FirstLaunchArgs = cmdArgs;

        _singleInstanceMutex = new Mutex(true, @"Global\StormDsDecryptor_SingleInstance_Mutex", out bool isNewInstance);
        // Proceed with normal WinUI 3 initialization
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var settings = SettingsManager.Load();
        if (!string.IsNullOrWhiteSpace(settings.LanguageCode))
        {
            LocalizationManager.SetLanguage(settings.LanguageCode);
        }
        else
        {
            LocalizationManager.InitSystemLanguage();
        }

        ThemeManager.ApplyTheme(settings.ThemeName);
        ThemeManager.ApplyAccent(settings.AccentIndex);
        ThemeManager.ApplyFont(settings.FontIndex);

        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }

    private static void RunCliBatchMode(string[] args)
    {
        try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"=== STORM DS DECRYPTOR {UpdateManager.CurrentVersion} (CLI Batch Mode) ===");

        var targets = args.Where(a => !a.StartsWith("-")).ToList();
        if (targets.Count == 0)
        {
            targets.Add(Directory.GetCurrentDirectory());
        }

        var files = new List<string>();
        string[] exts = { ".nds", ".dsi", ".srl", ".app", ".cia", ".tad", ".tmd" };

        foreach (var t in targets)
        {
            if (File.Exists(t))
            {
                files.Add(t);
            }
            else if (Directory.Exists(t))
            {
                foreach (var f in Directory.EnumerateFiles(t, "*.*", SearchOption.AllDirectories))
                {
                    if (exts.Contains(Path.GetExtension(f).ToLowerInvariant()) && !f.Contains("_Decrypted"))
                        files.Add(f);
                }
            }
        }

        Console.WriteLine($"Найдено файлов: {files.Count}");
        string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Decrypted_ROMs");
        Directory.CreateDirectory(outDir);

        int ok = 0;
        foreach (var f in files)
        {
            var item = DsiDecryptorEngine.InspectFile(f);
            string dest = Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + "_Decrypted.nds");
            var res = DsiDecryptorEngine.ProcessRom(item, dest, true, true, false);
            if (res.Success)
            {
                ok++;
                Console.WriteLine($"[OK] {item.FileName} -> {Path.GetFileName(dest)}");
            }
            else
            {
                Console.WriteLine($"[ERR] {item.FileName}: {res.Message}");
            }
        }
        Console.WriteLine($"Готово! Успешно обработано: {ok} из {files.Count}");
    }
}

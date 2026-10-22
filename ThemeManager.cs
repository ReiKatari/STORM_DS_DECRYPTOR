using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace StormDsDecryptor;

public record ThemePalette(
    string Name,
    string WindowBg,
    string HeaderBg,
    string CardBg,
    string CardBorder,
    string TextPrimary,
    string TextSecondary,
    bool IsLight = false);

public record AccentInfo(string Name, string ColorHex, string GlowHex, string SoftHex);
public record FontInfo(string Name, string FamilyName);

public static class ThemeManager
{
    public static event Action? ThemeChanged;

    public static readonly List<ThemePalette> Themes = new()
    {
        new("MIDNIGHT",   "#0E0A1A", "#141026", "#1A1432", "#31265A", "#F0F0F5", "#A098C0", false),
        new("NIGHT",      "#050508", "#0A0A10", "#101018", "#242436", "#F2F2F7", "#8E8EA8", false),
        new("DARK",       "#101216", "#161920", "#1D212A", "#2E3545", "#ECEFF4", "#9AA5B8", false),
        new("DAY",        "#F3F5FA", "#FFFFFF", "#FFFFFF", "#CBD5E1", "#0F172A", "#334155", true),
        new("FANTASY",    "#0D1026", "#131836", "#1A2148", "#32408F", "#EEF2FF", "#9AB0E8", false),
        new("CYBERPUNK",  "#0A0812", "#120E20", "#1A132E", "#4A2068", "#FFF9C4", "#FF80AB", false),
        new("SYNTHWAVE",  "#140824", "#1E0C36", "#281248", "#5E2496", "#FDE8FF", "#E088FF", false),
        new("BLOOD MOON", "#140608", "#1F090D", "#2B0D13", "#5C1B26", "#FFEBF0", "#D88A98", false),
        new("ARCTIC",     "#07141C", "#0B1E2A", "#10293A", "#1F5070", "#E8F8FF", "#88C4E0", false)
    };

    public static readonly AccentInfo[] Accents = new[]
    {
        new AccentInfo("Violet",  "#8B5CF6", "#A855F7", "#2A8B5CF6"),
        new AccentInfo("Cyan",    "#00E5FF", "#38BDF8", "#2A00E5FF"),
        new AccentInfo("Emerald", "#10B981", "#34D399", "#2A10B981"),
        new AccentInfo("Crimson", "#F43F5E", "#FB7185", "#2AF43F5E"),
        new AccentInfo("Amber",   "#F59E0B", "#FBBF24", "#2AF59E0B"),
        new AccentInfo("Magenta", "#EC4899", "#F472B6", "#2AEC4899"),
        new AccentInfo("Blue",    "#3B82F6", "#60A5FA", "#2A3B82F6"),
        new AccentInfo("Orange",  "#F97316", "#FB923C", "#2AF97316"),
        new AccentInfo("Lime",    "#84CC16", "#A3E635", "#2A84CC16"),
        new AccentInfo("Teal",    "#14B8A6", "#2DD4BF", "#2A14B8A6"),
        new AccentInfo("Indigo",  "#6366F1", "#818CF8", "#2A6366F1"),
        new AccentInfo("Gold",    "#EAB308", "#FACC15", "#2AEAB308"),
        new AccentInfo("Silver",  "#94A3B8", "#CBD5E1", "#2A94A3B8")
    };

    public static readonly FontInfo[] Fonts = new[]
    {
        new FontInfo("Inter Variable",   "Inter Variable, Segoe UI Variable Text, Segoe UI"),
        new FontInfo("Plus Jakarta Sans","Plus Jakarta Sans, Segoe UI Variable Text, Segoe UI"),
        new FontInfo("Geist Sans",       "Geist Sans, Segoe UI Variable Text, Segoe UI"),
        new FontInfo("Manrope",          "Manrope, Segoe UI Variable Text, Segoe UI"),
        new FontInfo("Outfit",           "Outfit, Segoe UI Variable Text, Segoe UI"),
        new FontInfo("Montserrat",       "Montserrat, Segoe UI Variable Text, Segoe UI"),
        new FontInfo("Rubik",            "Rubik, Segoe UI Variable Text, Segoe UI"),
        new FontInfo("Orbitron",         "Orbitron, Segoe UI Variable Display, Segoe UI"),
        new FontInfo("JetBrains Mono",   "JetBrains Mono, Cascadia Code, Consolas"),
        new FontInfo("Cascadia Code",    "Cascadia Code, Consolas, Segoe UI")
    };

    public static ThemePalette CurrentPalette { get; private set; } = Themes[0];
    public static AccentInfo CurrentAccent { get; private set; } = Accents[0];
    public static FontInfo CurrentFont { get; private set; } = Fonts[0];

    public static Color ParseColor(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6)
        {
            return Color.FromArgb(255,
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
        }
        if (hex.Length == 8)
        {
            return Color.FromArgb(
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16),
                Convert.ToByte(hex.Substring(6, 2), 16));
        }
        return Color.FromArgb(255, 139, 92, 246);
    }

    public static SolidColorBrush BrushFromHex(string hex) => new(ParseColor(hex));

    private static void SetBrush(ResourceDictionary res, string key, string hex)
    {
        Color c = ParseColor(hex);
        if (res.TryGetValue(key, out object? existing) && existing is SolidColorBrush sb)
        {
            sb.Color = c;
        }
        else
        {
            res[key] = new SolidColorBrush(c);
        }
    }

    public static void ApplyTheme(string themeName)
    {
        var palette = Themes.Find(t => t.Name.Equals(themeName, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];
        CurrentPalette = palette;
        UpdateAppResources();
    }

    public static void ApplyAccent(int index)
    {
        if (index < 0 || index >= Accents.Length) index = 0;
        CurrentAccent = Accents[index];
        UpdateAppResources();
    }

    public static void ApplyFont(int index)
    {
        if (index < 0 || index >= Fonts.Length) index = 0;
        CurrentFont = Fonts[index];
        UpdateAppResources();
    }

    public static void UpdateAppResources()
    {
        try
        {
            var res = Application.Current?.Resources;
            if (res == null) return;

            SetBrush(res, "BgPrimaryBrush", CurrentPalette.WindowBg);
            SetBrush(res, "BgSecondaryBrush", CurrentPalette.HeaderBg);
            SetBrush(res, "BgCardBrush", CurrentPalette.CardBg);
            SetBrush(res, "BgElevatedBrush", CurrentPalette.IsLight ? "#E2E8F0" : "#241D42");
            SetBrush(res, "BorderSubtleBrush", CurrentPalette.CardBorder);
            SetBrush(res, "TextPrimaryBrush", CurrentPalette.TextPrimary);
            SetBrush(res, "TextSecondaryBrush", CurrentPalette.TextSecondary);
            SetBrush(res, "TextMutedBrush", CurrentPalette.IsLight ? "#64748B" : "#7A7298");

            SetBrush(res, "AccentPrimaryBrush", CurrentAccent.ColorHex);
            SetBrush(res, "AccentGlowBrush", CurrentAccent.GlowHex);
            SetBrush(res, "AccentSoftBrush", CurrentAccent.SoftHex);

            SetBrush(res, "ToolTipBgBrush", CurrentPalette.IsLight ? "#FFFFFF" : CurrentPalette.HeaderBg);
            SetBrush(res, "ToolTipFgBrush", CurrentPalette.TextPrimary);
            SetBrush(res, "ToolTipBorderBrush", CurrentAccent.ColorHex);

            res["AppFontFamily"] = new FontFamily(CurrentFont.FamilyName);

            ThemeChanged?.Invoke();
        }
        catch { }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace KHZ.AssetRegister;

public static class ThemeManager
{
    public static IReadOnlyList<string> Themes { get; } =
        new[] { "System", "Light", "Dark", "Blue", "Green" };

    public static string CurrentTheme { get; private set; } = "System";

    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KHZ",
        "AssetRegister");

    private static string ThemePath => Path.Combine(SettingsDirectory, "theme.txt");

    public static void LoadAndApply()
    {
        var theme = "System";

        try
        {
            if (File.Exists(ThemePath))
            {
                var saved = File.ReadAllText(ThemePath).Trim();
                if (Themes.Contains(saved, StringComparer.OrdinalIgnoreCase))
                    theme = Themes.First(x => x.Equals(saved, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch
        {
            theme = "System";
        }

        Apply(theme, persist: false);
    }

    public static void Apply(string theme, bool persist = true)
    {
        var normalized = Themes.FirstOrDefault(
            x => x.Equals(theme, StringComparison.OrdinalIgnoreCase)) ?? "System";

        var resolved = normalized == "System"
            ? (SystemUsesDarkApps() ? "Dark" : "Light")
            : normalized;

        var palette = resolved switch
        {
            "Dark" => new Palette(
                "#111827",
                "#1F2937",
                "#172033",
                "#374151",
                "#F9FAFB",
                "#A7B0BF",
                "#3B82F6",
                "#FFFFFF",
                "#284A78",
                "#F59E0B"),

            "Blue" => new Palette(
                "#EAF2FF",
                "#F8FBFF",
                "#DCEAFF",
                "#A9C7EF",
                "#102A43",
                "#526B84",
                "#0B63CE",
                "#FFFFFF",
                "#C5DBF8",
                "#A35C00"),

            "Green" => new Palette(
                "#EDF8F1",
                "#FBFFFC",
                "#E1F3E7",
                "#B4D8C1",
                "#163A24",
                "#52705D",
                "#168C4B",
                "#FFFFFF",
                "#C9E9D4",
                "#9A5A00"),

            _ => new Palette(
                "#F3F6FA",
                "#FFFFFF",
                "#FAFAFA",
                "#D8DEE8",
                "#1F2937",
                "#667085",
                "#2563EB",
                "#FFFFFF",
                "#DCE8FA",
                "#9A6700")
        };

        SetBrush("AppBackgroundBrush", palette.AppBackground);
        SetBrush("SurfaceBrush", palette.Surface);
        SetBrush("SurfaceAltBrush", palette.SurfaceAlt);
        SetBrush("BorderBrush", palette.Border);
        SetBrush("TextBrush", palette.Text);
        SetBrush("MutedTextBrush", palette.MutedText);
        SetBrush("AccentBrush", palette.Accent);
        SetBrush("AccentForegroundBrush", palette.AccentForeground);
        SetBrush("SelectionBrush", palette.Selection);
        SetBrush("DirtyBrush", palette.Dirty);

        CurrentTheme = normalized;

        if (!persist)
            return;

        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(ThemePath, normalized);
        }
        catch
        {
        }
    }

    private static void SetBrush(string key, string color)
    {
        if (Application.Current == null)
            return;

        Application.Current.Resources[key] = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(color));
    }

    private static bool SystemUsesDarkApps()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);

            return Convert.ToInt32(value) == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed record Palette(
        string AppBackground,
        string Surface,
        string SurfaceAlt,
        string Border,
        string Text,
        string MutedText,
        string Accent,
        string AccentForeground,
        string Selection,
        string Dirty);
}

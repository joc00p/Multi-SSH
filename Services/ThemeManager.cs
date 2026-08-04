using System.Windows;
using System.Windows.Media;

namespace MultiSSH.Services;

/// <summary>
/// Runtime light/dark theming for the app chrome. Each colour lives as a brush in
/// Application.Current.Resources under the keys below; XAML references them with
/// DynamicResource and code-behind with SetResourceReference, so flipping the theme
/// updates the whole UI live. The terminal content palette (Campbell, etc.) is a
/// separate, per-session concern and is intentionally left untouched.
/// Dark is the original look, value-for-value, so it is a visual no-op.
/// </summary>
public static class ThemeManager
{
    // Resource keys — shared by the XAML (DynamicResource) and code-behind (SetResourceReference).
    public const string WindowBg     = "Chrome.WindowBg";
    public const string ToolbarBg    = "Chrome.ToolbarBg";
    public const string SidebarBg    = "Chrome.SidebarBg";
    public const string ContentBg    = "Chrome.ContentBg";
    public const string ButtonBg     = "Chrome.ButtonBg";
    public const string ButtonBorder = "Chrome.ButtonBorder";
    public const string Border       = "Chrome.Border";
    public const string Text         = "Chrome.Text";
    public const string MutedText    = "Chrome.MutedText";
    public const string HeaderBg     = "Chrome.HeaderBg";
    public const string Accent       = "Chrome.Accent";
    public const string SelectionBg  = "Chrome.SelectionBg";

    public static string Current { get; private set; } = "Dark";

    // key -> (dark, light)
    private static readonly Dictionary<string, (string dark, string light)> Palette = new()
    {
        [WindowBg]     = ("#1E1E24", "#F4F4F6"),
        [ToolbarBg]    = ("#25252B", "#E6E6EA"),
        [SidebarBg]    = ("#25252B", "#ECECEF"),
        [ContentBg]    = ("#12121A", "#FBFBFD"),
        [ButtonBg]     = ("#2D2D36", "#FFFFFF"),
        [ButtonBorder] = ("#3A3A44", "#C4C4CC"),
        [Border]       = ("#3A3A44", "#C8C8CE"),
        [Text]         = ("#FFFFFF", "#1E1E1E"),
        [MutedText]    = ("#AAAAAA", "#5E5E66"),
        [HeaderBg]     = ("#2A2A30", "#DEDEE4"),
        [Accent]       = ("#3B78FF", "#2A6BE0"),
        [SelectionBg]  = ("#403B78FF", "#332A6BE0"),
    };

    /// <summary>Populate the resource dictionary for the given theme ("Dark" or "Light").</summary>
    public static void Apply(string? theme)
    {
        bool light = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        Current = light ? "Light" : "Dark";

        var res = Application.Current.Resources;
        foreach (var (key, pair) in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? pair.light : pair.dark));
            brush.Freeze();
            res[key] = brush;
        }
    }

    /// <summary>Flip between Dark and Light; returns the new theme name.</summary>
    public static string Toggle()
    {
        Apply(Current == "Dark" ? "Light" : "Dark");
        return Current;
    }
}

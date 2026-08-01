using System.Windows.Media;

namespace MultiSSH.Terminal;

/// <summary>
/// A 16-colour ANSI palette plus default fg/bg and cursor colour.
/// Index 0-7 are the normal colours, 8-15 the bright variants.
/// </summary>
public class ColorScheme
{
    public required string Name { get; init; }
    public required Color Background { get; init; }
    public required Color Foreground { get; init; }
    public required Color Cursor { get; init; }
    public required Color[] Palette { get; init; } // 16 entries

    public static ColorScheme Get(string name) =>
        All.TryGetValue(name, out var s) ? s : Campbell;

    private static Color C(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromRgb(
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    public static readonly ColorScheme Campbell = new()
    {
        Name = "Campbell",
        Background = C("0C0C0C"),
        Foreground = C("CCCCCC"),
        Cursor = C("FFFFFF"),
        Palette = new[]
        {
            C("0C0C0C"), C("C50F1F"), C("13A10E"), C("C19C00"),
            C("0037DA"), C("881798"), C("3A96DD"), C("CCCCCC"),
            C("767676"), C("E74856"), C("16C60C"), C("F9F1A5"),
            C("3B78FF"), C("B4009E"), C("61D6D6"), C("F2F2F2"),
        }
    };

    public static readonly ColorScheme PuttyDefault = new()
    {
        Name = "PuTTY",
        Background = C("000000"),
        Foreground = C("BBBBBB"),
        Cursor = C("00FF00"),
        Palette = new[]
        {
            C("000000"), C("BB0000"), C("00BB00"), C("BBBB00"),
            C("0000BB"), C("BB00BB"), C("00BBBB"), C("BBBBBB"),
            C("555555"), C("FF5555"), C("55FF55"), C("FFFF55"),
            C("5555FF"), C("FF55FF"), C("55FFFF"), C("FFFFFF"),
        }
    };

    public static readonly ColorScheme SolarizedDark = new()
    {
        Name = "Solarized Dark",
        Background = C("002B36"),
        Foreground = C("839496"),
        Cursor = C("93A1A1"),
        Palette = new[]
        {
            C("073642"), C("DC322F"), C("859900"), C("B58900"),
            C("268BD2"), C("D33682"), C("2AA198"), C("EEE8D5"),
            C("002B36"), C("CB4B16"), C("586E75"), C("657B83"),
            C("839496"), C("6C71C4"), C("93A1A1"), C("FDF6E3"),
        }
    };

    public static readonly ColorScheme Dracula = new()
    {
        Name = "Dracula",
        Background = C("282A36"),
        Foreground = C("F8F8F2"),
        Cursor = C("F8F8F2"),
        Palette = new[]
        {
            C("21222C"), C("FF5555"), C("50FA7B"), C("F1FA8C"),
            C("BD93F9"), C("FF79C6"), C("8BE9FD"), C("F8F8F2"),
            C("6272A4"), C("FF6E6E"), C("69FF94"), C("FFFFA5"),
            C("D6ACFF"), C("FF92DF"), C("A4FFFF"), C("FFFFFF"),
        }
    };

    public static readonly IReadOnlyDictionary<string, ColorScheme> All =
        new Dictionary<string, ColorScheme>
        {
            [Campbell.Name] = Campbell,
            [PuttyDefault.Name] = PuttyDefault,
            [SolarizedDark.Name] = SolarizedDark,
            [Dracula.Name] = Dracula,
        };
}

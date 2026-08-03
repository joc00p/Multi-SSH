using System.IO;
using System.Text.Json;
using MultiSSH.Models;

namespace MultiSSH.Services;

/// <summary>
/// Application-wide preferences (as opposed to per-session settings), stored in
/// %AppData%\Multi-SSH\settings.json. Currently holds the defaults applied to
/// each new session — most importantly the default terminal font.
/// </summary>
public class AppSettings
{
    public string DefaultFontFamily { get; set; } = "Consolas";
    public double DefaultFontSize { get; set; } = 14;
    public string DefaultColorScheme { get; set; } = "Campbell";

    /// <summary>User-configured hot keys (key → command).</summary>
    public List<HotKey> HotKeys { get; set; } = new();

    /// <summary>Session-manager folder paths (e.g. "Client A", "Client A/Prod"),
    /// including empty folders so structure persists even with no connections in it.</summary>
    public List<string> SessionFolders { get; set; } = new();

    /// <summary>Which edge of the terminal area the session manager is docked to:
    /// "Left", "Right" or "Bottom".</summary>
    public string SessionManagerDock { get; set; } = "Left";

    /// <summary>True when the session manager is minimized to its edge tab.</summary>
    public bool SessionManagerCollapsed { get; set; }

    /// <summary>Panel size when docked to a side (Left/Right).</summary>
    public double SessionManagerWidth { get; set; } = 250;

    /// <summary>Panel size when docked to the bottom.</summary>
    public double SessionManagerHeight { get; set; } = 200;

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Multi-SSH");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static AppSettings? _current;
    public static AppSettings Current => _current ??= Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            _current = this;
        }
        catch { /* ignore write errors */ }
    }
}

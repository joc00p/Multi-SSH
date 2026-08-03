using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using MultiSSH.Models;

namespace MultiSSH.Views;

/// <summary>Base for session-manager tree nodes (folders and connections).</summary>
public abstract class TreeNodeVm : INotifyPropertyChanged
{
    private string _name = "";
    public string Name { get => _name; set { _name = value; OnChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>A folder that can hold sub-folders and connections.</summary>
public class FolderVm : TreeNodeVm
{
    public string Path { get; set; } = "";
    public ObservableCollection<TreeNodeVm> Children { get; } = new();

    private bool _expanded = true;
    public bool IsExpanded { get => _expanded; set { _expanded = value; OnChanged(); } }
}

/// <summary>A saved connection (leaf node).</summary>
public class ConnectionVm : TreeNodeVm
{
    public SessionConfig Config { get; }
    public ConnectionVm(SessionConfig c) { Config = c; Name = c.Display; }

    /// <summary>Colored ball fill for the connection type (blue = PowerShell, etc.).</summary>
    public Brush KindBrush => new SolidColorBrush(SessionView.KindColor(Config.Kind));

    /// <summary>Tooltip naming the connection type.</summary>
    public string KindTip => "Type: " + SessionConfig.KindName(Config.Kind);
}

/// <summary>Builds the folder/connection tree from the flat connection list plus
/// the folder registry. Fully tolerant of legacy data (empty FolderPath = root).</summary>
public static class SessionTree
{
    public static string ParentOf(string path)
    {
        int i = path.LastIndexOf('/');
        return i < 0 ? "" : path.Substring(0, i);
    }

    public static string NameOf(string path)
    {
        int i = path.LastIndexOf('/');
        return i < 0 ? path : path.Substring(i + 1);
    }

    /// <summary>All ancestor paths of a path, including itself (e.g. "a/b/c" -> a, a/b, a/b/c).</summary>
    private static IEnumerable<string> AncestorsAndSelf(string path)
    {
        if (string.IsNullOrEmpty(path)) yield break;
        var parts = path.Split('/');
        var acc = "";
        foreach (var part in parts)
        {
            acc = acc.Length == 0 ? part : acc + "/" + part;
            yield return acc;
        }
    }

    public static ObservableCollection<TreeNodeVm> Build(
        IEnumerable<SessionConfig> connections,
        IEnumerable<string> folderPaths,
        ISet<string> expanded)
    {
        var conns = connections.ToList();

        // Collect every folder path (explicit + implied by connections), with ancestors.
        var allFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in folderPaths)
            foreach (var a in AncestorsAndSelf(f)) allFolders.Add(a);
        foreach (var c in conns)
            foreach (var a in AncestorsAndSelf(c.FolderPath ?? "")) allFolders.Add(a);

        // Create a FolderVm per path.
        var map = new Dictionary<string, FolderVm>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in allFolders)
            map[path] = new FolderVm
            {
                Path = path,
                Name = NameOf(path),
                IsExpanded = expanded.Count == 0 || expanded.Contains(path),
            };

        var root = new ObservableCollection<TreeNodeVm>();

        // Link folders to parents (deepest last so parents exist first isn't required — we look up map).
        foreach (var path in allFolders.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var folder = map[path];
            var parent = ParentOf(path);
            if (parent.Length == 0) root.Add(folder);
            else if (map.TryGetValue(parent, out var pf)) pf.Children.Add(folder);
            else root.Add(folder);
        }

        // Attach connections.
        foreach (var c in conns.OrderBy(c => c.Display, StringComparer.OrdinalIgnoreCase))
        {
            var node = new ConnectionVm(c);
            var fp = c.FolderPath ?? "";
            if (fp.Length > 0 && map.TryGetValue(fp, out var folder)) folder.Children.Add(node);
            else root.Add(node);
        }

        return root;
    }
}

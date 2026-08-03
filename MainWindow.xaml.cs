using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MultiSSH.Models;
using MultiSSH.Services;
using MultiSSH.Views;

namespace MultiSSH;

public partial class MainWindow : Window
{
    private enum ViewMode { Tabs, Tiles }

    private readonly List<SessionConfig> _saved = new();
    private readonly List<SessionPane> _panes = new();
    private ViewMode _mode = ViewMode.Tabs;
    private SessionPane? _active;
    private SessionPane? _maximized;

    // Session-manager tree state.
    private ObservableCollection<TreeNodeVm> _rootNodes = new();
    private TreeNodeVm? _selectedNode;
    private readonly HashSet<string> _expandedFolders = new(StringComparer.OrdinalIgnoreCase);
    private Point _dragStart;
    private ConnectionVm? _dragNode;

    // Unsubscribe actions for the current tab strip's per-session event handlers.
    // Flushed on every RebuildContent so tab headers don't leak handlers/visuals.
    private readonly List<Action> _tabHeaderCleanup = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = AppTitle;
        LoadSaved();
        ApplyDockLayout();
        PreviewKeyDown += Window_PreviewKeyDown;
        Closing += OnClosing;
    }

    // -------------------- keep typing in the terminal --------------------

    /// <summary>
    /// Typing anywhere in the window goes to the active terminal, unless the
    /// user is in a text-entry control (the top "Send to all" bar). Clicking the
    /// sidebar tree or a toolbar button therefore never swallows keystrokes.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || _active == null) return;

        var focused = Keyboard.FocusedElement as DependencyObject;
        if (IsTextEntry(focused) || IsInTerminal(focused)) return;

        // Tab still moves focus normally; everything else belongs to the shell.
        if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        _active.Session.FocusTerminal();

        // Re-raise the key at the terminal. Printable characters arrive
        // separately as TextInput, which now lands on the terminal too, so
        // this only re-delivers the control keys the terminal maps itself.
        if (Keyboard.FocusedElement is IInputElement term && !ReferenceEquals(term, focused))
        {
            e.Handled = true;
            term.RaiseEvent(new KeyEventArgs(e.KeyboardDevice, e.InputSource, e.Timestamp, e.Key)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            });
        }
    }

    private static bool IsTextEntry(DependencyObject? o)
    {
        while (o != null)
        {
            if (o is TextBoxBase or ComboBox or PasswordBox) return true;
            o = SafeParent(o);
        }
        return false;
    }

    private static bool IsInTerminal(DependencyObject? o)
    {
        while (o != null)
        {
            if (o is Terminal.TerminalControl) return true;
            o = SafeParent(o);
        }
        return false;
    }

    /// <summary>Window title with the running version, e.g. "Multi-SSH v1.0.5".</summary>
    private static string AppTitle
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "Multi-SSH" : $"Multi-SSH v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    // -------------------- dockable session manager --------------------

    private enum DockSide { Left, Right, Bottom }

    private DockSide _dock = ParseDock(AppSettings.Current.SessionManagerDock);
    private bool _sidebarCollapsed = AppSettings.Current.SessionManagerCollapsed;

    private static DockSide ParseDock(string? s) => s switch
    {
        "Right" => DockSide.Right,
        "Bottom" => DockSide.Bottom,
        _ => DockSide.Left,
    };

    private void DockLeft_Click(object sender, RoutedEventArgs e) => SetDock(DockSide.Left);
    private void DockRight_Click(object sender, RoutedEventArgs e) => SetDock(DockSide.Right);
    private void DockBottom_Click(object sender, RoutedEventArgs e) => SetDock(DockSide.Bottom);

    private void SetDock(DockSide side)
    {
        _dock = side;
        AppSettings.Current.SessionManagerDock = side.ToString();
        AppSettings.Current.Save();
        ApplyDockLayout();
    }

    private void MinimizeSidebar_Click(object sender, RoutedEventArgs e) => SetSidebarCollapsed(true);

    private void SidebarTab_Click(object sender, MouseButtonEventArgs e) => SetSidebarCollapsed(false);

    private void SetSidebarCollapsed(bool collapsed)
    {
        _sidebarCollapsed = collapsed;
        AppSettings.Current.SessionManagerCollapsed = collapsed;
        AppSettings.Current.Save();
        ApplyDockLayout();
    }

    /// <summary>
    /// Persist the panel size after the user drags the splitter. The dragged
    /// dimension lives in the grid definition, not on the Border.
    /// </summary>
    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var s = AppSettings.Current;
        switch (_dock)
        {
            case DockSide.Left: s.SessionManagerWidth = LeftCol.Width.Value; break;
            case DockSide.Right: s.SessionManagerWidth = RightCol.Width.Value; break;
            case DockSide.Bottom: s.SessionManagerHeight = BottomRow.Height.Value; break;
        }
        s.Save();
    }

    /// <summary>
    /// Positions the session manager (or its minimized tab) and the splitter on
    /// the chosen edge, and collapses the grid tracks belonging to the others.
    /// </summary>
    private void ApplyDockLayout()
    {
        var s = AppSettings.Current;
        double width = s.SessionManagerWidth > 60 ? s.SessionManagerWidth : 250;
        double height = s.SessionManagerHeight > 60 ? s.SessionManagerHeight : 200;

        // Reset every track; only the docked edge gets a size below.
        LeftCol.Width = new GridLength(0);
        LeftSplitCol.Width = new GridLength(0);
        RightCol.Width = new GridLength(0);
        RightSplitCol.Width = new GridLength(0);
        BottomRow.Height = new GridLength(0);
        BottomSplitRow.Height = new GridLength(0);

        var panel = _sidebarCollapsed ? (FrameworkElement)SidebarTab : SidebarBorder;
        SidebarBorder.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarTab.Visibility = _sidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;

        // The minimized tab reads vertically on the side edges, horizontally at the bottom.
        SidebarTabText.LayoutTransform = _dock == DockSide.Bottom
            ? Transform.Identity
            : new RotateTransform(_dock == DockSide.Left ? 90 : -90);

        int panelCol, panelRow, panelColSpan = 1;
        switch (_dock)
        {
            case DockSide.Left:
                panelCol = 0; panelRow = 0;
                LeftCol.Width = _sidebarCollapsed ? GridLength.Auto : new GridLength(width);
                if (!_sidebarCollapsed) LeftSplitCol.Width = new GridLength(5);
                SetSplitter(1, 0, 1, GridResizeDirection.Columns);
                break;
            case DockSide.Right:
                panelCol = 4; panelRow = 0;
                RightCol.Width = _sidebarCollapsed ? GridLength.Auto : new GridLength(width);
                if (!_sidebarCollapsed) RightSplitCol.Width = new GridLength(5);
                SetSplitter(3, 0, 1, GridResizeDirection.Columns);
                break;
            default:
                panelCol = 0; panelRow = 2; panelColSpan = 5;
                BottomRow.Height = _sidebarCollapsed ? GridLength.Auto : new GridLength(height);
                if (!_sidebarCollapsed) BottomSplitRow.Height = new GridLength(5);
                SetSplitter(0, 1, 5, GridResizeDirection.Rows);
                break;
        }

        Grid.SetColumn(panel, panelCol);
        Grid.SetRow(panel, panelRow);
        Grid.SetColumnSpan(panel, panelColSpan);
        HighlightDockButton();
    }

    private void SetSplitter(int col, int row, int colSpan, GridResizeDirection dir)
    {
        Grid.SetColumn(SidebarSplitter, col);
        Grid.SetRow(SidebarSplitter, row);
        Grid.SetColumnSpan(SidebarSplitter, colSpan);
        SidebarSplitter.ResizeDirection = dir;
        SidebarSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
        if (dir == GridResizeDirection.Columns)
        {
            SidebarSplitter.Width = 5;
            SidebarSplitter.Height = double.NaN;
            SidebarSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            SidebarSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            SidebarSplitter.Cursor = Cursors.SizeWE;
        }
        else
        {
            SidebarSplitter.Height = 5;
            SidebarSplitter.Width = double.NaN;
            SidebarSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            SidebarSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            SidebarSplitter.Cursor = Cursors.SizeNS;
        }
    }

    private void HighlightDockButton()
    {
        var on = new SolidColorBrush(Color.FromRgb(0x3B, 0x78, 0xFF));
        var off = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44));
        DockLeftBtn.BorderBrush = _dock == DockSide.Left ? on : off;
        DockRightBtn.BorderBrush = _dock == DockSide.Right ? on : off;
        DockBottomBtn.BorderBrush = _dock == DockSide.Bottom ? on : off;
    }

    // -------------------- session manager (folder tree) --------------------

    private void LoadSaved()
    {
        _saved.Clear();
        _saved.AddRange(SessionStore.Load());
        RefreshTree();
    }

    private void RefreshTree()
    {
        CaptureExpansion();
        _rootNodes = SessionTree.Build(_saved, AppSettings.Current.SessionFolders, _expandedFolders);
        SessionTreeView.ItemsSource = _rootNodes;
    }

    private void CaptureExpansion()
    {
        void Walk(IEnumerable<TreeNodeVm> nodes)
        {
            foreach (var n in nodes)
                if (n is FolderVm f)
                {
                    if (f.IsExpanded) _expandedFolders.Add(f.Path);
                    else _expandedFolders.Remove(f.Path);
                    Walk(f.Children);
                }
        }
        Walk(_rootNodes);
    }

    private void PersistSaved() => SessionStore.Save(_saved);

    private SessionConfig? SelectedSaved() => (_selectedNode as ConnectionVm)?.Config;

    private string SelectedFolderPath() => _selectedNode switch
    {
        FolderVm f => f.Path,
        ConnectionVm c => c.Config.FolderPath ?? "",
        _ => "",
    };

    private void RemoveSaved(SessionConfig cfg)
    {
        if (MessageBox.Show(this, $"Remove saved connection \"{cfg.Display}\"?", "Multi-SSH",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _saved.Remove(cfg);
        PersistSaved();
        RefreshTree();
    }

    // ---- tree interaction ----

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _selectedNode = e.NewValue as TreeNodeVm;

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (NodeFromSource(e.OriginalSource as DependencyObject) is ConnectionVm c)
        {
            OpenSession(c.Config.Clone());
            e.Handled = true;
        }
    }

    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ContainerFromSource(e.OriginalSource as DependencyObject);
        if (item != null) { item.IsSelected = true; item.Focus(); }
    }

    private void RemoveConn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is ConnectionVm c)
        {
            e.Handled = true;
            RemoveSaved(c.Config);
        }
    }

    // ---- context menu: connections ----

    private void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        var cfg = SelectedSaved();
        if (cfg != null) OpenSession(cfg.Clone());
    }

    private void MenuEdit_Click(object sender, RoutedEventArgs e)
    {
        var cfg = SelectedSaved();
        if (cfg == null) return;
        var dlg = new ConfigDialog(cfg) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            int idx = _saved.IndexOf(cfg);
            if (idx >= 0) _saved[idx] = dlg.Result;
            PersistSaved();
            RefreshTree();
        }
    }

    private void MenuDuplicate_Click(object sender, RoutedEventArgs e)
    {
        var cfg = SelectedSaved();
        if (cfg == null) return;
        var copy = cfg.Clone();
        copy.Name = (string.IsNullOrWhiteSpace(cfg.Name) ? cfg.Display : cfg.Name) + " (copy)";
        _saved.Add(copy);
        PersistSaved();
        RefreshTree();
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        var cfg = SelectedSaved();
        if (cfg != null) RemoveSaved(cfg);
    }

    // ---- context menu / toolbar: folders ----

    private void NewFolder_Click(object sender, RoutedEventArgs e) => CreateFolder("");

    private void NewSubfolder_Click(object sender, RoutedEventArgs e) => CreateFolder(SelectedFolderPath());

    private void CreateFolder(string parentPath)
    {
        var name = InputDialog.Ask(this, "New Folder", "Folder name:");
        if (name == null) return;
        name = name.Replace('/', '-').Trim();
        if (name.Length == 0) return;
        var path = parentPath.Length == 0 ? name : parentPath + "/" + name;

        var folders = AppSettings.Current.SessionFolders;
        if (!folders.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)))
            folders.Add(path);
        if (parentPath.Length > 0) _expandedFolders.Add(parentPath);
        _expandedFolders.Add(path);
        AppSettings.Current.Save();
        RefreshTree();
    }

    private void RenameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is not FolderVm f)
        {
            MessageBox.Show(this, "Select a folder to rename.", "Multi-SSH",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var newName = InputDialog.Ask(this, "Rename Folder", "New name:", f.Name);
        if (newName == null) return;
        newName = newName.Replace('/', '-').Trim();
        if (newName.Length == 0) return;

        var oldPath = f.Path;
        var parent = SessionTree.ParentOf(oldPath);
        var newPath = parent.Length == 0 ? newName : parent + "/" + newName;
        if (string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase)) return;

        var folders = AppSettings.Current.SessionFolders;
        for (int i = 0; i < folders.Count; i++)
            if (PathEqualsOrUnder(folders[i], oldPath))
                folders[i] = ReplacePrefix(folders[i], oldPath, newPath);
        if (!folders.Any(x => string.Equals(x, newPath, StringComparison.OrdinalIgnoreCase)))
            folders.Add(newPath);

        foreach (var c in _saved)
            if (PathEqualsOrUnder(c.FolderPath ?? "", oldPath))
                c.FolderPath = ReplacePrefix(c.FolderPath ?? "", oldPath, newPath);

        _expandedFolders.Add(newPath);
        AppSettings.Current.Save();
        PersistSaved();
        RefreshTree();
    }

    private void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is not FolderVm f)
        {
            MessageBox.Show(this, "Select a folder to delete.", "Multi-SSH",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var path = f.Path;
        var parent = SessionTree.ParentOf(path);
        int count = _saved.Count(c => PathEqualsOrUnder(c.FolderPath ?? "", path));
        string where = parent.Length == 0 ? "the top level" : $"\"{parent}\"";
        string msg = count > 0
            ? $"Delete folder \"{path}\"?\n\n{count} connection(s) inside will move to {where}. No connections are deleted."
            : $"Delete empty folder \"{path}\"?";
        if (MessageBox.Show(this, msg, "Multi-SSH", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        foreach (var c in _saved)
            if (PathEqualsOrUnder(c.FolderPath ?? "", path))
                c.FolderPath = parent;

        AppSettings.Current.SessionFolders.RemoveAll(fp => PathEqualsOrUnder(fp, path));
        _expandedFolders.RemoveWhere(p => PathEqualsOrUnder(p, path));
        AppSettings.Current.Save();
        PersistSaved();
        RefreshTree();
    }

    // ---- drag & drop (move a connection into a folder) ----

    private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragNode = FromButton(e.OriginalSource as DependencyObject)
            ? null
            : NodeFromSource(e.OriginalSource as DependencyObject) as ConnectionVm;
    }

    private void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragNode == null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var data = new DataObject(typeof(ConnectionVm), _dragNode);
        DragDrop.DoDragDrop(SessionTreeView, data, DragDropEffects.Move);
        _dragNode = null;
    }

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ConnectionVm)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Tree_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ConnectionVm)) is not ConnectionVm dragged) return;
        var target = NodeFromSource(e.OriginalSource as DependencyObject);
        string newFolder = target switch
        {
            FolderVm f => f.Path,
            ConnectionVm c => c.Config.FolderPath ?? "",
            _ => "",
        };
        if (!string.Equals(dragged.Config.FolderPath ?? "", newFolder, StringComparison.OrdinalIgnoreCase))
        {
            dragged.Config.FolderPath = newFolder;
            PersistSaved();
            RefreshTree();
        }
    }

    // ---- helpers ----

    private static bool PathEqualsOrUnder(string p, string basePath) =>
        string.Equals(p, basePath, StringComparison.OrdinalIgnoreCase) ||
        p.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase);

    private static string ReplacePrefix(string p, string oldBase, string newBase) =>
        string.Equals(p, oldBase, StringComparison.OrdinalIgnoreCase) ? newBase : newBase + p.Substring(oldBase.Length);

    private static DependencyObject? SafeParent(DependencyObject o) =>
        (o is Visual || o is System.Windows.Media.Media3D.Visual3D)
            ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o);

    private static TreeNodeVm? NodeFromSource(DependencyObject? src)
        => ContainerFromSource(src)?.DataContext as TreeNodeVm;

    private static TreeViewItem? ContainerFromSource(DependencyObject? src)
    {
        while (src != null && src is not TreeViewItem) src = SafeParent(src);
        return src as TreeViewItem;
    }

    private static bool FromButton(DependencyObject? src)
    {
        while (src != null && src is not TreeViewItem)
        {
            if (src is Button) return true;
            src = SafeParent(src);
        }
        return false;
    }

    // -------------------- toolbar --------------------

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var cfg = SelectedSaved();
        if (cfg == null)
        {
            MessageBox.Show(this, "Select a saved session in the sidebar to open.", "Multi-SSH",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenSession(cfg.Clone());
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSaved() == null)
        {
            MessageBox.Show(this, "Select a saved session in the sidebar to edit.", "Multi-SSH",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        MenuEdit_Click(sender, e);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ConfigDialog(null, SelectedFolderPath()) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            var cfg = dlg.Result;
            if (!string.IsNullOrWhiteSpace(cfg.Host))
            {
                _saved.Add(cfg.Clone());
                PersistSaved();
                RefreshTree();
            }
            // Save only — do not open a terminal. Double-click the connection
            // in the tree to connect.
        }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_active != null) OpenSession(_active.Session.Config.Clone());
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_active != null) _ = _active.Session.ReconnectAsync();
    }

    // -------------------- broadcast to all sessions --------------------

    private void Broadcast_Click(object sender, RoutedEventArgs e) => BroadcastCurrent();

    private void Broadcast_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            BroadcastCurrent();
            e.Handled = true;
        }
    }

    private void BroadcastCurrent()
    {
        string text = (BroadcastCombo.Text ?? "").TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(text)) return;
        if (_panes.Count == 0)
        {
            MessageBox.Show(this, "No open sessions to send to.", "Multi-SSH",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Send the command followed by Enter to every open session.
        foreach (var pane in _panes)
            pane.Session.SendText(text + "\r");

        // Keep focus/selection handy for repeated sends.
        BroadcastCombo.SelectedIndex = -1;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog { Owner = this };
        dlg.ShowDialog();
    }

    private void HotKeys_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new HotKeysDialog { Owner = this };
        dlg.ShowDialog();
    }

    private void Tabs_Click(object sender, RoutedEventArgs e) => SetMode(ViewMode.Tabs);
    private void Tiles_Click(object sender, RoutedEventArgs e) => SetMode(ViewMode.Tiles);

    private void SetMode(ViewMode mode)
    {
        _mode = mode;
        _maximized = null; // switching layout exits a maximized pane
        RebuildContent();
    }

    // -------------------- session lifecycle --------------------

    private void OpenSession(SessionConfig cfg)
    {
        var view = new SessionView(cfg);
        var pane = new SessionPane(view);
        pane.CloseRequested += ClosePane;
        pane.ReconnectRequested += p => _ = p.Session.ReconnectAsync();
        pane.Activated += SetActive;
        pane.MaximizeToggleRequested += ToggleMaximize;
        // When the remote shell exits, destroy its window automatically.
        view.ShellExited += _ => ClosePane(pane);

        _panes.Add(pane);
        _active = pane;
        RebuildContent();
    }

    private void ClosePane(SessionPane pane)
    {
        if (!_panes.Remove(pane)) return; // already closed (idempotent)
        pane.Session.Close();
        if (_active == pane) _active = _panes.LastOrDefault();
        if (_maximized == pane) _maximized = null;
        RebuildContent();
    }

    private void ToggleMaximize(SessionPane pane)
    {
        _maximized = ReferenceEquals(_maximized, pane) ? null : pane;
        RebuildContent();
    }

    private void SetActive(SessionPane pane)
    {
        _active = pane;
        foreach (var p in _panes) p.Active = ReferenceEquals(p, pane);
        pane.Session.FocusTerminal();
    }

    // -------------------- content layout --------------------

    private void RebuildContent()
    {
        // Remove the previous tab strip's per-session handlers before rebuilding, so
        // a pane's StateChanged/TitleChanged don't accumulate a handler (and pin a
        // discarded TabControl) on every open/close/mode-switch/maximize.
        foreach (var cleanup in _tabHeaderCleanup) cleanup();
        _tabHeaderCleanup.Clear();

        // Detach everything first so no pane has two parents.
        foreach (var p in _panes) p.DetachFromParent();
        ContentHost.Children.Clear();

        EmptyHint.Visibility = _panes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_panes.Count == 0)
        {
            ContentHost.Children.Add(EmptyHint);
            HighlightModeButton();
            return;
        }

        if (_maximized != null && !_panes.Contains(_maximized))
            _maximized = null;

        if (_maximized != null)
        {
            // One session fills the tiled area; double-click again to restore.
            _maximized.HeaderVisible = true;
            ContentHost.Children.Add(_maximized);
            HighlightModeButton();
            SetActive(_maximized);
            return;
        }

        if (_mode == ViewMode.Tabs)
            BuildTabs();
        else
            BuildTiles();

        HighlightModeButton();

        if (_active == null) _active = _panes.LastOrDefault();
        if (_active != null) SetActive(_active);
    }

    private void BuildTabs()
    {
        var tabs = new TabControl { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        foreach (var pane in _panes)
        {
            pane.HeaderVisible = false;
            pane.Active = false;
            var item = new TabItem { Content = pane, Tag = pane, Header = BuildTabHeader(pane) };
            tabs.Items.Add(item);
            if (ReferenceEquals(pane, _active)) item.IsSelected = true;
        }
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is TabItem { Tag: SessionPane p })
                SetActive(p);
        };
        ContentHost.Children.Add(tabs);
    }

    private FrameworkElement BuildTabHeader(SessionPane pane)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 9, Height = 9,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(SessionView.DotColor(pane.Session.State)),
            ToolTip = pane.Session.StatusText,
        };
        panel.Children.Add(dot);

        var title = new TextBlock
        {
            Text = pane.Title,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 160,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // Named handlers so they can be removed when this tab strip is torn down.
        // (RebuildContent flushes _tabHeaderCleanup before building the next strip.)
        void OnState(SessionView _)
        {
            dot.Fill = new SolidColorBrush(SessionView.DotColor(pane.Session.State));
            dot.ToolTip = pane.Session.StatusText;
        }
        void OnTitle(SessionView _) => title.Text = pane.Title;
        pane.Session.StateChanged += OnState;
        pane.Session.TitleChanged += OnTitle;
        _tabHeaderCleanup.Add(() =>
        {
            pane.Session.StateChanged -= OnState;
            pane.Session.TitleChanged -= OnTitle;
        });

        var close = new Button
        {
            Content = "✕",
            Width = 18, Height = 18,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        close.Click += (_, _) => ClosePane(pane);
        panel.Children.Add(title);
        panel.Children.Add(close);
        return panel;
    }

    private void BuildTiles()
    {
        var grid = new UniformGrid { Margin = new Thickness(2) };
        // UniformGrid auto-computes rows/cols; every tile resizes to fit as we add more.
        int n = _panes.Count;
        grid.Columns = (int)Math.Ceiling(Math.Sqrt(n));
        grid.Rows = (int)Math.Ceiling((double)n / grid.Columns);

        foreach (var pane in _panes)
        {
            pane.HeaderVisible = true;
            grid.Children.Add(pane);
        }
        ContentHost.Children.Add(grid);
    }

    private void HighlightModeButton()
    {
        TabsBtn.BorderBrush = new SolidColorBrush(_mode == ViewMode.Tabs
            ? Color.FromRgb(0x3B, 0x78, 0xFF) : Color.FromRgb(0x3A, 0x3A, 0x44));
        TilesBtn.BorderBrush = new SolidColorBrush(_mode == ViewMode.Tiles
            ? Color.FromRgb(0x3B, 0x78, 0xFF) : Color.FromRgb(0x3A, 0x3A, 0x44));
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_panes.Count > 0)
        {
            var result = MessageBox.Show(
                $"You have {_panes.Count} open session{(_panes.Count == 1 ? "" : "s")}. Close them all and quit?",
                "Quit Multi-SSH",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        // Close connections without blocking the UI thread: a wedged socket in
        // Disconnect() must not hang the quit. Teardown is best-effort from here.
        foreach (var p in _panes) p.Session.CloseAsync();
    }
}

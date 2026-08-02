using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    public MainWindow()
    {
        InitializeComponent();
        LoadSaved();
        Closing += OnClosing;
    }

    // -------------------- saved session sidebar --------------------

    private void LoadSaved()
    {
        _saved.Clear();
        _saved.AddRange(SessionStore.Load());
        RefreshSavedList();
    }

    private void RefreshSavedList()
    {
        SavedList.Items.Clear();
        foreach (var s in _saved)
            SavedList.Items.Add(new ListBoxItem { Content = s.Display, Tag = s });
    }

    private void PersistSaved() => SessionStore.Save(_saved);

    private SessionConfig? SelectedSaved()
        => (SavedList.SelectedItem as ListBoxItem)?.Tag as SessionConfig;

    // Right-clicking a row must select it first, so context-menu actions
    // (Open/Edit/Delete) always target the row under the cursor — not whatever
    // happened to be left-click-selected before.
    private void SavedList_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(SavedList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (item != null) item.IsSelected = true;
    }

    private void SavedList_DoubleClick(object sender, RoutedEventArgs e)
    {
        var cfg = SelectedSaved();
        if (cfg != null) OpenSession(cfg.Clone());
    }

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
            RefreshSavedList();
            if (dlg.Result != null) OpenSession(dlg.Result.Clone());
        }
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        var cfg = SelectedSaved();
        if (cfg == null) return;
        if (MessageBox.Show(this, $"Delete saved session \"{cfg.Display}\"?", "Multi-SSH",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _saved.Remove(cfg);
            PersistSaved();
            RefreshSavedList();
        }
    }

    // -------------------- toolbar --------------------

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ConfigDialog { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            var cfg = dlg.Result;
            if (dlg.SaveToSidebar && !string.IsNullOrWhiteSpace(cfg.Host))
            {
                _saved.Add(cfg.Clone());
                PersistSaved();
                RefreshSavedList();
            }
            OpenSession(cfg);
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
        pane.Session.StateChanged += _ =>
        {
            dot.Fill = new SolidColorBrush(SessionView.DotColor(pane.Session.State));
            dot.ToolTip = pane.Session.StatusText;
        };
        panel.Children.Add(dot);

        var title = new TextBlock
        {
            Text = pane.Title,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 160,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        pane.Session.TitleChanged += _ => title.Text = pane.Title;
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
        foreach (var p in _panes) p.Session.Close();
    }
}

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

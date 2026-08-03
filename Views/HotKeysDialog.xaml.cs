using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MultiSSH.Models;
using MultiSSH.Services;

namespace MultiSSH.Views;

public partial class HotKeysDialog : Window
{
    private readonly List<HotKey> _keys;
    private HotKey? _capturing;
    private Button? _captureBtn;

    public HotKeysDialog()
    {
        InitializeComponent();
        _keys = AppSettings.Current.HotKeys.Select(k => k.Clone()).ToList();
        if (_keys.Count == 0) _keys.Add(new HotKey());
        BuildRows();
    }

    private void BuildRows()
    {
        RowsHost.Children.Clear();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var hKey = new TextBlock { Text = "Key", Width = 130, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 6, 0) };
        var hCmd = new TextBlock { Text = "Command", FontWeight = FontWeights.Bold };
        DockPanel.SetDock(hKey, Dock.Left);
        header.Children.Add(hKey);
        header.Children.Add(hCmd);
        RowsHost.Children.Add(header);

        foreach (var hk in _keys)
            RowsHost.Children.Add(BuildRow(hk));
    }

    private DockPanel BuildRow(HotKey hk)
    {
        var keyBtn = new Button
        {
            Content = hk.Display(),
            Width = 130,
            Height = 26,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Click, then press the key (or combo) to assign",
        };
        keyBtn.Click += (s, _) => StartCapture(hk, (Button)s);

        var remove = new Button
        {
            Content = "✕",
            Width = 24,
            Height = 26,
            Margin = new Thickness(6, 0, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)),
            ToolTip = "Remove this hot key",
        };
        remove.Click += (_, _) => { _keys.Remove(hk); BuildRows(); };

        var run = new CheckBox
        {
            Content = "↵",
            IsChecked = hk.SendEnter,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Send Enter after the command so it runs",
        };
        run.Checked += (_, _) => hk.SendEnter = true;
        run.Unchecked += (_, _) => hk.SendEnter = false;

        var cmd = new TextBox
        {
            Text = hk.Command,
            Height = 26,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas"),
        };
        cmd.TextChanged += (s, _) => hk.Command = ((TextBox)s).Text;

        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(keyBtn, Dock.Left);
        DockPanel.SetDock(remove, Dock.Right);
        DockPanel.SetDock(run, Dock.Right);
        row.Children.Add(keyBtn);
        row.Children.Add(remove);
        row.Children.Add(run);
        row.Children.Add(cmd); // fills remaining space
        return row;
    }

    private void StartCapture(HotKey hk, Button btn)
    {
        _capturing = hk;
        _captureBtn = btn;
        btn.Content = "Press a key…";
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_capturing != null && _captureBtn != null)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape)  // cancel capture without binding
            {
                _captureBtn.Content = _capturing.Display();
                _capturing = null; _captureBtn = null;
                e.Handled = true;
                return;
            }

            if (!IsModifierKey(key))
            {
                _capturing.Key = key.ToString();
                _capturing.Modifiers = Keyboard.Modifiers.ToString();
                _captureBtn.Content = _capturing.Display();
                _capturing = null; _captureBtn = null;
            }
            e.Handled = true; // swallow everything (incl. bare modifiers) while capturing
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private static bool IsModifierKey(Key k) => k is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or
        Key.System or Key.None or Key.DeadCharProcessed or Key.ImeProcessed;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        _keys.Add(new HotKey());
        BuildRows();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var valid = _keys
            .Where(k => k.ParsedKey != Key.None && !string.IsNullOrWhiteSpace(k.Command))
            .ToList();
        var s = AppSettings.Current;
        s.HotKeys = valid;
        s.Save();
        DialogResult = true;
    }
}

using System.Windows;
using System.Windows.Controls;

namespace MultiSSH.Views;

/// <summary>Minimal modal text-input prompt (used for folder names).</summary>
public class InputDialog : Window
{
    private readonly TextBox _box;
    public string Value => _box.Text.Trim();

    public InputDialog(string title, string prompt, string initial = "")
    {
        Title = title;
        Width = 380;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.WhiteSmoke;

        var grid = new Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(label, 0);
        grid.Children.Add(label);

        _box = new TextBox { Text = initial, Height = 26, VerticalContentAlignment = VerticalAlignment.Center };
        Grid.SetRow(_box, 1);
        grid.Children.Add(_box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var ok = new Button { Content = "OK", Width = 84, Height = 26, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button { Content = "Cancel", Width = 84, Height = 26, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;
        Loaded += (_, _) => { _box.SelectAll(); _box.Focus(); };
    }

    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var dlg = new InputDialog(title, prompt, initial) { Owner = owner };
        return dlg.ShowDialog() == true && dlg.Value.Length > 0 ? dlg.Value : null;
    }
}

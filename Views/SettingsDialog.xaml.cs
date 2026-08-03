using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MultiSSH.Services;

namespace MultiSSH.Views;

public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
        PopulateFonts();

        var s = AppSettings.Current;
        FontCombo.Text = s.DefaultFontFamily;
        FontSizeBox.Text = s.DefaultFontSize.ToString("0.#");
        SelectScheme(s.DefaultColorScheme);
        RecordFolderBox.Text = s.RecordingsFolder;
        UpdatePreview();
    }

    private void BrowseLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select recordings folder" };
        var current = string.IsNullOrWhiteSpace(RecordFolderBox.Text)
            ? AppSettings.Current.EffectiveRecordingsFolder : RecordFolderBox.Text;
        if (Directory.Exists(current)) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(this) == true)
            RecordFolderBox.Text = dlg.FolderName;
    }

    private void PopulateFonts()
    {
        // Monospaced fonts first (best for a terminal), then everything else.
        var preferred = new[] { "Consolas", "Cascadia Mono", "Cascadia Code", "Lucida Console", "Courier New" };
        foreach (var f in preferred)
            FontCombo.Items.Add(f);
        FontCombo.Items.Add(new Separator());
        foreach (var fam in Fonts.SystemFontFamilies
                     .Select(f => f.Source)
                     .Where(n => !preferred.Contains(n))
                     .OrderBy(n => n))
            FontCombo.Items.Add(fam);
    }

    private void SelectScheme(string name)
    {
        foreach (ComboBoxItem item in SchemeCombo.Items)
            if ((string)item.Content == name) { SchemeCombo.SelectedItem = item; return; }
        SchemeCombo.SelectedIndex = 0;
    }

    private void Font_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (Preview == null) return;
        try
        {
            Preview.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(FontCombo.Text) ? "Consolas" : FontCombo.Text);
            if (double.TryParse(FontSizeBox.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out var size) && size > 0)
                Preview.FontSize = size;
        }
        catch { /* invalid font name while typing */ }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Mutate the existing settings so hot keys, session folders, dock state, etc.
        // are preserved (never replace the whole object).
        var s = AppSettings.Current;
        s.DefaultFontFamily = string.IsNullOrWhiteSpace(FontCombo.Text) ? "Consolas" : FontCombo.Text.Trim();
        s.DefaultFontSize = double.TryParse(FontSizeBox.Text, out var sz) && sz > 0 ? sz : 14;
        s.DefaultColorScheme = (SchemeCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "Campbell";
        s.RecordingsFolder = RecordFolderBox.Text.Trim();
        s.Save();
        DialogResult = true;
    }
}

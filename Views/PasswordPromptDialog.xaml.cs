using System.Windows;

namespace MultiSSH.Views;

/// <summary>
/// A modal, masked prompt used at connect time when no password/passphrase was
/// saved (or a saved one was rejected). The entered value is never persisted —
/// it is handed straight to the connection for this session only.
/// </summary>
public partial class PasswordPromptDialog : Window
{
    public string Password => PassBox.Password;

    public PasswordPromptDialog(string prompt, string target, string? error = null)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        TargetText.Text = target;
        if (!string.IsNullOrEmpty(error))
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) => PassBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

using System.Windows;
using MultiSSH.Services;

namespace MultiSSH;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Populate the theme brushes before any window loads so DynamicResource lookups resolve.
        ThemeManager.Apply(AppSettings.Current.Theme);
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Unexpected error:\n\n" + args.Exception.Message,
                "Multi-SSH", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}

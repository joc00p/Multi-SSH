using System.Windows;

namespace MultiSSH;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Unexpected error:\n\n" + args.Exception.Message,
                "Multi-SSH", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}

using System.Windows;
using System.Windows.Threading;
using DiscForge.Core;

namespace DiscForge.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        LoadedDisc disc;
        try
        {
            var root = DiscRootLocator.Locate(Environment.ProcessPath, e.Args);
            disc = DiscLoader.Load(root);
        }
        catch (Exception exception)
        {
            disc = new LoadedDisc(string.Empty, null, null, null, VerificationPlan.SizeOnly(), [], exception.Message);
        }

        var window = new MainWindow(new MainViewModel(disc));
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "PLAY", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

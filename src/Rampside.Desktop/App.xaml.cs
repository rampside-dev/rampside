using System.Windows;
using System.Windows.Threading;
using Rampside.Desktop.ViewModels;
using Rampside.Hosting;

namespace Rampside.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var options = new RampsideHostOptions
        {
            UseFakeSim = e.Args.Contains("--fake", StringComparer.OrdinalIgnoreCase),
            FakePmdg = e.Args.Contains("--fake-pmdg", StringComparer.OrdinalIgnoreCase),
        };

        MainViewModel viewModel;
        try
        {
            viewModel = new MainViewModel(new RampsideHost(options));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Rampside could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // The window shuts the app down after it is closed and the host has stopped.
        MainWindow = new MainWindow(viewModel);
        MainWindow.Show();
        await viewModel.StartAsync();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the window alive beside the sim; a crash would also drop the ground services.
        MessageBox.Show(e.Exception.ToString(), "Rampside: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using EverLogger.App.ViewModels;

namespace EverLogger.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>
    /// Last-resort guard for unexpected UI exceptions. Crashing would end the process and lose
    /// any log data still queued for writing, so the error is reported in the status bar and the
    /// app keeps running (serial reception and log writing run on their own threads).
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Debug.WriteLine($"Unhandled UI exception: {e.Exception}");

        if (MainWindow?.DataContext is MainViewModel vm)
        {
            vm.StatusBarText = $"Internal error (app still running): {e.Exception.Message}";
        }

        e.Handled = true;
    }
}

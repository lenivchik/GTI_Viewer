using System.Windows;
using System.Windows.Threading;
using FirebirdViewer.Localization;
using FirebirdViewer.Services;

namespace FirebirdViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // The language has to be known before the main window is created, so its
        // labels come up translated from the first frame.
        var saved = new SettingsService().Load().Language;
        Loc.SetLanguage(saved ?? Loc.SystemDefaultCode());

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            Loc.T("App_UnhandledError"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            MessageBox.Show(ex.Message, Loc.T("App_FatalError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

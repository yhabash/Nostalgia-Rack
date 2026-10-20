using System.Windows;
using System.Windows.Threading;

namespace NostalgiaRack;

/// <summary>
/// The application itself. App.xaml says which window opens first (MainWindow) and loads
/// the champagne skin. If something unexpected goes wrong, we show what happened instead
/// of the app silently disappearing (copy the message and send it to Claude).
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "Something went wrong:\n\n" + e.Exception.Message + "\n\n" +
            "Details (press Ctrl+C to copy this whole message):\n" + e.Exception,
            "Nostalgia Rack", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true; // keep running if we can
    }
}

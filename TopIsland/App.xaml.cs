using System.Threading;
using System.Windows;
using TopIsland.Services;

namespace TopIsland;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private TrayIconService? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "Local\\TopIsland.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var monitorService = new MonitorService();
        var window = new MainWindow(monitorService);
        MainWindow = window;
        window.Show();
        _trayIcon = new TrayIconService(window, monitorService);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;

        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}
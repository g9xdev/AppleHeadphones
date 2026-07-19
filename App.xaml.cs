using AirPodsBattery.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AirPodsBattery;

public partial class App : Application
{
    private BatteryMonitor? _monitor;
    private TrayIconService? _tray;
    private FlyoutWindow? _flyout;
    private MainWindow? _dashboard;

    public App()
    {
        InitializeComponent();

        // Tray apps outlive their windows: only quit when we say so, not when
        // the last window closes.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _monitor = new BatteryMonitor(DispatcherQueue.GetForCurrentThread());
        _flyout = new FlyoutWindow(_monitor);
        _tray = new TrayIconService();

        _monitor.Updated += UpdateTrayIcon;
        _tray.StartupChecked = StartupService.IsEnabled();
        _tray.FlyoutRequested += () => _flyout.Toggle();
        _tray.DashboardRequested += ShowDashboard;
        _tray.SyncRequested += () => _flyout.ShowAndSync();
        _tray.StartupToggled += ToggleStartup;
        _tray.ExitRequested += ExitApp;

        _monitor.Start();
        UpdateTrayIcon();
    }

    private void UpdateTrayIcon()
    {
        var r = _monitor!.CurrentReading;
        bool stale = _monitor.IsStale;

        // Headline number: single level for AirPods Max, worst bud otherwise
        // (the bud that dies first is the one you care about).
        int? percent = r switch
        {
            null => null,
            { IsHeadphone: true } => r.LeftBattery ?? r.RightBattery,
            _ => new[] { r.LeftBattery, r.RightBattery }
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .DefaultIfEmpty(-1)
                    .Min() is int m and >= 0 ? m : null,
        };

        bool charging = r is not null && (r.LeftCharging || r.RightCharging);

        string tip = r is null
            ? "AirPods Battery — listening for broadcasts"
            : $"{r.Model} · {(percent is int p ? $"{p}%" : "—")}" +
              (charging ? " · charging" : string.Empty) +
              (stale ? " · stale" : string.Empty);

        _tray!.Update(percent, charging, stale, tip);
    }

    private void ToggleStartup()
    {
        bool enable = !StartupService.IsEnabled();
        StartupService.SetEnabled(enable);
        _tray!.StartupChecked = enable;
    }

    private void ShowDashboard()
    {
        if (_dashboard is null)
        {
            _dashboard = new MainWindow();
            _dashboard.Closed += (_, _) => _dashboard = null;
        }

        _dashboard.Activate();
    }

    private void ExitApp()
    {
        _tray?.Dispose();
        _monitor?.Dispose();
        _dashboard?.Close();
        _flyout?.Close();
        Exit();
    }
}

using System.Runtime.InteropServices;
using AirPodsBattery.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace AirPodsBattery;

/// <summary>
/// Borderless acrylic flyout anchored above the notification area, in the
/// style of the Windows volume/quick-settings popups. Shown by the tray icon,
/// hidden automatically when it loses focus. Never closed — only hidden — so
/// the app stays alive without any visible window.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    private static readonly Color LiveGreen = Color.FromArgb(255, 74, 222, 128);
    private static readonly Color IdleGray = Color.FromArgb(255, 156, 163, 175);

    private readonly BatteryMonitor _monitor;
    private readonly nint _hwnd;
    private DateTimeOffset _hiddenAt;

    public FlyoutWindow(BatteryMonitor monitor)
    {
        InitializeComponent();
        _monitor = monitor;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = false;

        // Rounded corners like system flyouts (DWMWA_WINDOW_CORNER_PREFERENCE = ROUND).
        int corner = 2;
        _ = DwmSetWindowAttribute(_hwnd, 33, ref corner, sizeof(int));

        _monitor.Updated += Render;

        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                _hiddenAt = DateTimeOffset.Now;
                AppWindow.Hide();
            }
        };

        Render();
    }

    public void Toggle()
    {
        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
            return;
        }

        // Clicking the tray icon while the flyout is open deactivates (and
        // auto-hides) it just before this runs — don't instantly reopen.
        if (DateTimeOffset.Now - _hiddenAt < TimeSpan.FromMilliseconds(300))
            return;

        ShowNearTray();
    }

    /// <summary>Show the flyout (if hidden) and run a sync — used by the tray menu.</summary>
    public void ShowAndSync()
    {
        if (!AppWindow.IsVisible)
            ShowNearTray();
        _ = SyncAsync();
    }

    private void ShowNearTray()
    {
        double scale = GetDpiForWindow(_hwnd) / 96.0;
        int width = (int)(396 * scale);
        int height = (int)(374 * scale);
        int margin = (int)(12 * scale);

        RectInt32 area = DisplayArea
            .GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)
            .WorkArea;

        AppWindow.MoveAndResize(new RectInt32(
            area.X + area.Width - width - margin,
            area.Y + area.Height - height - margin,
            width, height));

        AppWindow.Show();
        Activate(); // take focus so losing it later auto-hides the flyout

        Render();
        RefreshDeviceList();
        _ = RefreshSystemLineAsync();
    }

    // ---- Output device selection & sync -------------------------------------

    /// <summary>ComboBox item; ToString is what the dropdown displays.</summary>
    private sealed class DeviceItem
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public bool Missing { get; init; }
        public override string ToString() => Missing ? $"{Name} (not connected)" : Name;
    }

    private bool _refreshingDevices;

    private void RefreshDeviceList()
    {
        _refreshingDevices = true;
        try
        {
            var devices = AudioDeviceService.GetOutputDevices()
                .Select(d => new DeviceItem { Id = d.Id, Name = d.Name, Missing = !d.IsActive })
                .ToList();

            // Keep the saved preference selectable even while its endpoint is
            // absent (e.g. AirPods currently disconnected).
            AppSettings s = SettingsService.Current;
            if (s.OutputDeviceId is string savedId && devices.All(d => d.Id != savedId))
                devices.Add(new DeviceItem
                {
                    Id = savedId,
                    Name = s.OutputDeviceName ?? "Saved device",
                    Missing = true,
                });

            DeviceCombo.ItemsSource = devices;
            DeviceCombo.SelectedItem =
                devices.FirstOrDefault(d => d.Id == s.OutputDeviceId) ??
                devices.FirstOrDefault(d => d.Id == AudioDeviceService.GetDefaultOutputId());
        }
        finally
        {
            _refreshingDevices = false;
        }
    }

    private void DeviceCombo_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (_refreshingDevices || DeviceCombo.SelectedItem is not DeviceItem item)
            return;

        SettingsService.Current.OutputDeviceId = item.Id;
        SettingsService.Current.OutputDeviceName = item.Name;
        SettingsService.Save();
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e) => await SyncAsync();

    /// <summary>
    /// Connect the preferred device over Bluetooth if needed, wait for its
    /// audio endpoint to come up, then make it the default output.
    /// </summary>
    private async Task SyncAsync()
    {
        if (SettingsService.Current.OutputDeviceId is not string id)
        {
            SystemText.Text = "Pick an output device first, then Sync.";
            return;
        }

        string name = SettingsService.Current.OutputDeviceName ?? "device";
        SyncButton.IsEnabled = false;
        try
        {
            string? btError = null;
            if (!AudioDeviceService.IsActive(id))
            {
                SystemText.Text = "Connecting Bluetooth…";
                // Driver-level reconnect (the old control panel "Connect" button).
                if (!AudioDeviceService.TryBluetoothReconnect(id))
                {
                    // No Bluetooth KS filter behind this endpoint — fall back to
                    // paging the paired device directly.
                    btError = await BluetoothConnectService.EnsureConnectedAsync(name);
                }
            }

            SystemText.Text = "Waiting for audio endpoint…";
            bool active = await WaitForEndpointAsync(id, TimeSpan.FromSeconds(12));
            if (!active)
            {
                SystemText.Text = btError ??
                    $"{name} connected, but its audio endpoint never became active.";
                return;
            }

            AudioDeviceService.SetDefaultOutput(id);
            SystemText.Text = $"✓ {name} is now the default output";
            RefreshDeviceList();
        }
        catch (Exception ex)
        {
            SystemText.Text = $"Sync failed: {ex.Message}";
        }
        finally
        {
            SyncButton.IsEnabled = true;
        }
    }

    private static async Task<bool> WaitForEndpointAsync(string deviceId, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (AudioDeviceService.IsActive(deviceId))
                return true;
            await Task.Delay(500);
        }
        return AudioDeviceService.IsActive(deviceId);
    }

    private void Render()
    {
        var r = _monitor.CurrentReading;
        bool stale = _monitor.IsStale;

        StatusDot.Fill = new SolidColorBrush(stale ? IdleGray : LiveGreen);

        if (r is null)
        {
            ModelText.Text = "AirPods";
            HeroBox.Visibility = Visibility.Visible;
            BudsPanel.Visibility = Visibility.Collapsed;
            HeroGauge.SetState(string.Empty, null, charging: false, stale: true);
            StatusText.Text = _monitor.StartError ?? "Listening for Bluetooth broadcasts…";
            return;
        }

        ModelText.Text = r.Model;

        if (r.IsHeadphone)
        {
            // AirPods Max: one battery, no case — a single hero gauge.
            HeroBox.Visibility = Visibility.Visible;
            BudsPanel.Visibility = Visibility.Collapsed;
            HeroGauge.SetState(string.Empty,
                r.LeftBattery ?? r.RightBattery,
                r.LeftCharging || r.RightCharging,
                stale);
        }
        else
        {
            HeroBox.Visibility = Visibility.Collapsed;
            BudsPanel.Visibility = Visibility.Visible;
            LeftGauge.SetState("Left", r.LeftBattery, r.LeftCharging, stale);
            RightGauge.SetState("Right", r.RightBattery, r.RightCharging, stale);
            CaseGauge.SetState("Case", r.CaseBattery, r.CaseCharging, stale);
        }

        StatusText.Text = stale
            ? "No recent broadcast — AirPods may be idle, in a closed case, or out of range."
            : $"Live · {r.Rssi} dBm · updated {r.Timestamp:T}";
    }

    private async Task RefreshSystemLineAsync()
    {
        var levels = await SystemBatteryService.GetBluetoothBatteryLevelsAsync();
        var interesting = levels
            .Where(l => l.DeviceName.Contains("AirPods", StringComparison.OrdinalIgnoreCase) ||
                        l.DeviceName.Contains("Beats", StringComparison.OrdinalIgnoreCase))
            .ToList();

        SystemText.Text = interesting.Count == 0
            ? " "
            : "Windows reports: " +
              string.Join(" · ", interesting.Select(l => $"{l.DeviceName} {l.BatteryPercent}%"));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}

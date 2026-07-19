using AirPodsBattery.Models;
using AirPodsBattery.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AirPodsBattery;

public sealed partial class MainWindow : Window
{
    /// <summary>A BLE reading older than this is considered stale.</summary>
    private static readonly TimeSpan ReadingLifetime = TimeSpan.FromSeconds(15);

    private readonly AppleBleWatcher _bleWatcher = new();
    private readonly DispatcherTimer _systemPollTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _stalenessTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private AirPodsReading? _bestReading;

    public MainWindow()
    {
        InitializeComponent();
        Title = "AirPods Battery";

        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 480));

        _bleWatcher.ReadingReceived += OnBleReading;
        _systemPollTimer.Tick += async (_, _) => await RefreshSystemBatteryAsync();
        _stalenessTimer.Tick += (_, _) => ExpireStaleReading();

        Closed += (_, _) =>
        {
            _bleWatcher.Dispose();
            _systemPollTimer.Stop();
            _stalenessTimer.Stop();
        };

        StartMonitoring();
    }

    private async void StartMonitoring()
    {
        try
        {
            _bleWatcher.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not start Bluetooth LE scanning: {ex.Message}. " +
                              "Check that Bluetooth is enabled.";
        }

        _systemPollTimer.Start();
        _stalenessTimer.Start();
        await RefreshSystemBatteryAsync();
    }

    private void OnBleReading(object? sender, AirPodsReading reading)
    {
        // Advertisements arrive on a background thread; marshal to the UI thread.
        DispatcherQueue.TryEnqueue(() =>
        {
            // AirPods rotate their BLE address, so we can't key on MAC. Keep the
            // reading with the strongest signal seen recently — with one set of
            // AirPods nearby that is reliably yours.
            bool currentIsStale = _bestReading is null ||
                DateTimeOffset.Now - _bestReading.Timestamp > ReadingLifetime;

            if (currentIsStale || reading.Rssi >= _bestReading!.Rssi - 5)
            {
                _bestReading = reading;
                RenderBleReading(reading);
            }
        });
    }

    private void RenderBleReading(AirPodsReading r)
    {
        ModelText.Text = r.Model;
        StatusText.Text = $"Live Bluetooth LE broadcast · signal {r.Rssi} dBm";
        LastUpdatedText.Text = $"Updated {DateTime.Now:T}";

        if (r.IsHeadphone)
        {
            // AirPods Max: one battery, no case. The level arrives in the pod
            // nibbles — show whichever side is reporting.
            int? level = r.LeftBattery ?? r.RightBattery;
            LeftLabel.Text = "Headphones";
            SetCard(LeftPercent, LeftBar, LeftCharge, level, r.LeftCharging || r.RightCharging);
            RightCard.Visibility = Visibility.Collapsed;
            CaseCard.Visibility = Visibility.Collapsed;
        }
        else
        {
            LeftLabel.Text = "Left";
            RightCard.Visibility = Visibility.Visible;
            CaseCard.Visibility = Visibility.Visible;
            SetCard(LeftPercent, LeftBar, LeftCharge, r.LeftBattery, r.LeftCharging);
            SetCard(RightPercent, RightBar, RightCharge, r.RightBattery, r.RightCharging);
            SetCard(CasePercent, CaseBar, CaseCharge, r.CaseBattery, r.CaseCharging);
        }
    }

    private static void SetCard(TextBlock percentText, ProgressBar bar, TextBlock chargeText,
        int? level, bool charging)
    {
        if (level is int p)
        {
            percentText.Text = $"{p}%";
            bar.Value = p;
            chargeText.Text = charging ? "⚡ Charging" : " ";
        }
        else
        {
            percentText.Text = "—";
            bar.Value = 0;
            chargeText.Text = "Not reporting";
        }
    }

    private void ExpireStaleReading()
    {
        if (_bestReading is not null &&
            DateTimeOffset.Now - _bestReading.Timestamp > ReadingLifetime)
        {
            StatusText.Text = "No recent broadcast — AirPods may be idle, in a closed case, or out of range.";
        }
    }

    private async Task RefreshSystemBatteryAsync()
    {
        IReadOnlyList<SystemBatteryInfo> levels = await SystemBatteryService.GetBluetoothBatteryLevelsAsync();

        // Prefer devices that look like AirPods/Beats; otherwise list everything found.
        var interesting = levels
            .Where(l => l.DeviceName.Contains("AirPods", StringComparison.OrdinalIgnoreCase) ||
                        l.DeviceName.Contains("Beats", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var toShow = interesting.Count > 0 ? interesting : levels.ToList();

        SystemBatteryText.Text = toShow.Count == 0
            ? "No battery level reported yet. Windows only updates this while the device is connected over the handsfree profile."
            : string.Join("   ·   ", toShow.Select(l => $"{l.DeviceName}: {l.BatteryPercent}%"));

        LastUpdatedText.Text = $"Updated {DateTime.Now:T}";
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        await RefreshSystemBatteryAsync();
        RefreshButton.IsEnabled = true;
    }
}

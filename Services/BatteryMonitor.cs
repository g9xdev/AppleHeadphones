using AirPodsBattery.Models;
using Microsoft.UI.Dispatching;

namespace AirPodsBattery.Services;

/// <summary>
/// Owns a single BLE watcher for the app and tracks the best recent reading
/// (strongest signal), expiring it when broadcasts stop. All events are raised
/// on the dispatcher thread supplied at construction.
/// </summary>
public sealed class BatteryMonitor : IDisposable
{
    /// <summary>A BLE reading older than this is considered stale.</summary>
    private static readonly TimeSpan ReadingLifetime = TimeSpan.FromSeconds(15);

    private readonly AppleBleWatcher _bleWatcher = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _stalenessTimer;
    private bool _wasStale = true;

    public AirPodsReading? CurrentReading { get; private set; }

    public bool IsStale => CurrentReading is null ||
        DateTimeOffset.Now - CurrentReading.Timestamp > ReadingLifetime;

    /// <summary>Set when BLE scanning could not start (e.g. Bluetooth off).</summary>
    public string? StartError { get; private set; }

    /// <summary>Raised on the dispatcher thread whenever the reading or staleness changes.</summary>
    public event Action? Updated;

    public BatteryMonitor(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _stalenessTimer = dispatcher.CreateTimer();
        _stalenessTimer.Interval = TimeSpan.FromSeconds(5);
        _stalenessTimer.Tick += (_, _) =>
        {
            // Fire Updated when the reading crosses the staleness boundary so
            // listeners can dim their UI without polling.
            if (IsStale != _wasStale)
            {
                _wasStale = IsStale;
                Updated?.Invoke();
            }
        };

        _bleWatcher.ReadingReceived += OnBleReading;
    }

    public void Start()
    {
        try
        {
            _bleWatcher.Start();
        }
        catch (Exception ex)
        {
            StartError = $"Could not start Bluetooth LE scanning: {ex.Message}";
        }

        _stalenessTimer.Start();
        Updated?.Invoke();
    }

    private void OnBleReading(object? sender, AirPodsReading reading)
    {
        // Advertisements arrive on a background thread; marshal to the UI thread.
        _dispatcher.TryEnqueue(() =>
        {
            // AirPods rotate their BLE address, so we can't key on MAC. Keep the
            // reading with the strongest signal seen recently — with one set of
            // AirPods nearby that is reliably yours.
            bool currentIsStale = CurrentReading is null ||
                DateTimeOffset.Now - CurrentReading.Timestamp > ReadingLifetime;

            if (currentIsStale || reading.Rssi >= CurrentReading!.Rssi - 5)
            {
                CurrentReading = reading;
                _wasStale = false;
                Updated?.Invoke();
            }
        });
    }

    public void Dispose()
    {
        _bleWatcher.Dispose();
        _stalenessTimer.Stop();
    }
}

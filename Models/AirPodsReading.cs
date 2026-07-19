namespace AirPodsBattery.Models;

/// <summary>
/// A single decoded battery reading, either from Apple's BLE proximity-pairing
/// advertisement (per-component levels) or from the battery level Windows
/// reports for the paired Bluetooth device (single overall level).
/// </summary>
public sealed class AirPodsReading
{
    public string Model { get; init; } = "Unknown";

    /// <summary>True for AirPods Max (single battery, no case).</summary>
    public bool IsHeadphone { get; init; }

    /// <summary>0–100, or null when the component is not reporting (e.g. bud in ear out of case, or case closed).</summary>
    public int? LeftBattery { get; init; }
    public int? RightBattery { get; init; }
    public int? CaseBattery { get; init; }

    public bool LeftCharging { get; init; }
    public bool RightCharging { get; init; }
    public bool CaseCharging { get; init; }

    public short Rssi { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>Battery level Windows itself reports for a paired Bluetooth device (HFP battery indicator).</summary>
public sealed record SystemBatteryInfo(string DeviceName, int BatteryPercent);

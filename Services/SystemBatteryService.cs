using AirPodsBattery.Models;
using Windows.Devices.Enumeration.Pnp;

namespace AirPodsBattery.Services;

/// <summary>
/// Reads the battery level Windows itself tracks for paired Bluetooth devices.
/// This is the same value shown in Settings > Bluetooth &amp; devices, exposed as
/// the PnP device property DEVPKEY_Bluetooth_Battery:
///   {104EA319-6EE2-4701-BD47-8DDBF425BBE5}, pid 2  (a single 0–100 byte)
///
/// AirPods report this over the Hands-Free Profile battery indicator, so it is
/// one combined number (no per-bud detail), and some firmware/driver combos
/// don't populate it at all — which is why the app also decodes BLE
/// advertisements in <see cref="AppleBleWatcher"/>.
/// </summary>
public static class SystemBatteryService
{
    private const string BatteryPropertyKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string NamePropertyKey = "System.ItemNameDisplay";

    /// <summary>
    /// Returns every Bluetooth device node for which Windows currently has a
    /// battery level, e.g. ("AirPods Max", 80).
    /// </summary>
    public static async Task<IReadOnlyList<SystemBatteryInfo>> GetBluetoothBatteryLevelsAsync()
    {
        var results = new List<SystemBatteryInfo>();

        try
        {
            PnpObjectCollection devices = await PnpObject.FindAllAsync(
                PnpObjectType.Device,
                new[] { NamePropertyKey, BatteryPropertyKey });

            foreach (PnpObject device in devices)
            {
                if (!device.Properties.TryGetValue(BatteryPropertyKey, out object? batteryValue) ||
                    batteryValue is null)
                {
                    continue;
                }

                int percent = batteryValue switch
                {
                    byte b => b,
                    int i => i,
                    _ => -1
                };

                if (percent is < 0 or > 100)
                    continue;

                string name = device.Properties.TryGetValue(NamePropertyKey, out object? n) && n is string s
                    ? s
                    : "Bluetooth device";

                results.Add(new SystemBatteryInfo(name, percent));
            }
        }
        catch
        {
            // Enumeration can fail if the Bluetooth stack is off/restarting;
            // treat as "no data" and let the caller keep the last known value.
        }

        return results;
    }
}

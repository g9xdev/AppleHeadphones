using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace AirPodsBattery.Services;

/// <summary>
/// Forces a connection to a paired Bluetooth audio device. Windows has no
/// public "connect" API for classic audio profiles, but querying the device's
/// RFCOMM services over the air (uncached) makes Windows page the device and
/// establish the link — the audio profiles (A2DP/HFP) then connect on top.
/// </summary>
public static class BluetoothConnectService
{
    /// <summary>
    /// Ensures the paired Bluetooth device matching the output's friendly name
    /// is connected. Returns null on success or when the output isn't a known
    /// Bluetooth device (nothing to connect); an error message otherwise.
    /// </summary>
    public static async Task<string?> EnsureConnectedAsync(string outputDeviceName)
    {
        DeviceInformationCollection paired = await DeviceInformation.FindAllAsync(
            BluetoothDevice.GetDeviceSelectorFromPairingState(true));

        // Audio endpoint names embed the product name — "Headphones (AirPods Max)"
        // contains the Bluetooth device name "AirPods Max".
        DeviceInformation? match = paired.FirstOrDefault(d =>
            !string.IsNullOrEmpty(d.Name) &&
            outputDeviceName.Contains(d.Name, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            return null; // not a Bluetooth output — nothing to connect

        BluetoothDevice? device = await BluetoothDevice.FromIdAsync(match.Id);
        if (device is null)
            return "Bluetooth device is unavailable — is Bluetooth turned on?";

        try
        {
            if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                return null;

            // The uncached SDP query forces the connection attempt.
            _ = await device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);

            for (int i = 0; i < 20 && device.ConnectionStatus != BluetoothConnectionStatus.Connected; i++)
                await Task.Delay(400);

            return device.ConnectionStatus == BluetoothConnectionStatus.Connected
                ? null
                : "Could not connect — make sure the AirPods are on, in range, and not connected to another device.";
        }
        finally
        {
            device.Dispose();
        }
    }
}

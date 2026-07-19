using AirPodsBattery.Models;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace AirPodsBattery.Services;

/// <summary>
/// Listens for Apple "proximity pairing" BLE advertisements (manufacturer data,
/// company ID 0x004C, message type 0x07) and decodes battery / charging state.
///
/// The format is not documented by Apple; the offsets below are the widely used
/// community reverse engineering (OpenPods / MagicPods lineage):
///
///   byte 0      : 0x07  (proximity pairing message type)
///   byte 1      : 0x19  (payload length = 25)
///   byte 3      : device model (0x0E = AirPods Pro, 0x0A = AirPods Max, ...)
///   hex nibble 10 (bit 0x2) : "flipped" flag — swaps which nibble is left vs right
///   hex nibbles 12/13       : right / left battery, 0–10 => 0–100%, 0xF = unavailable
///   hex nibble 14           : charging bits (pods + case)
///   hex nibble 15           : case battery
///
/// AirPods Max have a single battery and no case; the level shows up in the
/// pod nibbles and the case nibble reads 0xF.
///
/// Caveats: the AirPods broadcast these packets from a randomized BLE address,
/// so we can't filter by MAC — instead the UI keeps the strongest recent signal.
/// Very new firmware revisions occasionally change/obfuscate parts of this
/// payload, in which case the system-reported level (see SystemBatteryService)
/// is the fallback.
/// </summary>
public sealed class AppleBleWatcher : IDisposable
{
    private const ushort AppleCompanyId = 0x004C;

    private readonly BluetoothLEAdvertisementWatcher _watcher;

    public event EventHandler<AirPodsReading>? ReadingReceived;

    public AppleBleWatcher()
    {
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
            AllowExtendedAdvertisements = false
        };
        _watcher.Received += OnAdvertisementReceived;
    }

    public void Start()
    {
        if (_watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
            _watcher.Start();
    }

    public void Stop()
    {
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            _watcher.Stop();
    }

    private void OnAdvertisementReceived(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (BluetoothLEManufacturerData md in args.Advertisement.ManufacturerData)
        {
            if (md.CompanyId != AppleCompanyId)
                continue;

            byte[] data = new byte[md.Data.Length];
            using (DataReader reader = DataReader.FromBuffer(md.Data))
            {
                reader.ReadBytes(data);
            }

            AirPodsReading? reading = TryParseProximityPairing(
                data, args.RawSignalStrengthInDBm, args.Timestamp);

            if (reading is not null)
                ReadingReceived?.Invoke(this, reading);
        }
    }

    private static AirPodsReading? TryParseProximityPairing(byte[] data, short rssi, DateTimeOffset ts)
    {
        // Proximity pairing message: type 0x07, declared payload length 0x19 (25),
        // giving 27 bytes of manufacturer data in total.
        if (data.Length != 27 || data[0] != 0x07 || data[1] != 0x19)
            return null;

        string hex = Convert.ToHexString(data); // 54 hex chars, uppercase

        static int Nibble(string s, int index) =>
            Convert.ToInt32(s[index].ToString(), 16);

        bool flipped = (Nibble(hex, 10) & 0x02) == 0;

        int rawLeft = Nibble(hex, flipped ? 12 : 13);
        int rawRight = Nibble(hex, flipped ? 13 : 12);
        int rawCase = Nibble(hex, 15);
        int chargeBits = Nibble(hex, 14);

        static int? ToPercent(int raw) => raw switch
        {
            >= 0 and <= 10 => raw * 10,
            _ => null // 0xF (and anything else) = not reporting
        };

        bool leftCharging = (chargeBits & (flipped ? 0b0010 : 0b0001)) != 0;
        bool rightCharging = (chargeBits & (flipped ? 0b0001 : 0b0010)) != 0;
        bool caseCharging = (chargeBits & 0b0100) != 0;

        (string model, bool isHeadphone) = data[3] switch
        {
            0x02 => ("AirPods (1st gen)", false),
            0x0F => ("AirPods (2nd gen)", false),
            0x13 => ("AirPods (3rd gen)", false),
            0x0E => ("AirPods Pro", false),
            0x14 => ("AirPods Pro (2nd gen)", false),
            0x24 => ("AirPods Pro 2 (USB-C)", false),
            0x0A => ("AirPods Max", true),
            0x1F => ("AirPods Max (USB-C)", true),
            var b => ($"Apple audio device (0x{b:X2})", false)
        };

        return new AirPodsReading
        {
            Model = model,
            IsHeadphone = isHeadphone,
            LeftBattery = ToPercent(rawLeft),
            RightBattery = ToPercent(rawRight),
            CaseBattery = ToPercent(rawCase),
            LeftCharging = leftCharging,
            RightCharging = rightCharging,
            CaseCharging = caseCharging,
            Rssi = rssi,
            Timestamp = ts
        };
    }

    public void Dispose()
    {
        _watcher.Received -= OnAdvertisementReceived;
        Stop();
    }
}

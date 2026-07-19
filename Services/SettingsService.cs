using System.Text.Json;

namespace AirPodsBattery.Services;

public sealed class AppSettings
{
    /// <summary>MMDevice endpoint ID of the preferred audio output.</summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>Friendly name of the preferred output, kept so we can show it
    /// (and match the paired Bluetooth device) while it is disconnected.</summary>
    public string? OutputDeviceName { get; set; }
}

/// <summary>Tiny JSON settings store in %LocalAppData%\AirPodsBattery.</summary>
public static class SettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AirPodsBattery", "settings.json");

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            // Corrupt settings are not worth crashing over — start fresh.
        }
        return new();
    }

    public static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath,
            JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
    }
}

using Microsoft.Win32;

namespace AirPodsBattery.Services;

/// <summary>
/// "Start with Windows" via the per-user Run registry key — the standard
/// mechanism for unpackaged desktop apps (no admin rights, no Task Scheduler).
/// The registry itself is the source of truth; nothing is cached.
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AirPodsBattery";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

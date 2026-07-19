# AirPods Battery — Windows tray app

A small native Windows app (WinUI 3 / Windows App SDK, C#) that lives in the
notification area and shows the battery level of AirPods — including AirPods Max
and AirPods Pro — connected or paired over Bluetooth. It can also reconnect the
headphones and switch Windows' audio output to them in one click.

## Features

- **Live tray badge.** The notification-area icon *is* the battery percentage,
  colour-coded green / amber / red (gray when idle) with a bolt while charging.
- **Acrylic flyout.** Left-click the icon for a Windows-style popup: a large
  ring gauge for AirPods Max, or three gauges (left / right / case) for in-ear
  models, plus signal strength and the level Windows itself reports.
- **Output device + Sync.** Pick a preferred audio output and press **Sync** to
  reconnect it over Bluetooth (if disconnected) and make it the default output.
- **Start with Windows.** A toggle in the tray menu that registers the app in
  the per-user startup key.
- **Dashboard window.** The original detailed window is still available from the
  tray menu ("Open dashboard").

## How it reads the battery level

Apple does not expose AirPods battery through the standard Bluetooth GATT
Battery Service on Windows, so the app combines two sources:

1. **Apple BLE proximity-pairing broadcasts** (`Services/AppleBleWatcher.cs`).
   AirPods continuously broadcast a manufacturer-specific BLE advertisement
   (Apple company ID `0x004C`, message type `0x07`). Decoding it yields per-bud
   and case battery plus charging state. The format is community
   reverse-engineered (same approach as OpenPods/MagicPods), so very new
   firmware may occasionally stop reporting parts of it. AirPods Max show a
   single "Headphones" battery since they have no case.

2. **The level Windows itself tracks** (`Services/SystemBatteryService.cs`).
   Windows stores a single 0–100 battery value for paired Bluetooth audio
   devices (PnP property `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2`, the value
   shown in Settings → Bluetooth & devices). This works only while the device
   is connected with the handsfree profile active, and some driver/firmware
   combinations never populate it — hence source #1.

Because AirPods randomize their BLE address, the app can't match broadcasts by
MAC; it displays the strongest recent signal. If a friend's AirPods are sitting
right next to your PC, readings can briefly mix.

## How Sync reconnects and switches output

There is no public Windows API for either half of this, so the app uses the same
mechanisms as established audio-switcher utilities:

- **Reconnect** (`Services/AudioDeviceService.cs`): walk the audio endpoint's
  device topology to the Bluetooth KS filter behind it and issue
  `KSPROPERTY_ONESHOT_RECONNECT` — the exact driver command behind the old
  control panel's "Connect" button.
- **Set default output**: the undocumented-but-stable-since-Vista `IPolicyConfig`
  COM interface (`SetDefaultEndpoint` for the Console / Multimedia /
  Communications roles), which is what the Settings app does under the hood.

The preferred device is saved to
`%LocalAppData%\AirPodsBattery\settings.json`. A disconnected device stays
selectable (shown as "(not connected)") so Sync always has a target.

> Tip: pick the **"Headphones (…)"** endpoint, not **"Headset (…)"** — the
> former is A2DP stereo, the latter is mono hands-free.

## Requirements

- Windows 10 2004 (build 19041) or Windows 11
- Bluetooth enabled; AirPods paired to the PC
- To build: Visual Studio 2022/18 with the **Windows application development**
  workload (see the build note below)

## Build & run

This is a WinUI 3 project, so it must be built with **Visual Studio's MSBuild**,
which ships the Appx/PRI packaging tasks. Building with the bare `dotnet` CLI
fails with `MSB4062 … Microsoft.Build.Packaging.Pri.Tasks` because the .NET SDK
alone does not include that tooling.

From a Developer Command Prompt (or with the full path to `MSBuild.exe`):

```
msbuild AirPodsBattery.csproj -t:Restore,Build -p:Configuration=Release -p:Platform=x64
```

Or open `AirPodsBattery.csproj` in Visual Studio and press F5 (select the x64
platform).

The project is configured as **unpackaged** (`WindowsPackageType=None`) with a
self-contained Windows App SDK, so the built `.exe` under
`bin\x64\Release\net8.0-windows10.0.19041.0\` runs directly — no MSIX install and
no store signing needed. Unpackaged desktop apps do not need a declared
Bluetooth capability to use the BLE advertisement watcher.

## Troubleshooting

- **Tray icon shows "–" / gauges show "—":** open the AirPods case lid (for
  in-ear models) or take the AirPods Max out of their Smart Case and move them
  near the PC — broadcasts pause when the device sleeps.
- **"Reported by Windows" is empty:** that value only exists while the AirPods
  are actively connected as a headset; reconnect them from quick settings.
- **Sync says the endpoint never became active:** the device is off or out of
  range, or it is a wired output with no Bluetooth filter to reconnect.
- **BLE scan fails to start:** confirm the PC has Bluetooth LE (Bluetooth 4.0+)
  and that Bluetooth is on; Settings → Privacy & security may also need
  Bluetooth access allowed for desktop apps.

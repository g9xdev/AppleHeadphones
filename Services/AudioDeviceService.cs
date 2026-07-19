using System.Runtime.InteropServices;

namespace AirPodsBattery.Services;

public sealed record AudioOutputDevice(string Id, string Name, bool IsActive);

/// <summary>
/// Enumerates audio render endpoints (MMDevice API) and sets the system
/// default output. Switching the default uses the undocumented-but-stable
/// IPolicyConfig COM interface — the mechanism every audio-switcher utility
/// relies on, since Windows exposes no public API for it.
/// </summary>
public static class AudioDeviceService
{
    private const uint DeviceStateActive = 0x1;
    private const uint DeviceStateNotPresent = 0x4;
    private const uint DeviceStateUnplugged = 0x8;
    private const uint DeviceStateMaskAll = 0xF;
    private const uint ClsctxAll = 0x17;

    public static IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        var result = new List<AudioOutputDevice>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

        if (enumerator.EnumAudioEndpoints(EDataFlow.Render,
                DeviceStateMaskAll, out IMMDeviceCollection collection) != 0)
            return result;

        collection.GetCount(out uint count);
        for (uint i = 0; i < count; i++)
        {
            if (collection.Item(i, out IMMDevice device) != 0) continue;
            if (device.GetState(out uint state) != 0) continue;

            // Active and unplugged endpoints are always listed. Absent ones are
            // listed only when they are Bluetooth — a disconnected headset must
            // stay selectable so Sync has a target; stale HDMI ghosts must not.
            bool include = state is DeviceStateActive or DeviceStateUnplugged ||
                (state == DeviceStateNotPresent && GetBluetoothFilterId(device) is not null);
            if (!include) continue;

            if (device.GetId(out string id) != 0) continue;
            result.Add(new AudioOutputDevice(id, GetFriendlyName(device), state == DeviceStateActive));
        }
        return result;
    }

    /// <summary>
    /// Forces the Bluetooth audio device backing this endpoint to (re)connect —
    /// the same driver command as the old control panel's "Connect" button
    /// (KSPROPERTY_ONESHOT_RECONNECT on the endpoint's Bluetooth KS filter).
    /// Returns false when the endpoint has no Bluetooth filter (wired device)
    /// or the command could not be issued.
    /// </summary>
    public static bool TryBluetoothReconnect(string endpointId)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        if (enumerator.GetDevice(endpointId, out IMMDevice endpoint) != 0) return false;

        string? filterId = GetBluetoothFilterId(endpoint);
        if (filterId is null) return false;

        if (enumerator.GetDevice(filterId, out IMMDevice filter) != 0) return false;

        Guid iid = IID_IKsControl;
        if (filter.Activate(ref iid, ClsctxAll, 0, out object obj) != 0) return false;

        var ksControl = (IKsControl)obj;
        var property = new KSPROPERTY
        {
            Set = KSPROPSETID_BtAudio,
            Id = 0,     // KSPROPERTY_ONESHOT_RECONNECT
            Flags = 1,  // KSPROPERTY_TYPE_GET (the "get" is the trigger)
        };
        return ksControl.KsProperty(ref property, (uint)Marshal.SizeOf<KSPROPERTY>(), 0, 0, out _) == 0;
    }

    /// <summary>Walks the endpoint's device topology to the adapter filter it
    /// is wired to; returns its ID when that filter is a Bluetooth one.</summary>
    private static string? GetBluetoothFilterId(IMMDevice endpoint)
    {
        Guid iid = IID_IDeviceTopology;
        if (endpoint.Activate(ref iid, ClsctxAll, 0, out object obj) != 0) return null;

        var topology = (IDeviceTopology)obj;
        if (topology.GetConnectorCount(out uint count) != 0) return null;

        for (uint i = 0; i < count; i++)
        {
            if (topology.GetConnector(i, out IConnector connector) != 0) continue;
            if (connector.GetDeviceIdConnectedTo(out string filterId) != 0) continue;
            if (filterId.StartsWith(@"{2}.\\?\bth", StringComparison.OrdinalIgnoreCase))
                return filterId; // bthenum / bthhfenum
        }
        return null;
    }

    /// <summary>True when the endpoint exists and is ACTIVE (ready for audio).</summary>
    public static bool IsActive(string deviceId)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        if (enumerator.GetDevice(deviceId, out IMMDevice device) != 0) return false;
        return device.GetState(out uint state) == 0 && state == DeviceStateActive;
    }

    public static string? GetDefaultOutputId()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out IMMDevice device) != 0)
            return null;
        return device.GetId(out string id) == 0 ? id : null;
    }

    /// <summary>Makes the endpoint the default for all three roles, matching
    /// what the Settings app does when you pick an output device.</summary>
    public static void SetDefaultOutput(string deviceId)
    {
        var policy = (IPolicyConfig)new PolicyConfigComObject();
        Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Console));
        Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Multimedia));
        Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Communications));
    }

    private static string GetFriendlyName(IMMDevice device)
    {
        const string fallback = "Unknown device";
        if (device.OpenPropertyStore(0 /* STGM_READ */, out IPropertyStore store) != 0)
            return fallback;

        PROPERTYKEY key = PKEY_Device_FriendlyName;
        if (store.GetValue(ref key, out PROPVARIANT value) != 0)
            return fallback;

        try
        {
            const ushort VtLpwstr = 31;
            return value.vt == VtLpwstr
                ? Marshal.PtrToStringUni(value.pointerValue) ?? fallback
                : fallback;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    // ---- COM interop --------------------------------------------------------

    private enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    private static readonly PROPERTYKEY PKEY_Device_FriendlyName =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    // Verified against Windows SDK 10.0.28000 headers (ksmedia.h, devicetopology.h, ks.h)
    private static readonly Guid KSPROPSETID_BtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    private static readonly Guid IID_IDeviceTopology = new("2A07407E-6497-4A18-9787-32F79BD0D98F");
    private static readonly Guid IID_IKsControl = new("28F54685-06FD-11D2-B27A-00A0C9223196");

    [StructLayout(LayoutKind.Sequential)]
    private struct KSPROPERTY
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY(Guid fmtid, uint pid)
    {
        public Guid fmtid = fmtid;
        public uint pid = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        private readonly ushort _r1, _r2, _r3;
        public nint pointerValue;
        private readonly nint _p2;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, nint activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    /// <summary>Vtable order verified against devicetopology.h — unused slots
    /// are declared only to keep the used methods at the correct positions.</summary>
    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
        [PreserveSig] int Unused_GetSubunitCount();
        [PreserveSig] int Unused_GetSubunit();
        [PreserveSig] int Unused_GetPartById();
        [PreserveSig] int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int Unused_GetSignalPath();
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int Unused_GetType();
        [PreserveSig] int Unused_GetDataFlow();
        [PreserveSig] int Unused_ConnectTo();
        [PreserveSig] int Unused_Disconnect();
        [PreserveSig] int Unused_IsConnected();
        [PreserveSig] int GetConnectedTo(out IConnector other);
        [PreserveSig] int Unused_GetConnectorIdConnectedTo();
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string deviceId);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KSPROPERTY property, uint propertyLength,
            nint data, uint dataLength, out uint bytesReturned);
        [PreserveSig] int KsMethod(ref KSPROPERTY method, uint methodLength,
            nint data, uint dataLength, out uint bytesReturned);
        [PreserveSig] int KsEvent(ref KSPROPERTY ksEvent, uint eventLength,
            nint data, uint dataLength, out uint bytesReturned);
    }

    /// <summary>
    /// Undocumented interface from AudioSes.dll. The first ten vtable slots are
    /// format/period/share-mode methods we never call — they are declared only
    /// to keep SetDefaultEndpoint at the correct slot.
    /// </summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int Unused_GetMixFormat();
        [PreserveSig] int Unused_GetDeviceFormat();
        [PreserveSig] int Unused_ResetDeviceFormat();
        [PreserveSig] int Unused_SetDeviceFormat();
        [PreserveSig] int Unused_GetProcessingPeriod();
        [PreserveSig] int Unused_SetProcessingPeriod();
        [PreserveSig] int Unused_GetShareMode();
        [PreserveSig] int Unused_SetShareMode();
        [PreserveSig] int Unused_GetPropertyValue();
        [PreserveSig] int Unused_SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    }
}

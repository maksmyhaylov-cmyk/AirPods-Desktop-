using System.Runtime.InteropServices;

namespace AirPodsWin;

public enum DataFlow { Render = 0, Capture = 1 }

public sealed record AudioEndpoint(string Id, string Name, DataFlow Flow)
{
    /// <summary>
    /// Windows exposes AirPods as two endpoints: "Headphones" (A2DP, stereo, good quality)
    /// and "Headset ... Hands-Free" (HFP, mono, phone-call quality).
    /// </summary>
    public bool IsHandsFree =>
        Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("Hands Free", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("Handsfree", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Core Audio helpers: list endpoints and change Windows default devices.</summary>
public static class AudioDevices
{
    private const uint DeviceStateActive = 0x1;

    private const uint DeviceStateUnplugged = 0x8;

    public static List<AudioEndpoint> GetActive(DataFlow flow) => Enumerate(flow, DeviceStateActive);

    private static List<AudioEndpoint> Enumerate(DataFlow flow, uint stateMask)
    {
        var result = new List<AudioEndpoint>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(flow, stateMask, out var collection));
            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                collection.Item(i, out var device);
                device.GetId(out var id);
                result.Add(new AudioEndpoint(id, GetFriendlyName(device) ?? id, flow));
                Marshal.ReleaseComObject(device);
            }
            Marshal.ReleaseComObject(collection);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
        return result;
    }

    private static T WithDevice<T>(string deviceId, Func<IMMDevice, T> action, T fallback)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            if (enumerator.GetDevice(deviceId, out var device) != 0) return fallback;
            try { return action(device); }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Shared-mode mix format: (channels, sample rate). HFP is 1 channel at 16 kHz or less.</summary>
    public static (int Channels, int SampleRate)? GetMixFormat(string deviceId) =>
        WithDevice<(int, int)?>(deviceId, device =>
        {
            var iid = typeof(IAudioClient).GUID;
            if (device.Activate(ref iid, 0x17, IntPtr.Zero, out var obj) != 0) return null;
            var client = (IAudioClient)obj;
            try
            {
                if (client.GetMixFormat(out var fmt) != 0) return null;
                try { return (Marshal.ReadInt16(fmt, 2), Marshal.ReadInt32(fmt, 4)); }
                finally { Marshal.FreeCoTaskMem(fmt); }
            }
            finally
            {
                Marshal.ReleaseComObject(client);
            }
        }, null);

    /// <summary>Names of processes that are currently recording from (or playing to) the endpoint.</summary>
    public static List<string> GetActiveSessionApps(string deviceId) =>
        WithDevice(deviceId, device =>
        {
            var apps = new List<string>();
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 0x17, IntPtr.Zero, out var obj) != 0) return apps;
            var manager = (IAudioSessionManager2)obj;
            try
            {
                if (manager.GetSessionEnumerator(out var sessions) != 0) return apps;
                try
                {
                    sessions.GetCount(out int count);
                    for (int i = 0; i < count; i++)
                    {
                        if (sessions.GetSession(i, out var session) != 0) continue;
                        try
                        {
                            var control = (IAudioSessionControl2)session;
                            if (control.GetState(out int state) != 0 || state != 1 /* Active */) continue;
                            control.GetProcessId(out uint pid);
                            string name;
                            try { name = pid == 0 ? "Windows" : System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
                            catch { name = "PID " + pid; }
                            if (!apps.Contains(name)) apps.Add(name);
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(session);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(sessions);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(manager);
            }
            return apps;
        }, new List<string>());

    /// <summary>Opens the endpoint in shared mode for a while (used by diagnostics to emulate a call).</summary>
    public static void HoldOpen(string deviceId, TimeSpan duration, Action? whileOpen = null) =>
        WithDevice(deviceId, device =>
        {
            var iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 0x17, IntPtr.Zero, out var obj));
            var client = (IAudioClient)obj;
            try
            {
                Marshal.ThrowExceptionForHR(client.GetMixFormat(out var fmt));
                try
                {
                    Marshal.ThrowExceptionForHR(client.Initialize(0 /* shared */, 0, 10_000_000, 0, fmt, IntPtr.Zero));
                }
                finally
                {
                    Marshal.FreeCoTaskMem(fmt);
                }
                Marshal.ThrowExceptionForHR(client.Start());
                Thread.Sleep(duration / 2);
                whileOpen?.Invoke();
                Thread.Sleep(duration / 2);
                client.Stop();
            }
            finally
            {
                Marshal.ReleaseComObject(client);
            }
            return true;
        }, false);

    public static string? GetDefaultId(DataFlow flow, int role = 1)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0) return null;
            device.GetId(out var id);
            Marshal.ReleaseComObject(device);
            return id;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Sets the endpoint as default for all roles (console, multimedia, communications).</summary>
    public static void SetDefault(string deviceId)
    {
        var policy = (IPolicyConfig)new PolicyConfigComObject();
        try
        {
            for (int role = 0; role <= 2; role++)
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, role));
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    /// <summary>
    /// Asks the Bluetooth audio driver to connect (or disconnect) the device behind the stereo
    /// endpoints matching <paramref name="nameFilter"/> — the same thing the "Connect" button in
    /// Windows sound settings does. No admin rights needed. Returns how many endpoints were signalled.
    /// </summary>
    public static int BluetoothConnect(string nameFilter, bool connect) => BluetoothConnect(nameFilter, connect, null);

    public static int BluetoothConnect(string nameFilter, bool connect, List<string>? log)
    {
        int done = 0;
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            var targets = Enumerate(DataFlow.Render, DeviceStateActive | DeviceStateUnplugged)
                .Where(e => e.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) && !e.IsHandsFree);
            foreach (var endpoint in targets)
            {
                int hr = enumerator.GetDevice(endpoint.Id, out var device);
                if (hr != 0) { log?.Add($"{endpoint.Name}: GetDevice 0x{hr:X8}"); continue; }
                try
                {
                    string result = SendBtAudioOneShot(enumerator, device, connect);
                    log?.Add($"{endpoint.Name}: {result}");
                    if (result.StartsWith("ok")) done++;
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
        return done;
    }

    // Endpoint -> IDeviceTopology -> connector -> connected part of the BT audio KS filter ->
    // its topology -> filter device -> IKsControl.
    private static string SendBtAudioOneShot(IMMDeviceEnumerator enumerator, IMMDevice device, bool connect)
    {
        var iidTopology = typeof(IDeviceTopology).GUID;
        int hr = device.Activate(ref iidTopology, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var topoObj);
        if (hr != 0) return $"Activate(IDeviceTopology) 0x{hr:X8}";
        var topology = (IDeviceTopology)topoObj;
        IConnector? connector = null, other = null;
        try
        {
            if ((hr = topology.GetConnector(0, out connector)) != 0) return $"GetConnector 0x{hr:X8}";
            if ((hr = connector.GetConnectedTo(out other)) != 0) return $"GetConnectedTo 0x{hr:X8}";
            if ((hr = ((IPart)other).GetTopologyObject(out var filterTopology)) != 0) return $"GetTopologyObject 0x{hr:X8}";
            string filterId;
            try
            {
                if ((hr = filterTopology.GetDeviceId(out filterId)) != 0) return $"GetDeviceId 0x{hr:X8}";
            }
            finally
            {
                Marshal.ReleaseComObject(filterTopology);
            }
            if ((hr = enumerator.GetDevice(filterId, out var filterDevice)) != 0) return $"GetDevice(filter) 0x{hr:X8}";
            object ksObj;
            try
            {
                var iidKs = typeof(IKsControl).GUID;
                if ((hr = filterDevice.Activate(ref iidKs, 0x17, IntPtr.Zero, out ksObj)) != 0)
                    return $"Activate(IKsControl) 0x{hr:X8}";
            }
            finally
            {
                Marshal.ReleaseComObject(filterDevice);
            }
            var ks = (IKsControl)ksObj;
            try
            {
                var prop = new KsPropertyHeader
                {
                    Set = new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D"), // KSPROPSETID_BtAudio
                    Id = connect ? 0u /* ONESHOT_RECONNECT */ : 1u /* ONESHOT_DISCONNECT */,
                    Flags = 1, // KSPROPERTY_TYPE_GET
                };
                hr = ks.KsProperty(ref prop, (uint)Marshal.SizeOf<KsPropertyHeader>(), IntPtr.Zero, 0, out _);
                // The driver may report an error code even though the request was accepted.
                return hr == 0 ? "ok" : $"ok (KsProperty 0x{hr:X8})";
            }
            finally
            {
                Marshal.ReleaseComObject(ks);
            }
        }
        finally
        {
            if (other != null) Marshal.ReleaseComObject(other);
            if (connector != null) Marshal.ReleaseComObject(connector);
            Marshal.ReleaseComObject(topology);
        }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KsPropertyHeader
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
        [PreserveSig] int GetSubunitCount(out uint count);
        [PreserveSig] int GetSubunit(uint index, out IntPtr subunit);
        [PreserveSig] int GetPartById(uint id, out IntPtr part);
        [PreserveSig] int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector other);
    }

    [ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPart
    {
        [PreserveSig] int GetName(out IntPtr name);
        [PreserveSig] int GetLocalId(out uint id);
        [PreserveSig] int GetGlobalId(out IntPtr id);
        [PreserveSig] int GetPartType(out int type);
        [PreserveSig] int GetSubType(out Guid subType);
        [PreserveSig] int GetControlInterfaceCount(out uint count);
        [PreserveSig] int GetControlInterface(uint index, out IntPtr control);
        [PreserveSig] int EnumPartsIncoming(out IntPtr parts);
        [PreserveSig] int EnumPartsOutgoing(out IntPtr parts);
        [PreserveSig] int GetTopologyObject(out IDeviceTopology topology);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KsPropertyHeader property, uint propertyLength, IntPtr data, uint dataLength, out uint bytesReturned);
    }

    private static string? GetFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0) return null;
        try
        {
            var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try
            {
                return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.pointer) : null;
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
        public PropertyKey(Guid f, int p) { fmtid = f; pid = p; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    // Undocumented but stable since Windows 7; used by every "audio switcher" tool.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}

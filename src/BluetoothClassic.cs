using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AirPodsWin;

/// <summary>
/// Toggles Bluetooth profiles on paired AirPods via BluetoothSetServiceState.
/// Turning off Hands-Free (HFP) stops Windows from dropping to mono "call quality"
/// sound whenever an app opens the microphone. Needs administrator rights.
/// </summary>
public static class BluetoothClassic
{
    public static readonly Guid HandsFreeService = new("0000111e-0000-1000-8000-00805f9b34fb");
    public static readonly Guid HeadsetService = new("00001108-0000-1000-8000-00805f9b34fb");
    public static readonly Guid AudioSinkService = new("0000110b-0000-1000-8000-00805f9b34fb");

    private const uint ServiceDisable = 0x00;
    private const uint ServiceEnable = 0x01;

    public static bool IsAdmin =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Names of paired Bluetooth devices containing the filter (e.g. "AirPods").</summary>
    public static List<string> FindPaired(string nameFilter) =>
        EnumeratePaired()
            .Where(d => d.szName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.szName)
            .ToList();

    /// <summary>Entry point used by the elevated helper process.</summary>
    public static int RunElevatedCommand(string action, string nameFilter)
    {
        var devices = EnumeratePaired()
            .Where(d => d.szName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (devices.Count == 0) return 2;

        foreach (var dev in devices)
        {
            var d = dev;
            switch (action)
            {
                case "hfp-off":
                    SetService(ref d, HandsFreeService, false);
                    SetService(ref d, HeadsetService, false);
                    break;
                case "hfp-on":
                    SetService(ref d, HandsFreeService, true);
                    try { SetService(ref d, HeadsetService, true); }
                    catch (Win32Exception) { /* most AirPods don't expose HSP */ }
                    break;
                case "reconnect":
                    SetService(ref d, AudioSinkService, false);
                    Thread.Sleep(1500);
                    SetService(ref d, AudioSinkService, true);
                    break;
                default:
                    return 3;
            }
        }
        return 0;
    }

    /// <summary>Runs <see cref="RunElevatedCommand"/> in a UAC-elevated copy of this exe.</summary>
    public static void RunElevated(string action, string nameFilter)
    {
        if (IsAdmin)
        {
            int code = RunElevatedCommand(action, nameFilter);
            if (code != 0) throw new InvalidOperationException(ErrorText(code));
            return;
        }

        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("--bt");
        psi.ArgumentList.Add(action);
        psi.ArgumentList.Add(nameFilter);

        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException(ErrorText(p.ExitCode));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Скасовано: потрібні права адміністратора.");
        }
    }

    private static string ErrorText(int code) => code switch
    {
        2 => "Спарені AirPods не знайдено. Спершу підключіть їх у налаштуваннях Bluetooth Windows.",
        _ => $"Помилка Bluetooth (код {code}).",
    };

    private static void SetService(ref BluetoothDeviceInfo device, Guid service, bool enable)
    {
        var guid = service;
        uint err = BluetoothSetServiceState(IntPtr.Zero, ref device, ref guid, enable ? ServiceEnable : ServiceDisable);
        // 87 = ERROR_INVALID_PARAMETER: the device doesn't expose that profile, which is fine.
        if (err != 0 && err != 87 && err != 1168 /* ERROR_NOT_FOUND */)
            throw new Win32Exception((int)err);
    }

    private static List<BluetoothDeviceInfo> EnumeratePaired()
    {
        var list = new List<BluetoothDeviceInfo>();
        var search = new BluetoothDeviceSearchParams
        {
            dwSize = Marshal.SizeOf<BluetoothDeviceSearchParams>(),
            fReturnAuthenticated = true,
            fReturnRemembered = true,
            fReturnConnected = true,
            fReturnUnknown = false,
            fIssueInquiry = false,
            cTimeoutMultiplier = 0,
        };
        var info = new BluetoothDeviceInfo { dwSize = Marshal.SizeOf<BluetoothDeviceInfo>(), szName = "" };

        IntPtr find = BluetoothFindFirstDevice(ref search, ref info);
        if (find == IntPtr.Zero) return list;
        try
        {
            do
            {
                list.Add(info);
                info = new BluetoothDeviceInfo { dwSize = Marshal.SizeOf<BluetoothDeviceInfo>(), szName = "" };
            } while (BluetoothFindNextDevice(find, ref info));
        }
        finally
        {
            BluetoothFindDeviceClose(find);
        }
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothDeviceInfo
    {
        public int dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SystemTime stLastSeen;
        public SystemTime stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceSearchParams
    {
        public int dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref BluetoothDeviceSearchParams search, ref BluetoothDeviceInfo info);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(IntPtr find, ref BluetoothDeviceInfo info);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr find);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothSetServiceState(IntPtr radio, ref BluetoothDeviceInfo device, ref Guid service, uint flags);
}

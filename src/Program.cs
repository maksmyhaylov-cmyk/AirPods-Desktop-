namespace AirPodsWin;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Elevated helper mode: AirPodsWin.exe --bt <hfp-off|hfp-on|reconnect> <name filter>
        if (args.Length >= 3 && args[0] == "--bt")
        {
            try { return BluetoothClassic.RunElevatedCommand(args[1], args[2]); }
            catch { return 1; }
        }

        // Diagnostics: AirPodsWin.exe --diag [seconds] -> %AppData%\AirPodsWin\diag.txt
        if (args.Length >= 1 && args[0] == "--diag")
            return Diagnostics.Run(args.Length >= 2 && int.TryParse(args[1], out int sec) ? sec : 15);

        if (args.Length >= 1 && args[0] == "--mictest")
            return Diagnostics.MicTest(args.Length >= 2 ? args[1] : "AirPods");

        // AirPodsWin.exe --connect|--disconnect [name filter]: exit code = endpoints signalled.
        if (args.Length >= 1 && args[0] is "--connect" or "--disconnect")
        {
            var log = new List<string>();
            int n = AudioDevices.BluetoothConnect(args.Length >= 2 ? args[1] : "AirPods", args[0] == "--connect", log);
            Directory.CreateDirectory(Path.GetDirectoryName(Diagnostics.FilePath)!);
            File.WriteAllLines(Diagnostics.FilePath, log.DefaultIfEmpty("no matching endpoints"));
            return n;
        }

        using var mutex = new Mutex(true, "AirPodsWin.SingleInstance", out bool isNew);
        if (!isNew) return 0;

        ApplicationConfiguration.Initialize();
        bool startHidden = args.Contains("--tray");
        Application.Run(new MainForm(startHidden));
        return 0;
    }
}

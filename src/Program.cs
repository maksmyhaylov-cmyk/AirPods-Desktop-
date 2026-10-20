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

        using var mutex = new Mutex(true, "AirPodsWin.SingleInstance", out bool isNew);
        if (!isNew) return 0;

        ApplicationConfiguration.Initialize();
        bool startHidden = args.Contains("--tray");
        Application.Run(new MainForm(startHidden));
        return 0;
    }
}

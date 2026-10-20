using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth.Advertisement;

namespace AirPodsWin;

/// <summary>
/// "AirPodsWin.exe --diag [seconds]": writes audio endpoints, paired Bluetooth devices and
/// raw Apple BLE packets to %AppData%\AirPodsWin\diag.txt. Used to debug real hardware.
/// </summary>
public static class Diagnostics
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AirPodsWin", "diag.txt");

    private static string Format(string id) =>
        AudioDevices.GetMixFormat(id) is { } f ? $"{f.Channels}ch {f.SampleRate} Hz" : "?";

    /// <summary>"--mictest": opens the AirPods microphone like a call app and logs what happens to the output.</summary>
    public static int MicTest(string filter)
    {
        var sb = new StringBuilder();
        var mic = AudioDevices.GetActive(DataFlow.Capture)
            .FirstOrDefault(d => d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        void Dump(string when)
        {
            sb.AppendLine($"-- {when}");
            foreach (var d in AudioDevices.GetActive(DataFlow.Render).Concat(AudioDevices.GetActive(DataFlow.Capture))
                         .Where(d => d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                sb.AppendLine($"   {d.Flow} {d.Name}: {Format(d.Id)} apps=[{string.Join(", ", AudioDevices.GetActiveSessionApps(d.Id))}]");
        }
        Dump("before");
        if (mic == null) sb.AppendLine("мікрофон AirPods не знайдено");
        else
        {
            try { AudioDevices.HoldOpen(mic.Id, TimeSpan.FromSeconds(8), () => Dump("mic open")); }
            catch (Exception ex) { sb.AppendLine("error: " + ex.Message); }
            Thread.Sleep(3000);
            Dump("after");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, sb.ToString());
        return 0;
    }

    public static int Run(int seconds)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"AirPodsWin diag {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            sb.AppendLine($"\n== {flow} (active) ==");
            try
            {
                string? media = AudioDevices.GetDefaultId(flow, 1);
                string? comms = AudioDevices.GetDefaultId(flow, 2);
                foreach (var d in AudioDevices.GetActive(flow))
                {
                    string mark = (d.Id == media ? " [default]" : "") + (d.Id == comms ? " [comms]" : "");
                    sb.AppendLine($"{d.Name}{mark}{(d.IsHandsFree ? " (HFP)" : "")}  {Format(d.Id)}" +
                                  $"  apps=[{string.Join(", ", AudioDevices.GetActiveSessionApps(d.Id))}]\n    {d.Id}");
                }
            }
            catch (Exception ex) { sb.AppendLine("error: " + ex.Message); }
        }

        sb.AppendLine("\n== Paired Bluetooth ==");
        try
        {
            foreach (var n in BluetoothClassic.FindPaired("")) sb.AppendLine(n);
        }
        catch (Exception ex) { sb.AppendLine("error: " + ex.Message); }

        sb.AppendLine($"\n== Apple BLE packets ({seconds} s) ==");
        var lines = new List<string>();
        int total = 0;
        var appleTypes = new SortedDictionary<byte, int>();
        // No advertisement filter: we also want to know whether the radio hears anything at all.
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        watcher.Received += (_, e) =>
        {
            Interlocked.Increment(ref total);
            foreach (var md in e.Advertisement.ManufacturerData)
            {
                if (md.CompanyId != AirPodsState.AppleCompanyId) continue;
                var data = md.Data.ToArray();
                if (data.Length > 0) lock (appleTypes) appleTypes[data[0]] = appleTypes.GetValueOrDefault(data[0]) + 1;
                if (data.Length < 2 || data[0] != 0x07) continue; // only proximity pairing
                var s = AirPodsState.TryParse(data, e.RawSignalStrengthInDBm);
                string line = $"{DateTime.Now:HH:mm:ss.fff} {e.RawSignalStrengthInDBm,4} dBm  {Convert.ToHexString(data)}"
                              + (s == null ? "  (не розпізнано)" :
                                  $"\n    {s.Model} L={s.LeftBattery} R={s.RightBattery} C={s.CaseBattery}" +
                                  $" chg={s.LeftCharging}/{s.RightCharging}/{s.CaseCharging}" +
                                  $" ear={s.LeftInEar}/{s.RightInEar} lid={s.LidOpen}");
                lock (lines) lines.Add(line);
            }
        };
        watcher.Stopped += (_, e) => { lock (lines) lines.Add("watcher stopped: " + e.Error); };
        try
        {
            watcher.Start();
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            watcher.Stop();
        }
        catch (Exception ex) { lines.Add("error: " + ex.Message); }
        lock (lines) foreach (var l in lines) sb.AppendLine(l);
        sb.AppendLine($"\nУсього BLE-пакетів: {total}; Apple за типами: " +
                      string.Join(", ", appleTypes.Select(kv => $"0x{kv.Key:X2}×{kv.Value}")));

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, sb.ToString());
        return 0;
    }
}

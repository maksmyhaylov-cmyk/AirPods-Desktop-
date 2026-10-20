using System.Text.Json;
using Microsoft.Win32;

namespace AirPodsWin;

public sealed class Settings
{
    /// <summary>Part of the Bluetooth name used to find the AirPods audio devices.</summary>
    public string DeviceNameFilter { get; set; } = "AirPods";
    public int MinRssi { get; set; } = -70;
    public bool AutoPause { get; set; } = true;
    public bool AutoResume { get; set; } = true;
    /// <summary>Make the stereo "Headphones" endpoint default instead of the Hands-Free one.</summary>
    public bool ForceStereoOutput { get; set; } = true;
    /// <summary>Keep the default microphone on another device so Windows never switches AirPods to HFP.</summary>
    public bool AvoidAirPodsMic { get; set; } = false;
    public bool LowBatteryNotify { get; set; } = true;
    public int LowBatteryLevel { get; set; } = 20;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AirPodsWin");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* corrupted file: fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not critical */ }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "AirPodsWin";

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue(RunName, false);
        }
    }
}

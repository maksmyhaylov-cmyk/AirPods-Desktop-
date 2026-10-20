using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth.Advertisement;

namespace AirPodsWin;

/// <summary>
/// Listens for AirPods BLE advertisements and keeps the most likely "own" device:
/// the one with the strongest signal (other people's AirPods nearby are weaker).
/// </summary>
public sealed class AirPodsScanner : IDisposable
{
    private BluetoothLEAdvertisementWatcher? _watcher;
    private DateTime _lastPacket = DateTime.Now;
    private bool _wanted;
    private readonly object _lock = new();
    private AirPodsState? _current;

    public event Action<AirPodsState?>? StateChanged;
    public event Action<string>? Error;

    /// <summary>Packets weaker than this are ignored (dBm).</summary>
    public int MinRssi { get; set; } = -110;

    public AirPodsState? Current
    {
        get { lock (_lock) return _current; }
    }

    private BluetoothLEAdvertisementWatcher CreateWatcher()
    {
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Passive,
        };
        // No AdvertisementFilter: a manufacturer filter with only CompanyId and empty data
        // matches nothing on real hardware. Apple packets are picked out in OnReceived.
        watcher.Received += OnReceived;
        watcher.Stopped += (_, e) =>
        {
            if (e.Error != Windows.Devices.Bluetooth.BluetoothError.Success)
                Error?.Invoke($"Bluetooth: {e.Error}. Пробую перезапустити сканер…");
        };
        return watcher;
    }

    public void Start()
    {
        _wanted = true;
        try
        {
            _watcher ??= CreateWatcher();
            if (_watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
            {
                _lastPacket = DateTime.Now;
                _watcher.Start();
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke("Не вдалося запустити Bluetooth-сканер: " + ex.Message);
        }
    }

    public void Stop()
    {
        _wanted = false;
        if (_watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            _watcher.Stop();
    }

    /// <summary>
    /// Called by a timer. The watcher dies when the Bluetooth stack restarts (adapter plugged in,
    /// radio toggled, sleep): it either reports Aborted/Stopped or silently stops delivering packets.
    /// In both cases a fresh watcher is created.
    /// </summary>
    public void EnsureRunning()
    {
        if (!_wanted) return;
        bool dead = _watcher == null
            || _watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started
            || DateTime.Now - _lastPacket > TimeSpan.FromSeconds(60);
        if (!dead) return;
        if (_watcher != null)
        {
            _watcher.Received -= OnReceived;
            try { if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) _watcher.Stop(); }
            catch { /* already dead */ }
            _watcher = null;
        }
        Start();
    }

    /// <summary>Drops the state if nothing was heard for a while (called by a timer).</summary>
    public void ExpireStale(TimeSpan maxAge)
    {
        bool changed = false;
        lock (_lock)
        {
            if (_current != null && DateTime.Now - _current.Received > maxAge)
            {
                _current = null;
                changed = true;
            }
        }
        if (changed) StateChanged?.Invoke(null);
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        _lastPacket = DateTime.Now;
        if (args.RawSignalStrengthInDBm < MinRssi) return;

        foreach (var md in args.Advertisement.ManufacturerData)
        {
            if (md.CompanyId != AirPodsState.AppleCompanyId) continue;

            var state = AirPodsState.TryParse(md.Data.ToArray(), args.RawSignalStrengthInDBm);
            if (state == null) continue;

            lock (_lock)
            {
                bool accept = _current == null
                    || DateTime.Now - _current.Received > TimeSpan.FromSeconds(10)
                    || _current.Model == state.Model
                    || state.Rssi > _current.Rssi + 10;
                if (!accept) return;
                // Case level is "unknown" while both pods are out of it: keep the last known value.
                if (state.CaseBattery == null && _current?.Model == state.Model)
                    state = state with { CaseBattery = _current.CaseBattery };
                _current = state;
            }
            StateChanged?.Invoke(state);
            return;
        }
    }

    public void Dispose()
    {
        Stop();
        if (_watcher != null) _watcher.Received -= OnReceived;
    }
}

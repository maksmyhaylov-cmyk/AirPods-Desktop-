using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth.Advertisement;

namespace AirPodsWin;

/// <summary>
/// Listens for AirPods BLE advertisements and keeps the most likely "own" device:
/// the one with the strongest signal (other people's AirPods nearby are weaker).
/// </summary>
public sealed class AirPodsScanner : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly object _lock = new();
    private AirPodsState? _current;

    public event Action<AirPodsState?>? StateChanged;
    public event Action<string>? Error;

    /// <summary>Packets weaker than this are ignored (dBm).</summary>
    public int MinRssi { get; set; } = -70;

    public AirPodsState? Current
    {
        get { lock (_lock) return _current; }
    }

    public AirPodsScanner()
    {
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Passive,
        };
        var filter = new BluetoothLEManufacturerData { CompanyId = AirPodsState.AppleCompanyId };
        _watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(filter);
        _watcher.Received += OnReceived;
        _watcher.Stopped += (_, e) =>
        {
            if (e.Error != Windows.Devices.Bluetooth.BluetoothError.Success)
                Error?.Invoke($"Bluetooth: {e.Error}");
        };
    }

    public void Start()
    {
        try
        {
            if (_watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
                _watcher.Start();
        }
        catch (Exception ex)
        {
            Error?.Invoke("Не вдалося запустити Bluetooth-сканер: " + ex.Message);
        }
    }

    public void Stop()
    {
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            _watcher.Stop();
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
                _current = state;
            }
            StateChanged?.Invoke(state);
            return;
        }
    }

    public void Dispose()
    {
        Stop();
        _watcher.Received -= OnReceived;
    }
}

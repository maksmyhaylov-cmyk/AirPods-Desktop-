using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AirPodsWin;

public sealed class MainForm : Form
{
    private readonly Settings _settings = Settings.Load();
    private readonly AirPodsScanner _scanner = new();
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };

    private readonly Label _title = new();
    private readonly Label _status = new();
    private readonly BatteryView _left = new() { Caption = "Ліва" };
    private readonly BatteryView _right = new() { Caption = "Права" };
    private readonly BatteryView _case = new() { Caption = "Кейс" };
    private readonly Label _audioInfo = new();

    private bool _startHidden;
    private bool _exiting;
    private IntPtr _trayIconHandle;

    // Ear detection
    private int? _inEarCount;
    private int? _pendingInEar;
    private DateTime _pendingSince;
    private bool _pausedByUs;
    private const int EarDebounceMs = 500;
    private readonly System.Windows.Forms.Timer _earTimer = new() { Interval = EarDebounceMs };

    // Audio
    private bool _stereoWasPresent;
    private bool _lowBatteryNotified;

    public MainForm(bool startHidden)
    {
        _startHidden = startHidden;

        Text = "AirPodsWin";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(400, 640);
        Icon = MakeIcon(null, false);

        BuildUi();
        BuildTray();

        _scanner.MinRssi = _settings.MinRssi;
        _scanner.StateChanged += s => BeginInvokeSafe(() => OnAirPodsState(s));
        _scanner.Error += msg => BeginInvokeSafe(() => _status.Text = msg);

        _timer.Tick += (_, _) => OnTick();
        _earTimer.Tick += (_, _) => ConfirmPendingInEar();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            _startHidden = false;
            if (!IsHandleCreated) CreateHandle();
            value = false;
            OnLoadOnce();
        }
        base.SetVisibleCore(value);
    }

    private bool _loaded;
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        OnLoadOnce();
    }

    private void OnLoadOnce()
    {
        if (_loaded) return;
        _loaded = true;
        _scanner.Start();
        _timer.Start();
        OnTick();
    }

    // ---------------------------------------------------------------- UI

    private void BuildUi()
    {
        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(16),
            AutoScroll = true,
        };
        Controls.Add(root);

        _title.Text = "Шукаю AirPods…";
        _title.Font = new Font("Segoe UI Semibold", 15f);
        _title.AutoSize = true;
        root.Controls.Add(_title);

        _status.Text = "Відкрийте кейс біля комп'ютера або вставте навушники у вуха.";
        _status.ForeColor = Theme.SubText;
        _status.MaximumSize = new Size(360, 0);
        _status.AutoSize = true;
        _status.Margin = new Padding(3, 2, 3, 10);
        root.Controls.Add(_status);

        var batteries = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        batteries.Controls.AddRange(new Control[] { _left, _right, _case });
        root.Controls.Add(batteries);

        root.Controls.Add(Header("Звук"));
        _audioInfo.ForeColor = Theme.SubText;
        _audioInfo.MaximumSize = new Size(360, 0);
        _audioInfo.AutoSize = true;
        root.Controls.Add(_audioInfo);

        root.Controls.Add(Check("Завжди стерео (не перемикатися на Hands-Free)",
            _settings.ForceStereoOutput, v => { _settings.ForceStereoOutput = v; ApplyAudioPolicy(true); }));
        root.Controls.Add(Check("Не використовувати мікрофон AirPods",
            _settings.AvoidAirPodsMic, v => { _settings.AvoidAirPodsMic = v; ApplyAudioPolicy(true); }));

        var btnRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(370, 0) };
        btnRow.Controls.Add(Button("Вимкнути Hands-Free", () => RunBluetooth("hfp-off",
            "Hands-Free вимкнено. Тепер AirPods завжди грають у стерео-якості (мікрофон AirPods не працюватиме).")));
        btnRow.Controls.Add(Button("Увімкнути Hands-Free", () => RunBluetooth("hfp-on",
            "Hands-Free увімкнено. Мікрофон AirPods знову доступний.")));
        btnRow.Controls.Add(Button("Підключити", ConnectAirPods));
        root.Controls.Add(btnRow);

        root.Controls.Add(Header("Автоматика"));
        root.Controls.Add(Check("Пауза, коли виймаю навушник з вуха",
            _settings.AutoPause, v => _settings.AutoPause = v));
        root.Controls.Add(Check("Продовжувати відтворення, коли вставляю назад",
            _settings.AutoResume, v => _settings.AutoResume = v));
        root.Controls.Add(Check($"Сповіщення про низький заряд (<{_settings.LowBatteryLevel}%)",
            _settings.LowBatteryNotify, v => _settings.LowBatteryNotify = v));
        root.Controls.Add(Check("Запускати разом з Windows",
            SafeGet(() => Settings.StartWithWindows), v => Settings.StartWithWindows = v));
    }

    private static Label Header(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Semibold", 11f),
        AutoSize = true,
        Margin = new Padding(3, 12, 3, 4),
    };

    private CheckBox Check(string text, bool value, Action<bool> onChange)
    {
        var cb = new CheckBox
        {
            Text = text,
            Checked = value,
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            ForeColor = Theme.Text,
            Margin = new Padding(3, 4, 3, 4),
        };
        cb.CheckedChanged += (_, _) =>
        {
            try { onChange(cb.Checked); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "AirPodsWin"); }
            _settings.Save();
        };
        return cb;
    }

    private static Button Button(string text, Action onClick)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Padding = new Padding(6, 3, 6, 3),
            Margin = new Padding(3, 6, 6, 3),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = Theme.Button;
        b.Click += (_, _) => onClick();
        return b;
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Відкрити", null, (_, _) => ShowWindow());
        menu.Items.Add("Підключити AirPods", null, (_, _) => ConnectAirPods());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Вихід", null, (_, _) => ExitApp());

        _tray.ContextMenuStrip = menu;
        _tray.Text = "AirPodsWin — пошук AirPods";
        _tray.Icon = Icon;
        _tray.Visible = true;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
    }

    private void ShowWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            // Keep running in the tray so battery and auto-pause keep working.
            e.Cancel = true;
            Hide();
            return;
        }
        _settings.Save();
        _timer.Stop();
        _scanner.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    // ---------------------------------------------------------- AirPods

    private void OnAirPodsState(AirPodsState? s)
    {
        if (s == null)
        {
            _title.Text = "Шукаю AirPods…";
            _status.Text = "Сигнал втрачено. Відкрийте кейс біля комп'ютера або вставте навушники у вуха.";
            _left.SetState(null, false, false);
            _right.SetState(null, false, false);
            _case.SetState(null, false, false);
            _tray.Text = "AirPodsWin — AirPods не знайдено";
            SetTrayIcon(null, false);
            return;
        }

        _title.Text = s.Model;
        _status.Text = DescribeEars(s) + $"   ·   сигнал {s.Rssi} dBm" + (s.Rssi < -90 && s.AnyInEar
            ? "\n⚠ Дуже слабкий Bluetooth-сигнал: звук може заїкатися. Перевірте антену або використайте USB-адаптер."
            : "");
        _left.SetState(s.LeftBattery, s.LeftCharging, s.LeftInEar);
        _right.SetState(s.RightBattery, s.RightCharging, s.RightInEar);
        _case.SetState(s.CaseBattery, s.CaseCharging, s.LidOpen);
        _case.Visible = !s.Model.StartsWith("AirPods Max", StringComparison.Ordinal);
        _right.Visible = _case.Visible;
        if (!_case.Visible) _left.Caption = "Заряд";

        string tip = $"{s.Model}\nЛ: {Pct(s.LeftBattery)}  П: {Pct(s.RightBattery)}  Кейс: {Pct(s.CaseBattery)}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        SetTrayIcon(s.LowestPodBattery, s.LeftCharging || s.RightCharging);

        HandleEarDetection(s);
        HandleLowBattery(s);
    }

    private static string Pct(int? v) => v is int x ? x + "%" : "—";

    private static string DescribeEars(AirPodsState s) => (s.LeftInEar, s.RightInEar) switch
    {
        (true, true) => "Обидва навушники у вухах",
        (true, false) => "У вусі тільки лівий",
        (false, true) => "У вусі тільки правий",
        _ => s.LidOpen ? "Кейс відкрито" : "Навушники не у вухах",
    };

    private void HandleEarDetection(AirPodsState s)
    {
        int count = (s.LeftInEar ? 1 : 0) + (s.RightInEar ? 1 : 0);
        if (_inEarCount == null)
        {
            _inEarCount = count;
            return;
        }
        if (count == _inEarCount)
        {
            _pendingInEar = null;
            _earTimer.Stop();
            return;
        }
        // Debounce: packets from the two pods can briefly disagree. AirPods send a packet only
        // every ~1.3 s, so waiting for a second packet would add seconds: confirm by timer instead,
        // unless a contradicting packet arrives within the window.
        if (_pendingInEar != count)
        {
            _pendingInEar = count;
            _pendingSince = DateTime.Now;
            _earTimer.Stop();
            _earTimer.Start();
            return;
        }
        ConfirmPendingInEar();
    }

    private void ConfirmPendingInEar()
    {
        if (_pendingInEar is not int count || _inEarCount is not int previous) return;
        if (DateTime.Now - _pendingSince < TimeSpan.FromMilliseconds(EarDebounceMs)) return;
        _earTimer.Stop();
        _inEarCount = count;
        _pendingInEar = null;
        _ = OnInEarChangedAsync(previous, count);
    }

    private async Task OnInEarChangedAsync(int previous, int now)
    {
        try
        {
            if (now < previous && _settings.AutoPause)
            {
                if (await MediaControl.PauseIfPlayingAsync()) _pausedByUs = true;
            }
            else if (now > previous && _pausedByUs && (now == 2 || previous == 0))
            {
                _pausedByUs = false;
                if (_settings.AutoResume) await MediaControl.PlayAsync();
            }
        }
        catch
        {
            // Media sessions are best-effort.
        }
    }

    private void HandleLowBattery(AirPodsState s)
    {
        if (!_settings.LowBatteryNotify || s.LowestPodBattery is not int level) return;
        bool charging = s.LeftCharging || s.RightCharging;
        if (level <= _settings.LowBatteryLevel && !charging && s.AnyInEar && !_lowBatteryNotified)
        {
            _lowBatteryNotified = true;
            _tray.ShowBalloonTip(5000, "AirPods: низький заряд", $"{s.Model}: залишилось {level}%", ToolTipIcon.Warning);
        }
        else if (level > _settings.LowBatteryLevel + 5 || charging)
        {
            _lowBatteryNotified = false;
        }
    }

    private void OnTick()
    {
        _scanner.EnsureRunning();
        _scanner.ExpireStale(TimeSpan.FromSeconds(30));
        if (_scanner.Current is { } s) HandleEarDetection(s);
        ApplyAudioPolicy(false);
    }

    // ------------------------------------------------------------- Audio

    private void ApplyAudioPolicy(bool userTriggered)
    {
        try
        {
            string filter = _settings.DeviceNameFilter;
            var outputs = AudioDevices.GetActive(DataFlow.Render);
            var pods = outputs.Where(d => d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            var stereo = pods.FirstOrDefault(d => !d.IsHandsFree);
            var handsFree = pods.FirstOrDefault(d => d.IsHandsFree);

            if (_settings.ForceStereoOutput && stereo != null)
            {
                string? media = AudioDevices.GetDefaultId(DataFlow.Render, 1);
                string? comms = AudioDevices.GetDefaultId(DataFlow.Render, 2);
                bool justConnected = !_stereoWasPresent;
                bool onHandsFree = handsFree != null && (media == handsFree.Id || comms == handsFree.Id);
                bool notDefault = media != stereo.Id || comms != stereo.Id;
                // Only take over when AirPods just connected, Windows fell back to Hands-Free,
                // or the user toggled the option; otherwise respect a manual choice of speakers.
                if (notDefault && (justConnected || onHandsFree || userTriggered))
                    AudioDevices.SetDefault(stereo.Id);
            }
            _stereoWasPresent = stereo != null;

            var inputs = AudioDevices.GetActive(DataFlow.Capture);
            var podMics = inputs.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            string? noOtherMic = null;
            if (_settings.AvoidAirPodsMic && podMics.Count > 0)
            {
                // Windows 11 exposes AirPods as ONE output that silently drops to mono HFP whenever
                // their mic is opened, so the only reliable fix is to keep the default mic elsewhere.
                string? micMedia = AudioDevices.GetDefaultId(DataFlow.Capture, 1);
                string? micComms = AudioDevices.GetDefaultId(DataFlow.Capture, 2);
                bool podsMicDefault = podMics.Any(i => i.Id == micMedia || i.Id == micComms);
                // Virtual devices (SplitCam, Voicemod...) would silently mute calls, so only real mics.
                var other = inputs.Except(podMics).FirstOrDefault(i => !IsVirtualMic(i.Name));
                if (podsMicDefault && other != null) AudioDevices.SetDefault(other.Id);
                else if (podsMicDefault) noOtherMic = "Немає іншого мікрофона — щоб звук не псувався, вимкніть Hands-Free.";
            }

            // Who is holding the AirPods mic right now (that is what switches them to mono).
            var micUsers = podMics.SelectMany(m => AudioDevices.GetActiveSessionApps(m.Id)).Distinct().ToList();

            string? currentId = AudioDevices.GetDefaultId(DataFlow.Render, 1);
            var current = outputs.FirstOrDefault(o => o.Id == currentId);
            string text = pods.Count == 0
                ? $"AirPods не підключені до ПК. Вихід зараз: {current?.Name ?? "—"}"
                : $"Вихід зараз: {current?.Name ?? "—"}";
            if (current?.IsHandsFree == true || micUsers.Count > 0)
                text += "\n⚠ Режим Hands-Free (моно)" +
                        (micUsers.Count > 0 ? $": мікрофон AirPods використовує {string.Join(", ", micUsers)}" : "");
            if (pods.Count > 0 && podMics.Count == 0) text += "\n✓ Hands-Free вимкнено — завжди стерео";
            if (noOtherMic != null) text += "\n" + noOtherMic;
            _audioInfo.Text = text;
        }
        catch (Exception ex)
        {
            _audioInfo.Text = "Не вдалося прочитати аудіопристрої: " + ex.Message;
        }
    }

    private static bool IsVirtualMic(string name) =>
        new[] { "Virtual", "Voicemod", "SplitCam", "VB-Audio", "CABLE", "Mixer", "Steam Streaming", "NVIDIA Broadcast" }
            .Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>Connects AirPods through the Bluetooth audio driver (like "Connect" in Windows settings).</summary>
    private async void ConnectAirPods()
    {
        UseWaitCursor = true;
        try
        {
            string filter = _settings.DeviceNameFilter;
            int signalled = await Task.Run(() => AudioDevices.BluetoothConnect(filter, true));
            if (signalled == 0)
            {
                // No stereo endpoint known yet (e.g. drivers were never installed): restart the profile.
                await Task.Run(() => BluetoothClassic.RunElevated("reconnect", filter));
            }
            await Task.Delay(5000);
            ApplyAudioPolicy(true);
            bool connected = AudioDevices.GetActive(DataFlow.Render)
                .Any(d => d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
            _tray.ShowBalloonTip(4000, "AirPodsWin", connected
                ? "AirPods підключено."
                : "Не вдалося підключити. Якщо Windows теж не може — видаліть AirPods у Параметрах Bluetooth і спаруйте знову.",
                connected ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "AirPodsWin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private async void RunBluetooth(string action, string successText)
    {
        UseWaitCursor = true;
        try
        {
            string filter = _settings.DeviceNameFilter;
            await Task.Run(() => BluetoothClassic.RunElevated(action, filter));
            _tray.ShowBalloonTip(4000, "AirPodsWin", successText, ToolTipIcon.Info);
            await Task.Delay(2000);
            ApplyAudioPolicy(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "AirPodsWin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    // -------------------------------------------------------------- Icon

    private (int? Level, bool Charging)? _trayIconState;

    private void SetTrayIcon(int? level, bool charging)
    {
        // AirPods send several packets per second; redraw only when the picture changes.
        if (_trayIconState == (level, charging)) return;
        _trayIconState = (level, charging);
        var old = _trayIconHandle;
        var icon = MakeIcon(level, charging);
        _trayIconHandle = icon.Handle;
        _tray.Icon = icon;
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    /// <summary>Tray icon: a ring that fills with the lowest pod battery level.</summary>
    private static Icon MakeIcon(int? level, bool charging)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var r = new Rectangle(3, 3, 26, 26);
            using (var track = new Pen(Color.FromArgb(120, 128, 128, 128), 4)) g.DrawEllipse(track, r);
            if (level is int l)
            {
                Color c = charging ? Theme.Charging : l <= 20 ? Theme.Low : Theme.Accent;
                using var pen = new Pen(c, 4);
                g.DrawArc(pen, r, -90, Math.Max(10, 360 * l / 100f));
                string t = l >= 100 ? "F" : (l / 10).ToString();
                using var font = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(Color.White);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(t, font, brush, r, sf);
            }
            else
            {
                using var brush = new SolidBrush(Color.White);
                g.FillEllipse(brush, 10, 10, 5, 12);
                g.FillEllipse(brush, 17, 10, 5, 12);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    // ------------------------------------------------------------ Helpers

    private void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    private static T SafeGet<T>(Func<T> f)
    {
        try { return f(); } catch { return default!; }
    }
}

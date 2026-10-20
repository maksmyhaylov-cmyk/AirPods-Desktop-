using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AirPodsWin;

/// <summary>
/// Compact 16:9 widget (like the iPhone battery widget) plus the app controller:
/// BLE state, ear detection, audio policy and the tray icon. Settings live in <see cref="SettingsForm"/>.
/// </summary>
public sealed class MainForm : Form
{
    private readonly Settings _settings = Settings.Load();
    private readonly AirPodsScanner _scanner = new();
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };

    // Widget
    private readonly Label _title = new() { AutoEllipsis = true };
    private readonly Label _status = new() { AutoEllipsis = true };
    private readonly IconButton _gear = new() { Glyph = "" };
    private readonly IconButton _close = new() { Glyph = "" };
    private readonly BatteryView _left = new() { Caption = "Ліва", Glyph = BatteryGlyph.LeftPod };
    private readonly BatteryView _right = new() { Caption = "Права", Glyph = BatteryGlyph.RightPod };
    private readonly BatteryView _case = new() { Caption = "Кейс", Glyph = BatteryGlyph.Case };

    // Settings window
    private readonly SettingsForm _settingsForm;
    private readonly GroupCard _soundCard = new();
    private readonly Label _audioInfo = new();
    private readonly GroupCard _autoCard = new();
    private readonly Label _footer = new() { Tag = "small" };

    private bool _startHidden;
    private bool _exiting;
    private IntPtr _trayIconHandle;
    private AirPodsState? _lastState;
    private string? _hfpWarning;

    // Ear detection
    private int? _inEarCount;
    private int? _pendingInEar;
    private DateTime _pendingSince;
    private bool _pausedByUs;
    private const int EarDebounceMs = 500;
    private readonly System.Windows.Forms.Timer _earTimer = new() { Interval = EarDebounceMs };

    // Case-open card and auto-connect
    private readonly PopupForm _popup = new();
    private readonly System.Windows.Forms.Timer _popupTimer = new() { Interval = 1000 };
    private DateTime _lastCaseOpen = DateTime.MinValue;
    private bool _popupDismissed;
    private bool _connecting;
    private bool _podsConnected;
    private string? _deviceName;

    // Audio
    private bool _stereoWasPresent;
    private bool _lowBatteryNotified;

    public MainForm(bool startHidden)
    {
        _startHidden = startHidden;

        Text = "AirPodsWin";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // All sizes are computed from DeviceDpi in Relayout(), so no WinForms auto-scaling.
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Card;
        Icon = MakeIcon(null, false);

        BuildWidget();
        _settingsForm = BuildSettings();
        BuildTray();
        Relayout();

        _scanner.MinRssi = _settings.MinRssi;
        _scanner.StateChanged += s => BeginInvokeSafe(() => OnAirPodsState(s));
        _scanner.Error += msg => BeginInvokeSafe(() => _status.Text = msg);

        _timer.Tick += (_, _) => OnTick();
        _earTimer.Tick += (_, _) => ConfirmPendingInEar();
        _popupTimer.Tick += (_, _) => CheckCaseClosed();
        _popup.ConnectRequested += () => ConnectAirPods(interactive: false);
        _popup.Dismissed += () => _popupDismissed = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW: borderless windows get no shadow otherwise
            return cp;
        }
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
        // DeviceDpi is only final once the handle exists.
        ApplyFonts();
        Relayout();
        PlaceWidget();
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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.RoundCorners(Handle);
    }

    // ------------------------------------------------------------ Widget

    private float S => DeviceDpi / 96f;

    private void BuildWidget()
    {
        _title.Text = "Шукаю AirPods…";
        _status.Text = "Відкрийте кейс поруч із комп'ютером";
        foreach (var l in new[] { _title, _status }) { l.AutoSize = false; l.BackColor = Theme.Card; }
        _title.ForeColor = Theme.Text;
        _status.ForeColor = Theme.SubText;
        Controls.AddRange(new Control[] { _title, _status, _gear, _close, _left, _right, _case });

        _gear.Click += (_, _) => ShowSettings();
        _close.Click += (_, _) => HideWidget();

        // The whole widget is a drag handle (it has no title bar).
        foreach (Control c in new Control[] { this, _title, _status, _left, _right, _case })
            c.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) BeginDrag(); };
        ApplyFonts();
    }

    private void ApplyFonts()
    {
        float s = S;
        _title.Font = Theme.SemiboldFont(19, s, display: true);
        _status.Font = Theme.Font(13, s);
    }

    /// <summary>16:9 card: title and status on top, three gauges below.</summary>
    private void Relayout()
    {
        float s = S;
        int w = (int)(360 * s), h = (int)(203 * s), pad = (int)(16 * s), btn = (int)(26 * s);
        ClientSize = new Size(w, h);
        _close.SetBounds(w - pad - btn, (int)(14 * s), btn, btn);
        _gear.SetBounds(_close.Left - (int)(8 * s) - btn, _close.Top, btn, btn);
        _title.SetBounds(pad, (int)(12 * s), _gear.Left - pad - (int)(6 * s), (int)(28 * s));
        _status.SetBounds(pad, (int)(40 * s), w - pad * 2, (int)(20 * s));

        var gauge = BatteryView.PreferredFor(s);
        var gauges = new[] { _left, _right, _case }.Where(g => g.Visible).ToArray();
        int slot = (w - pad) / Math.Max(1, gauges.Length);
        int top = h - gauge.Height - (int)(8 * s);
        for (int i = 0; i < gauges.Length; i++)
            gauges[i].SetBounds(pad / 2 + i * slot + (slot - gauge.Width) / 2, top, gauge.Width, gauge.Height);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyFonts();
        Relayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Hairline border so the white card stands out on light wallpapers (and on Windows 10).
        using var pen = new Pen(Color.FromArgb(28, 0, 0, 0));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    /// <summary>Saved position, or bottom-right above the tray like a flyout.</summary>
    private void PlaceWidget()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        if (_settings.WidgetLeft is int x && _settings.WidgetTop is int y
            && Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(new Rectangle(x, y, Width, Height))))
        {
            Location = new Point(x, y);
            return;
        }
        int m = (int)(12 * S);
        Location = new Point(area.Right - Width - m, area.Bottom - Height - m);
    }

    private void BeginDrag()
    {
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
        _settings.WidgetLeft = Left;
        _settings.WidgetTop = Top;
        _settings.Save();
    }

    private void ShowWidget()
    {
        if (!Visible) PlaceWidget();
        Show();
        Activate();
    }

    private void HideWidget() => Hide();

    // ---------------------------------------------------------- Settings

    private SettingsForm BuildSettings()
    {
        _soundCard.AddToggle("Завжди стерео", "Не перемикатися на Hands-Free",
            _settings.ForceStereoOutput, v => { _settings.ForceStereoOutput = v; SaveAnd(() => ApplyAudioPolicy(true)); });
        _soundCard.AddToggle("Без мікрофона AirPods", "Звук не стає моно під час дзвінків",
            _settings.AvoidAirPodsMic, v => { _settings.AvoidAirPodsMic = v; SaveAnd(() => ApplyAudioPolicy(true)); });
        _soundCard.AddAction("Вимкнути Hands-Free", () => RunBluetooth("hfp-off",
            "Hands-Free вимкнено. Тепер AirPods завжди грають у стерео-якості (мікрофон AirPods не працюватиме)."));
        _soundCard.AddAction("Увімкнути Hands-Free", () => RunBluetooth("hfp-on",
            "Hands-Free увімкнено. Мікрофон AirPods знову доступний."));
        _soundCard.AddAction("Підключити AirPods", () => ConnectAirPods());

        _autoCard.AddToggle("Автопідключення", "Коли відкриваю кейс поруч із ПК",
            _settings.AutoConnect, v => { _settings.AutoConnect = v; SaveAnd(null); });
        _autoCard.AddToggle("Картка як на iPhone", "Заряд при відкритті кейса",
            _settings.ShowCasePopup, v => { _settings.ShowCasePopup = v; SaveAnd(null); });
        _autoCard.AddToggle("Автопауза", "Коли виймаю навушник з вуха",
            _settings.AutoPause, v => { _settings.AutoPause = v; SaveAnd(null); });
        _autoCard.AddToggle("Автопродовження", "Коли вставляю навушник назад",
            _settings.AutoResume, v => { _settings.AutoResume = v; SaveAnd(null); });
        _autoCard.AddToggle("Низький заряд", $"Сповіщення, коли менше {_settings.LowBatteryLevel}%",
            _settings.LowBatteryNotify, v => { _settings.LowBatteryNotify = v; SaveAnd(null); });
        _autoCard.AddToggle("Запуск з Windows", "Працювати у треї після входу",
            SafeGet(() => Settings.StartWithWindows), v => SaveAnd(() => Settings.StartWithWindows = v));

        _footer.Text = $"AirPodsWin {Application.ProductVersion.Split('+')[0]}";
        _footer.TextAlign = ContentAlignment.TopCenter;

        return new SettingsForm(Icon!,
            new(new Label { Text = "ЗВУК" }, 6, true),
            new(_soundCard, 6),
            new(_audioInfo, 26, true),
            new(new Label { Text = "АВТОМАТИКА" }, 6, true),
            new(_autoCard, 18),
            new(_footer, 16));
    }

    private void SaveAnd(Action? action)
    {
        try { action?.Invoke(); }
        catch (Exception ex) { MessageBox.Show(_settingsForm, ex.Message, "AirPodsWin"); }
        _settings.Save();
    }

    private void ShowSettings()
    {
        _settingsForm.Show();
        if (_settingsForm.WindowState == FormWindowState.Minimized) _settingsForm.WindowState = FormWindowState.Normal;
        _settingsForm.Activate();
    }

    // -------------------------------------------------------------- Tray

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Показати віджет", null, (_, _) => ShowWidget());
        menu.Items.Add("Налаштування", null, (_, _) => ShowSettings());
        menu.Items.Add("Підключити AirPods", null, (_, _) => ConnectAirPods());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Вихід", null, (_, _) => ExitApp());

        _tray.ContextMenuStrip = menu;
        _tray.Text = "AirPodsWin — пошук AirPods";
        _tray.Icon = Icon;
        _tray.Visible = true;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (Visible) HideWidget(); else ShowWidget();
        };
    }

    private void ExitApp()
    {
        _exiting = true;
        _settingsForm.AllowClose = true;
        _settingsForm.Close();
        _popup.Close();
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            // Keep running in the tray so battery and auto-pause keep working.
            e.Cancel = true;
            HideWidget();
            return;
        }
        _settings.Save();
        _timer.Stop();
        _scanner.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    // ---------------------------------------------------------- AirPods

    private void OnAirPodsState(AirPodsState? s)
    {
        _lastState = s;
        UpdateStatus();
        if (s == null)
        {
            _title.Text = "Шукаю AirPods…";
            _left.SetState(null, false, false);
            _right.SetState(null, false, false);
            _case.SetState(null, false, false);
            _tray.Text = "AirPodsWin — AirPods не знайдено";
            SetTrayIcon(null, false);
            return;
        }

        _title.Text = s.Model;
        _left.SetState(s.LeftBattery, s.LeftCharging, s.LeftInEar);
        _right.SetState(s.RightBattery, s.RightCharging, s.RightInEar);
        // The lid bit is meaningless while the pods are out of the case.
        _case.SetState(s.CaseBattery, s.CaseCharging, s.LidOpen && !s.AnyInEar);
        bool isMax = s.Model.StartsWith("AirPods Max", StringComparison.Ordinal);
        if (_case.Visible == isMax)
        {
            _case.Visible = _right.Visible = !isMax;
            _left.Caption = isMax ? "Заряд" : "Ліва";
            _left.Glyph = isMax ? BatteryGlyph.Headphones : BatteryGlyph.LeftPod;
            Relayout();
        }

        string tip = $"{s.Model}\nЛ: {Pct(s.LeftBattery)}  П: {Pct(s.RightBattery)}  Кейс: {Pct(s.CaseBattery)}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        SetTrayIcon(s.LowestPodBattery, s.LeftCharging || s.RightCharging);

        HandleEarDetection(s);
        HandleLowBattery(s);
        HandleCaseOpen(s);
    }

    // ------------------------------------------------- Case-open card

    private PopupForm.ConnectionState Connection =>
        _podsConnected ? PopupForm.ConnectionState.Connected
        : _connecting ? PopupForm.ConnectionState.Connecting
        : PopupForm.ConnectionState.NotConnected;

    /// <summary>
    /// Like iOS/macOS: opening the case near the PC shows the card and (optionally) connects.
    /// Only a nearby case counts, so someone else's AirPods across the room don't trigger it.
    /// </summary>
    private void HandleCaseOpen(AirPodsState s)
    {
        bool open = s.InCase && s.LidOpen && s.Rssi >= -85;
        if (!open)
        {
            _popup.UpdateCard(s, Connection);
            return;
        }

        bool newOpening = DateTime.Now - _lastCaseOpen > TimeSpan.FromSeconds(10);
        _lastCaseOpen = DateTime.Now;
        if (newOpening)
        {
            _popupDismissed = false;
            if (_settings.AutoConnect && !_podsConnected && !_connecting) ConnectAirPods(interactive: false);
        }

        if (_settings.ShowCasePopup && !_popupDismissed)
        {
            if (_popup.IsShown) _popup.UpdateCard(s, Connection);
            else _popup.ShowCard(s, DeviceName(s), Connection);
            _popupTimer.Start();
        }
    }

    /// <summary>Hides the card a few seconds after the lid closes (AirPods then stop advertising).</summary>
    private void CheckCaseClosed()
    {
        if (DateTime.Now - _lastCaseOpen < TimeSpan.FromSeconds(4)) return;
        _popupTimer.Stop();
        _popup.HideCard();
    }

    /// <summary>The Bluetooth name the user gave the AirPods, like the iPhone card shows.</summary>
    private string DeviceName(AirPodsState s)
    {
        if (_deviceName == null)
        {
            string? paired = SafeGet(() => BluetoothClassic.FindPaired(_settings.DeviceNameFilter).FirstOrDefault());
            _deviceName = paired?.Replace(" - Find My", "", StringComparison.OrdinalIgnoreCase).Trim() ?? "";
        }
        return _deviceName.Length > 0 ? _deviceName : s.Model;
    }

    /// <summary>"--demo-popup": shows the card with sample data (for screenshots and design work).</summary>
    public void ShowDemoPopup()
    {
        var demo = new AirPodsState("AirPods Pro 2", 75, 75, 49, true, true, false, false, false, true, true, -50, DateTime.Now);
        _popup.ShowCard(demo, DeviceName(demo), _podsConnected ? PopupForm.ConnectionState.Connected : PopupForm.ConnectionState.NotConnected);
    }

    /// <summary>One line under the title: a warning in orange if something hurts the sound, else the ear state.</summary>
    private void UpdateStatus()
    {
        var s = _lastState;
        string? warning = _hfpWarning
            ?? (s is { AnyInEar: true, Rssi: < -90 } ? $"Слабкий сигнал Bluetooth ({s.Rssi} dBm) — звук може заїкатися" : null);
        _status.Text = warning ?? (s == null ? "Відкрийте кейс поруч із комп'ютером" : DescribeEars(s));
        _status.ForeColor = warning != null ? Theme.Orange : Theme.SubText;
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
            _podsConnected = pods.Count > 0;
            if (_lastState != null) _popup.UpdateCard(_lastState, Connection);

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
            _audioInfo.ForeColor = text.Contains('⚠') ? Theme.Orange : Theme.SubText;
            _hfpWarning = micUsers.Count > 0 ? $"Моно: мікрофон AirPods використовує {string.Join(", ", micUsers)}"
                : current?.IsHandsFree == true ? "Режим Hands-Free — низька якість звуку" : null;
            UpdateStatus();
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
    /// <param name="interactive">
    /// From a menu/button: may fall back to the elevated helper (UAC) and reports the result.
    /// From the case-open card or auto-connect: silent, no UAC; the card shows the progress.
    /// </param>
    private async void ConnectAirPods(bool interactive = true)
    {
        if (_connecting) return;
        _connecting = true;
        if (_lastState != null) _popup.UpdateCard(_lastState, Connection);
        try
        {
            string filter = _settings.DeviceNameFilter;
            int signalled = await Task.Run(() => AudioDevices.BluetoothConnect(filter, true));
            if (signalled == 0 && interactive)
            {
                // No stereo endpoint known yet (e.g. drivers were never installed): restart the profile.
                await Task.Run(() => BluetoothClassic.RunElevated("reconnect", filter));
            }
            // The A2DP endpoint appears a few seconds after the link comes up.
            bool connected = false;
            for (int i = 0; i < 16 && !connected; i++)
            {
                await Task.Delay(500);
                connected = AudioDevices.GetActive(DataFlow.Render)
                    .Any(d => d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
            }
            _connecting = false;
            ApplyAudioPolicy(true);
            if (interactive)
                _tray.ShowBalloonTip(4000, "AirPodsWin", connected
                    ? "AirPods підключено."
                    : "Не вдалося підключити. Якщо Windows теж не може — видаліть AirPods у Параметрах Bluetooth і спаруйте знову.",
                    connected ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            if (interactive) MessageBox.Show(this, ex.Message, "AirPodsWin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _connecting = false;
            if (_lastState != null) _popup.UpdateCard(_lastState, Connection);
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

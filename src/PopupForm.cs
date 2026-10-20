using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AirPodsWin;

/// <summary>
/// iPhone-style card that slides up when the case is opened near the PC: device name, drawn
/// AirPods and case with battery bars, and a Connect button while AirPods are not connected.
/// It is a layered window, so the big rounded corners and the shadow are real per-pixel alpha.
/// </summary>
public sealed class PopupForm : Form
{
    public enum ConnectionState { NotConnected, Connecting, Connected }

    private AirPodsState? _state;
    private string _name = "AirPods";
    private ConnectionState _connection = ConnectionState.Connected;

    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    private float _progress; // 0 = hidden below, 1 = fully shown
    private bool _showing;
    private bool _hoverClose, _hoverButton;
    private RectangleF _closeRect, _buttonRect;

    /// <summary>The user pressed "Connect".</summary>
    public event Action? ConnectRequested;
    /// <summary>The user closed the card with the X.</summary>
    public event Action? Dismissed;

    private float S => DeviceDpi / 96f;
    private int Shadow => (int)(28 * S);
    private const float CardW = 340, Radius = 38;

    public PopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        _anim.Tick += (_, _) => Step();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 /* WS_EX_LAYERED */ | 0x80 /* WS_EX_TOOLWINDOW */ | 0x08000000 /* WS_EX_NOACTIVATE */;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public bool IsShown => _showing;

    public void ShowCard(AirPodsState state, string name, ConnectionState connection)
    {
        _state = state;
        _name = name;
        _connection = connection;
        if (!_showing)
        {
            _showing = true;
            _progress = 0;
            if (!Visible) Show();
            _anim.Start();
        }
        Render();
    }

    public void UpdateCard(AirPodsState state, ConnectionState connection)
    {
        if (!_showing) return;
        _state = state;
        _connection = connection;
        Render();
    }

    public void HideCard()
    {
        if (!_showing) return;
        _showing = false;
        _anim.Start();
    }

    private void Step()
    {
        float target = _showing ? 1 : 0;
        _progress += (target - _progress) * 0.22f;
        if (Math.Abs(target - _progress) < 0.01f)
        {
            _progress = target;
            _anim.Stop();
            if (!_showing) { Hide(); return; }
        }
        Render();
    }

    private float CardH => _connection == ConnectionState.Connected ? 318 : 384;

    // ---------------------------------------------------------- Drawing

    private void Render()
    {
        if (!IsHandleCreated || _state == null) return;
        float s = S;
        int sh = Shadow;
        int w = (int)(CardW * s) + sh * 2, h = (int)(CardH * s) + sh * 2;

        // Slide up from below the bottom edge of the work area with an ease-out.
        var area = Screen.PrimaryScreen!.WorkingArea;
        float ease = 1 - (float)Math.Pow(1 - _progress, 3);
        int finalTop = area.Bottom - h + sh - (int)(24 * s);
        int top = finalTop + (int)((1 - ease) * h * 0.6f);
        var location = new Point(area.Left + (area.Width - w) / 2, top);

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            DrawCard(g, new RectangleF(sh, sh, CardW * s, CardH * s), s);
        }
        UpdateLayered(bmp, location, (byte)(255 * Math.Clamp(ease * 1.3f, 0, 1)));
    }

    private void DrawCard(Graphics g, RectangleF card, float s)
    {
        // Soft shadow: stacked translucent rounded rects.
        for (int i = Shadow; i > 0; i -= 2)
        {
            var r = card;
            r.Inflate(i * 0.6f, i * 0.6f);
            r.Offset(0, i * 0.25f);
            using var p = Theme.RoundedRect(r, Radius * s + i * 0.6f);
            using var b = new SolidBrush(Color.FromArgb((int)(5 * (1 - i / (float)Shadow)) + 1, 0, 0, 0));
            g.FillPath(b, p);
        }
        using (var path = Theme.RoundedRect(card, Radius * s))
        using (var white = new SolidBrush(Color.White))
            g.FillPath(white, path);

        var st = _state!;
        float cx = card.X + card.Width / 2;

        // Title
        using (var font = Theme.SemiboldFont(23, s, display: true))
        using (var brush = new SolidBrush(Theme.Text))
        {
            var sf = new StringFormat(StringFormatFlags.NoWrap)
                { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            g.DrawString(_name, font, brush, new RectangleF(card.X + 50 * s, card.Y + 30 * s, card.Width - 100 * s, 34 * s), sf);
        }

        // Close button
        _closeRect = new RectangleF(card.Right - 50 * s, card.Y + 22 * s, 30 * s, 30 * s);
        using (var bg = new SolidBrush(_hoverClose ? Color.FromArgb(209, 209, 214) : Color.FromArgb(229, 229, 234)))
            g.FillEllipse(bg, _closeRect);
        using (var pen = new Pen(Color.FromArgb(110, 110, 115), 2.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float c = 5.5f * s, ccx = _closeRect.X + _closeRect.Width / 2, ccy = _closeRect.Y + _closeRect.Height / 2;
            g.DrawLine(pen, ccx - c, ccy - c, ccx + c, ccy + c);
            g.DrawLine(pen, ccx + c, ccy - c, ccx - c, ccy + c);
        }

        // Products: one battery for the pair when both pods agree (like iOS), separate L/R otherwise.
        float imgY = card.Y + 160 * s, barY = card.Y + 252 * s;
        bool together = st.LeftBattery is int l && st.RightBattery is int r2 && Math.Abs(l - r2) <= 10
                        && st.LeftCharging == st.RightCharging;
        bool isMax = st.Model.StartsWith("AirPods Max", StringComparison.Ordinal);
        if (isMax)
        {
            DrawHeadphones(g, cx, imgY, s);
            DrawBattery(g, cx, barY, st.LeftBattery, st.LeftCharging, null, s);
        }
        else
        {
            float podsX = card.X + card.Width * 0.30f, caseX = card.X + card.Width * 0.71f;
            if (together)
            {
                DrawPod(g, podsX - 21 * s, imgY, s, left: true);
                DrawPod(g, podsX + 21 * s, imgY, s, left: false);
                DrawBattery(g, podsX, barY, Math.Min(st.LeftBattery!.Value, st.RightBattery!.Value), st.LeftCharging, null, s);
            }
            else
            {
                DrawPod(g, podsX - 32 * s, imgY, s, left: true);
                DrawPod(g, podsX + 32 * s, imgY, s, left: false);
                DrawBattery(g, podsX - 36 * s, barY, st.LeftBattery, st.LeftCharging, "Л", s);
                DrawBattery(g, podsX + 36 * s, barY, st.RightBattery, st.RightCharging, "П", s);
            }
            DrawCase(g, caseX, imgY, s, st.CaseCharging);
            DrawBattery(g, caseX, barY, st.CaseBattery, st.CaseCharging, null, s);
        }

        // Connect button
        _buttonRect = RectangleF.Empty;
        if (_connection != ConnectionState.Connected)
        {
            _buttonRect = new RectangleF(card.X + 24 * s, card.Bottom - 80 * s, card.Width - 48 * s, 52 * s);
            bool busy = _connection == ConnectionState.Connecting;
            var color = busy ? Color.FromArgb(120, Theme.Blue) : _hoverButton ? Color.FromArgb(0, 104, 217) : Theme.Blue;
            using (var p = Theme.RoundedRect(_buttonRect, 14 * s))
            using (var b = new SolidBrush(color))
                g.FillPath(b, p);
            using var font = Theme.SemiboldFont(17, s);
            using var white = new SolidBrush(Color.White);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(busy ? "Підключення…" : "Підключити", font, white, _buttonRect, sf);
        }
    }

    /// <summary>iOS battery glyph with the percentage under it.</summary>
    private static void DrawBattery(Graphics g, float cx, float y, int? level, bool charging, string? label, float s)
    {
        float w = 27 * s, h = 13 * s;
        var body = new RectangleF(cx - w / 2 - 1.5f * s, y, w, h);
        using (var p = Theme.RoundedRect(body, 3.5f * s))
        using (var pen = new Pen(Color.FromArgb(170, 170, 175), 1.2f * s))
            g.DrawPath(pen, p);
        using (var nub = new SolidBrush(Color.FromArgb(170, 170, 175)))
            g.FillRectangle(nub, body.Right + 1 * s, y + h * 0.32f, 2 * s, h * 0.36f);
        if (level is int lv)
        {
            var fill = new RectangleF(body.X + 2 * s, body.Y + 2 * s, (body.Width - 4 * s) * Math.Max(0.06f, lv / 100f), body.Height - 4 * s);
            using var p = Theme.RoundedRect(fill, 2 * s);
            using var b = new SolidBrush(lv <= 20 && !charging ? Theme.Red : Theme.Green);
            g.FillPath(b, p);
        }
        if (charging)
        {
            float bx = body.X + body.Width / 2, by = body.Y + body.Height / 2;
            PointF[] bolt =
            {
                new(bx + 2 * s, by - 7.5f * s), new(bx - 4.5f * s, by + 1 * s), new(bx - 0.3f * s, by + 1 * s),
                new(bx - 2 * s, by + 7.5f * s), new(bx + 4.5f * s, by - 1 * s), new(bx + 0.3f * s, by - 1 * s),
            };
            using var outline = new Pen(Color.White, 2f * s) { LineJoin = LineJoin.Round };
            g.DrawPolygon(outline, bolt);
            using var black = new SolidBrush(Color.FromArgb(40, 40, 40));
            g.FillPolygon(black, bolt);
        }
        using var font = Theme.Font(15, s);
        using var brush = new SolidBrush(level is null ? Theme.SubText : Color.FromArgb(60, 60, 67));
        var sf = new StringFormat { Alignment = StringAlignment.Center };
        string text = (label != null ? label + " " : "") + (level is int v ? $"{v}%" : "—");
        g.DrawString(text, font, brush, new RectangleF(cx - 50 * s, y + h + 5 * s, 100 * s, 22 * s), sf);
    }

    /// <summary>Stylised AirPods Pro bud: glossy head with a silicone tip and a stem.</summary>
    private static void DrawPod(Graphics g, float cx, float cy, float s, bool left)
    {
        var state = g.Save();
        g.TranslateTransform(cx, cy);
        if (!left) g.ScaleTransform(-1, 1);
        g.RotateTransform(8);

        var stem = new RectangleF(-1 * s, -6 * s, 12 * s, 54 * s);
        var head = new RectangleF(-20 * s, -36 * s, 34 * s, 33 * s);
        var tip = new RectangleF(-27 * s, -30 * s, 17 * s, 24 * s);

        // contact shadow
        using (var shadow = new SolidBrush(Color.FromArgb(25, 0, 0, 0)))
            g.FillEllipse(shadow, -10 * s, 46 * s, 26 * s, 6 * s);

        using (var stemPath = Theme.RoundedRect(stem, 6 * s))
        using (var stemBrush = new LinearGradientBrush(stem, Color.White, Color.FromArgb(214, 214, 220), LinearGradientMode.Horizontal))
        using (var edge = new Pen(Color.FromArgb(205, 205, 212), 1f * s))
        {
            g.FillPath(stemBrush, stemPath);
            g.DrawPath(edge, stemPath);
        }
        using (var headPath = new GraphicsPath())
        {
            headPath.AddEllipse(head);
            using var headBrush = new PathGradientBrush(headPath)
            {
                CenterColor = Color.White,
                CenterPoint = new PointF(head.X + head.Width * 0.62f, head.Y + head.Height * 0.35f),
                SurroundColors = new[] { Color.FromArgb(206, 206, 213) },
            };
            g.FillPath(headBrush, headPath);
            using var edge = new Pen(Color.FromArgb(200, 200, 207), 1f * s);
            g.DrawPath(edge, headPath);
        }
        // silicone tip and its dark mesh
        using (var tipBrush = new LinearGradientBrush(tip, Color.FromArgb(236, 236, 240), Color.FromArgb(196, 196, 202), LinearGradientMode.Horizontal))
            g.FillEllipse(tipBrush, tip);
        using (var mesh = new SolidBrush(Color.FromArgb(120, 120, 126)))
            g.FillEllipse(mesh, tip.X + 3 * s, tip.Y + 6 * s, 7 * s, 12 * s);
        // sensor dot
        using (var dot = new SolidBrush(Color.FromArgb(60, 60, 66)))
            g.FillEllipse(dot, 2 * s, -22 * s, 4 * s, 4 * s);
        g.Restore(state);
    }

    /// <summary>Stylised charging case, slightly open-looking like the iOS pop-up.</summary>
    private static void DrawCase(Graphics g, float cx, float cy, float s, bool charging)
    {
        var body = new RectangleF(cx - 64 * s, cy - 48 * s, 128 * s, 98 * s);
        using (var shadow = new SolidBrush(Color.FromArgb(28, 0, 0, 0)))
            g.FillEllipse(shadow, body.X + 10 * s, body.Bottom - 4 * s, body.Width - 20 * s, 10 * s);
        using (var path = Theme.RoundedRect(body, 30 * s))
        {
            using var brush = new LinearGradientBrush(body, Color.White, Color.FromArgb(222, 222, 228), 70f);
            g.FillPath(brush, path);
            using var edge = new Pen(Color.FromArgb(208, 208, 214), 1.2f * s);
            g.DrawPath(edge, path);
        }
        // lid seam with a soft highlight under it
        float seamY = body.Y + body.Height * 0.36f;
        using (var seam = new Pen(Color.FromArgb(196, 196, 203), 1.4f * s))
            g.DrawLine(seam, body.X + 2 * s, seamY, body.Right - 2 * s, seamY);
        using (var light = new Pen(Color.FromArgb(180, 255, 255, 255), 1.2f * s))
            g.DrawLine(light, body.X + 4 * s, seamY + 1.6f * s, body.Right - 4 * s, seamY + 1.6f * s);
        // status light
        using var led = new SolidBrush(charging ? Theme.Green : Color.FromArgb(150, 150, 156));
        g.FillEllipse(led, cx - 2.5f * s, seamY + 11 * s, 5 * s, 5 * s);
    }

    private static void DrawHeadphones(Graphics g, float cx, float cy, float s)
    {
        using var band = new Pen(Color.FromArgb(200, 200, 206), 9 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(band, cx - 50 * s, cy - 60 * s, 100 * s, 100 * s, 190, 160);
        foreach (int dir in new[] { -1, 1 })
        {
            var cup = new RectangleF(cx + dir * 44 * s - 18 * s, cy - 10 * s, 36 * s, 56 * s);
            using var p = Theme.RoundedRect(cup, 14 * s);
            using var b = new LinearGradientBrush(cup, Color.White, Color.FromArgb(212, 212, 218), LinearGradientMode.Horizontal);
            g.FillPath(b, p);
            using var edge = new Pen(Color.FromArgb(200, 200, 207), 1f * s);
            g.DrawPath(edge, p);
        }
    }

    // ------------------------------------------------------------ Input

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool close = _closeRect.Contains(e.Location), button = _buttonRect.Contains(e.Location);
        Cursor = close || (button && _connection == ConnectionState.NotConnected) ? Cursors.Hand : Cursors.Default;
        if (close != _hoverClose || button != _hoverButton)
        {
            _hoverClose = close;
            _hoverButton = button;
            Render();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverClose = _hoverButton = false;
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_closeRect.Contains(e.Location))
        {
            HideCard();
            Dismissed?.Invoke();
        }
        else if (_buttonRect.Contains(e.Location) && _connection == ConnectionState.NotConnected)
        {
            ConnectRequested?.Invoke();
        }
    }

    // ------------------------------------------------------ Layered window

    private void UpdateLayered(Bitmap bmp, Point location, byte opacity)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBitmap);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var dst = new POINT { x = location.X, y = location.Y };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size,
        IntPtr srcDc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
}

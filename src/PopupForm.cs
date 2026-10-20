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
        float imgY = card.Y + 150 * s, barY = card.Y + 252 * s;
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
            float podsX = card.X + card.Width * 0.29f, caseX = card.X + card.Width * 0.71f;
            if (together)
            {
                DrawPod(g, podsX - 31 * s, imgY, s, left: true);
                DrawPod(g, podsX + 31 * s, imgY, s, left: false);
                DrawBattery(g, podsX, barY, Math.Min(st.LeftBattery!.Value, st.RightBattery!.Value), st.LeftCharging, null, s);
            }
            else
            {
                DrawPod(g, podsX - 33 * s, imgY, s, left: true);
                DrawPod(g, podsX + 33 * s, imgY, s, left: false);
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

    /// <summary>
    /// AirPods Pro bud seen from the front (our own vector drawing): glossy head with the black
    /// sensor grille, a silicone ear tip facing inwards, a stem with the L/R mark and a silver mic cap.
    /// </summary>
    private static void DrawPod(Graphics g, float cx, float cy, float s, bool left)
    {
        // contact shadow
        using (var shadow = new SolidBrush(Color.FromArgb(22, 0, 0, 0)))
            g.FillEllipse(shadow, cx - 16 * s, cy + 50 * s, 30 * s, 6 * s);

        var state = g.Save();
        g.TranslateTransform(cx, cy);
        g.ScaleTransform(s, s);
        if (!left) g.ScaleTransform(-1, 1);

        // stem
        var stem = new RectangleF(-10, -10, 14, 60);
        using (var stemPath = Theme.RoundedRect(stem, 7))
        {
            using (var b = new LinearGradientBrush(stem, Color.Empty, Color.Empty, LinearGradientMode.Horizontal))
            {
                b.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(214, 214, 220), Color.White, Color.FromArgb(236, 236, 240), Color.FromArgb(200, 200, 207) },
                    Positions = new[] { 0f, 0.35f, 0.7f, 1f },
                };
                g.FillPath(b, stemPath);
            }
            // silver microphone cap at the bottom of the stem
            var clip = g.Clip;
            g.SetClip(stemPath, CombineMode.Intersect);
            var cap = new RectangleF(stem.X, stem.Bottom - 6, stem.Width, 6);
            using (var silver = new LinearGradientBrush(cap, Color.FromArgb(150, 150, 156), Color.FromArgb(225, 225, 230), LinearGradientMode.Horizontal))
                g.FillRectangle(silver, cap);
            g.Clip = clip;
            using var edge = new Pen(Color.FromArgb(205, 205, 212), 0.8f);
            g.DrawPath(edge, stemPath);
        }

        // head
        var head = new RectangleF(-27, -55, 50, 48);
        using (var headPath = new GraphicsPath())
        {
            headPath.AddEllipse(head);
            using var b = new PathGradientBrush(headPath)
            {
                CenterColor = Color.White,
                CenterPoint = new PointF(head.X + head.Width * 0.38f, head.Y + head.Height * 0.32f),
                SurroundColors = new[] { Color.FromArgb(212, 212, 219) },
                FocusScales = new PointF(0.45f, 0.45f),
            };
            g.FillPath(b, headPath);
            using var edge = new Pen(Color.FromArgb(206, 206, 213), 0.8f);
            g.DrawPath(edge, headPath);
        }

        // black sensor grille
        var grille = g.Save();
        g.TranslateTransform(-11, -43);
        g.RotateTransform(-38);
        using (var path = Theme.RoundedRect(new RectangleF(-9, -3.6f, 18, 7.2f), 3.6f))
        using (var black = new SolidBrush(Color.FromArgb(28, 28, 30)))
            g.FillPath(black, path);
        g.Restore(grille);

        // silicone ear tip facing inwards
        var tip = new RectangleF(1, -37, 30, 30);
        using (var tipPath = new GraphicsPath())
        {
            tipPath.AddEllipse(tip);
            using var b = new PathGradientBrush(tipPath)
            {
                CenterColor = Color.FromArgb(250, 250, 252),
                CenterPoint = new PointF(tip.X + tip.Width * 0.4f, tip.Y + tip.Height * 0.35f),
                SurroundColors = new[] { Color.FromArgb(214, 214, 220) },
            };
            g.FillPath(b, tipPath);
            using var edge = new Pen(Color.FromArgb(200, 200, 207), 0.8f);
            g.DrawPath(edge, tipPath);
        }
        var hole = new RectangleF(13, -26, 11, 10);
        using (var b = new LinearGradientBrush(hole, Color.FromArgb(105, 105, 112), Color.FromArgb(160, 160, 166), 60f))
            g.FillEllipse(b, hole);
        g.Restore(state);

        // L / R mark on the stem (drawn unmirrored)
        using var font = Theme.Font(10, s);
        using var mark = new SolidBrush(Color.FromArgb(196, 196, 202));
        var sf = new StringFormat { Alignment = StringAlignment.Center };
        float markX = left ? cx - 3 * s : cx + 3 * s;
        g.DrawString(left ? "L" : "R", font, mark, new RectangleF(markX - 8 * s, cy + 22 * s, 16 * s, 14 * s), sf);
    }

    /// <summary>Charging case seen from the front (our own vector drawing), with the lid seam and status light.</summary>
    private static void DrawCase(Graphics g, float cx, float cy, float s, bool charging)
    {
        var body = new RectangleF(cx - 64 * s, cy - 44 * s, 128 * s, 100 * s);
        using (var shadow = new SolidBrush(Color.FromArgb(24, 0, 0, 0)))
            g.FillEllipse(shadow, body.X + 8 * s, body.Bottom - 3 * s, body.Width - 16 * s, 10 * s);

        // softer top corners, rounder bottom corners like the real case
        float rt = 22 * s, rb = 38 * s;
        using var path = new GraphicsPath();
        path.AddArc(body.X, body.Y, rt * 2, rt * 2, 180, 90);
        path.AddArc(body.Right - rt * 2, body.Y, rt * 2, rt * 2, 270, 90);
        path.AddArc(body.Right - rb * 2, body.Bottom - rb * 2, rb * 2, rb * 2, 0, 90);
        path.AddArc(body.X, body.Bottom - rb * 2, rb * 2, rb * 2, 90, 90);
        path.CloseFigure();

        using (var b = new LinearGradientBrush(body, Color.Empty, Color.Empty, LinearGradientMode.Vertical))
        {
            b.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.FromArgb(244, 244, 247), Color.White, Color.FromArgb(246, 246, 249), Color.FromArgb(224, 224, 230) },
                Positions = new[] { 0f, 0.3f, 0.7f, 1f },
            };
            g.FillPath(b, path);
        }
        // side shading for volume
        using (var side = new LinearGradientBrush(body, Color.FromArgb(28, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), LinearGradientMode.Horizontal))
        {
            side.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.FromArgb(26, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(26, 0, 0, 0) },
                Positions = new[] { 0f, 0.18f, 0.82f, 1f },
            };
            g.FillPath(side, path);
        }
        using (var edge = new Pen(Color.FromArgb(210, 210, 216), 1f * s))
            g.DrawPath(edge, path);

        // lid seam with a highlight under it
        float seamY = body.Y + body.Height * 0.3f;
        using (var seam = new Pen(Color.FromArgb(200, 200, 207), 1.3f * s))
            g.DrawLine(seam, body.X + 1.5f * s, seamY, body.Right - 1.5f * s, seamY);
        using (var light = new Pen(Color.White, 1.4f * s))
            g.DrawLine(light, body.X + 3 * s, seamY + 1.5f * s, body.Right - 3 * s, seamY + 1.5f * s);

        // status light
        var led = new RectangleF(cx - 3 * s, body.Y + body.Height * 0.55f, 6 * s, 6 * s);
        using (var glow = new SolidBrush(Color.FromArgb(charging ? 60 : 0, Theme.Green)))
            g.FillEllipse(glow, RectangleF.Inflate(led, 3 * s, 3 * s));
        using (var dot = new SolidBrush(charging ? Theme.Green : Color.FromArgb(160, 160, 166)))
            g.FillEllipse(dot, led);
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

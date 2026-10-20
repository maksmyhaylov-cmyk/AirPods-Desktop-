using System.Drawing.Drawing2D;

namespace AirPodsWin;

/// <summary>iOS-style switch with a sliding knob.</summary>
public sealed class ToggleSwitch : Control
{
    private bool _checked;
    private float _pos; // 0 = off, 1 = on (animated)
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };

    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        _anim.Tick += (_, _) =>
        {
            float target = _checked ? 1 : 0;
            _pos += Math.Sign(target - _pos) * 0.2f;
            if (Math.Abs(target - _pos) < 0.2f) { _pos = target; _anim.Stop(); }
            Invalidate();
        };
    }

    public static Size PreferredFor(float scale) => new((int)(51 * scale), (int)(31 * scale));

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            if (IsHandleCreated && Visible) _anim.Start(); else _pos = value ? 1 : 0;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Sets the value without raising <see cref="CheckedChanged"/>.</summary>
    public void SetSilently(bool value)
    {
        _checked = value;
        _pos = value ? 1 : 0;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        Checked = !Checked;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) Checked = !Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var track = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.RoundedRect(track, track.Height / 2))
        using (var brush = new SolidBrush(Lerp(Theme.SwitchOff, Theme.Green, _pos)))
            g.FillPath(brush, path);

        float inset = 2f * DeviceDpi / 96f;
        float knob = track.Height - inset * 2;
        float x = track.X + inset + (track.Width - knob - inset * 2) * _pos;
        using (var shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
            g.FillEllipse(shadow, x, track.Y + inset + 1.2f, knob, knob);
        using (var white = new SolidBrush(Color.White))
            g.FillEllipse(white, x, track.Y + inset, knob, knob);

        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Theme.Blue, 1.5f);
            using var path = Theme.RoundedRect(track, track.Height / 2);
            g.DrawPath(focus, path);
        }
    }

    private static Color Lerp(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Rounded "inset grouped" card: rows with a title, optional subtitle and a switch or an action.</summary>
public sealed class GroupCard : Control
{
    private sealed class Row
    {
        public required string Title;
        public string? Subtitle;
        public ToggleSwitch? Toggle;
        public Action? Click;
        public RectangleF Bounds;
    }

    private readonly List<Row> _rows = new();
    private int _hover = -1, _pressed = -1;

    private float S => DeviceDpi / 96f;
    private const float Radius = 12, Pad = 16;

    public GroupCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public ToggleSwitch AddToggle(string title, string? subtitle, bool value, Action<bool> onChange)
    {
        var toggle = new ToggleSwitch();
        toggle.SetSilently(value);
        toggle.CheckedChanged += (_, _) => onChange(toggle.Checked);
        Controls.Add(toggle);
        _rows.Add(new Row { Title = title, Subtitle = subtitle, Toggle = toggle });
        return toggle;
    }

    public void AddAction(string title, Action onClick) =>
        _rows.Add(new Row { Title = title, Click = onClick });

    /// <summary>Lays rows out for the given width and sets the card height.</summary>
    public void LayoutRows(int width)
    {
        float s = S, y = 0;
        foreach (var row in _rows)
        {
            float h = (row.Subtitle == null ? 46 : 60) * s;
            row.Bounds = new RectangleF(0, y, width, h);
            if (row.Toggle != null)
            {
                row.Toggle.Size = ToggleSwitch.PreferredFor(s);
                row.Toggle.Location = new Point((int)(width - Pad * s - row.Toggle.Width), (int)(y + (h - row.Toggle.Height) / 2));
            }
            y += h;
        }
        Size = new Size(width, (int)Math.Ceiling(y));
        Invalidate();
    }

    public void ApplyTheme()
    {
        foreach (var row in _rows) row.Toggle?.Invalidate();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = S;
        g.Clear(Theme.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using var card = Theme.RoundedRect(new RectangleF(0, 0, Width - 0.5f, Height - 0.5f), Radius * s);
        using (var brush = new SolidBrush(Theme.Card)) g.FillPath(brush, card);

        using var titleFont = Theme.Font(15, s);
        using var subFont = Theme.Font(12.5f, s);
        using var textBrush = new SolidBrush(Theme.Text);
        using var blueBrush = new SolidBrush(Theme.Blue);
        using var subBrush = new SolidBrush(Theme.SubText);
        using var sepPen = new Pen(Theme.Separator, Math.Max(1f, 0.5f * s));

        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var b = row.Bounds;

            if (row.Click != null && (i == _pressed || i == _hover))
            {
                var state = g.Save();
                g.SetClip(card);
                using var hl = new SolidBrush(i == _pressed ? Theme.CardPressed : Theme.Hover);
                g.FillRectangle(hl, b);
                g.Restore(state);
            }

            float left = Pad * s;
            float right = row.Toggle != null ? row.Toggle.Left - 8 * s : b.Right - Pad * s;
            var titleSize = g.MeasureString(row.Title, titleFont);
            float contentH = titleSize.Height + (row.Subtitle != null ? 17 * s : 0);
            float ty = b.Y + (b.Height - contentH) / 2;
            var sfTitle = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
            g.DrawString(row.Title, titleFont, row.Click != null ? blueBrush : textBrush,
                new RectangleF(left, ty, right - left, titleSize.Height), sfTitle);
            if (row.Subtitle != null)
                g.DrawString(row.Subtitle, subFont, subBrush,
                    new RectangleF(left, ty + titleSize.Height - 1 * s, right - left, 18 * s), sfTitle);

            if (i < _rows.Count - 1)
                g.DrawLine(sepPen, left, b.Bottom - 0.5f, b.Right, b.Bottom - 0.5f);
        }
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].Click != null && _rows[i].Bounds.Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitTest(e.Location);
        Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        if (hit != _hover) { _hover = hit; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = _pressed = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = HitTest(e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int hit = HitTest(e.Location);
        int pressed = _pressed;
        _pressed = -1;
        Invalidate();
        if (hit >= 0 && hit == pressed) _rows[hit].Click!();
    }
}

/// <summary>Round gray icon button (Segoe Fluent/MDL2 glyph), like the small buttons on iOS cards.</summary>
public sealed class IconButton : Control
{
    private bool _hover;
    public string Glyph { get; set; } = "";

    public IconButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var bg = new SolidBrush(_hover ? Theme.CardPressed : Theme.Back))
            g.FillEllipse(bg, 0, 0, Width - 1, Height - 1);
        string family = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        using var font = new Font(family, Height * 0.4f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(_hover ? Theme.Text : Color.FromArgb(99, 99, 102));
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Glyph, font, brush, new RectangleF(0, 1, Width, Height), sf);
    }
}

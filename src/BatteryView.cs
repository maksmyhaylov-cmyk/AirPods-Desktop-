using System.Drawing.Drawing2D;

namespace AirPodsWin;

/// <summary>Round battery gauge with a caption, e.g. "Ліва 80%".</summary>
public sealed class BatteryView : Control
{
    private int? _level;
    private bool _charging;
    private bool _active;

    public string Caption { get; set; } = "";

    public BatteryView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(110, 130);
        BackColor = Color.Transparent;
    }

    /// <param name="active">Highlight state, e.g. pod is in the ear or case lid is open.</param>
    public void SetState(int? level, bool charging, bool active)
    {
        if (_level == level && _charging == charging && _active == active) return;
        _level = level;
        _charging = charging;
        _active = active;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int d = Math.Min(Width - 10, Height - 40);
        var ring = new Rectangle((Width - d) / 2, 4, d, d);

        using (var bg = new Pen(Theme.Track, 8))
            g.DrawEllipse(bg, ring);

        if (_level is int lvl)
        {
            Color c = _charging ? Theme.Charging : lvl <= 20 ? Theme.Low : Theme.Accent;
            using var fg = new Pen(c, 8) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float sweep = Math.Max(2f, 360f * lvl / 100f);
            g.DrawArc(fg, ring, -90, sweep);
        }

        string text = _level is int v ? $"{v}%" : "—";
        using (var font = new Font("Segoe UI Semibold", 14f))
        using (var brush = new SolidBrush(Theme.Text))
        {
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, brush, ring, sf);
        }

        if (_charging)
        {
            using var font = new Font("Segoe UI Symbol", 10f);
            using var brush = new SolidBrush(Theme.Charging);
            var sf = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString("⚡", font, brush, new RectangleF(ring.X, ring.Y + ring.Height * 0.68f, ring.Width, 20), sf);
        }

        using (var font = new Font("Segoe UI", 10f, _active ? FontStyle.Bold : FontStyle.Regular))
        using (var brush = new SolidBrush(_active ? Theme.Text : Theme.SubText))
        {
            var sf = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(Caption, font, brush, new RectangleF(0, ring.Bottom + 8, Width, 24), sf);
        }
    }
}

public static class Theme
{
    public static readonly Color Back = Color.FromArgb(32, 32, 36);
    public static readonly Color Panel = Color.FromArgb(44, 44, 50);
    public static readonly Color Track = Color.FromArgb(62, 62, 70);
    public static readonly Color Text = Color.FromArgb(240, 240, 245);
    public static readonly Color SubText = Color.FromArgb(160, 160, 170);
    public static readonly Color Accent = Color.FromArgb(52, 199, 89);
    public static readonly Color Charging = Color.FromArgb(255, 204, 0);
    public static readonly Color Low = Color.FromArgb(255, 69, 58);
    public static readonly Color Button = Color.FromArgb(10, 132, 255);
}

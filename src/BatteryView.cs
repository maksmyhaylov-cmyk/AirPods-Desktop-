using System.Drawing.Drawing2D;

namespace AirPodsWin;

public enum BatteryGlyph { LeftPod, RightPod, Case, Headphones }

/// <summary>
/// iPhone battery-widget style gauge: a thin ring with the device drawn inside,
/// the percentage below and a caption under it.
/// </summary>
public sealed class BatteryView : Control
{
    private int? _level;
    private bool _charging;
    private bool _active;

    public string Caption { get; set; } = "";
    public BatteryGlyph Glyph { get; set; }

    private float S => DeviceDpi / 96f;

    public BatteryView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public static Size PreferredFor(float scale) => new((int)(104 * scale), (int)(124 * scale));

    /// <param name="active">Pod is in the ear / case lid is open: shown with a dot next to the caption.</param>
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
        g.Clear(Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        float s = S;
        float d = 62 * s, stroke = 5.5f * s;
        var ring = new RectangleF((Width - d) / 2f, 4 * s, d, d);

        using (var track = new Pen(Theme.Track, stroke))
            g.DrawEllipse(track, ring);

        if (_level is int lvl)
        {
            Color c = _charging || lvl > 20 ? Theme.Green : Theme.Red;
            using var pen = new Pen(c, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, ring, -90, Math.Max(1f, 360f * lvl / 100f));
        }

        DrawGlyph(g, ring, s, _level is null ? Theme.SubText : Theme.Text);

        if (_charging) DrawBolt(g, ring, s);

        float y = ring.Bottom + 8 * s;
        using (var font = Theme.SemiboldFont(17, s, display: true))
        using (var brush = new SolidBrush(_level is null ? Theme.SubText : Theme.Text))
        {
            var sf = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(_level is int v ? $"{v}%" : "—", font, brush, new RectangleF(0, y, Width, 24 * s), sf);
        }

        y += 24 * s;
        using (var font = Theme.Font(12.5f, s))
        using (var brush = new SolidBrush(Theme.SubText))
        {
            var size = g.MeasureString(Caption, font);
            float dot = _active ? 6 * s : 0, gap = _active ? 5 * s : 0;
            float x = (Width - size.Width - dot - gap) / 2f;
            if (_active)
            {
                using var green = new SolidBrush(Theme.Green);
                g.FillEllipse(green, x, y + (size.Height - dot) / 2f, dot, dot);
            }
            g.DrawString(Caption, font, brush, x + dot + gap, y);
        }
    }

    private void DrawGlyph(Graphics g, RectangleF ring, float s, Color color)
    {
        float cx = ring.X + ring.Width / 2f, cy = ring.Y + ring.Height / 2f;
        using var brush = new SolidBrush(color);
        using var pen = new Pen(color, 1.6f * s);
        switch (Glyph)
        {
            case BatteryGlyph.LeftPod:
            case BatteryGlyph.RightPod:
            {
                // AirPods Pro bud: rounded head with a short stem pointing down and inwards.
                float dir = Glyph == BatteryGlyph.LeftPod ? 1 : -1;
                var state = g.Save();
                g.TranslateTransform(cx, cy);
                g.RotateTransform(-12 * dir);
                using (var head = Theme.RoundedRect(new RectangleF(-8 * s, -13 * s, 16 * s, 15 * s), 7 * s))
                    g.FillPath(brush, head);
                using (var stem = Theme.RoundedRect(new RectangleF(dir > 0 ? 1 * s : -6 * s, -2 * s, 5 * s, 16 * s), 2.5f * s))
                    g.FillPath(brush, stem);
                g.Restore(state);
                break;
            }
            case BatteryGlyph.Case:
            {
                var body = new RectangleF(cx - 14 * s, cy - 10 * s, 28 * s, 21 * s);
                using (var path = Theme.RoundedRect(body, 6 * s))
                    g.DrawPath(pen, path);
                g.DrawLine(pen, body.X + 1 * s, body.Y + 7 * s, body.Right - 1 * s, body.Y + 7 * s);
                g.FillEllipse(brush, cx - 1.5f * s, body.Y + 12 * s, 3 * s, 3 * s);
                break;
            }
            case BatteryGlyph.Headphones:
            {
                g.DrawArc(pen, cx - 11 * s, cy - 12 * s, 22 * s, 22 * s, 180, 180);
                using (var l = Theme.RoundedRect(new RectangleF(cx - 13 * s, cy - 2 * s, 7 * s, 12 * s), 3 * s)) g.FillPath(brush, l);
                using (var r = Theme.RoundedRect(new RectangleF(cx + 6 * s, cy - 2 * s, 7 * s, 12 * s), 3 * s)) g.FillPath(brush, r);
                break;
            }
        }
    }

    /// <summary>Small green bolt badge on the top-right of the ring, like the iOS widget.</summary>
    private static void DrawBolt(Graphics g, RectangleF ring, float s)
    {
        float r = 10 * s;
        float bx = ring.Right - r * 1.2f, by = ring.Y - r * 0.2f;
        using (var bg = new SolidBrush(Theme.Card)) g.FillEllipse(bg, bx - 2 * s, by - 2 * s, 2 * r + 4 * s, 2 * r + 4 * s);
        using (var green = new SolidBrush(Theme.Green)) g.FillEllipse(green, bx, by, 2 * r, 2 * r);
        float cx = bx + r, cy = by + r;
        PointF[] bolt =
        {
            new(cx + 1.5f * s, cy - 6 * s), new(cx - 4 * s, cy + 1 * s), new(cx - 0.3f * s, cy + 1 * s),
            new(cx - 1.5f * s, cy + 6 * s), new(cx + 4 * s, cy - 1 * s), new(cx + 0.3f * s, cy - 1 * s),
        };
        using var white = new SolidBrush(Color.White);
        g.FillPolygon(white, bolt);
    }
}

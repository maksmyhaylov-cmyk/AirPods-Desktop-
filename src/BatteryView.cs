using System.Drawing.Drawing2D;

namespace AirPodsWin;

public enum BatteryGlyph { LeftPod, RightPod, Case, Headphones }

/// <summary>
/// Gauge in the style of the iOS "Batteries" widget: a thick ring, a monochrome device glyph
/// inside, a bolt that cuts the top of the ring while charging, the percentage below.
/// The glyphs are drawn here (Apple's SF Symbols may not be shipped outside Apple platforms).
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
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        float s = S;
        float stroke = 7 * s, d = 60 * s;
        var ring = new RectangleF((Width - d) / 2f, 6 * s, d, d);

        using (var track = new Pen(Theme.Track, stroke))
            g.DrawEllipse(track, ring);
        if (_level is int lvl)
        {
            Color c = _charging || lvl > 20 ? Theme.Green : Theme.Red;
            using var pen = new Pen(c, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, ring, -90, Math.Max(1f, 360f * lvl / 100f));
        }
        if (_charging) DrawBolt(g, ring, stroke, s);

        var inner = RectangleF.Inflate(ring, -stroke * 1.4f, -stroke * 1.4f);
        DrawGlyph(g, inner, _level is null ? Theme.SubText : Color.FromArgb(28, 28, 30));

        float y = ring.Bottom + 9 * s;
        using (var font = Theme.SemiboldFont(17, s, display: true))
        using (var brush = new SolidBrush(_level is null ? Theme.SubText : Theme.Text))
        {
            var sf = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(_level is int v ? $"{v}%" : "—", font, brush, new RectangleF(0, y, Width, 24 * s), sf);
        }

        y += 23 * s;
        using (var font = Theme.Font(12, s))
        using (var brush = new SolidBrush(Theme.SubText))
        {
            var size = g.MeasureString(Caption, font);
            float dot = _active ? 6 * s : 0, gap = _active ? 4 * s : 0;
            float x = (Width - size.Width - dot - gap) / 2f;
            if (_active)
            {
                using var green = new SolidBrush(Theme.Green);
                g.FillEllipse(green, x, y + (size.Height - dot) / 2f, dot, dot);
            }
            g.DrawString(Caption, font, brush, x + dot + gap, y);
        }
    }

    /// <summary>Monochrome glyphs fitted into <paramref name="box"/> (the inside of the ring).</summary>
    private void DrawGlyph(Graphics g, RectangleF box, Color color)
    {
        string asset = Glyph switch
        {
            BatteryGlyph.LeftPod => Art.GlyphLeft,
            BatteryGlyph.RightPod => Art.GlyphRight,
            BatteryGlyph.Case => Art.GlyphCase,
            _ => Art.GlyphHeadphones,
        };
        if (Art.Get(asset) is { } image)
        {
            Art.DrawFitted(g, image, box, _level is null ? 0.35f : 1f);
            return;
        }

        float u = box.Width / 32f; // glyphs are designed on a 32x32 grid
        var state = g.Save();
        g.TranslateTransform(box.X, box.Y);
        g.ScaleTransform(u, u);
        using var fill = new SolidBrush(color);
        using var tipBrush = new SolidBrush(Color.FromArgb(142, 142, 147));
        switch (Glyph)
        {
            case BatteryGlyph.LeftPod:
            case BatteryGlyph.RightPod:
                // AirPods Pro bud seen from the front: round head, ear tip to the inside, straight stem.
                if (Glyph == BatteryGlyph.RightPod) { g.TranslateTransform(32, 0); g.ScaleTransform(-1, 1); }
                g.FillEllipse(fill, 7.5f, 4.5f, 13, 13);
                using (var stem = Theme.RoundedRect(new RectangleF(8.6f, 12, 4.4f, 16.5f), 2.2f))
                    g.FillPath(fill, stem);
                g.FillEllipse(tipBrush, 16.5f, 9.5f, 9, 9);
                using (var hole = new SolidBrush(Theme.Card))
                    g.FillEllipse(hole, 19.6f, 12.6f, 2.8f, 2.8f);
                break;

            case BatteryGlyph.Case:
            {
                // Charging case: filled rounded body, lid seam and status light cut out.
                using (var body = Theme.RoundedRect(new RectangleF(3, 7.5f, 26, 19), 6.5f))
                    g.FillPath(fill, body);
                using (var cut = new Pen(Theme.Card, 1.3f))
                    g.DrawLine(cut, 3, 13.5f, 29, 13.5f);
                using (var cutBrush = new SolidBrush(Theme.Card))
                    g.FillEllipse(cutBrush, 15, 17.5f, 2.2f, 2.2f);
                break;
            }

            case BatteryGlyph.Headphones:
            {
                using var band = new Pen(color, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(band, 6, 5, 20, 20, 180, 180);
                using (var l = Theme.RoundedRect(new RectangleF(3.5f, 14, 7, 12), 3)) g.FillPath(fill, l);
                using (var r = Theme.RoundedRect(new RectangleF(21.5f, 14, 7, 12), 3)) g.FillPath(fill, r);
                break;
            }
        }
        g.Restore(state);
    }

    /// <summary>Green bolt sitting on the top of the ring, with a gap cut into the ring like iOS.</summary>
    private static void DrawBolt(Graphics g, RectangleF ring, float stroke, float s)
    {
        float cx = ring.X + ring.Width / 2, cy = ring.Y;
        using (var gap = new SolidBrush(Theme.Card))
            g.FillRectangle(gap, cx - 6.5f * s, cy - stroke, 13 * s, stroke * 2);
        PointF[] bolt =
        {
            new(cx + 2.4f * s, cy - 8.5f * s), new(cx - 4.8f * s, cy + 1.2f * s), new(cx - 0.4f * s, cy + 1.2f * s),
            new(cx - 2.4f * s, cy + 8.5f * s), new(cx + 4.8f * s, cy - 1.2f * s), new(cx + 0.4f * s, cy - 1.2f * s),
        };
        using var green = new SolidBrush(Theme.Green);
        g.FillPolygon(green, bolt);
    }
}

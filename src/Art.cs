using System.Reflection;

namespace AirPodsWin;

/// <summary>
/// Optional artwork (PNG with transparency) embedded from src/Assets. When a file is missing
/// the views fall back to their vector drawings, so every image is optional.
/// </summary>
public static class Art
{
    // Card at the bottom of the screen (case-open pop-up)
    public const string Hero = "hero.png";            // case with both pods, on white (one picture)
    public const string Pods = "pods.png";            // both buds together
    public const string PodLeft = "pod-left.png";
    public const string PodRight = "pod-right.png";
    public const string Case = "case.png";
    public const string Headphones = "headphones.png";

    // Monochrome glyphs inside the widget rings (drawn as-is, black on transparent works best)
    public const string GlyphLeft = "glyph-left.png";
    public const string GlyphRight = "glyph-right.png";
    public const string GlyphCase = "glyph-case.png";
    public const string GlyphHeadphones = "glyph-headphones.png";

    private static readonly Dictionary<string, Image?> Cache = new();

    public static Image? Get(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var image)) return image;
            image = Load(name);
            Cache[name] = image;
            return image;
        }
    }

    private static Image? Load(string name)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("AirPodsWin.Assets." + name);
            if (stream == null) return null;
            // Copy: GDI+ needs the stream alive for the lifetime of the image otherwise.
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return new Bitmap(new MemoryStream(ms.ToArray()));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Draws the image fitted (aspect preserved) and centred in <paramref name="box"/>.</summary>
    public static void DrawFitted(Graphics g, Image image, RectangleF box, float opacity = 1f)
    {
        float k = Math.Min(box.Width / image.Width, box.Height / image.Height);
        float w = image.Width * k, h = image.Height * k;
        var dest = new Rectangle((int)(box.X + (box.Width - w) / 2), (int)(box.Y + (box.Height - h) / 2), (int)w, (int)h);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        if (opacity >= 1f)
        {
            g.DrawImage(image, dest);
            return;
        }
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = opacity });
        g.DrawImage(image, dest, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attrs);
    }
}

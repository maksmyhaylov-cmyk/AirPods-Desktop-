using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AirPodsWin;

/// <summary>
/// iOS-like light palette and drawing helpers (grouped settings style).
/// </summary>
public static class Theme
{
    // iOS light palette (systemGroupedBackground etc.). The app is always light, like the iPhone widget.
    public static readonly Color Back = Color.FromArgb(242, 242, 247);
    public static readonly Color Card = Color.White;
    public static readonly Color CardPressed = Color.FromArgb(229, 229, 234);
    public static readonly Color Hover = Color.FromArgb(10, 0, 0, 0);
    public static readonly Color Text = Color.Black;
    public static readonly Color SubText = Color.FromArgb(142, 142, 147);
    public static readonly Color Separator = Color.FromArgb(198, 198, 200);
    public static readonly Color Track = Color.FromArgb(229, 229, 234);
    public static readonly Color SwitchOff = Color.FromArgb(233, 233, 234);
    public static readonly Color Blue = Color.FromArgb(0, 122, 255);
    public static readonly Color Green = Color.FromArgb(52, 199, 89);
    public static readonly Color Red = Color.FromArgb(255, 59, 48);
    public static readonly Color Orange = Color.FromArgb(255, 149, 0);

    // Names used by the tray icon.
    public static Color Accent => Green;
    public static Color Charging => Green;
    public static Color Low => Red;

    /// <summary>Segoe UI Variable (Windows 11) is the closest system font to San Francisco.</summary>
    private static readonly string Family = FontExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    private static readonly string DisplayFamily = FontExists("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";

    /// <summary>Font sized in pixels at 96 DPI, multiplied by <paramref name="scale"/>.</summary>
    public static Font Font(float px, float scale, FontStyle style = FontStyle.Regular, bool display = false) =>
        new(display ? DisplayFamily : Family, px * scale, style, GraphicsUnit.Pixel);

    public static Font SemiboldFont(float px, float scale, bool display = false)
    {
        string semibold = (display ? DisplayFamily : Family) + " Semibold";
        return FontExists(semibold)
            ? new Font(semibold, px * scale, FontStyle.Regular, GraphicsUnit.Pixel)
            : Font(px, scale, FontStyle.Bold, display);
    }

    private static bool FontExists(string name)
    {
        using var f = new Font(name, 10f);
        return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase);
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Windows 11 rounded corners for a borderless window (ignored on Windows 10).</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        int round = 2; // DWMWCP_ROUND
        try { DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int)); }
        catch { /* older Windows */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

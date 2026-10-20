namespace AirPodsWin;

/// <summary>
/// iOS Settings-like window: section headers, grouped cards and footnotes stacked vertically.
/// The controls are created (and kept up to date) by <see cref="MainForm"/>.
/// </summary>
public sealed class SettingsForm : Form
{
    /// <param name="Gap">Space after the item, in 96-DPI pixels.</param>
    /// <param name="Indent">Section headers and footnotes are indented like in iOS.</param>
    public sealed record Item(Control Control, int Gap, bool Indent = false);

    private readonly Panel _root = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Item[] _items;
    private bool _inRelayout;

    /// <summary>Set when the whole app exits; otherwise closing only hides the window.</summary>
    public bool AllowClose { get; set; }

    private float S => DeviceDpi / 96f;

    public SettingsForm(Icon icon, params Item[] items)
    {
        _items = items;
        Text = "AirPodsWin — Налаштування";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = _root.BackColor = Theme.Back;
        Controls.Add(_root);
        foreach (var item in items)
        {
            _root.Controls.Add(item.Control);
            if (item.Control is Label label) label.TextChanged += (_, _) => Relayout();
        }
        ApplyFonts();
        Relayout();
    }

    private void ApplyFonts()
    {
        float s = S;
        foreach (var item in _items)
        {
            if (item.Control is not Label l) continue;
            l.AutoSize = false;
            l.BackColor = Theme.Back;
            l.Font = Theme.Font(l.Tag as string == "small" ? 11.5f : 12.5f, s);
            if (l.ForeColor != Theme.Orange) l.ForeColor = Theme.SubText;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyFonts();
        Relayout();
        CenterToScreen();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyFonts();
        Relayout();
    }

    public void Relayout()
    {
        if (_inRelayout) return;
        _inRelayout = true;
        try
        {
            float s = S;
            int margin = (int)(16 * s), width = (int)(380 * s), inner = width - margin * 2;
            int scroll = _root.AutoScrollPosition.Y;
            int y = (int)(20 * s) + scroll;
            foreach (var item in _items)
            {
                int indent = item.Indent ? margin : 0;
                int w = inner - indent;
                int h = item.Control switch
                {
                    GroupCard card => LayoutCard(card, w),
                    Label l => TextRenderer.MeasureText(l.Text, l.Font, new Size(w, 0),
                        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height,
                    var c => c.Height,
                };
                item.Control.SetBounds(margin + indent, y, w, h);
                y += h + (int)(item.Gap * s);
            }
            int content = y - scroll;
            int maxHeight = (int)(Screen.FromControl(this).WorkingArea.Height * 0.9);
            ClientSize = new Size(width + (content > maxHeight ? SystemInformation.VerticalScrollBarWidth : 0),
                Math.Min(content, maxHeight));
        }
        finally
        {
            _inRelayout = false;
        }
    }

    private static int LayoutCard(GroupCard card, int width)
    {
        card.LayoutRows(width);
        return card.Height;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}

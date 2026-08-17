using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeLimitWidget;

/// <summary>
/// Paints the widget: dark rounded pill, two progress bars with labels,
/// and the animated Clawd mascot on the right. All sizes derive from the
/// widget height so the layout survives DPI / taskbar-height changes.
/// </summary>
public sealed class WidgetRenderer : IDisposable
{
    // Palette (matches the mockup)
    private static readonly Color Pill = Color.FromArgb(0x23, 0x23, 0x26);
    private static readonly Color PillBorder = Color.FromArgb(0x3A, 0x3A, 0x3E);
    private static readonly Color Track = Color.FromArgb(0x33, 0x33, 0x37);
    private static readonly Color BarBlue = Color.FromArgb(0x2F, 0x80, 0xED);
    private static readonly Color BarOrange = Color.FromArgb(0xE8, 0x96, 0x3C);
    private static readonly Color BarRed = Color.FromArgb(0xE0, 0x52, 0x52);
    private static readonly Color LabelColor = Color.FromArgb(0xD9, 0xD9, 0xD9);

    private Font? _labelFont;
    private float _labelFontHeightPx = -1;

    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>When set, the pill background is not painted — the taskbar shows through.</summary>
    public bool Transparent { get; set; }

    public readonly MascotAnimator Mascot = new();

    /// <summary>Computes the widget width for a given height (pill is drawn inset).</summary>
    public static int WidthFor(int height)
    {
        float u = height / 48f;
        return (int)(200 * u);
    }

    public void Paint(Graphics g, Rectangle bounds, UsageSnapshot usage)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        float u = bounds.Height / 48f; // layout unit: 1px at the 48px reference height

        // Rounded pill, inset so the taskbar shows around it (skipped in transparent mode —
        // the color-keyed window background lets the taskbar surface show through instead)
        var pill = new Rectangle(bounds.X, bounds.Y + (int)(3 * u), bounds.Width, bounds.Height - (int)(6 * u));
        if (!Transparent)
        {
            using var path = RoundedRect(pill, (int)(8 * u));
            using var pillBrush = new SolidBrush(Pill);
            using var borderPen = new Pen(PillBorder);
            g.FillPath(pillBrush, path);
            g.DrawPath(borderPen, path);
        }

        double worst = usage.WorstPercent;

        // Layout: bars stacked on the left, mascot on the right
        int padX = (int)(9 * u);
        int mascotW = (int)(78 * u);
        int barX = pill.X + padX;
        int barW = pill.Width - padX * 2 - mascotW - (int)(4 * u);
        int barH = Math.Max(4, (int)(6 * u));

        EnsureFont(9f * u);
        float labelH = _labelFontHeightPx;

        float groupH = barH + labelH;
        float gap = 2 * u;
        float top = pill.Y + (pill.Height - (groupH * 2 + gap)) / 2f;

        DrawGroup(g, usage.FiveHour, "5-Hour Limit", barX, top, barW, barH, u);
        DrawGroup(g, usage.SevenDay, "Weekly Limit", barX, top + groupH + gap, barW, barH, u);

        // Mascot
        var mascotRect = new Rectangle(pill.Right - padX - mascotW, pill.Y + (int)(4 * u),
            mascotW, pill.Height - (int)(8 * u));

        // Hard pixel edges for the sprite — antialiased fills leave seams between cells.
        g.PixelOffsetMode = PixelOffsetMode.None;
        g.SmoothingMode = SmoothingMode.None;
        Mascot.SetMood(worst);
        Mascot.Draw(g, mascotRect, worst);
    }

    private void DrawGroup(Graphics g, WindowUsage? win, string label,
        int x, float y, int w, int h, float u)
    {
        // Track
        using (var trackPath = RoundedRect(new Rectangle(x, (int)y, w, h), h / 2))
        using (var trackBrush = new SolidBrush(Track))
            g.FillPath(trackBrush, trackPath);

        // Fill
        double pct = win?.EffectivePercent ?? 0;
        if (pct > 0)
        {
            int fillW = Math.Max(h, (int)(w * Math.Min(pct, 100) / 100.0));
            Color color = pct >= 95 ? BarRed : pct >= 80 ? BarOrange : BarBlue;
            bool stale = win is not null && win.IsStale(StaleAfter);
            if (stale)
                color = Color.FromArgb(150, color);

            using var fillPath = RoundedRect(new Rectangle(x, (int)y, fillW, h), h / 2);
            using var fillBrush = new SolidBrush(color);
            g.FillPath(fillBrush, fillPath);
        }

        // Label
        g.DrawString(label, _labelFont!, GetLabelBrush(), x - 1 * u, y + h);
    }

    private static SolidBrush? _labelBrush;
    private static SolidBrush GetLabelBrush() => _labelBrush ??= new SolidBrush(LabelColor);

    private void EnsureFont(float sizePx)
    {
        if (_labelFont is not null && Math.Abs(_labelFontHeightPx0 - sizePx) < 0.5f)
            return;
        _labelFont?.Dispose();
        _labelFont = new Font("Segoe UI", sizePx, FontStyle.Regular, GraphicsUnit.Pixel);
        _labelFontHeightPx0 = sizePx;
        _labelFontHeightPx = _labelFont.GetHeight();
    }

    private float _labelFontHeightPx0 = -1;

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || r.Width <= 0 || r.Height <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _labelFont?.Dispose();
    }
}

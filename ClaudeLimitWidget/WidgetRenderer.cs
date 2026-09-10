using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeLimitWidget;

/// <summary>
/// Paints one widget: dark rounded pill, two progress bars, and the animated Clawd
/// mascot. All sizes derive from the widget height so the layout survives DPI and
/// taskbar-height changes.
///
/// Two layouts: the single-account one keeps the original "bar over full label"
/// look; when several accounts are tracked each widget instead shows the account
/// name on top with short "5h"/"wk" tags, so the pills can be told apart without
/// growing taller.
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
    private static readonly Color AccountColor = Color.FromArgb(0xF0, 0xF0, 0xF0);
    private static readonly Color TagColor = Color.FromArgb(0x94, 0x94, 0x9A);
    private static readonly Color Unknown = Color.FromArgb(0x5A, 0x5A, 0x60);

    private Font? _labelFont;
    private Font? _accountFont;
    private float _labelFontHeightPx = -1;
    private float _labelFontHeightPx0 = -1;

    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>When set, the pill background is not painted — the taskbar shows through.</summary>
    public bool Transparent { get; set; }

    /// <summary>
    /// Account name drawn on the widget. Empty keeps the original single-account
    /// layout; non-empty switches to the compact per-account layout.
    /// </summary>
    public string AccountLabel { get; set; } = "";

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
        bool hasData = usage.HasData;
        bool named = AccountLabel.Length > 0;

        int padX = (int)(9 * u);
        int mascotW = (int)((named ? 58 : 78) * u);
        int barX = pill.X + padX;
        int barAreaW = pill.Width - padX * 2 - mascotW - (int)(4 * u);

        EnsureFonts(9f * u);

        if (named)
            PaintNamed(g, pill, usage, barX, barAreaW, u);
        else
            PaintPlain(g, pill, usage, barX, barAreaW, u);

        var mascotRect = new Rectangle(pill.Right - padX - mascotW, pill.Y + (int)(4 * u),
            mascotW, pill.Height - (int)(8 * u));

        // Hard pixel edges for the sprite — antialiased fills leave seams between cells.
        g.PixelOffsetMode = PixelOffsetMode.None;
        g.SmoothingMode = SmoothingMode.None;
        Mascot.SetMood(worst, hasData);
        Mascot.Draw(g, mascotRect, worst);
    }

    /// <summary>Original layout: each bar sits above its full label.</summary>
    private void PaintPlain(Graphics g, Rectangle pill, UsageSnapshot usage, int barX, int barW, float u)
    {
        int barH = Math.Max(4, (int)(6 * u));
        float labelH = _labelFontHeightPx;
        float groupH = barH + labelH;
        float gap = 2 * u;
        float top = pill.Y + (pill.Height - (groupH * 2 + gap)) / 2f;

        DrawBar(g, usage.FiveHour, barX, top, barW, barH);
        g.DrawString("5-Hour Limit", _labelFont!, GetBrush(LabelColor), barX - 1 * u, top + barH);

        float second = top + groupH + gap;
        DrawBar(g, usage.SevenDay, barX, second, barW, barH);
        g.DrawString("Weekly Limit", _labelFont!, GetBrush(LabelColor), barX - 1 * u, second + barH);
    }

    /// <summary>
    /// Multi-account layout: account name on top, then both bars with short tags to
    /// the right, so one glance says which account and which window.
    /// </summary>
    private void PaintNamed(Graphics g, Rectangle pill, UsageSnapshot usage, int barX, int barAreaW, float u)
    {
        int barH = Math.Max(5, (int)(7 * u));
        int tagW = (int)(16 * u);
        int barW = Math.Max((int)(20 * u), barAreaW - tagW);

        float nameH = _labelFontHeightPx;
        float gapAfterName = 1 * u;
        float gapBars = 4 * u;
        float block = nameH + gapAfterName + barH + gapBars + barH;
        float top = pill.Y + (pill.Height - block) / 2f;

        g.DrawString(Ellipsize(g, AccountLabel, _accountFont!, barAreaW), _accountFont!,
            GetBrush(AccountColor), barX - 1 * u, top);

        float y1 = top + nameH + gapAfterName;
        DrawBar(g, usage.FiveHour, barX, y1, barW, barH);
        g.DrawString("5h", _labelFont!, GetBrush(TagColor), barX + barW + 3 * u, y1 - 3 * u);

        float y2 = y1 + barH + gapBars;
        DrawBar(g, usage.SevenDay, barX, y2, barW, barH);
        g.DrawString("wk", _labelFont!, GetBrush(TagColor), barX + barW + 3 * u, y2 - 3 * u);
    }

    private void DrawBar(Graphics g, WindowUsage? win, int x, float y, int w, int h)
    {
        using (var trackPath = RoundedRect(new Rectangle(x, (int)y, w, h), h / 2))
            g.FillPath(GetBrush(Track), trackPath);

        // Unknown (never fetched) reads as dim dashes, so it cannot be mistaken
        // for a real 0%.
        if (win is null)
        {
            int dashW = Math.Max(2, h / 2);
            for (int dx = h / 2; dx + dashW < w - h / 2; dx += dashW * 2)
                g.FillRectangle(GetBrush(Unknown), x + dx, (int)y + h / 2 - 1, dashW, Math.Max(1, h / 3));
            return;
        }

        double pct = win.EffectivePercent;
        if (pct <= 0)
            return;

        int fillW = Math.Max(h, (int)(w * Math.Min(pct, 100) / 100.0));
        Color color = pct >= 95 ? BarRed : pct >= 80 ? BarOrange : BarBlue;
        if (win.IsStale(StaleAfter))
            color = Color.FromArgb(150, color);

        using var fillPath = RoundedRect(new Rectangle(x, (int)y, fillW, h), h / 2);
        g.FillPath(GetBrush(color), fillPath);
    }

    private static string Ellipsize(Graphics g, string text, Font font, float maxWidth)
    {
        if (g.MeasureString(text, font).Width <= maxWidth)
            return text;
        for (int len = text.Length - 1; len > 1; len--)
        {
            string candidate = text[..len] + "…";
            if (g.MeasureString(candidate, font).Width <= maxWidth)
                return candidate;
        }
        return text[..1];
    }

    // Brushes are re-created rarely; cache by color to avoid churn on every paint.
    private readonly Dictionary<int, SolidBrush> _brushes = new();

    private SolidBrush GetBrush(Color c)
    {
        if (!_brushes.TryGetValue(c.ToArgb(), out var brush))
            _brushes[c.ToArgb()] = brush = new SolidBrush(c);
        return brush;
    }

    private void EnsureFonts(float sizePx)
    {
        if (_labelFont is not null && Math.Abs(_labelFontHeightPx0 - sizePx) < 0.5f)
            return;
        _labelFont?.Dispose();
        _accountFont?.Dispose();
        _labelFont = new Font("Segoe UI", sizePx, FontStyle.Regular, GraphicsUnit.Pixel);
        _accountFont = new Font("Segoe UI", sizePx, FontStyle.Bold, GraphicsUnit.Pixel);
        _labelFontHeightPx0 = sizePx;
        _labelFontHeightPx = _labelFont.GetHeight();
    }

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
        _accountFont?.Dispose();
        foreach (var brush in _brushes.Values)
            brush.Dispose();
        _brushes.Clear();
    }
}

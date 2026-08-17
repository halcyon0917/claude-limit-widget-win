using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeLimitWidget;

/// <summary>
/// Custom dark tooltip shown above the widget: every limit window with a mini bar,
/// percentage and reset countdown, extra-usage credits, plan/org, last session
/// details, and data freshness.
/// </summary>
public sealed class HoverTooltip : Form
{
    private static readonly Color Bg = Color.FromArgb(0x20, 0x20, 0x22);
    private static readonly Color TextMain = Color.FromArgb(0xE6, 0xE6, 0xE6);
    private static readonly Color TextDim = Color.FromArgb(0x9A, 0x9A, 0x9A);
    private static readonly Color Track = Color.FromArgb(0x33, 0x33, 0x37);
    private static readonly Color BarBlue = Color.FromArgb(0x2F, 0x80, 0xED);
    private static readonly Color BarOrange = Color.FromArgb(0xE8, 0x96, 0x3C);
    private static readonly Color BarRed = Color.FromArgb(0xE0, 0x52, 0x52);
    private static readonly Color Separator = Color.FromArgb(0x3A, 0x3A, 0x3E);

    private readonly Font _title = new("Segoe UI", 9.5f, FontStyle.Bold);
    private readonly Font _row = new("Segoe UI", 9f);
    private readonly Font _rowBold = new("Segoe UI", 9f, FontStyle.Bold);
    private readonly Font _small = new("Segoe UI", 8.25f);

    private abstract record Row;
    private sealed record TitleRow(string Left, string Right) : Row;
    private sealed record BarRow(string Label, double Percent, string Detail, bool Stale) : Row;
    private sealed record TextRow(string Text, bool Dim) : Row;
    private sealed record SeparatorRow : Row;

    private List<Row> _rows = new();

    private const int PadX = 14;
    private const int PadY = 10;
    private const int LabelW = 96;
    private const int BarW = 84;
    private const int PctW = 40;
    private const int RowH = 22;
    private const int TitleH = 24;
    private const int SepH = 9;
    private const int TextH = 19;

    public HoverTooltip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Bg;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= TaskbarInterop.WS_EX_TOOLWINDOW | TaskbarInterop.WS_EX_NOACTIVATE | TaskbarInterop.WS_EX_TOPMOST;
            return cp;
        }
    }

    public void ShowFor(UsageSnapshot usage, Rectangle widgetScreenBounds, TimeSpan staleAfter)
    {
        _rows = BuildRows(usage, staleAfter);

        int width, height = PadY * 2;
        using (var g = CreateGraphics())
        {
            float maxDetail = 0, maxText = 0;
            foreach (var row in _rows)
            {
                switch (row)
                {
                    case BarRow b:
                        maxDetail = Math.Max(maxDetail, g.MeasureString(b.Detail, _row).Width);
                        height += RowH;
                        break;
                    case TitleRow t:
                        maxText = Math.Max(maxText, g.MeasureString(t.Left, _title).Width +
                                                    g.MeasureString(t.Right, _small).Width + 24);
                        height += TitleH;
                        break;
                    case TextRow tr:
                        maxText = Math.Max(maxText, g.MeasureString(tr.Text, _row).Width);
                        height += TextH;
                        break;
                    case SeparatorRow:
                        height += SepH;
                        break;
                }
            }
            width = PadX * 2 + Math.Max((int)(LabelW + BarW + PctW + 10 + maxDetail), (int)maxText);
        }

        Size = new Size(width, height);

        var screen = Screen.FromRectangle(widgetScreenBounds).WorkingArea;
        int x = Math.Min(Math.Max(screen.Left, widgetScreenBounds.Left + (widgetScreenBounds.Width - Width) / 2),
                         screen.Right - Width);
        int y = widgetScreenBounds.Top - Height - 8;
        if (y < screen.Top)
            y = widgetScreenBounds.Bottom + 8;
        Location = new Point(x, y);

        Region = new Region(RoundedRect(new Rectangle(0, 0, Width, Height), 8));
        Show();
        Invalidate();
    }

    private static List<Row> BuildRows(UsageSnapshot usage, TimeSpan staleAfter)
    {
        var rows = new List<Row>();

        string planText = "";
        if (usage.Account is { } acct)
        {
            if (acct.Plan.Length > 0)
                planText = char.ToUpper(acct.Plan[0]) + acct.Plan[1..] + " plan";
            string tier = PrettyTier(acct.RateLimitTier);
            if (tier.Length > 0)
                planText += planText.Length > 0 ? $" ({tier})" : tier;
            if (acct.Organization.Length > 0 && !acct.Organization.Contains('@'))
                planText += (planText.Length > 0 ? " · " : "") + acct.Organization;
            if (acct.Email.Length > 0)
                planText = acct.Email + (planText.Length > 0 ? " · " + planText : "");
        }
        rows.Add(new TitleRow("Claude Usage", planText));

        AddWindow(rows, "5-Hour Limit", usage.FiveHour, staleAfter);
        AddWindow(rows, "Weekly Limit", usage.SevenDay, staleAfter);
        AddWindow(rows, "Weekly · Opus", usage.SevenDayOpus, staleAfter, optional: true);
        AddWindow(rows, "Weekly · Sonnet", usage.SevenDaySonnet, staleAfter, optional: true);
        foreach (var scoped in usage.Scoped)
            AddWindow(rows, scoped.Label, scoped.Usage, staleAfter, optional: true);

        if (usage.ExtraUsage is { IsEnabled: true } extra)
        {
            string detail = extra.UsedCredits is { } used && extra.MonthlyLimit is { } limit
                ? $"${used:0.00} of ${limit:0.00}"
                : extra.UsedCredits is { } u ? $"${u:0.00} used" : "enabled";
            rows.Add(new BarRow("Extra usage", extra.Utilization ?? 0, detail, false));
        }

        if (usage.FiveHour is null && usage.SevenDay is null)
            rows.Add(new TextRow("Waiting for data — use Claude Code once to populate.", Dim: false));

        if (usage.Session is { } s && (s.Model.Length > 0 || s.CostUsd > 0))
        {
            rows.Add(new SeparatorRow());
            var bits = new List<string>();
            if (s.Model.Length > 0) bits.Add(s.Model);
            if (s.CostUsd > 0) bits.Add($"${s.CostUsd:0.00}");
            if (s.DurationMs > 0) bits.Add(FormatDuration(TimeSpan.FromMilliseconds(s.DurationMs)));
            if (s.LinesAdded > 0 || s.LinesRemoved > 0) bits.Add($"+{s.LinesAdded}/−{s.LinesRemoved}");
            if (s.Workspace.Length > 0) bits.Add(s.Workspace);
            rows.Add(new TextRow("Last session:  " + string.Join(" · ", bits), Dim: true));
        }

        var newest = new[] { usage.FiveHour, usage.SevenDay, usage.SevenDayOpus, usage.SevenDaySonnet }
            .Where(w => w is not null)
            .OrderByDescending(w => w!.FetchedAt)
            .FirstOrDefault();
        if (newest is not null)
        {
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(newest.FetchedAt);
            string ago = age.TotalMinutes < 1 ? "just now"
                : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
                : $"{(int)age.TotalHours} h ago";
            string staleNote = newest.IsStale(staleAfter) ? " · stale" : "";
            rows.Add(new TextRow($"Updated {ago} via {newest.Source}{staleNote}", Dim: true));
        }

        return rows;
    }

    private static void AddWindow(List<Row> rows, string label, WindowUsage? w, TimeSpan staleAfter, bool optional = false)
    {
        if (w is null)
        {
            if (!optional)
                rows.Add(new BarRow(label, 0, "no data yet", false));
            return;
        }
        rows.Add(new BarRow(label, w.EffectivePercent, ResetText(w), w.IsStale(staleAfter)));
    }

    private static string ResetText(WindowUsage w)
    {
        if (w.ResetsAt <= 0)
            return "";
        var resets = DateTimeOffset.FromUnixTimeSeconds(w.ResetsAt).ToLocalTime();
        var until = resets - DateTimeOffset.Now;

        if (until <= TimeSpan.Zero)
            return "reset";

        string when = resets.Date == DateTime.Today ? $"{resets:h:mm tt}"
            : resets.Date == DateTime.Today.AddDays(1) ? $"tomorrow {resets:h:mm tt}"
            : $"{resets:ddd h:mm tt}";
        return $"resets {when} · in {FormatDuration(until)}";
    }

    private static string FormatDuration(TimeSpan t) => t.TotalDays >= 1
        ? $"{(int)t.TotalDays}d {t.Hours}h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{Math.Max(1, t.Minutes)}m";

    private static string PrettyTier(string tier)
    {
        // e.g. "default_claude_max_20x" → "20x"
        int i = tier.LastIndexOf('_');
        string last = i >= 0 ? tier[(i + 1)..] : tier;
        return last.EndsWith('x') && last.Length <= 4 ? last : "";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var mainBrush = new SolidBrush(TextMain);
        using var dimBrush = new SolidBrush(TextDim);
        using var trackBrush = new SolidBrush(Track);
        using var sepPen = new Pen(Separator);

        float y = PadY;
        foreach (var row in _rows)
        {
            switch (row)
            {
                case TitleRow t:
                    g.DrawString(t.Left, _title, mainBrush, PadX - 2, y + 1);
                    if (t.Right.Length > 0)
                    {
                        var sz = g.MeasureString(t.Right, _small);
                        g.DrawString(t.Right, _small, dimBrush, Width - PadX - sz.Width + 2, y + 4);
                    }
                    y += TitleH;
                    break;

                case BarRow b:
                {
                    float cy = y + RowH / 2f;
                    g.DrawString(b.Label, _row, mainBrush, PadX - 2, cy - 9);

                    // mini bar
                    int barX = PadX + LabelW;
                    int barH = 6;
                    var track = new Rectangle(barX, (int)(cy - barH / 2f), BarW, barH);
                    using (var tp = RoundedRect(track, barH / 2))
                        g.FillPath(trackBrush, tp);
                    double pct = Math.Min(b.Percent, 100);
                    if (pct > 0)
                    {
                        Color c = pct >= 95 ? BarRed : pct >= 80 ? BarOrange : BarBlue;
                        if (b.Stale)
                            c = Color.FromArgb(150, c);
                        var fill = new Rectangle(barX, track.Y, Math.Max(barH, (int)(BarW * pct / 100)), barH);
                        using var fp = RoundedRect(fill, barH / 2);
                        using var fb = new SolidBrush(c);
                        g.FillPath(fb, fp);
                    }

                    // percent + detail
                    string pctText = $"{b.Percent:0}%";
                    var pctSize = g.MeasureString(pctText, _rowBold);
                    g.DrawString(pctText, _rowBold, mainBrush, barX + BarW + PctW - pctSize.Width, cy - 9);
                    if (b.Detail.Length > 0)
                        g.DrawString(b.Detail, _row, dimBrush, barX + BarW + PctW + 10, cy - 9);
                    y += RowH;
                    break;
                }

                case SeparatorRow:
                    g.DrawLine(sepPen, PadX, y + SepH / 2f, Width - PadX, y + SepH / 2f);
                    y += SepH;
                    break;

                case TextRow tr:
                    g.DrawString(tr.Text, _row, tr.Dim ? dimBrush : mainBrush, PadX - 2, y + 1);
                    y += TextH;
                    break;
            }
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _title.Dispose();
            _row.Dispose();
            _rowBold.Dispose();
            _small.Dispose();
        }
        base.Dispose(disposing);
    }
}

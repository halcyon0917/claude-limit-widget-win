namespace ClaudeLimitWidget;

/// <summary>
/// The pixel-art Clawd mascot, drawn nearest-neighbor from a small grid.
/// Chars: 'X' = body, 'E' = eye (dark), '.' = transparent.
/// </summary>
public static class ClawdSprite
{
    public const int GridW = 13;
    public const int GridH = 10;

    private static readonly string[] EyesOpen =
    {
        "..XXXXXXXXX..",
        "..XXXXXXXXX..",
        "..XXEXXXEXX..",
        "..XXEXXXEXX..",
        "XXXXXXXXXXXXX",
        "XXXXXXXXXXXXX",
        "..XXXXXXXXX..",
        "..XXXXXXXXX..",
        "...X.X..X.X..",
        "...X.X..X.X..",
    };

    private static readonly string[] EyesClosed =
    {
        "..XXXXXXXXX..",
        "..XXXXXXXXX..",
        "..XXXXXXXXX..",
        "..XXEXXXEXX..",
        "XXXXXXXXXXXXX",
        "XXXXXXXXXXXXX",
        "..XXXXXXXXX..",
        "..XXXXXXXXX..",
        "...X.X..X.X..",
        "...X.X..X.X..",
    };

    public static readonly Color Body = Color.FromArgb(0xE0, 0x7B, 0x54);       // coral
    public static readonly Color BodyWorried = Color.FromArgb(0xE8, 0x96, 0x3C); // orange tint
    public static readonly Color BodyPanic = Color.FromArgb(0xE0, 0x52, 0x52);   // red tint
    private static readonly Color Eye = Color.FromArgb(0x1A, 0x1A, 0x1A);
    private static readonly Color Sweat = Color.FromArgb(0x7A, 0xB8, 0xF5);

    /// <summary>
    /// Draws the mascot into <paramref name="dest"/> (whole pixels, centered),
    /// with mood driven by the worst utilization percentage.
    /// </summary>
    public static void Draw(Graphics g, Rectangle dest, bool blink, int bounceOffset, double worstPercent)
    {
        string[] grid = blink ? EyesClosed : EyesOpen;

        Color body = worstPercent >= 95 ? BodyPanic
                   : worstPercent >= 80 ? BodyWorried
                   : Body;

        int scale = Math.Max(1, Math.Min(dest.Width / GridW, dest.Height / GridH));
        int w = GridW * scale, h = GridH * scale;
        int ox = dest.X + (dest.Width - w) / 2;
        int oy = dest.Y + (dest.Height - h) / 2 + bounceOffset;

        using var bodyBrush = new SolidBrush(body);
        using var eyeBrush = new SolidBrush(Eye);

        for (int y = 0; y < GridH; y++)
        {
            for (int x = 0; x < GridW; x++)
            {
                char c = grid[y][x];
                if (c == '.')
                    continue;
                g.FillRectangle(c == 'E' ? eyeBrush : bodyBrush,
                    ox + x * scale, oy + y * scale, scale, scale);
            }
        }

        // sweat drop when nervous
        if (worstPercent >= 80 && !blink)
        {
            using var sweatBrush = new SolidBrush(Sweat);
            g.FillRectangle(sweatBrush, ox + (GridW - 1) * scale, oy, scale, scale * 2);
        }
    }
}

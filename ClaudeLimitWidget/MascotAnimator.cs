using System.Reflection;
using System.Text.Json;

namespace ClaudeLimitWidget;

/// <summary>
/// Plays the pixel-art mascot animations converted from the site's SVGs
/// (tools/convert_mascots.py → sprites.json, embedded resource).
///
/// Mood by usage: &lt;10% cheering · &lt;50% wander/idle · &lt;85% workout · ≥85% waving.
/// The wander band is a little behavior loop instead of a fixed animation:
/// Clawd walks to a random spot on the stage (mirrored when heading left),
/// stands for a while, blinks, glances around, occasionally hops — so a quiet
/// day doesn't look like a frozen sprite.
/// </summary>
public sealed class MascotAnimator
{
    private sealed record SpriteRect(float X, float Y, float W, float H, Color Color, bool IsBody);
    private sealed record Anim(string Name, SpriteRect[][] Frames, float W, float H, int FrameMs);

    private static readonly Color BodyColor = Color.FromArgb(0xDD, 0x77, 0x5B);
    private static readonly Color BodyWorried = Color.FromArgb(0xE8, 0x96, 0x3C);
    private static readonly Color BodyPanic = Color.FromArgb(0xE0, 0x52, 0x52);

    private static readonly Dictionary<string, Anim> Anims = LoadAnims();

    // idle frame indices (see convert_mascots.py)
    private const int IdleStand = 0;
    private const int IdleBlink = 1;
    private const int IdleGlanceLeft = 2;
    private const int IdleGlanceRight = 3;
    private const int IdleHop = 4;

    private readonly Random _rng = new();

    private string _mood = "walking";
    private int _frame;
    private long _acc;

    // ---- wander state (only for the "walking" mood) ----
    private enum Wander { Stand, Walk }

    private Wander _state = Wander.Stand;
    private float _x = 0.5f;          // 0..1 across the stage's free width
    private float _targetX = 0.5f;
    private int _facing = 1;          // 1 = right, -1 = left
    private long _stateMs;
    private long _standDurMs = 2500;
    private int _idleFrame = IdleStand;
    private long _gestureLeftMs;      // >0 while a blink/glance/hop is being held

    private const float WalkSpeedPerMs = 0.00028f; // full stage crossing ≈ 3.5 s

    private static Dictionary<string, Anim> LoadAnims()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("ClaudeLimitWidget.sprites.json")
            ?? throw new InvalidOperationException("sprites.json resource missing");
        using var doc = JsonDocument.Parse(stream);

        var result = new Dictionary<string, Anim>();
        foreach (var animProp in doc.RootElement.EnumerateObject())
        {
            var el = animProp.Value;
            var frames = new List<SpriteRect[]>();
            foreach (var frame in el.GetProperty("frames").EnumerateArray())
            {
                var rects = new List<SpriteRect>();
                foreach (var r in frame.EnumerateArray())
                {
                    Color c = ParseColor(r[4].GetString()!);
                    rects.Add(new SpriteRect(
                        (float)r[0].GetDouble(), (float)r[1].GetDouble(),
                        (float)r[2].GetDouble(), (float)r[3].GetDouble(),
                        c, c.ToArgb() == BodyColor.ToArgb()));
                }
                frames.Add(rects.ToArray());
            }
            result[animProp.Name] = new Anim(
                animProp.Name,
                frames.ToArray(),
                (float)el.GetProperty("w").GetDouble(),
                (float)el.GetProperty("h").GetDouble(),
                el.GetProperty("frameMs").GetInt32());
        }
        return result;
    }

    private static Color ParseColor(string s) => s switch
    {
        "black" => Color.FromArgb(0x1A, 0x1A, 0x1A),
        "white" => Color.White,
        _ => Color.FromArgb(
            Convert.ToInt32(s.Substring(1, 2), 16),
            Convert.ToInt32(s.Substring(3, 2), 16),
            Convert.ToInt32(s.Substring(5, 2), 16)),
    };

    public static string MoodFor(double worstPercent) => worstPercent switch
    {
        < 10 => "cheering",
        < 50 => "walking",
        < 85 => "workout",
        _ => "waving",
    };

    /// <summary>Sets the mood; restarts animation state only when it changes.</summary>
    public void SetMood(double worstPercent)
    {
        string name = MoodFor(worstPercent);
        if (_mood == name)
            return;
        _mood = name;
        _frame = 0;
        _acc = 0;
        _state = Wander.Stand;
        _stateMs = 0;
        _idleFrame = IdleStand;
        _gestureLeftMs = 0;
        _x = 0.5f;
    }

    /// <summary>Advances by elapsedMs. Returns true when the rendered image changed.</summary>
    public bool Tick(int elapsedMs)
    {
        if (_mood != "walking")
        {
            var anim = Anims[_mood];
            _acc += elapsedMs;
            if (_acc < anim.FrameMs)
                return false;
            _frame = (_frame + (int)(_acc / anim.FrameMs)) % anim.Frames.Length;
            _acc %= anim.FrameMs;
            return true;
        }

        return _state == Wander.Stand ? TickStand(elapsedMs) : TickWalk(elapsedMs);
    }

    private bool TickStand(int e)
    {
        _stateMs += e;
        bool dirty = false;

        if (_gestureLeftMs > 0)
        {
            _gestureLeftMs -= e;
            if (_gestureLeftMs <= 0 && _idleFrame != IdleStand)
            {
                _idleFrame = IdleStand;
                dirty = true;
            }
        }
        else
        {
            // Occasional bored gestures (probabilities per tick, e ≈ 50 ms)
            double roll = _rng.NextDouble();
            if (roll < e / 3200.0)
                dirty = StartGesture(IdleBlink, 180);
            else if (roll < e / 3200.0 + e / 5500.0)
                dirty = StartGesture(_rng.Next(2) == 0 ? IdleGlanceLeft : IdleGlanceRight, 700);
            else if (roll < e / 3200.0 + e / 5500.0 + e / 11000.0)
                dirty = StartGesture(IdleHop, 260);
        }

        if (_stateMs >= _standDurMs)
        {
            // pick a new spot at least a quarter-stage away
            float target;
            do { target = (float)_rng.NextDouble(); }
            while (Math.Abs(target - _x) < 0.25f);
            _targetX = target;
            _facing = target > _x ? 1 : -1;
            _state = Wander.Walk;
            _stateMs = 0;
            _frame = 0;
            _acc = 0;
            _idleFrame = IdleStand;
            _gestureLeftMs = 0;
            dirty = true;
        }
        return dirty;
    }

    private bool StartGesture(int frame, int holdMs)
    {
        _idleFrame = frame;
        _gestureLeftMs = holdMs;
        return true;
    }

    private bool TickWalk(int e)
    {
        bool dirty = false;

        var anim = Anims["walking"];
        _acc += e;
        if (_acc >= anim.FrameMs)
        {
            _frame = (_frame + (int)(_acc / anim.FrameMs)) % anim.Frames.Length;
            _acc %= anim.FrameMs;
            dirty = true;
        }

        float step = WalkSpeedPerMs * e * _facing;
        _x += step;
        dirty = true;

        if ((_facing > 0 && _x >= _targetX) || (_facing < 0 && _x <= _targetX))
        {
            _x = Math.Clamp(_targetX, 0f, 1f);
            _state = Wander.Stand;
            _stateMs = 0;
            _standDurMs = _rng.Next(2200, 7000);
            _idleFrame = IdleStand;
        }
        return dirty;
    }

    public void Draw(Graphics g, Rectangle dest, double worstPercent)
    {
        Color body = worstPercent >= 95 ? BodyPanic
                   : worstPercent >= 80 ? BodyWorried
                   : BodyColor;

        if (_mood != "walking")
        {
            var anim = Anims[_mood];
            float scale = Math.Min(dest.Width / anim.W, dest.Height / anim.H);
            float ox = dest.X + (dest.Width - anim.W * scale) / 2f;
            float oy = dest.Y + (dest.Height - anim.H * scale);
            DrawFrame(g, anim.Frames[Math.Min(_frame, anim.Frames.Length - 1)],
                anim.W, scale, ox, oy, mirrored: false, body);
            return;
        }

        // Wander mode: sprite keeps its natural size and moves across the stage.
        var walk = Anims["walking"];
        var idle = Anims["idle"];
        float s = dest.Height / walk.H;
        float spriteW = walk.W * s;
        float freeW = Math.Max(0, dest.Width - spriteW);
        float x0 = dest.X + _x * freeW;
        float y0 = dest.Y + (dest.Height - walk.H * s);

        SpriteRect[] frame = _state == Wander.Walk
            ? walk.Frames[Math.Min(_frame, walk.Frames.Length - 1)]
            : idle.Frames[Math.Min(_idleFrame, idle.Frames.Length - 1)];

        DrawFrame(g, frame, walk.W, s, x0, y0, mirrored: _facing < 0, body);
    }

    private static void DrawFrame(Graphics g, SpriteRect[] rects, float animW,
        float scale, float ox, float oy, bool mirrored, Color body)
    {
        foreach (var r in rects)
        {
            float rx = mirrored ? animW - r.X - r.W : r.X;
            // Round edges (not size) so adjacent cells stay seamless at any scale.
            int x0 = (int)MathF.Round(ox + rx * scale);
            int y0 = (int)MathF.Round(oy + r.Y * scale);
            int x1 = (int)MathF.Round(ox + (rx + r.W) * scale);
            int y1 = (int)MathF.Round(oy + (r.Y + r.H) * scale);
            if (x1 <= x0 || y1 <= y0)
                continue;
            using var brush = new SolidBrush(r.IsBody ? body : r.Color);
            g.FillRectangle(brush, x0, y0, x1 - x0, y1 - y0);
        }
    }
}

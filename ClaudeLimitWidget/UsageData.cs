namespace ClaudeLimitWidget;

/// <summary>State of one rate-limit window (5-hour, 7-day, or per-model 7-day).</summary>
public sealed record WindowUsage
{
    /// <summary>Utilization 0–100.</summary>
    public double Percent { get; init; }

    /// <summary>Unix seconds when the window resets; 0 if unknown.</summary>
    public long ResetsAt { get; init; }

    /// <summary>Unix seconds when this value was obtained.</summary>
    public long FetchedAt { get; init; }

    /// <summary>"statusline" or "api".</summary>
    public string Source { get; init; } = "";

    /// <summary>Percent adjusted for a reset that has already passed.</summary>
    public double EffectivePercent =>
        ResetsAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= ResetsAt ? 0 : Percent;

    public bool IsStale(TimeSpan maxAge) =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() - FetchedAt > maxAge.TotalSeconds;
}

/// <summary>A model/surface-scoped window from the API's limits[] array, e.g. "Weekly · Fable".</summary>
public sealed record ScopedWindow(string Label, WindowUsage Usage);

/// <summary>Pay-as-you-go overage credits (Max plans with extra usage enabled).</summary>
public sealed record ExtraUsageInfo
{
    public bool IsEnabled { get; init; }
    public double? MonthlyLimit { get; init; }
    public double? UsedCredits { get; init; }
    public double? Utilization { get; init; }
}

/// <summary>Details of the most recent Claude Code session, captured by the statusline bridge.</summary>
public sealed record SessionInfo
{
    public string Model { get; init; } = "";
    public double CostUsd { get; init; }
    public long DurationMs { get; init; }
    public long LinesAdded { get; init; }
    public long LinesRemoved { get; init; }
    public string Workspace { get; init; } = "";
    public long FetchedAt { get; init; }
}

/// <summary>Subscription/account facts (from the profile endpoint or local Claude Code config).</summary>
public sealed record AccountInfo
{
    public string Plan { get; init; } = "";
    public string Organization { get; init; } = "";
    public string RateLimitTier { get; init; } = "";
    public string Email { get; init; } = "";
}

/// <summary>Snapshot of everything the widget can show.</summary>
public sealed record UsageSnapshot
{
    public WindowUsage? FiveHour { get; init; }
    public WindowUsage? SevenDay { get; init; }
    public WindowUsage? SevenDayOpus { get; init; }
    public WindowUsage? SevenDaySonnet { get; init; }

    /// <summary>Per-model weekly windows (API limits[] with kind = weekly_scoped).</summary>
    public IReadOnlyList<ScopedWindow> Scoped { get; init; } = Array.Empty<ScopedWindow>();

    public ExtraUsageInfo? ExtraUsage { get; init; }
    public SessionInfo? Session { get; init; }
    public AccountInfo? Account { get; init; }

    /// <summary>Worst utilization across the two primary windows — drives colors and mood.</summary>
    public double WorstPercent => Math.Max(FiveHour?.EffectivePercent ?? 0, SevenDay?.EffectivePercent ?? 0);

    public static readonly UsageSnapshot Empty = new();
}

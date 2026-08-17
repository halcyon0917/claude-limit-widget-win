using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeLimitWidget;

/// <summary>
/// Aggregates usage data and raises <see cref="UsageUpdated"/>.
///
/// Auth modes:
///  - "cli": follows the Claude Code CLI login — token from ~/.claude/.credentials.json
///    (re-read every poll, watched for account switches) plus the statusline bridge file.
///  - "standalone": widget-owned token from <see cref="TokenStore"/> (DPAPI), refreshed
///    via OAuth when expired. The statusline file is ignored — it reflects the CLI account.
///
/// Account identity (plan/org/email) comes from the profile endpoint for whichever token
/// is in use; on a token/account change all cached data is cleared immediately.
/// All work happens off the UI thread.
/// </summary>
public sealed class UsageService : IDisposable
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string ProfileUrl = "https://api.anthropic.com/api/oauth/profile";
    private const string UserAgent = "claude-code/2.1.201";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly bool _standalone;
    private readonly System.Threading.Timer _pollTimer;
    private readonly FileSystemWatcher? _usageWatcher;
    private readonly FileSystemWatcher? _credsWatcher;
    private readonly object _gate = new();

    private WindowUsage? _fiveHour;
    private WindowUsage? _sevenDay;
    private WindowUsage? _sevenDayOpus;
    private WindowUsage? _sevenDaySonnet;
    private IReadOnlyList<ScopedWindow> _scoped = Array.Empty<ScopedWindow>();
    private ExtraUsageInfo? _extraUsage;
    private SessionInfo? _session;
    private AccountInfo? _account;
    private string _lastTokenMark = "";

    public event Action<UsageSnapshot>? UsageUpdated;

    public UsageSnapshot Current
    {
        get
        {
            lock (_gate)
                return new UsageSnapshot
                {
                    FiveHour = _fiveHour,
                    SevenDay = _sevenDay,
                    SevenDayOpus = _sevenDayOpus,
                    SevenDaySonnet = _sevenDaySonnet,
                    Scoped = _scoped,
                    ExtraUsage = _extraUsage,
                    Session = _session,
                    Account = _account,
                };
        }
    }

    public UsageService(Config config)
    {
        _standalone = config.AuthMode == "standalone";
        int pollSeconds = Math.Max(60, config.ApiPollSeconds);

        Directory.CreateDirectory(Config.DataDir);

        if (!_standalone)
        {
            _account = ReadLocalAccountInfo();
            ReadUsageFile();

            _usageWatcher = new FileSystemWatcher(Config.DataDir, "usage.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            // Changed + Renamed both fire depending on how the atomic replace lands.
            _usageWatcher.Changed += (_, _) => DebounceUsageRead();
            _usageWatcher.Created += (_, _) => DebounceUsageRead();
            _usageWatcher.Renamed += (_, _) => DebounceUsageRead();

            // Watch the CLI credentials so an account switch (/login) is picked up
            // immediately instead of on the next scheduled poll.
            string claudeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            if (Directory.Exists(claudeDir))
            {
                _credsWatcher = new FileSystemWatcher(claudeDir, ".credentials.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                _credsWatcher.Changed += (_, _) => DebouncePoll();
                _credsWatcher.Created += (_, _) => DebouncePoll();
                _credsWatcher.Renamed += (_, _) => DebouncePoll();
            }
        }

        _pollTimer = new System.Threading.Timer(_ => PollApi(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(pollSeconds));
    }

    /// <summary>Force an immediate re-read + API poll (tray "Refresh now").</summary>
    public void RefreshNow()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (!_standalone)
            {
                lock (_gate)
                    _account = ReadLocalAccountInfo() ?? _account;
                ReadUsageFile();
            }
            lock (_gate)
                _profileFetched = false; // re-fetch identity too
            PollApi();
        });
    }

    // ---- debouncers ----

    private System.Threading.Timer? _usageDebounce;
    private System.Threading.Timer? _pollDebounce;

    private void DebounceUsageRead()
    {
        _usageDebounce?.Dispose();
        _usageDebounce = new System.Threading.Timer(_ => ReadUsageFile(), null, 250, Timeout.Infinite);
    }

    private void DebouncePoll()
    {
        _pollDebounce?.Dispose();
        _pollDebounce = new System.Threading.Timer(_ => PollApi(), null, 750, Timeout.Infinite);
    }

    // ---- Source A: usage.json from the statusline bridge (cli mode only) ----

    private void ReadUsageFile()
    {
        try
        {
            if (!File.Exists(Config.UsageFilePath))
                return;

            // The bridge replaces the file atomically; retry briefly around the swap.
            string? text = null;
            for (int i = 0; i < 3 && text is null; i++)
            {
                try { text = File.ReadAllText(Config.UsageFilePath); }
                catch (IOException) { Thread.Sleep(100); }
            }
            if (text is null)
                return;

            if (JsonNode.Parse(text) is not JsonObject obj)
                return;

            Offer(
                ParseFileWindow(obj["five_hour"]),
                ParseFileWindow(obj["seven_day"]),
                ParseFileWindow(obj["seven_day_opus"]),
                ParseFileWindow(obj["seven_day_sonnet"]),
                extra: null,
                session: ParseSession(obj["session"]));
        }
        catch
        {
            // Bad snapshot; keep last known state.
        }
    }

    private static WindowUsage? ParseFileWindow(JsonNode? node)
    {
        if (node is not JsonObject o || o["pct"] is not JsonNode pct)
            return null;
        return new WindowUsage
        {
            Percent = pct.GetValue<double>(),
            ResetsAt = o["resetsAt"]?.GetValue<long?>() ?? 0,
            FetchedAt = o["fetchedAt"]?.GetValue<long?>() ?? 0,
            Source = "statusline",
        };
    }

    private static SessionInfo? ParseSession(JsonNode? node)
    {
        if (node is not JsonObject o)
            return null;
        return new SessionInfo
        {
            Model = o["model"]?.GetValue<string>() ?? "",
            CostUsd = o["costUsd"]?.GetValue<double?>() ?? 0,
            DurationMs = o["durationMs"]?.GetValue<long?>() ?? 0,
            LinesAdded = o["linesAdded"]?.GetValue<long?>() ?? 0,
            LinesRemoved = o["linesRemoved"]?.GetValue<long?>() ?? 0,
            Workspace = o["workspace"]?.GetValue<string>() ?? "",
            FetchedAt = o["fetchedAt"]?.GetValue<long?>() ?? 0,
        };
    }

    // ---- Source B: OAuth usage endpoint ----

    private bool _profileFetched;

    private void PollApi()
    {
        try
        {
            string? token = GetToken();
            if (token is null)
                return;

            // Detect a different token → different (or re-signed) account: drop stale data.
            string mark = token.Length > 24 ? token[..24] : token;
            bool tokenChanged;
            lock (_gate)
            {
                tokenChanged = _lastTokenMark.Length > 0 && _lastTokenMark != mark;
                _lastTokenMark = mark;
                if (tokenChanged)
                {
                    Log.Write("token changed → clearing cached usage data");
                    _fiveHour = _sevenDay = _sevenDayOpus = _sevenDaySonnet = null;
                    _extraUsage = null;
                    _session = null;
                    _profileFetched = false;
                }
            }
            if (tokenChanged)
                UsageUpdated?.Invoke(Current);

            bool needProfile;
            lock (_gate)
                needProfile = !_profileFetched || _account is null;
            if (needProfile)
                FetchProfile(token);

            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var resp = Http.Send(req);
            if (!resp.IsSuccessStatusCode)
                return; // 401 → token rotated; next poll re-reads/refreshes

            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (JsonNode.Parse(body) is not JsonObject obj)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var scoped = ParseScopedLimits(obj["limits"], now);
            lock (_gate)
            {
                if (!_scoped.SequenceEqual(scoped))
                    _scoped = scoped;
            }
            Offer(
                ParseApiWindow(obj["five_hour"], now),
                ParseApiWindow(obj["seven_day"], now),
                ParseApiWindow(obj["seven_day_opus"], now),
                ParseApiWindow(obj["seven_day_sonnet"], now),
                ParseExtraUsage(obj["extra_usage"]),
                session: null);
        }
        catch
        {
            // Network failure — widget keeps showing last known data.
        }
    }

    private void FetchProfile(string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ProfileUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var resp = Http.Send(req);
            if (!resp.IsSuccessStatusCode)
                return;

            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (JsonNode.Parse(body) is not JsonObject obj)
                return;

            var org = obj["organization"] as JsonObject;
            var acct = obj["account"] as JsonObject;

            string plan = PrettyPlan(org?["organization_type"]?.GetValue<string>() ?? "");
            if (plan.Length == 0 && acct?["has_claude_max"]?.GetValue<bool?>() == true) plan = "Max";
            if (plan.Length == 0 && acct?["has_claude_pro"]?.GetValue<bool?>() == true) plan = "Pro";

            var info = new AccountInfo
            {
                Plan = plan,
                Organization = org?["name"]?.GetValue<string>() ?? "",
                RateLimitTier = org?["rate_limit_tier"]?.GetValue<string>() ?? "",
                Email = acct?["email"]?.GetValue<string>() ?? "",
            };

            bool changed;
            lock (_gate)
            {
                changed = !info.Equals(_account);
                _account = info;
                _profileFetched = true;
            }
            if (changed)
                UsageUpdated?.Invoke(Current);
        }
        catch
        {
            // Identity is cosmetic; usage polling continues without it.
        }
    }

    private static string PrettyPlan(string orgType) => orgType switch
    {
        "claude_team" => "Team",
        "claude_max" => "Max",
        "claude_pro" => "Pro",
        "claude_enterprise" => "Enterprise",
        _ => "",
    };

    private static WindowUsage? ParseApiWindow(JsonNode? node, long now)
    {
        if (node is not JsonObject o || o["utilization"] is not JsonNode util
            || util.GetValueKind() is JsonValueKind.Null)
            return null;

        long resetsAt = 0;
        if (o["resets_at"] is JsonNode r && r.GetValueKind() == JsonValueKind.String &&
            DateTimeOffset.TryParse(r.GetValue<string>(), out var dto))
            resetsAt = dto.ToUnixTimeSeconds();

        return new WindowUsage
        {
            Percent = util.GetValue<double>(),
            ResetsAt = resetsAt,
            FetchedAt = now,
            Source = "api",
        };
    }

    /// <summary>
    /// Reads model/surface-scoped weekly windows from the newer limits[] array
    /// (kind = "weekly_scoped"). These carry real data even when the flat
    /// seven_day_opus / seven_day_sonnet fields are null.
    /// </summary>
    private static List<ScopedWindow> ParseScopedLimits(JsonNode? node, long now)
    {
        var result = new List<ScopedWindow>();
        if (node is not JsonArray arr)
            return result;

        foreach (var item in arr)
        {
            if (item is not JsonObject o)
                continue;
            if (o["kind"]?.GetValue<string>() != "weekly_scoped")
                continue;
            if (o["percent"] is not JsonNode pct || pct.GetValueKind() is not JsonValueKind.Number)
                continue;

            string model = o["scope"]?["model"]?["display_name"]?.GetValue<string>() ?? "";
            string surface = o["scope"]?["surface"]?.GetValue<string>() ?? "";
            string label = model.Length > 0 ? $"Weekly · {model}"
                : surface.Length > 0 ? $"Weekly · {surface}"
                : "Weekly · scoped";

            long resetsAt = 0;
            if (o["resets_at"] is JsonNode r && r.GetValueKind() == JsonValueKind.String &&
                DateTimeOffset.TryParse(r.GetValue<string>(), out var dto))
                resetsAt = dto.ToUnixTimeSeconds();

            result.Add(new ScopedWindow(label, new WindowUsage
            {
                Percent = pct.GetValue<double>(),
                ResetsAt = resetsAt,
                FetchedAt = now,
                Source = "api",
            }));
        }
        return result;
    }

    private static ExtraUsageInfo? ParseExtraUsage(JsonNode? node)
    {
        if (node is not JsonObject o)
            return null;
        return new ExtraUsageInfo
        {
            IsEnabled = o["is_enabled"]?.GetValue<bool?>() ?? false,
            MonthlyLimit = AsDouble(o["monthly_limit"]),
            UsedCredits = AsDouble(o["used_credits"]),
            Utilization = AsDouble(o["utilization"]),
        };

        static double? AsDouble(JsonNode? n) =>
            n is not null && n.GetValueKind() is JsonValueKind.Number ? n.GetValue<double>() : null;
    }

    // ---- token acquisition ----

    private string? GetToken() => _standalone ? GetStandaloneToken() : ReadCliToken();

    private static string? GetStandaloneToken()
    {
        var tokens = TokenStore.Load();
        if (tokens is null || tokens.AccessToken.Length == 0)
            return null;

        if (!tokens.IsExpired)
            return tokens.AccessToken;

        if (tokens.RefreshToken.Length == 0)
            return null; // long-lived token that finally expired → user must sign in again

        var rotated = OAuthClient.Refresh(tokens.RefreshToken);
        if (rotated is null)
            return null;
        TokenStore.Save(rotated);
        return rotated.AccessToken;
    }

    private static string? ReadCliToken()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", ".credentials.json");
            if (!File.Exists(path))
                return null;

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject obj ||
                obj["claudeAiOauth"] is not JsonObject oauth)
                return null;

            long expiresAtMs = oauth["expiresAt"]?.GetValue<long?>() ?? 0;
            if (expiresAtMs > 0 && expiresAtMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000)
                return null; // expired or about to; Claude Code will refresh it on next use

            return oauth["accessToken"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static AccountInfo? ReadLocalAccountInfo()
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string plan = "", org = "", tier = "", email = "";

            string credPath = Path.Combine(home, ".claude", ".credentials.json");
            if (File.Exists(credPath) &&
                JsonNode.Parse(File.ReadAllText(credPath)) is JsonObject creds &&
                creds["claudeAiOauth"] is JsonObject oauth)
            {
                string sub = oauth["subscriptionType"]?.GetValue<string>() ?? "";
                if (sub.Length > 0)
                    plan = char.ToUpper(sub[0]) + sub[1..];
            }

            string cfgPath = Path.Combine(home, ".claude.json");
            if (File.Exists(cfgPath) &&
                JsonNode.Parse(File.ReadAllText(cfgPath)) is JsonObject cfg &&
                cfg["oauthAccount"] is JsonObject acct)
            {
                org = acct["organizationName"]?.GetValue<string>() ?? "";
                email = acct["emailAddress"]?.GetValue<string>() ?? "";
                tier = acct["userRateLimitTier"]?.GetValue<string>()
                    ?? acct["organizationRateLimitTier"]?.GetValue<string>() ?? "";
            }

            if (plan == "" && org == "" && tier == "")
                return null;
            return new AccountInfo { Plan = plan, Organization = org, RateLimitTier = tier, Email = email };
        }
        catch
        {
            return null;
        }
    }

    // ---- Merge ----

    private void Offer(WindowUsage? fiveHour, WindowUsage? sevenDay,
        WindowUsage? opus, WindowUsage? sonnet, ExtraUsageInfo? extra, SessionInfo? session)
    {
        bool changed = false;
        lock (_gate)
        {
            changed |= Newer(ref _fiveHour, fiveHour);
            changed |= Newer(ref _sevenDay, sevenDay);
            changed |= Newer(ref _sevenDayOpus, opus);
            changed |= Newer(ref _sevenDaySonnet, sonnet);
            if (extra is not null && !extra.Equals(_extraUsage))
            {
                _extraUsage = extra;
                changed = true;
            }
            if (session is not null && session.FetchedAt >= (_session?.FetchedAt ?? 0) && !session.Equals(_session))
            {
                _session = session;
                changed = true;
            }
        }
        if (changed)
            UsageUpdated?.Invoke(Current);

        static bool Newer(ref WindowUsage? slot, WindowUsage? candidate)
        {
            if (candidate is null || candidate.FetchedAt < (slot?.FetchedAt ?? 0) || candidate.Equals(slot))
                return false;
            slot = candidate;
            return true;
        }
    }

    public void Dispose()
    {
        _pollTimer.Dispose();
        _usageWatcher?.Dispose();
        _credsWatcher?.Dispose();
        _usageDebounce?.Dispose();
        _pollDebounce?.Dispose();
    }
}

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeLimitWidget;

/// <summary>
/// Aggregates usage data and raises <see cref="UsageUpdated"/>.
///
/// One instance serves ONE account; the app runs one per enabled account.
///
/// Account kinds:
///  - Cli: follows the Claude Code CLI login — token from ~/.claude/.credentials.json
///    (re-read every poll, watched for account switches) plus the statusline bridge file.
///  - OAuth: widget-owned token from <see cref="TokenStore"/> (DPAPI), refreshed when
///    expired. The statusline file is ignored — it reflects the CLI account, not this one.
///  - SetupToken: a pasted long-lived token; usable until it expires, never refreshed.
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

    private readonly AccountConfig _account_;
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

    /// <summary>
    /// Email of the account the cached figures were fetched for. Kept apart from
    /// <see cref="_account"/> (which is display identity and may be refreshed from
    /// local config) so a slot that changed hands is detected instead of showing
    /// one account's numbers under another's name.
    /// </summary>
    private string _dataOwnerEmail = "";
    private TokenStatus _tokenStatus = TokenStatus.Ok;

    /// <summary>
    /// Set when the API rejects this credential outright (e.g. a token without the
    /// user:profile scope). Explains a permanently empty widget.
    /// </summary>
    private string _apiNotice = "";

    /// <summary>
    /// Set when the credential is refused for a reason retrying cannot fix (a missing
    /// scope). Polling stops entirely: it can never succeed, and the failed requests
    /// get the token rate limited, which also blocks signing in again to repair it.
    /// Cleared by re-authenticating, which builds a fresh service.
    /// </summary>
    private bool _scopeBlocked;

    /// <summary>
    /// Fingerprint of the token the identity was last fetched for. A different token
    /// means the profile must be re-checked — the credential may now belong to someone
    /// else. It is only a trigger to re-ask, never grounds to discard data: tokens
    /// rotate constantly for one unchanged account.
    /// </summary>
    private string _profileTokenMark = "";

    private enum TokenStatus { Ok, Missing, Expired }

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
                    Notice = BuildNotice(),
                };
        }
    }

    /// <summary>The account this poller serves.</summary>
    public AccountConfig Account => _account_;

    /// <param name="stagger">
    /// Index among the running pollers; spreads first polls a few seconds apart so
    /// several accounts do not all hit the endpoint at the same instant.
    /// </param>
    public UsageService(AccountConfig account, Config config, int stagger = 0)
    {
        _account_ = account;
        int pollSeconds = Math.Max(60, config.ApiPollSeconds);

        Directory.CreateDirectory(Config.DataDir);

        // Seed from the last run so a reboot shows the previous reading (marked
        // stale) rather than an empty bar until the first poll succeeds.
        LoadCache();

        if (_account_.UsesStatusline)
        {
            var local = ReadLocalAccountInfo();
            DiscardIfDifferentOwner(local?.Email ?? "");
            _account = local ?? _account;
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
            TimeSpan.FromSeconds(2 + stagger * 3), TimeSpan.FromSeconds(pollSeconds));
    }

    /// <summary>Force an immediate re-read + API poll (tray "Refresh now").</summary>
    public void RefreshNow()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_account_.UsesStatusline)
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

    private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;
    private int _backoffSeconds;

    private void PollApi()
    {
        try
        {
            lock (_gate)
            {
                if (_scopeBlocked)
                    return; // hopeless until re-authenticated; do not add to the noise
                if (DateTimeOffset.UtcNow < _backoffUntil)
                    return; // rate limited; wait it out rather than making it worse
            }

            string? token = GetToken();
            if (token is null)
            {
                Log.Write($"[{_account_.Id}] no usable token ({_tokenStatus})");
                return;
            }

            // A changed token may be a changed person, so re-ask who this is. Without
            // this the first answer sticks forever and a CLI /login as someone else
            // shows the new figures under the previous name.
            string mark = Fingerprint(token);
            bool needProfile;
            lock (_gate)
            {
                if (_profileTokenMark != mark)
                {
                    _profileTokenMark = mark;
                    _profileFetched = false;
                }
                needProfile = !_profileFetched || _account is null;
            }
            if (needProfile)
                FetchProfile(token);

            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var resp = Http.Send(req);
            if ((int)resp.StatusCode == 429)
            {
                // Backing off matters: the limit is per token, so continuing to poll
                // every few minutes keeps it tripped and also blocks sign-ins.
                // The endpoint states how long to wait; trust it over guesswork and
                // only fall back to doubling when the header is absent.
                int wait = resp.Headers.RetryAfter?.Delta is { } delta
                    ? (int)delta.TotalSeconds
                    : resp.Headers.RetryAfter?.Date is { } date
                        ? (int)(date - DateTimeOffset.UtcNow).TotalSeconds
                        : 0;
                lock (_gate)
                {
                    _backoffSeconds = wait > 0
                        ? Math.Clamp(wait, 60, 3600)
                        : (_backoffSeconds == 0 ? 300 : Math.Min(_backoffSeconds * 2, 1800));
                    _backoffUntil = DateTimeOffset.UtcNow.AddSeconds(_backoffSeconds);
                }
                Log.Write($"usage poll rate limited; backing off {_backoffSeconds}s");
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                string why = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Log.Write($"[{_account_.Id}] usage poll failed HTTP {(int)resp.StatusCode}: {Trim(why)}");
                if ((int)resp.StatusCode == 403 && why.Contains("scope requirement"))
                {
                    lock (_gate)
                        _scopeBlocked = true;
                    Log.Write($"[{_account_.Id}] scope refused; polling stopped until re-authenticated");
                    SetApiNotice("This token lacks the user:profile scope — tray → Accounts → sign in again.");
                }
                else
                {
                    SetApiNotice("");
                }
                return; // 401 → token rotated; next poll re-reads/refreshes
            }
            SetApiNotice("");

            lock (_gate)
            {
                _backoffSeconds = 0;
                _backoffUntil = DateTimeOffset.MinValue;
            }

            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (JsonNode.Parse(body) is not JsonObject obj)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Log.Write($"[{_account_.Id}] usage poll ok; five_hour={(obj["five_hour"] is JsonObject fh ? fh["utilization"]?.ToString() ?? "null" : "absent")}");
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
            {
                string why = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Log.Write($"[{_account_.Id}] profile fetch failed HTTP {(int)resp.StatusCode}: {Trim(why)}");
                if ((int)resp.StatusCode == 403 && why.Contains("scope requirement"))
                {
                    lock (_gate)
                        _scopeBlocked = true;
                    Log.Write($"[{_account_.Id}] scope refused; polling stopped until re-authenticated");
                    SetApiNotice("This token lacks the user:profile scope — tray → Accounts → sign in again.");
                }
                return;
            }

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
                // A different person behind the same slot (CLI /login elsewhere, or a
                // re-auth to another account) invalidates every cached figure. Identity
                // is the right key here: access tokens rotate every few hours for the
                // very same account, so comparing tokens would discard good data.
                DiscardIfDifferentOwner(info.Email);
                if (info.Email.Length > 0)
                    _dataOwnerEmail = info.Email;

                changed = !info.Equals(_account);
                _account = info;
                _profileFetched = true;
            }
            if (changed)
                Publish();
        }
        catch
        {
            // Identity is cosmetic; usage polling continues without it.
        }
    }

    /// <summary>
    /// Drops cached figures when they demonstrably belong to a different account.
    /// Unknown identity on either side is not evidence of a change, so it keeps them.
    /// Caller holds <see cref="_gate"/> (or is still constructing).
    /// </summary>
    private void DiscardIfDifferentOwner(string currentEmail)
    {
        if (currentEmail.Length == 0 || _dataOwnerEmail.Length == 0)
        {
            if (_dataOwnerEmail.Length == 0)
                _dataOwnerEmail = currentEmail;
            return;
        }
        if (string.Equals(_dataOwnerEmail, currentEmail, StringComparison.OrdinalIgnoreCase))
            return;

        Log.Write($"slot changed hands ({_dataOwnerEmail} -> {currentEmail}); clearing cached usage");
        _fiveHour = _sevenDay = _sevenDayOpus = _sevenDaySonnet = null;
        _scoped = Array.Empty<ScopedWindow>();
        _extraUsage = null;
        _session = null;
        _account = null;
        _dataOwnerEmail = currentEmail;
    }

    /// <summary>Stable, non-reversible id for a token, to notice it was replaced.</summary>
    private static string Fingerprint(string token) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(token)))[..16];

    /// <summary>Short, single-line form of an error body for the log.</summary>
    private static string Trim(string body)
    {
        // ReplaceLineEndings keeps this free of escape sequences.
        string flat = body.ReplaceLineEndings(" ").Trim();
        return flat.Length > 160 ? flat[..160] : flat;
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

    private string? GetToken() => _account_.Kind switch
    {
        AccountKind.Cli => ReadCliToken(),
        _ => GetStoredToken(),
    };

    /// <summary>
    /// Token for an OAuth or setup-token account. Only OAuth accounts can refresh:
    /// a setup token has no refresh half, so when it lapses the user must re-add it.
    /// </summary>
    private string? GetStoredToken()
    {
        var tokens = TokenStore.Load(_account_.Id);
        if (tokens is null || tokens.AccessToken.Length == 0)
            return Fail(TokenStatus.Missing);

        if (!tokens.IsExpired)
            return Ok(tokens.AccessToken);

        if (tokens.RefreshToken.Length == 0)
            return Fail(TokenStatus.Expired); // long-lived token expired → re-add it

        var rotated = OAuthClient.Refresh(tokens.RefreshToken);
        if (rotated is null)
            return Fail(TokenStatus.Expired);
        TokenStore.Save(_account_.Id, rotated);
        return Ok(rotated.AccessToken);
    }

    private string? ReadCliToken()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", ".credentials.json");
            if (!File.Exists(path))
                return Fail(TokenStatus.Missing);

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject obj ||
                obj["claudeAiOauth"] is not JsonObject oauth)
                return Fail(TokenStatus.Missing);

            long expiresAtMs = oauth["expiresAt"]?.GetValue<long?>() ?? 0;
            if (expiresAtMs > 0 && expiresAtMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000)
                return Fail(TokenStatus.Expired); // Claude Code renews it on its next run

            string? token = oauth["accessToken"]?.GetValue<string>();
            return token is null ? Fail(TokenStatus.Missing) : Ok(token);
        }
        catch
        {
            return Fail(TokenStatus.Missing);
        }
    }

    private string? Fail(TokenStatus status)
    {
        SetTokenStatus(status);
        return null;
    }

    private string Ok(string token)
    {
        SetTokenStatus(TokenStatus.Ok);
        return token;
    }

    private void SetTokenStatus(TokenStatus status)
    {
        bool changed;
        lock (_gate)
        {
            changed = _tokenStatus != status;
            _tokenStatus = status;
        }
        if (changed)
            Publish();
    }

    /// <summary>
    /// Explains an empty or frozen widget. A missing/expired token is the common
    /// case after a reboot: Claude Code renews it only when it next runs.
    /// </summary>
    private void SetApiNotice(string notice)
    {
        bool changed;
        lock (_gate)
        {
            changed = _apiNotice != notice;
            _apiNotice = notice;
        }
        if (changed)
            Publish();
    }

    private string BuildNotice()
    {
        if (_apiNotice.Length > 0)
            return _apiNotice;
        if (_tokenStatus == TokenStatus.Ok)
            return "";
        bool expired = _tokenStatus == TokenStatus.Expired;
        return _account_.Kind switch
        {
            AccountKind.Cli => expired
                ? "Claude Code token expired — run claude once to renew it."
                : "No Claude Code login found — run claude and sign in.",
            AccountKind.SetupToken => expired
                ? "Setup token expired — tray → Accounts → re-add this account."
                : "No token stored — tray → Accounts → re-add this account.",
            _ => expired
                ? "Sign-in expired — tray → Accounts → sign in again."
                : "Not signed in — tray → Accounts → sign in.",
        };
    }

    // ---- last-known figures, so a restart is not a blank widget ----

    private void Publish()
    {
        var snapshot = Current;
        SaveCache(snapshot);
        UsageUpdated?.Invoke(snapshot);
    }

    private void SaveCache(UsageSnapshot snapshot)
    {
        if (!snapshot.HasData)
            return;
        try
        {
            Directory.CreateDirectory(Config.DataDir);
            var payload = new JsonObject
            {
                ["snapshot"] = JsonNode.Parse(JsonSerializer.Serialize(snapshot)),
            };
            string cachePath = Config.CacheFilePath(_account_.Id);
            string tmp = cachePath + ".tmp";
            File.WriteAllText(tmp, payload.ToJsonString());
            File.Move(tmp, cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"cache write failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void LoadCache()
    {
        try
        {
            string cachePath = Config.CacheFilePath(_account_.Id);
            if (!File.Exists(cachePath))
                return;
            if (JsonNode.Parse(File.ReadAllText(cachePath)) is not JsonObject payload)
                return;
            var snapshot = payload["snapshot"] is JsonNode n
                ? JsonSerializer.Deserialize<UsageSnapshot>(n.ToJsonString())
                : null;
            if (snapshot is null)
                return;

            lock (_gate)
            {
                // Restored with their original timestamps, so live data always wins
                // the merge and an elapsed window still decays to zero on its own.
                _fiveHour = snapshot.FiveHour;
                _sevenDay = snapshot.SevenDay;
                _sevenDayOpus = snapshot.SevenDayOpus;
                _sevenDaySonnet = snapshot.SevenDaySonnet;
                _scoped = snapshot.Scoped ?? Array.Empty<ScopedWindow>();
                _extraUsage = snapshot.ExtraUsage;
                _session ??= snapshot.Session;
                // Restoring the identity too is what lets the next profile fetch notice
                // that a different account now owns this slot and drop these figures.
                _account ??= snapshot.Account;
                _dataOwnerEmail = snapshot.Account?.Email ?? "";
            }
        }
        catch
        {
            // Corrupt cache -> start empty rather than crash.
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
            Publish();

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

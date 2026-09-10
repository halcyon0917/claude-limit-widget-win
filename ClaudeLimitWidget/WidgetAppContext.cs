namespace ClaudeLimitWidget;

/// <summary>
/// Owns one poller + one taskbar widget per enabled account, plus the shared tray
/// icon and the explorer-restart watcher.
///
/// Widgets are slotted right-to-left from the tray (slot 0 nearest the clock) and
/// only show their account name when more than one is on screen, so a single
/// account keeps the original uncluttered layout. When explorer dies it destroys
/// the embedded windows with the taskbar, so each is watched and rebuilt
/// individually while its poller and last figures survive untouched.
/// </summary>
public sealed class WidgetAppContext : ApplicationContext
{
    private sealed class Runtime : IDisposable
    {
        public required AccountConfig Account { get; init; }
        public UsageService? Service { get; set; }
        public TaskbarWindow Widget { get; set; } = null!;
        public UsageSnapshot Snapshot { get; set; } = UsageSnapshot.Empty;

        public bool WidgetDead => Widget.IsDisposed || Widget.NeedsRecreate;

        public void Dispose()
        {
            Service?.Dispose();
            try { Widget?.Dispose(); } catch { /* already gone */ }
        }
    }

    private readonly Config _config;
    private readonly TrayIcon _tray;
    private readonly MessageWindow _messageWindow;
    private readonly System.Windows.Forms.Timer _watchdog;
    private readonly SynchronizationContext _sync;
    private readonly List<Runtime> _runtimes = new();
    private readonly bool _demo;

    private System.Windows.Forms.Timer? _demoTimer;
    private bool _exiting;

    public WidgetAppContext(bool demo)
    {
        _demo = demo;
        _config = demo ? DemoConfig() : Config.Load();
        _sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _tray = new TrayIcon(_config);
        _tray.ExitRequested += () =>
        {
            _exiting = true;
            foreach (var runtime in _runtimes)
                try { runtime.Widget.Close(); } catch { }
            ExitThread();
        };
        _tray.RefreshRequested += () =>
        {
            foreach (var runtime in _runtimes)
                runtime.Service?.RefreshNow();
        };
        _tray.FloatModeChanged += floatMode =>
        {
            _config.FloatMode = floatMode;
            SaveConfig();
            foreach (var runtime in _runtimes.Where(r => !r.WidgetDead))
                runtime.Widget.SetFloatMode(floatMode);
        };
        _tray.TransparentChanged += transparent =>
        {
            _config.TransparentBackground = transparent;
            SaveConfig();
            foreach (var runtime in _runtimes.Where(r => !r.WidgetDead))
                runtime.Widget.SetTransparent(transparent);
        };
        _tray.AddAccountRequested += OnAddAccount;
        _tray.AddCliAccountRequested += OnAddCliAccount;
        _tray.AccountEnabledChanged += OnAccountEnabledChanged;
        _tray.AccountRenameRequested += OnAccountRename;
        _tray.AccountRemoveRequested += OnAccountRemove;
        _tray.AccountReauthRequested += OnAccountReauth;

        _messageWindow = new MessageWindow();
        _messageWindow.TaskbarCreated += () =>
        {
            Log.Write("TaskbarCreated broadcast received");
            ScheduleRecovery(1500);
        };

        // Belt and braces: a broadcast can be missed, so poll for dead widgets too.
        _watchdog = new System.Windows.Forms.Timer { Interval = 2000 };
        _watchdog.Tick += (_, _) =>
        {
            if (_exiting)
                return;
            foreach (var runtime in _runtimes.Where(r => r.WidgetDead).ToList())
            {
                Log.Write($"watchdog: widget for '{runtime.Account.Id}' dead, recreating");
                RecreateWidget(runtime);
            }
        };
        _watchdog.Start();

        BuildRuntimes();

        if (demo)
            _demoTimer = StartDemo();
    }

    // ---- runtime lifecycle ----

    private void BuildRuntimes()
    {
        foreach (var runtime in _runtimes)
            runtime.Dispose();
        _runtimes.Clear();

        var enabled = _config.Accounts.Where(a => a.Enabled).ToList();
        for (int slot = 0; slot < enabled.Count; slot++)
        {
            var runtime = new Runtime { Account = enabled[slot] };

            if (!_demo)
            {
                runtime.Service = new UsageService(enabled[slot], _config, slot);
                runtime.Snapshot = runtime.Service.Current;
                var captured = runtime;
                runtime.Service.UsageUpdated += snapshot =>
                    _sync.Post(_ => ApplyUsage(captured, snapshot), null);
            }

            runtime.Widget = CreateWidget(runtime, slot);
            runtime.Widget.SetUsage(runtime.Snapshot);
            runtime.Widget.Start();
            _runtimes.Add(runtime);
        }

        UpdateTray();
    }

    private TaskbarWindow CreateWidget(Runtime runtime, int slot)
    {
        var widget = new TaskbarWindow(_config.FloatMode);
        widget.SetStaleAfter(TimeSpan.FromMinutes(_config.StaleMinutes));
        widget.SetTransparent(_config.TransparentBackground);
        widget.Slot = slot;
        widget.SetAccountLabel(LabelFor(runtime));
        return widget;
    }

    /// <summary>Names appear only when more than one widget is on screen.</summary>
    private string LabelFor(Runtime runtime) =>
        _config.Accounts.Count(a => a.Enabled) > 1
            ? runtime.Account.DisplayName(runtime.Snapshot.Account)
            : "";

    private void RecreateWidget(Runtime runtime)
    {
        int slot = Math.Max(0, _runtimes.IndexOf(runtime));
        try { runtime.Widget.Dispose(); } catch { /* already gone */ }

        runtime.Widget = CreateWidget(runtime, slot);
        runtime.Widget.SetUsage(runtime.Snapshot);
        runtime.Widget.Start();
    }

    private void ScheduleRecovery(int delayMs)
    {
        var delay = new System.Windows.Forms.Timer { Interval = delayMs };
        delay.Tick += (_, _) =>
        {
            delay.Dispose();
            if (_exiting)
                return;
            foreach (var runtime in _runtimes.ToList())
            {
                if (runtime.WidgetDead)
                    RecreateWidget(runtime);
                else
                    runtime.Widget.OnTaskbarRecreated();
            }
        };
        delay.Start();
    }

    private void ApplyUsage(Runtime runtime, UsageSnapshot snapshot)
    {
        runtime.Snapshot = snapshot;
        if (!runtime.WidgetDead)
        {
            runtime.Widget.SetUsage(snapshot);
            runtime.Widget.SetAccountLabel(LabelFor(runtime));
        }
        UpdateTray();
    }

    private void UpdateTray()
    {
        var snapshots = _runtimes.ToDictionary(r => r.Account.Id, r => r.Snapshot);
        _tray.SetAccounts(_config.Accounts, snapshots);
        _tray.SetTooltip(_config.Accounts, snapshots);
    }

    private void SaveConfig()
    {
        if (!_demo)
            _config.Save();
    }

    // ---- account commands ----

    private void OnAddAccount()
    {
        if (_demo)
            return;

        using var dialog = new AddAccountDialog();
        if (dialog.ShowDialog() != DialogResult.OK || dialog.Tokens is null)
            return;

        if (Rejected(dialog.Tokens.AccessToken))
            return;

        var account = new AccountConfig
        {
            Id = AccountConfig.NewId(),
            Kind = dialog.Kind,
            Label = dialog.AccountLabel,
            Enabled = true,
        };
        TokenStore.Save(account.Id, dialog.Tokens);
        _config.Accounts.Add(account);
        SaveConfig();
        Log.Write($"account added: {account.Id} ({account.Kind})");
        BuildRuntimes();
    }

    private void OnAddCliAccount()
    {
        if (_demo || _config.Accounts.Any(a => a.Kind == AccountKind.Cli))
            return;

        _config.Accounts.Add(new AccountConfig { Id = "cli", Kind = AccountKind.Cli, Enabled = true });
        SaveConfig();
        BuildRuntimes();
    }

    private void OnAccountEnabledChanged(string id, bool enabled)
    {
        var account = _config.Accounts.FirstOrDefault(a => a.Id == id);
        if (account is null || account.Enabled == enabled)
            return;
        account.Enabled = enabled;
        SaveConfig();
        BuildRuntimes();
    }

    private void OnAccountRename(string id)
    {
        var account = _config.Accounts.FirstOrDefault(a => a.Id == id);
        if (account is null)
            return;

        var runtime = _runtimes.FirstOrDefault(r => r.Account.Id == id);
        string current = account.Label.Length > 0 ? account.Label : account.DisplayName(runtime?.Snapshot.Account);
        string? name = TextPrompt.Show("Rename account", "Name shown on the widget:", current);
        if (name is null)
            return;

        account.Label = name;
        SaveConfig();
        foreach (var r in _runtimes.Where(r => !r.WidgetDead))
            r.Widget.SetAccountLabel(LabelFor(r));
        UpdateTray();
    }

    private void OnAccountRemove(string id)
    {
        var account = _config.Accounts.FirstOrDefault(a => a.Id == id);
        if (account is null)
            return;

        string name = account.DisplayName(_runtimes.FirstOrDefault(r => r.Account.Id == id)?.Snapshot.Account);
        var confirm = MessageBox.Show(
            account.Kind == AccountKind.Cli
                ? $"Stop tracking “{name}”? The Claude Code login itself is untouched."
                : $"Remove “{name}”? Its stored token is deleted from this machine.",
            "Claude Limit Widget", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK)
            return;

        if (account.Kind != AccountKind.Cli)
            TokenStore.Clear(account.Id);
        TryDeleteCache(account.Id);

        _config.Accounts.Remove(account);
        SaveConfig();
        Log.Write($"account removed: {id}");
        BuildRuntimes();
    }

    private void OnAccountReauth(string id)
    {
        var account = _config.Accounts.FirstOrDefault(a => a.Id == id);
        if (account is null || account.Kind == AccountKind.Cli)
            return;

        using var dialog = new AddAccountDialog("Sign in again", account.Label);
        if (dialog.ShowDialog() != DialogResult.OK || dialog.Tokens is null)
            return;

        if (Rejected(dialog.Tokens.AccessToken))
            return;

        TokenStore.Save(account.Id, dialog.Tokens);
        account.Kind = dialog.Kind;
        account.Label = dialog.AccountLabel;
        SaveConfig();
        BuildRuntimes();
    }

    /// <summary>
    /// Verifies a credential can actually read usage before it becomes an account,
    /// so an inference-only token fails loudly here rather than as a widget that
    /// never fills in.
    /// </summary>
    private static bool Rejected(string token)
    {
        string? problem = ClaudeApi.DescribeUnusable(token);
        if (problem is null)
            return false;

        Log.Write("account rejected: " + problem.ReplaceLineEndings(" "));
        MessageBox.Show(problem, "Claude Limit Widget", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return true;
    }

    private static void TryDeleteCache(string accountId)
    {
        try { File.Delete(Config.CacheFilePath(accountId)); } catch { /* nothing to lose */ }
    }

    // ---- demo ----

    private static Config DemoConfig() => new()
    {
        // Peek, don't Load: demo must not migrate or overwrite the real config.
        TransparentBackground = Config.PeekTransparentBackground(),
        Accounts =
        {
            new AccountConfig { Id = "demo1", Kind = AccountKind.OAuth, Label = "personal", Enabled = true },
            new AccountConfig { Id = "demo2", Kind = AccountKind.Cli, Label = "work", Enabled = true },
        },
    };

    private System.Windows.Forms.Timer StartDemo()
    {
        // Sweep 0→100% so every mascot mood shows, with the second account offset
        // so the two widgets are visibly independent.
        double pct = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 200 };
        timer.Tick += (_, _) =>
        {
            pct = (pct + 0.5) % 104;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            for (int i = 0; i < _runtimes.Count; i++)
            {
                double basePct = (pct + i * 37) % 104;
                WindowUsage W(double p, long resetIn) => new()
                {
                    Percent = Math.Min(p, 100),
                    ResetsAt = now + resetIn,
                    FetchedAt = now,
                    Source = "demo",
                };
                ApplyUsage(_runtimes[i], new UsageSnapshot
                {
                    FiveHour = W(basePct, 4800),
                    SevenDay = W(basePct * 0.6, 86400 * 3),
                    Scoped = new[] { new ScopedWindow("Weekly · Fable", W(basePct * 0.8, 86400 * 3)) },
                    Session = new SessionInfo
                    {
                        Model = "Fable 5",
                        CostUsd = 1.23,
                        DurationMs = 18 * 60_000,
                        LinesAdded = 240,
                        LinesRemoved = 85,
                        Workspace = "claude-limit-widget-win",
                        FetchedAt = now,
                    },
                    Account = new AccountInfo
                    {
                        Plan = "Team",
                        RateLimitTier = "default_claude_max_5x",
                        Email = $"{_runtimes[i].Account.Label}@example.com",
                    },
                });
            }
        };
        timer.Start();
        return timer;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watchdog.Dispose();
            _demoTimer?.Dispose();
            _messageWindow.Dispose();
            _tray.Dispose();
            foreach (var runtime in _runtimes)
                runtime.Dispose();
            _runtimes.Clear();
        }
        base.Dispose(disposing);
    }
}

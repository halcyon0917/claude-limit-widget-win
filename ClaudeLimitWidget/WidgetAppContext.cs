namespace ClaudeLimitWidget;

/// <summary>
/// Owns the widget window, tray icon, data service, and explorer-restart watcher.
/// When explorer dies it destroys the embedded widget window with the taskbar, so
/// this context watches for that and recreates the whole form. It also swaps the
/// UsageService when the user changes the data source (CLI login vs standalone).
/// </summary>
public sealed class WidgetAppContext : ApplicationContext
{
    private readonly Config _config;
    private readonly TrayIcon _tray;
    private readonly MessageWindow _messageWindow;
    private readonly System.Windows.Forms.Timer? _demoTimer;
    private readonly System.Windows.Forms.Timer _watchdog;
    private readonly SynchronizationContext _sync;
    private readonly bool _demo;

    private TaskbarWindow _widget;
    private UsageService? _usageService;
    private UsageSnapshot _lastSnapshot = UsageSnapshot.Empty;
    private bool _exiting;

    public WidgetAppContext(bool demo)
    {
        _demo = demo;
        _config = Config.Load();

        _widget = CreateWidget();
        _sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _tray = new TrayIcon(_config);
        _tray.ExitRequested += () =>
        {
            _exiting = true;
            _widget.Close();
            ExitThread();
        };
        _tray.FloatModeChanged += floatMode =>
        {
            _config.FloatMode = floatMode;
            _config.Save();
            if (!_widget.IsDisposed && !_widget.NeedsRecreate)
                _widget.SetFloatMode(floatMode);
        };
        _tray.TransparentChanged += transparent =>
        {
            _config.TransparentBackground = transparent;
            _config.Save();
            if (!_widget.IsDisposed && !_widget.NeedsRecreate)
                _widget.SetTransparent(transparent);
        };
        _tray.RefreshRequested += () => _usageService?.RefreshNow();
        _tray.AuthModeChangeRequested += OnAuthModeChangeRequested;
        _tray.SignInRequested += () => PromptSignIn();
        _tray.SignOutRequested += OnSignOut;

        _messageWindow = new MessageWindow();
        _messageWindow.TaskbarCreated += () =>
        {
            Log.Write("TaskbarCreated broadcast received");
            ScheduleRecovery(1500);
        };

        // Belt and braces: also poll for a dead widget window (broadcast can be missed).
        _watchdog = new System.Windows.Forms.Timer { Interval = 2000 };
        _watchdog.Tick += (_, _) =>
        {
            if (!_exiting && (_widget.IsDisposed || _widget.NeedsRecreate))
            {
                Log.Write("watchdog: widget window dead, recreating");
                RecreateWidget();
            }
        };
        _watchdog.Start();

        if (demo)
        {
            _demoTimer = StartDemo();
        }
        else
        {
            StartService();
        }

        _widget.SetUsage(_lastSnapshot);
        _widget.Start();
    }

    // ---- data service lifecycle ----

    private void StartService()
    {
        _usageService?.Dispose();
        _usageService = new UsageService(_config);
        _usageService.UsageUpdated += snapshot => _sync.Post(_ => ApplyUsage(snapshot), null);
        ApplyUsage(_usageService.Current);
    }

    private void OnAuthModeChangeRequested(string mode)
    {
        if (mode == _config.AuthMode || _demo)
        {
            _tray.SetAuthMode(_config.AuthMode);
            return;
        }

        if (mode == "standalone" && !TokenStore.Exists)
        {
            // Needs a token first; a successful sign-in flips the mode.
            if (!PromptSignIn())
                _tray.SetAuthMode(_config.AuthMode);
            return;
        }

        SwitchMode(mode);
    }

    /// <summary>Shows the sign-in dialog; on success switches to standalone. Returns true when signed in.</summary>
    private bool PromptSignIn()
    {
        if (_demo)
            return false;
        using var dialog = new SignInDialog();
        if (dialog.ShowDialog() != DialogResult.OK)
            return false;
        SwitchMode("standalone");
        return true;
    }

    private void OnSignOut()
    {
        TokenStore.Clear();
        if (_config.AuthMode == "standalone")
            SwitchMode("cli");
        else
            _tray.SetAuthMode(_config.AuthMode);
    }

    private void SwitchMode(string mode)
    {
        Log.Write($"auth mode → {mode}");
        _config.AuthMode = mode;
        _config.Save();
        _tray.SetAuthMode(mode);
        _lastSnapshot = UsageSnapshot.Empty;
        if (!_widget.IsDisposed && !_widget.NeedsRecreate)
            _widget.SetUsage(_lastSnapshot);
        if (!_demo)
            StartService();
    }

    // ---- widget window lifecycle ----

    private TaskbarWindow CreateWidget()
    {
        var widget = new TaskbarWindow(_config.FloatMode);
        widget.SetStaleAfter(TimeSpan.FromMinutes(_config.StaleMinutes));
        widget.SetTransparent(_config.TransparentBackground);
        return widget;
    }

    private void ScheduleRecovery(int delayMs)
    {
        var delay = new System.Windows.Forms.Timer { Interval = delayMs };
        delay.Tick += (_, _) =>
        {
            delay.Dispose();
            if (_exiting)
                return;
            if (_widget.IsDisposed || _widget.NeedsRecreate)
                RecreateWidget();
            else
                _widget.OnTaskbarRecreated();
        };
        delay.Start();
    }

    private void RecreateWidget()
    {
        try { _widget.Dispose(); } catch { /* already gone */ }

        _widget = CreateWidget();
        _widget.SetUsage(_lastSnapshot);
        _widget.Start();
    }

    private void ApplyUsage(UsageSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        if (!_widget.IsDisposed && !_widget.NeedsRecreate)
            _widget.SetUsage(snapshot);
        _tray.SetTooltip(snapshot);
        _tray.SetAccount(snapshot.Account, _config.AuthMode);
    }

    private System.Windows.Forms.Timer StartDemo()
    {
        // Sweep 0→100% over ~40 s so every mascot mood shows
        // (cheering <10, walking <50, workout <85, waving ≥85).
        double pct = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 200 };
        timer.Tick += (_, _) =>
        {
            pct = (pct + 0.5) % 104;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            WindowUsage W(double p, long resetIn) => new()
            {
                Percent = Math.Min(p, 100),
                ResetsAt = now + resetIn,
                FetchedAt = now,
                Source = "demo",
            };
            // Only the rows a typical account actually returns — optional windows
            // (Opus/Sonnet, extra usage) stay off so the demo doesn't promise
            // more than the live API delivers.
            ApplyUsage(new UsageSnapshot
            {
                FiveHour = W(pct, 4800),
                SevenDay = W(pct * 0.6, 86400 * 3),
                Scoped = new[] { new ScopedWindow("Weekly · Fable", W(pct * 0.8, 86400 * 3)) },
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
                    Email = "demo@example.com",
                },
            });
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
            _usageService?.Dispose();
            _messageWindow.Dispose();
            _tray.Dispose();
            _widget.Dispose();
        }
        base.Dispose(disposing);
    }
}

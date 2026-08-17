using System.Runtime.InteropServices;

namespace ClaudeLimitWidget;

/// <summary>Tray icon giving access to settings even when the embed is misbehaving.</summary>
public sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly NotifyIcon _icon;
    private readonly IntPtr _hIcon;
    private readonly ToolStripMenuItem _cliMode;
    private readonly ToolStripMenuItem _standaloneMode;
    private readonly ToolStripMenuItem _signOut;
    private readonly ToolStripMenuItem _accountLabel;

    public event Action? RefreshRequested;
    public event Action<bool>? FloatModeChanged;
    public event Action<bool>? TransparentChanged;
    /// <summary>Raised with the requested auth mode ("cli" | "standalone").</summary>
    public event Action<string>? AuthModeChangeRequested;
    public event Action? SignInRequested;
    public event Action? SignOutRequested;
    public event Action? ExitRequested;

    public TrayIcon(Config config)
    {
        _icon = new NotifyIcon
        {
            Text = "Claude Limit Widget",
            Visible = true,
        };

        // Draw Clawd as the tray icon
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            ClawdSprite.Draw(g, new Rectangle(0, 2, 32, 28), blink: false, bounceOffset: 0, worstPercent: 0);
            _hIcon = bmp.GetHicon();
            _icon.Icon = Icon.FromHandle(_hIcon);
        }

        var menu = new ContextMenuStrip();

        _accountLabel = new ToolStripMenuItem("Account: …") { Enabled = false };

        var refresh = new ToolStripMenuItem("Refresh now");
        refresh.Click += (_, _) => RefreshRequested?.Invoke();

        // Data source submenu
        _cliMode = new ToolStripMenuItem("Claude Code CLI login");
        _cliMode.Click += (_, _) => AuthModeChangeRequested?.Invoke("cli");
        _standaloneMode = new ToolStripMenuItem("Standalone account");
        _standaloneMode.Click += (_, _) => AuthModeChangeRequested?.Invoke("standalone");
        var signIn = new ToolStripMenuItem("Sign in / change account…");
        signIn.Click += (_, _) => SignInRequested?.Invoke();
        _signOut = new ToolStripMenuItem("Sign out of standalone account");
        _signOut.Click += (_, _) => SignOutRequested?.Invoke();

        var source = new ToolStripMenuItem("Data source");
        source.DropDownItems.AddRange(new ToolStripItem[]
        {
            _cliMode,
            _standaloneMode,
            new ToolStripSeparator(),
            signIn,
            _signOut,
        });

        var embed = new ToolStripMenuItem("Embed in taskbar") { Checked = !config.FloatMode, CheckOnClick = true };
        embed.CheckedChanged += (_, _) => FloatModeChanged?.Invoke(!embed.Checked);

        var transparent = new ToolStripMenuItem("Transparent background")
        {
            Checked = config.TransparentBackground,
            CheckOnClick = true,
        };
        transparent.CheckedChanged += (_, _) => TransparentChanged?.Invoke(transparent.Checked);

        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.IsEnabled(), CheckOnClick = true };
        autostart.CheckedChanged += (_, _) =>
        {
            try { Autostart.SetEnabled(autostart.Checked); }
            catch { autostart.Checked = Autostart.IsEnabled(); }
        };

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.AddRange(new ToolStripItem[]
        {
            _accountLabel,
            new ToolStripSeparator(),
            refresh,
            source,
            new ToolStripSeparator(),
            embed,
            transparent,
            autostart,
            new ToolStripSeparator(),
            exit,
        });
        _icon.ContextMenuStrip = menu;

        SetAuthMode(config.AuthMode);
    }

    public void SetAuthMode(string mode)
    {
        bool standalone = mode == "standalone";
        _cliMode.Checked = !standalone;
        _standaloneMode.Checked = standalone;
        _signOut.Enabled = TokenStore.Exists;
    }

    public void SetAccount(AccountInfo? account, string mode)
    {
        string who = account?.Email is { Length: > 0 } email ? email : "(unknown)";
        _accountLabel.Text = $"Account: {who} · {(mode == "standalone" ? "standalone" : "CLI")}";
    }

    public void SetTooltip(UsageSnapshot usage)
    {
        string F(WindowUsage? w) => w is null ? "—" : $"{w.EffectivePercent:0}%";
        string text = $"Claude · 5h {F(usage.FiveHour)} · week {F(usage.SevenDay)}";
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero)
            DestroyIcon(_hIcon);
    }
}

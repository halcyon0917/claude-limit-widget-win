using System.Runtime.InteropServices;

namespace ClaudeLimitWidget;

/// <summary>
/// Tray icon and menu. Owns the Accounts submenu (one entry per tracked account,
/// plus the add/remove commands) and the global display toggles, so settings stay
/// reachable even when an embed is misbehaving.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly NotifyIcon _icon;
    private readonly IntPtr _hIcon;
    private readonly ToolStripMenuItem _accountsMenu;

    public event Action? RefreshRequested;
    public event Action<bool>? FloatModeChanged;
    public event Action<bool>? TransparentChanged;
    public event Action? ExitRequested;

    /// <summary>Add a browser/token account.</summary>
    public event Action? AddAccountRequested;
    /// <summary>Track the Claude Code CLI login as an account.</summary>
    public event Action? AddCliAccountRequested;
    /// <summary>Show/hide one account's widget (id, enabled).</summary>
    public event Action<string, bool>? AccountEnabledChanged;
    public event Action<string>? AccountRenameRequested;
    public event Action<string>? AccountRemoveRequested;
    public event Action<string>? AccountReauthRequested;

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

        var refresh = new ToolStripMenuItem("Refresh now");
        refresh.Click += (_, _) => RefreshRequested?.Invoke();

        _accountsMenu = new ToolStripMenuItem("Accounts");

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
            _accountsMenu,
            new ToolStripSeparator(),
            refresh,
            new ToolStripSeparator(),
            embed,
            transparent,
            autostart,
            new ToolStripSeparator(),
            exit,
        });
        _icon.ContextMenuStrip = menu;
    }

    /// <summary>Rebuilds the Accounts submenu from the current account list and data.</summary>
    public void SetAccounts(IReadOnlyList<AccountConfig> accounts, IReadOnlyDictionary<string, UsageSnapshot> snapshots)
    {
        _accountsMenu.DropDownItems.Clear();

        bool hasCli = accounts.Any(a => a.Kind == AccountKind.Cli);

        foreach (var account in accounts)
        {
            snapshots.TryGetValue(account.Id, out var snapshot);
            string name = account.DisplayName(snapshot?.Account);
            string figures = snapshot is { HasData: true }
                ? $"  ({snapshot.FiveHour?.EffectivePercent ?? 0:0}% / {snapshot.SevenDay?.EffectivePercent ?? 0:0}%)"
                : "";

            var entry = new ToolStripMenuItem($"{name}{figures}");

            var show = new ToolStripMenuItem("Show in taskbar")
            {
                Checked = account.Enabled,
                CheckOnClick = true,
            };
            string id = account.Id;
            show.CheckedChanged += (_, _) => AccountEnabledChanged?.Invoke(id, show.Checked);

            var rename = new ToolStripMenuItem("Rename…");
            rename.Click += (_, _) => AccountRenameRequested?.Invoke(id);

            entry.DropDownItems.Add(show);
            entry.DropDownItems.Add(rename);

            if (account.Kind != AccountKind.Cli)
            {
                var reauth = new ToolStripMenuItem("Sign in again…");
                reauth.Click += (_, _) => AccountReauthRequested?.Invoke(id);
                entry.DropDownItems.Add(reauth);
            }

            var remove = new ToolStripMenuItem(account.Kind == AccountKind.Cli ? "Stop tracking" : "Remove account");
            remove.Click += (_, _) => AccountRemoveRequested?.Invoke(id);
            entry.DropDownItems.Add(new ToolStripSeparator());
            entry.DropDownItems.Add(remove);

            // Kind + account identity, for when two accounts share a short name
            string kindText = account.Kind switch
            {
                AccountKind.Cli => "Claude Code CLI login",
                AccountKind.SetupToken => "setup token",
                _ => "browser sign-in",
            };
            string email = snapshot?.Account?.Email ?? "";
            entry.ToolTipText = email.Length > 0 ? $"{email} · {kindText}" : kindText;

            _accountsMenu.DropDownItems.Add(entry);
        }

        if (accounts.Count > 0)
            _accountsMenu.DropDownItems.Add(new ToolStripSeparator());

        var add = new ToolStripMenuItem("Add account (sign in or token)…");
        add.Click += (_, _) => AddAccountRequested?.Invoke();
        _accountsMenu.DropDownItems.Add(add);

        if (!hasCli)
        {
            var addCli = new ToolStripMenuItem("Track Claude Code CLI login");
            addCli.Click += (_, _) => AddCliAccountRequested?.Invoke();
            _accountsMenu.DropDownItems.Add(addCli);
        }
    }

    /// <summary>Hover text: the worst window per account, trimmed to the 63-char limit.</summary>
    public void SetTooltip(IReadOnlyList<AccountConfig> accounts, IReadOnlyDictionary<string, UsageSnapshot> snapshots)
    {
        var parts = new List<string>();
        foreach (var account in accounts.Where(a => a.Enabled))
        {
            snapshots.TryGetValue(account.Id, out var snapshot);
            string name = account.DisplayName(snapshot?.Account);
            parts.Add(snapshot is { HasData: true }
                ? $"{name} {snapshot.FiveHour?.EffectivePercent ?? 0:0}%/{snapshot.SevenDay?.EffectivePercent ?? 0:0}%"
                : $"{name} —");
        }

        string text = parts.Count == 0 ? "Claude Limit Widget" : string.Join(" · ", parts);
        _icon.Text = text.Length <= 63 ? text : text[..62] + "…";
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero)
            DestroyIcon(_hIcon);
    }
}

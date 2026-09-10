using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeLimitWidget;

public sealed class Config
{
    [JsonPropertyName("floatMode")]
    public bool FloatMode { get; set; }

    [JsonPropertyName("apiPollSeconds")]
    public int ApiPollSeconds { get; set; } = 180;

    [JsonPropertyName("staleMinutes")]
    public int StaleMinutes { get; set; } = 30;

    /// <summary>Skip the dark pill background so the widget sits directly on the taskbar.</summary>
    [JsonPropertyName("transparentBackground")]
    public bool TransparentBackground { get; set; }

    /// <summary>
    /// Tracked accounts, in taskbar order (first = nearest the tray). Each enabled
    /// entry gets its own widget.
    /// </summary>
    [JsonPropertyName("accounts")]
    public List<AccountConfig> Accounts { get; set; } = new();

    /// <summary>
    /// Legacy single-source setting ("cli" | "standalone"), kept only so an existing
    /// install migrates into <see cref="Accounts"/> on first load.
    /// </summary>
    [JsonPropertyName("authMode")]
    public string? AuthMode { get; set; }

    // ---------------------------------------------------------------------------
    // Where state lives
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The one folder holding everything the widget persists: config, tokens, caches,
    /// the statusline hand-off file and the log.
    ///
    /// It is deliberately NOT under AppData. Windows virtualizes AppData per MSIX
    /// package: a process that inherits Claude Desktop's package identity — the
    /// statusline bridge it spawns, or anything launched from its integrated
    /// terminal — reads and writes a private copy under
    /// Packages\Claude_*\LocalCache, invisible to a normally launched widget. Two
    /// instances then diverge without any error, and the bridge can never feed the
    /// widget. A dot-folder in the profile root is not virtualized, so every context
    /// sees the same files.
    /// </summary>
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-limit-widget");

    /// <summary>Config and token files. Same folder as <see cref="DataDir"/>; kept for call sites.</summary>
    public static string Dir => Root;

    /// <summary>Caches, usage.json and the log. Same folder as <see cref="Dir"/>; kept for call sites.</summary>
    public static string DataDir => Root;

    private static string FilePath => Path.Combine(Root, "config.json");

    /// <summary>Snapshot written by the statusline bridge, read by the CLI account's poller.</summary>
    public static string UsageFilePath => Path.Combine(Root, "usage.json");

    /// <summary>
    /// Last known figures for one account, persisted so a restart shows the previous
    /// reading instead of an empty bar while waiting for the first successful poll.
    /// </summary>
    public static string CacheFilePath(string accountId) =>
        Path.Combine(Root, $"cache-{accountId}.json");

    /// <summary>Pre-multi-account cache file, migrated to the first account on upgrade.</summary>
    public static string LegacyCacheFilePath => Path.Combine(Root, "cache.json");

    [JsonIgnore]
    public IEnumerable<AccountConfig> EnabledAccounts => Accounts.Where(a => a.Enabled);

    // ---------------------------------------------------------------------------
    // Package identity (diagnostics)
    // ---------------------------------------------------------------------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? fullName);

    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    /// <summary>
    /// Full name of the MSIX package this process runs under, or null when none is
    /// reported. Diagnostic only, and best-effort: a process can have its AppData
    /// virtualized by a package's silo without carrying a package identity this API
    /// reports — instances launched from Claude Desktop's integrated terminal were
    /// redirected yet answered "no package" here. The data root living outside
    /// AppData is what actually makes the widget immune; this just annotates the log
    /// when identity IS visible (e.g. a bridge spawned directly by the packaged app).
    /// </summary>
    public static string? CurrentPackage()
    {
        try
        {
            int length = 0;
            int rc = GetCurrentPackageFullName(ref length, null);
            if (rc == APPMODEL_ERROR_NO_PACKAGE || length == 0)
                return null;
            var sb = new StringBuilder(length);
            rc = GetCurrentPackageFullName(ref length, sb);
            return rc == 0 ? sb.ToString() : null;
        }
        catch
        {
            return null; // pre-Win8 or unusual host; treat as unpackaged
        }
    }

    // ---------------------------------------------------------------------------
    // Load / save
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Reads a single display preference without migrating or saving. Demo mode uses
    /// this so running --demo never mutates the real configuration.
    /// </summary>
    public static bool PeekTransparentBackground()
    {
        try
        {
            if (!File.Exists(FilePath))
                return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            return doc.RootElement.TryGetProperty("transparentBackground", out var value)
                   && value.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    public static Config Load()
    {
        string? package = CurrentPackage();
        if (package is not null)
            Log.Write($"running inside package {package}; AppData would be virtualized here, state root is {Root}");

        MigrateFromAppData();

        Config config;
        try
        {
            config = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config()
                : new Config();
        }
        catch (Exception ex)
        {
            Log.Write($"config load failed ({ex.Message}); using defaults");
            config = new Config();
        }

        if (config.Migrate())
            config.Save();

        Log.Write($"config loaded: {config.Accounts.Count} account(s), transparent={config.TransparentBackground} float={config.FloatMode}");
        return config;
    }

    /// <summary>
    /// Legacy AppData locations from before the root moved, in priority order. The
    /// process's own AppData view comes first; then any MSIX package's virtualized
    /// LocalCache, where an instance launched from inside such a package left its state.
    /// </summary>
    private static IEnumerable<(string Roaming, string Local)> LegacyAppDataRoots()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (appData.Length > 0 && localAppData.Length > 0)
            yield return (Path.Combine(appData, "ClaudeLimitWidget"), Path.Combine(localAppData, "ClaudeLimitWidget"));

        string packages = localAppData.Length > 0 ? Path.Combine(localAppData, "Packages") : "";
        if (packages.Length == 0 || !Directory.Exists(packages))
            yield break;

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(packages); }
        catch { yield break; }

        foreach (string pkg in dirs)
            yield return (Path.Combine(pkg, "LocalCache", "Roaming", "ClaudeLimitWidget"),
                          Path.Combine(pkg, "LocalCache", "Local", "ClaudeLimitWidget"));
    }

    /// <summary>
    /// One-time move of an existing install's files into <see cref="Root"/>. Copies
    /// rather than moves, so the old location stays intact as a fallback.
    /// </summary>
    private static void MigrateFromAppData()
    {
        try
        {
            if (File.Exists(FilePath))
                return; // already on the new root

            foreach (var (roaming, local) in LegacyAppDataRoots())
            {
                if (!File.Exists(Path.Combine(roaming, "config.json")))
                    continue;

                Directory.CreateDirectory(Root);
                CopyMatching(roaming, "config.json");
                CopyMatching(roaming, "auth*.dat");
                CopyMatching(local, "cache*.json");
                CopyMatching(local, "usage.json");
                Log.Write($"migrated state into {Root} from {roaming} + {local}");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"state migration skipped: {ex.Message}");
        }
    }

    private static void CopyMatching(string sourceDir, string pattern)
    {
        if (!Directory.Exists(sourceDir))
            return;
        foreach (string file in Directory.EnumerateFiles(sourceDir, pattern))
        {
            string target = Path.Combine(Root, Path.GetFileName(file));
            if (!File.Exists(target))
                File.Copy(file, target);
        }
    }

    /// <summary>
    /// Brings a pre-multi-account config forward. The old install had exactly one
    /// source (authMode) and one token file, so it becomes the first account and
    /// keeps working untouched. Returns true when something changed.
    /// </summary>
    private bool Migrate()
    {
        if (Accounts.Count > 0)
        {
            AuthMode = null;
            return false;
        }

        bool standalone = AuthMode == "standalone";
        var account = new AccountConfig
        {
            Id = standalone ? TokenStore.LegacyAccountId : "cli",
            Kind = standalone ? AccountKind.OAuth : AccountKind.Cli,
            Enabled = true,
        };
        Accounts.Add(account);
        AuthMode = null;

        // Only claim the old auth.dat when the migrated account is the one that
        // used it; a CLI account has no stored token, so leave the file alone.
        if (standalone)
            TokenStore.MigrateLegacy();
        MigrateLegacyCache(account.Id);

        Log.Write($"migrated legacy config to account '{account.Id}' ({account.Kind})");
        return true;
    }

    private static void MigrateLegacyCache(string accountId)
    {
        try
        {
            if (File.Exists(LegacyCacheFilePath) && !File.Exists(CacheFilePath(accountId)))
                File.Move(LegacyCacheFilePath, CacheFilePath(accountId));
        }
        catch
        {
            // A lost cache only costs one poll.
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }));
    }
}

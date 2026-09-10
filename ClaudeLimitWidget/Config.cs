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

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeLimitWidget");

    private static string FilePath => Path.Combine(Dir, "config.json");

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeLimitWidget");

    /// <summary>Path of the usage snapshot written by the statusline bridge (CLI account only).</summary>
    public static string UsageFilePath => Path.Combine(DataDir, "usage.json");

    /// <summary>
    /// Last known figures for one account, persisted so a restart shows the previous
    /// reading instead of an empty bar while waiting for the first successful poll.
    /// </summary>
    public static string CacheFilePath(string accountId) =>
        Path.Combine(DataDir, $"cache-{accountId}.json");

    /// <summary>Pre-multi-account cache file, migrated to the first account on upgrade.</summary>
    public static string LegacyCacheFilePath => Path.Combine(DataDir, "cache.json");

    [JsonIgnore]
    public IEnumerable<AccountConfig> EnabledAccounts => Accounts.Where(a => a.Enabled);

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
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }));
    }
}

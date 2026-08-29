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

    /// <summary>"cli" = follow the Claude Code CLI login; "standalone" = widget's own token.</summary>
    [JsonPropertyName("authMode")]
    public string AuthMode { get; set; } = "cli";

    /// <summary>Skip the dark pill background so the widget sits directly on the taskbar.</summary>
    [JsonPropertyName("transparentBackground")]
    public bool TransparentBackground { get; set; }

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeLimitWidget");

    private static string FilePath => Path.Combine(Dir, "config.json");

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeLimitWidget");

    /// <summary>Path of the usage snapshot written by the statusline bridge.</summary>
    public static string UsageFilePath => Path.Combine(DataDir, "usage.json");

    /// <summary>
    /// Last known figures, persisted so a restart shows the previous reading
    /// instead of an empty bar while waiting for the first successful poll.
    /// </summary>
    public static string CacheFilePath => Path.Combine(DataDir, "cache.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    Log.Write($"config loaded: transparent={loaded.TransparentBackground} float={loaded.FloatMode} auth={loaded.AuthMode}");
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"config load failed ({ex.Message}); using defaults");
        }
        return new Config();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        Log.Write($"config saved: transparent={TransparentBackground} float={FloatMode} auth={AuthMode}");
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

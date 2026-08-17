using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeLimitWidget;

/// <summary>
/// Runs when the exe is invoked with --statusline by Claude Code.
/// Reads the statusline JSON from stdin, merges rate_limits + session details into
/// %LOCALAPPDATA%\ClaudeLimitWidget\usage.json (atomically), and prints a
/// short status text that Claude Code displays as the statusline.
/// </summary>
public static class StatuslineBridge
{
    private static readonly string[] Windows = { "five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet" };

    public static int Run()
    {
        string input;
        try
        {
            input = Console.In.ReadToEnd();
        }
        catch
        {
            return 0;
        }

        JsonNode? root = null;
        try { root = JsonNode.Parse(input); } catch { /* malformed stdin */ }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        JsonNode? rateLimits = root?["rate_limits"];

        // Load the previous snapshot so anything missing from this update keeps its last value.
        JsonObject snapshot = LoadExisting() ?? new JsonObject();

        foreach (string w in Windows)
            MergeWindow(snapshot, rateLimits, w, now);

        bool sessionCaptured = MergeSession(snapshot, root, now);

        if (rateLimits is not null || sessionCaptured)
        {
            try
            {
                Directory.CreateDirectory(Config.DataDir);
                string tmp = Config.UsageFilePath + ".tmp";
                File.WriteAllText(tmp, snapshot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, Config.UsageFilePath, overwrite: true);
            }
            catch
            {
                // Never break the statusline over an IO race.
            }
        }

        Console.Out.Write(BuildStatusText(root, snapshot));
        return 0;
    }

    private static JsonObject? LoadExisting()
    {
        try
        {
            if (File.Exists(Config.UsageFilePath))
                return JsonNode.Parse(File.ReadAllText(Config.UsageFilePath)) as JsonObject;
        }
        catch { }
        return null;
    }

    private static void MergeWindow(JsonObject snapshot, JsonNode? rateLimits, string name, long now)
    {
        JsonNode? win = rateLimits?[name];
        if (win is null)
            return;

        double? pct = win["used_percentage"]?.GetValue<double?>();
        if (pct is null)
            return;

        snapshot[name] = new JsonObject
        {
            ["pct"] = pct.Value,
            ["resetsAt"] = win["resets_at"]?.GetValue<long?>() ?? 0,
            ["fetchedAt"] = now,
            ["source"] = "statusline",
        };
    }

    private static bool MergeSession(JsonObject snapshot, JsonNode? root, long now)
    {
        if (root is null)
            return false;

        string? model = root["model"]?["display_name"]?.GetValue<string>();
        JsonNode? cost = root["cost"];
        if (model is null && cost is null)
            return false;

        string workspace = "";
        try
        {
            string? dir = root["workspace"]?["project_dir"]?.GetValue<string>()
                       ?? root["workspace"]?["current_dir"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(dir))
                workspace = Path.GetFileName(dir.TrimEnd('\\', '/'));
        }
        catch { }

        snapshot["session"] = new JsonObject
        {
            ["model"] = model ?? "",
            ["costUsd"] = cost?["total_cost_usd"]?.GetValue<double?>() ?? 0,
            ["durationMs"] = cost?["total_duration_ms"]?.GetValue<long?>() ?? 0,
            ["linesAdded"] = cost?["total_lines_added"]?.GetValue<long?>() ?? 0,
            ["linesRemoved"] = cost?["total_lines_removed"]?.GetValue<long?>() ?? 0,
            ["workspace"] = workspace,
            ["fetchedAt"] = now,
        };
        return true;
    }

    private static string BuildStatusText(JsonNode? root, JsonObject snapshot)
    {
        string model = root?["model"]?["display_name"]?.GetValue<string>() ?? "Claude";

        static string Fmt(JsonObject snap, string key)
        {
            if (snap[key] is JsonObject w && w["pct"] is JsonNode p)
                return $"{Math.Round(p.GetValue<double>())}%";
            return "–";
        }

        return $"{model} | 5h {Fmt(snapshot, "five_hour")} | wk {Fmt(snapshot, "seven_day")}";
    }
}

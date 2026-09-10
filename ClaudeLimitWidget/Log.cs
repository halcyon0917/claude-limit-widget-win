namespace ClaudeLimitWidget;

/// <summary>Tiny diagnostic logger: %USERPROFILE%\.claude-limit-widget\widget.log.</summary>
public static class Log
{
    private static readonly object Gate = new();

    // Resolved per call rather than cached in a static: a path captured at type
    // initialisation would outlive any later change in how the root resolves.
    private static string PathName => Path.Combine(Config.DataDir, "widget.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Config.DataDir);
                File.AppendAllText(PathName, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break the widget
        }
    }
}

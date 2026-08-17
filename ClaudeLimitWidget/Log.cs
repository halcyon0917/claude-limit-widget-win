namespace ClaudeLimitWidget;

/// <summary>Tiny diagnostic logger: %LOCALAPPDATA%\ClaudeLimitWidget\widget.log.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string PathName = System.IO.Path.Combine(Config.DataDir, "widget.log");

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

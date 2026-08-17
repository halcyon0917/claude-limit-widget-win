namespace ClaudeLimitWidget;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--statusline"))
        {
            // Invoked by Claude Code as the statusline command: read stdin JSON,
            // persist rate limits for the widget, print a one-line status, exit.
            return StatuslineBridge.Run();
        }

        bool demo = args.Contains("--demo");

        using var mutex = new Mutex(true, @"Local\ClaudeLimitWidget", out bool isNew);
        if (!isNew)
            return 0; // already running

        ApplicationConfiguration.Initialize();
        Application.Run(new WidgetAppContext(demo));
        return 0;
    }
}

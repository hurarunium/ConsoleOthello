using Avalonia;

namespace OthelloConsole;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--search-check"))
        {
            SearchCheck.Run(args);
            return;
        }
        if (args.Contains("--bench"))
        {
            BenchMode.Run(args);
            return;
        }
        if (args.Contains("--console"))
        {
            Othello.Main(args).GetAwaiter().GetResult();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

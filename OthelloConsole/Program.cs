using Avalonia;

namespace OthelloConsole;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--convert-records"))
        {
            ConvertRecordsMode.Run(args);
            return;
        }
        if (args.Contains("--analyze"))
        {
            AnalyzeMode.Run(args);
            return;
        }
        if (args.Contains("--train-pattern"))
        {
            PatternTrainer.Run(args);
            return;
        }
        if (args.Contains("--pattern-check"))
        {
            PatternCheck.Run(args);
            return;
        }
        if (args.Contains("--search-check"))
        {
            SearchCheck.Run(args);
            return;
        }
        if (args.Contains("--endgame-bench"))
        {
            EndgameBench.Run(args);
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

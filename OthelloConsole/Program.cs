using Avalonia;

namespace OthelloConsole;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 開発用: 探索で評価するたびに、差分評価の値を盤面から作り直した値と比べる(とても遅くなる)
        OthelloAI.VerifyIncrementalEval = args.Contains("--verify-eval");
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
        if (args.Contains("--mpc-stats"))
        {
            MpcStats.Run(args);
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

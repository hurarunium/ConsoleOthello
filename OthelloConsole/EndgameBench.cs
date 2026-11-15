using System.Diagnostics;

/// <summary>
/// 終盤の速さの測定(--endgame-bench、設計書 Phase 7)。
/// WTHOR の実戦の棋譜から、空きマスが決まった数の局面を取り出し、OthelloAI に手を選ばせて、
/// 1局面あたりの時間と局面数を測る。完全読みを速くしたときに、どれだけ速くなったかを比べるために使う。
/// 局面は毎回同じ順番で取り出すので、同じ条件なら同じ局面で比べられる。
///
/// 例: --endgame-bench --wthor ../external/wthor/WTH_2020.wtb --empties 16,18,20 --positions 30
///     --level 15  (標準。残り20マスから完全読みに入るレベル)
///     --force-solve  (レベルに関係なく完全読みする。完全読みに入る上限を見直すときに使う)
/// </summary>
public static class EndgameBench
{
    public static void Run(string[] args)
    {
        string wthorPath = ValueOf(args, "--wthor") ?? "../external/wthor/WTH_2020.wtb";
        int[] emptiesList = (ValueOf(args, "--empties") ?? "16,18,20").Split(',').Select(int.Parse).ToArray();
        int positions = int.Parse(ValueOf(args, "--positions") ?? "30");
        int level = int.Parse(ValueOf(args, "--level") ?? AiLevel.Max.ToString());
        bool forceSolve = args.Contains("--force-solve");

        PatternEvaluator evaluator = EvaluatorFile.LoadFromArgs(args, out string description);
        Console.WriteLine($"評価関数: {description}");
        Console.WriteLine(forceSolve
            ? $"棋譜: {wthorPath}  すべて完全読み"
            : $"棋譜: {wthorPath}  レベル{level}(残り{AiLevel.PerfectSolveEmpties(level)}マスから完全読み)");

        var ai = new OthelloAI(evaluator);
        OthelloDebugLog.SuppressOutput = true;
        try
        {
            foreach (int empties in emptiesList)
                Measure(ai, wthorPath, empties, positions, level, forceSolve);
        }
        finally
        {
            OthelloDebugLog.SuppressOutput = false;
        }
    }

    private static void Measure(OthelloAI ai, string wthorPath, int empties, int positions, int level, bool forceSolve)
    {
        // 1局から1局面だけ取る(同じ局の続きの局面ばかりにならないように)
        var boards = new List<BitBoard>();
        var seen = new HashSet<(ulong, ulong)>();
        foreach (TrainingSample sample in TrainingData.LoadWthor(wthorPath, minEmpties: empties))
        {
            if (TrainingData.EmptyCount(sample) != empties) continue;
            var board = new BitBoard(sample.Player, sample.Opponent);
            if (board.GetMoves() == 0 || !seen.Add((board.Player, board.Opponent))) continue;
            boards.Add(board);
            if (boards.Count >= positions) break;
        }

        var times = new List<double>();
        long totalNodes = 0;
        foreach (BitBoard board in boards)
        {
            var stopwatch = Stopwatch.StartNew();
            ai.ChooseMove(board, level, forceSolve);
            stopwatch.Stop();
            times.Add(stopwatch.Elapsed.TotalMilliseconds);
            totalNodes += ai.LastNodeCount;
        }

        times.Sort();
        double total = times.Sum();
        double median = times.Count == 0 ? 0 : times[times.Count / 2];
        Console.WriteLine($"残り{empties}マス: {boards.Count}局面  平均 {total / Math.Max(1, times.Count):F0}ms  中央値 {median:F0}ms  "
                        + $"最大 {(times.Count == 0 ? 0 : times[^1]):F0}ms  10秒超え {times.Count(t => t > AiLevel.TargetThinkTime.TotalMilliseconds)}局面  "
                        + $"局面数 {totalNodes / Math.Max(1, boards.Count):N0}/局面  速さ {totalNodes / Math.Max(1, total) / 1000:F2}M局面/秒");
    }

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

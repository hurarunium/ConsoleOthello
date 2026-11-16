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
///     --transcript f5d6c3...  (棋譜を並べ直し、各局面で手を選ぶ時間を測る。ベンチで遅かった手を探すときに使う)
///     --only-move 41  (--transcript のうち、その手目の局面だけを測る)
///     --show-log  (AIのログ〈反復深化の深さごとの局面の数など〉を止めずに出す。遅い手の原因を探すときに使う)
///     --mpc / --no-mpc  (中盤の探索で MPC を必ず使う / 使わない。どちらもなければレベルどおり)
///     --mpc-t 1.8  (MPC の余裕を変える。切り方を選ぶときに使う)
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
        bool? mpcOverride = AiLevel.MpcOverrideFromArgs(args);
        double? mpcSelectivity = AiLevel.MpcSelectivityFromArgs(args);
        bool useMpc = mpcOverride ?? AiLevel.UsesMpc(level);

        PatternEvaluator evaluator = EvaluatorFile.LoadFromArgs(args, out string description);
        Console.WriteLine($"評価関数: {description}");
        Console.WriteLine(forceSolve
            ? $"棋譜: {wthorPath}  すべて完全読み"
            : $"棋譜: {wthorPath}  レベル{level}(残り{AiLevel.PerfectSolveEmpties(level)}マスから完全読み)、MPC{(useMpc ? $"あり({mpcSelectivity ?? AiLevel.MpcSelectivity(level):F2})" : "なし")}");

        OthelloAI ai = OthelloAI.CreateFromArgs(evaluator, args, out string orderingDescription);
        Console.WriteLine($"並べ替え用の評価: {orderingDescription}");
        OthelloDebugLog.SuppressOutput = !args.Contains("--show-log");
        try
        {
            string? transcript = ValueOf(args, "--transcript");
            if (transcript != null)
            {
                MeasureTranscript(ai, transcript, level, forceSolve, int.Parse(ValueOf(args, "--only-move") ?? "0"));
                return;
            }
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

    /// <summary>棋譜(f5d6形式、パスは書かない)を初期局面から並べ直し、各局面で手を選ぶ時間を書く</summary>
    private static void MeasureTranscript(OthelloAI ai, string transcript, int level, bool forceSolve, int onlyMove)
    {
        // 初期局面(黒の番): d5・e4 が黒、d4・e5 が白
        var board = new BitBoard((1UL << 28) | (1UL << 35), (1UL << 27) | (1UL << 36));
        bool blackToMove = true;
        var times = new List<double>();
        for (int i = 0; i + 1 < transcript.Length; i += 2)
        {
            if (board.GetMoves() == 0)
            {
                board = new BitBoard(board.Opponent, board.Player); // パス
                blackToMove = !blackToMove;
            }
            int col = char.ToLowerInvariant(transcript[i]) - 'a';
            int row = transcript[i + 1] - '1';
            int move = row * 8 + col;
            if ((board.GetMoves() & (1UL << move)) == 0)
                throw new ArgumentException($"{i / 2 + 1}手目の {transcript.Substring(i, 2)} は打てません。");

            if (onlyMove != 0 && i / 2 + 1 != onlyMove)
            {
                board = board.DoMove(move);
                blackToMove = !blackToMove;
                continue;
            }
            var stopwatch = Stopwatch.StartNew();
            int chosen = ai.ChooseMove(board, level, forceSolve);
            stopwatch.Stop();
            (int r, int c) = BitBoard.BitToCoord(chosen);
            Console.WriteLine($"{i / 2 + 1,2}手目 {(blackToMove ? "黒" : "白")} 残り{board.EmptyCount,2}マス  {stopwatch.Elapsed.TotalMilliseconds,8:F0}ms  "
                            + $"局面数 {ai.LastNodeCount,12:N0}  選んだ手 {(char)('a' + c - 1)}{r}  棋譜の手 {transcript.Substring(i, 2)}");

            times.Add(stopwatch.Elapsed.TotalMilliseconds);

            board = board.DoMove(move);
            blackToMove = !blackToMove;
        }
        if (times.Count > 1)
            Console.WriteLine($"合計: {times.Count}局面  平均 {times.Average():F0}ms  最大 {times.Max():F0}ms  "
                            + $"10秒超え {times.Count(t => t > AiLevel.TargetThinkTime.TotalMilliseconds)}局面  合計 {times.Sum() / 1000:F1}秒");
    }

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

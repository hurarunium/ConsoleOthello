using System.Numerics;

/// <summary>
/// 時間で止まる探索の確認(--timed-check。企画 P-06、設計書 Phase 1)。
/// - 引数なし: 持ち時間つきの探索(ChooseMoveTimed)が、持ち時間を守り、合法手を返すかを確かめる。
///   序盤〜終盤の局面を、実際の対局のように1つの OthelloAI で続けて読ませ、外側の時計で時間を測る。
///   例: --timed-check --budget-ms 150 --positions 20
///       --timed-check --budget-ms 100        (サーバーが遅い場合に備えて、短い持ち時間でも守れるかを見る)
///       --timed-check --empties 12,8 --positions 400   (残りマスの区分を指定する。終盤のパスの局面を多く見たいとき)
/// - --fingerprint: レベル1〜15の探索が変わっていないことを確かめるための「指紋」を出す。
///   固定の局面で OthelloAI.ChooseMove を呼び、選んだ手とノード数をまとめて表示する。
///   時間の確認を入れる前と後で出力を比べて、同じなら探索は変わっていない。
/// </summary>
public static class TimedCheck
{
    public static void Run(string[] args)
    {
        PatternEvaluator evaluator = EvaluatorFile.LoadFromArgs(args, out string description);
        Console.WriteLine($"評価関数: {description}");

        OthelloDebugLog.SuppressOutput = true;
        try
        {
            if (args.Contains("--fingerprint"))
            {
                PrintFingerprint(evaluator);
                return;
            }
            CheckTimedSearch(evaluator, args);
        }
        finally
        {
            OthelloDebugLog.SuppressOutput = false;
        }
    }

    // ---- 持ち時間の確認 ----

    /// <summary>1つの持ち時間について、残りマスごとに局面を読ませて、時間と手を確かめる</summary>
    private static void CheckTimedSearch(PatternEvaluator evaluator, string[] args)
    {
        double budgetMs = double.Parse(ValueOf(args, "--budget-ms") ?? "150", System.Globalization.CultureInfo.InvariantCulture);
        int positionsPerBucket = int.Parse(ValueOf(args, "--positions") ?? "20");
        double toleranceMs = double.Parse(ValueOf(args, "--tolerance-ms") ?? "5", System.Globalization.CultureInfo.InvariantCulture);
        int ttBits = int.Parse(ValueOf(args, "--tt-bits") ?? "20");
        var budget = TimeSpan.FromMilliseconds(budgetMs);
        int[] emptiesList = ValueOf(args, "--empties")?.Split(',').Select(int.Parse).ToArray()
                            ?? new[] { 58, 52, 46, 40, 34, 28, 24, 22, 20, 18, 16, 14, 12, 8 };

        int solveEmpties = int.Parse(ValueOf(args, "--solve-empties") ?? "22");
        double midgameShare = double.Parse(ValueOf(args, "--midgame-share") ?? "0.15", System.Globalization.CultureInfo.InvariantCulture);
        int verifyMaxEmpties = int.Parse(ValueOf(args, "--verify-max") ?? "18");
        var ai = new OthelloAI(evaluator, ttBits)
        {
            MpcOverride = args.Contains("--mpc") ? true : null,
            EndgameSolveEmpties = solveEmpties,
            MidgameTimeShare = midgameShare,
            KeepTranspositionTable = args.Contains("--keep-tt"),
        };
        // 終盤の勝敗の読みが正しいかを確かめる相手(時間を気にせず、終局まで読み切る)
        var verifier = new OthelloAI(evaluator, 20);
        int failures = 0;

        // 打てる手がない局面では、手を返さない
        TimedMoveResult none = ai.ChooseMoveTimed(new BitBoard(1UL, 0UL), budget);
        Console.WriteLine($"打てる手がない局面: 返した手={none.Move}  {(none.Move == -1 ? "[OK]" : "[NG]")}");
        if (none.Move != -1) failures++;

        // 最初の1手を想定したウォームアップ(最初の1手は2000ms。JITの暖機に使う)
        TimedMoveResult first = ai.ChooseMoveTimed(InitialBoard(), TimeSpan.FromMilliseconds(2000));
        Console.WriteLine($"最初の1手(持ち時間2000ms): {first.Elapsed.TotalMilliseconds:F1}ms  深さ{first.CompletedDepth}  ノード{first.Nodes}");

        Console.WriteLine($"持ち時間 {budgetMs:F0}ms、許す超過 {toleranceMs:F0}ms、1区分 {positionsPerBucket}局面、置換表 2^{ttBits}({(args.Contains("--keep-tt") ? "手をまたいで残す" : "毎手空にする")})、MPC {(args.Contains("--mpc") ? "あり" : "なし")}、残り{solveEmpties}マス以下で勝敗を読む(中盤の割合 {midgameShare:F2})、勝敗の確認は残り{verifyMaxEmpties}マス以下");
        Console.WriteLine("残りマス | 局面 | 平均ms | 最大ms | 最大の超過ms | 超過した局面 | 平均の深さ | 読み切り | 時間切れ | 不正な手 | 勝敗が分かった | 勝敗の食い違い | 相手が打てない局面");

        double overallMaxMs = 0;
        int overallOver = 0;
        int overallTotal = 0;
        foreach (int empties in emptiesList)
        {
            var random = new Random(5000 + empties);
            int count = 0, over = 0, illegal = 0, solved = 0, timedOut = 0, passPositions = 0, endgameSolved = 0, mismatches = 0;
            double sumMs = 0, maxMs = 0, sumDepth = 0;
            for (int i = 0; i < positionsPerBucket; i++)
            {
                if (!TryCreatePosition(random, empties, out BitBoard board)) { i--; continue; }
                if (new BitBoard(board.Opponent, board.Player).GetMoves() == 0) passPositions++;

                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                TimedMoveResult result = ai.ChooseMoveTimed(board, budget);
                double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;

                if (result.Move < 0 || ((board.GetMoves() >> result.Move) & 1UL) == 0) illegal++;
                if (elapsedMs > budgetMs + toleranceMs) over++;
                if (result.Solved) solved++;
                if (result.EndgameOutcome is int outcome)
                {
                    endgameSolved++;
                    if (empties <= verifyMaxEmpties && !IsOutcomeCorrect(verifier, board, result.Move, outcome)) mismatches++;
                }
                if (result.TimedOut) timedOut++;
                sumMs += elapsedMs;
                sumDepth += result.CompletedDepth;
                maxMs = Math.Max(maxMs, elapsedMs);
                count++;
            }
            Console.WriteLine($"{empties,8} | {count,4} | {sumMs / count,6:F1} | {maxMs,6:F1} | {Math.Max(0, maxMs - budgetMs),12:F1} | {over,11} | {sumDepth / count,10:F1} | {solved,8} | {timedOut,8} | {illegal,8} | {endgameSolved,12} | {mismatches,14} | {passPositions,17}");
            overallMaxMs = Math.Max(overallMaxMs, maxMs);
            overallOver += over;
            overallTotal += count;
            failures += illegal + mismatches;
        }

        Console.WriteLine($"合計 {overallTotal}局面  最大 {overallMaxMs:F1}ms  許す超過({budgetMs + toleranceMs:F0}ms)を超えた局面 {overallOver}");
        if (overallOver > 0) failures++;
        Console.WriteLine(failures == 0 ? "[OK] すべての局面で、持ち時間を守り、合法手を返した" : $"[NG] 守れなかった項目 {failures}");
        if (failures != 0) Environment.ExitCode = 1;
    }

    /// <summary>
    /// 終盤の読みが出した結果(勝ち1・引き分け0・負け-1)が、終局まで読み切った正しい結果と同じか。
    /// 勝ち・引き分けなら、選んだ手もその結果を保つか(負けのときは手を問わない)
    /// </summary>
    private static bool IsOutcomeCorrect(OthelloAI verifier, BitBoard board, int move, int outcome)
    {
        int exact = Math.Sign(verifier.ExactValue(board));
        if (exact != outcome) return false;
        if (outcome < 0) return true;
        int afterMove = -Math.Sign(verifier.ExactValue(board.DoMove(move)));
        return afterMove == outcome;
    }

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    // ---- 局面の作り方 ----

    /// <summary>黒が先手の初期局面(手番の側がPlayer)</summary>
    private static BitBoard InitialBoard() => new BitBoard((1UL << 28) | (1UL << 35), (1UL << 27) | (1UL << 36));

    /// <summary>
    /// 初期局面からランダムに打って、残りマスが emptiesTarget になった局面を作る。
    /// 途中で両者とも打てなくなったら false(終局したので使わない)。手番の側が打てる局面だけを返す
    /// </summary>
    internal static bool TryCreatePosition(Random random, int emptiesTarget, out BitBoard board)
    {
        board = InitialBoard();
        while (board.EmptyCount > emptiesTarget)
        {
            ulong moves = board.GetMoves();
            if (moves == 0)
            {
                // パス: 手番を入れ替える。相手も打てなければ終局
                var passed = new BitBoard(board.Opponent, board.Player);
                if (passed.GetMoves() == 0) return false;
                board = passed;
                continue;
            }
            board = board.DoMove(PickRandomMove(random, moves));
        }
        return board.GetMoves() != 0;
    }

    private static int PickRandomMove(Random random, ulong moves)
    {
        int pick = random.Next(BitOperations.PopCount(moves));
        for (int i = 0; i < pick; i++) moves &= moves - 1;
        return BitOperations.TrailingZeroCount(moves);
    }

    // ---- 指紋 ----

    /// <summary>レベルごとに固定の局面を読ませ、選んだ手の並びとノード数の合計を出す</summary>
    private static void PrintFingerprint(PatternEvaluator evaluator)
    {
        // (レベル, 局面の数, 残りマスの候補)。レベル12以上は中盤でMPCを使い、終盤は完全読みに入る
        (int level, int count, int[] empties)[] cases =
        {
            (1, 20, new[] { 56, 44, 30 }),
            (3, 20, new[] { 56, 44, 30, 14 }),
            (6, 12, new[] { 56, 44, 30, 14 }),
            (9, 6, new[] { 50, 36, 22 }),
            (12, 4, new[] { 50, 36 }),
            (15, 2, new[] { 50 }),
        };

        foreach ((int level, int count, int[] emptiesList) in cases)
        {
            ulong moveHash = 14695981039346656037UL; // FNV-1a
            long nodes = 0;
            int played = 0;
            var random = new Random(1000 + level);
            for (int i = 0; i < count; i++)
            {
                int empties = emptiesList[i % emptiesList.Length];
                if (!TryCreatePosition(random, empties, out BitBoard board)) continue;
                var ai = new OthelloAI(evaluator, transpositionTableSizeBits: 20);
                int move = ai.ChooseMove(board, level);
                moveHash = (moveHash ^ (ulong)move) * 1099511628211UL;
                nodes += ai.LastNodeCount;
                played++;
            }
            Console.WriteLine($"レベル{level,2}: {played}局面  手の指紋={moveHash:X16}  ノード数の合計={nodes}");
        }
    }
}

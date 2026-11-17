using System.Diagnostics;
using System.Numerics;

/// <summary>
/// 持ち時間つきの探索どうしの対局(--timed-match。設計書 Phase 2・5)。設定の違いで、強さと時間を比べる。
/// 同じ序盤(ランダムに8手)で、先手・後手を入れ替えて2局ずつ打つ。2つのAIは同じ持ち時間で、1つずつ順に動かす(並べると時間が長く出るため)。
///
/// 例: --timed-match --games 100 --budget-ms 150 --a-keep-tt
///       A は置換表を手をまたいで残し、B は毎手空にする。
///     --timed-match --games 100 --a-solve-empties 22 --b-solve-empties 18
///       A は残り22マス以下、B は残り18マス以下から勝敗を読む。
/// 設定は --a-... / --b-... で指定する(book、keep-tt、solve-empties、midgame-share、no-mpc、mpc-t)。
/// </summary>
public static class TimedMatch
{

    public static void Run(string[] args)
    {
        PatternEvaluator evaluator = EvaluatorFile.LoadFromArgs(args, out string description);
        Console.WriteLine($"評価関数: {description}");

        int games = int.Parse(ValueOf(args, "--games") ?? "100");
        double budgetMs = double.Parse(ValueOf(args, "--budget-ms") ?? "150", System.Globalization.CultureInfo.InvariantCulture);
        int ttBits = int.Parse(ValueOf(args, "--tt-bits") ?? "20");
        int openingPlies = int.Parse(ValueOf(args, "--opening-plies") ?? "8");
        var budget = TimeSpan.FromMilliseconds(budgetMs);

        OthelloAI aiA = Create(evaluator, ttBits, args, "--a-");
        OthelloAI aiB = Create(evaluator, ttBits, args, "--b-");
        Console.WriteLine($"持ち時間 {budgetMs:F0}ms、置換表 2^{ttBits}、{games}局(最初の{openingPlies}手はランダム。同じ序盤で先手・後手を入れ替える)");
        Console.WriteLine($"A: {Describe(args, "--a-")}");
        Console.WriteLine($"B: {Describe(args, "--b-")}");

        OthelloDebugLog.SuppressOutput = true;
        try
        {
            var statsA = new SideStats();
            var statsB = new SideStats();
            int aWins = 0, bWins = 0, draws = 0, aDiscs = 0, bDiscs = 0;
            for (int game = 0; game < games; game++)
            {
                bool aIsBlack = game % 2 == 0;
                var random = new Random(9000 + game / 2);
                (int blackDiscs, int whiteDiscs) = PlayGame(random, openingPlies, aIsBlack ? aiA : aiB, aIsBlack ? aiB : aiA, budget,
                                                            aIsBlack ? statsA : statsB, aIsBlack ? statsB : statsA);
                int a = aIsBlack ? blackDiscs : whiteDiscs;
                int b = aIsBlack ? whiteDiscs : blackDiscs;
                aDiscs += a; bDiscs += b;
                if (a > b) aWins++; else if (b > a) bWins++; else draws++;
                if ((game + 1) % 10 == 0 || game + 1 == games)
                    Console.WriteLine($"  {game + 1}局まで: A {aWins}勝 B {bWins}勝 引き分け {draws}(Aの勝率 {(aWins + draws * 0.5) / (game + 1) * 100:F1}%)");
            }

            Console.WriteLine($"結果: A {aWins}勝 B {bWins}勝 引き分け {draws}  Aの勝率 {(aWins + draws * 0.5) / games * 100:F1}%  石の差の平均 {(aDiscs - bDiscs) / (double)games:F1}(Aから見て)");
            Console.WriteLine($"A: {statsA.Describe(budgetMs)}");
            Console.WriteLine($"B: {statsB.Describe(budgetMs)}");
            if (statsA.OverCount + statsB.OverCount > 0) Environment.ExitCode = 1;
        }
        finally
        {
            OthelloDebugLog.SuppressOutput = false;
        }
    }

    private static OthelloAI Create(PatternEvaluator evaluator, int ttBits, string[] args, string prefix) => new(evaluator, ttBits)
    {
        Book = args.Contains(prefix + "book") ? LoadBook(args) : null,
        MpcOverride = args.Contains(prefix + "no-mpc") ? false : null,
        MpcSelectivityOverride = ValueOf(args, prefix + "mpc-t") is string t ? double.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null,
        EndgameSolveEmpties = int.Parse(ValueOf(args, prefix + "solve-empties") ?? "22"),
        MidgameTimeShare = double.Parse(ValueOf(args, prefix + "midgame-share") ?? "0.15", System.Globalization.CultureInfo.InvariantCulture),
        KeepTranspositionTable = args.Contains(prefix + "keep-tt"),
    };

    private static OpeningBook LoadBook(string[] args)
        => OpeningBook.LoadFromArgs(args.Where(a => a != "--no-book").ToArray(), out string reason)
           ?? throw new FileNotFoundException($"定石を読めません: {reason}");

    private static string Describe(string[] args, string prefix)
        => $"定石 {(args.Contains(prefix + "book") ? "あり" : "なし")}、置換表を{(args.Contains(prefix + "keep-tt") ? "残す" : "毎手空にする")}、MPC {(args.Contains(prefix + "no-mpc") ? "なし" : $"あり(余裕 {ValueOf(args, prefix + "mpc-t") ?? "2.5"})")}、残り{ValueOf(args, prefix + "solve-empties") ?? "22"}マス以下で勝敗を読む(中盤の割合 {ValueOf(args, prefix + "midgame-share") ?? "0.15"})";

    /// <summary>1局打つ。黒と白の最終の石数を返す(空きマスは数えない)</summary>
    private static (int black, int white) PlayGame(Random random, int openingPlies, OthelloAI black, OthelloAI white, TimeSpan budget, SideStats blackStats, SideStats whiteStats)
    {
        BitBoard board = new BitBoard((1UL << 28) | (1UL << 35), (1UL << 27) | (1UL << 36)); // 手番の側が Player(最初は黒)
        bool blackToMove = true;
        int plies = 0;
        while (true)
        {
            ulong moves = board.GetMoves();
            if (moves == 0)
            {
                var passed = new BitBoard(board.Opponent, board.Player);
                if (passed.GetMoves() == 0) break; // 両方打てないので終局
                board = passed;
                blackToMove = !blackToMove;
                continue;
            }

            int move;
            if (plies < openingPlies)
            {
                move = PickRandomMove(random, moves);
            }
            else
            {
                OthelloAI ai = blackToMove ? black : white;
                SideStats stats = blackToMove ? blackStats : whiteStats;
                long start = Stopwatch.GetTimestamp();
                TimedMoveResult result = ai.ChooseMoveTimed(board, budget);
                stats.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds, result, budget.TotalMilliseconds);
                move = result.Move;
            }
            board = board.DoMove(move);
            blackToMove = !blackToMove;
            plies++;
        }

        int playerDiscs = BitOperations.PopCount(board.Player);
        int opponentDiscs = BitOperations.PopCount(board.Opponent);
        // いまの手番の側が Player。blackToMove なら Player は黒
        return blackToMove ? (playerDiscs, opponentDiscs) : (opponentDiscs, playerDiscs);
    }

    private static int PickRandomMove(Random random, ulong moves)
    {
        int pick = random.Next(BitOperations.PopCount(moves));
        for (int i = 0; i < pick; i++) moves &= moves - 1;
        return BitOperations.TrailingZeroCount(moves);
    }

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>片方のAIの、時間と深さの記録</summary>
    private sealed class SideStats
    {
        private int _moves, _searched, _endgameKnown, _timedOut, _fromBook;
        private double _sumMs, _maxMs, _sumDepth;
        public int OverCount { get; private set; }

        public void Add(double elapsedMs, TimedMoveResult result, double budgetMs)
        {
            _moves++;
            _sumMs += elapsedMs;
            _maxMs = Math.Max(_maxMs, elapsedMs);
            if (elapsedMs > budgetMs + 5) OverCount++;
            if (result.CompletedDepth > 0) { _searched++; _sumDepth += result.CompletedDepth; }
            if (result.EndgameOutcome != null) _endgameKnown++;
            if (result.FromBook) _fromBook++;
            if (result.TimedOut) _timedOut++;
        }

        public string Describe(double budgetMs)
            => $"{_moves}手  平均 {_sumMs / Math.Max(1, _moves):F1}ms  最大 {_maxMs:F1}ms  持ち時間+5msを超えた手 {OverCount}  中盤の平均の深さ {_sumDepth / Math.Max(1, _searched):F1}  定石の手 {_fromBook}  勝敗が分かった手 {_endgameKnown}  読み切れなかった手 {_timedOut}";
    }
}

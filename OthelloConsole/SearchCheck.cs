using System.Numerics;
using static OthelloSystem;

/// <summary>
/// 探索の確認(--search-check)。OthelloAI が選ぶ手が、工夫を入れない単純な探索の最善手と同じかを比べる。
/// 評価関数や探索を変えたときに、探索の不具合を入れていないかを確かめるために使う。
///
/// - 終盤: 完全読みは近似を使わないので、食い違いが1つでもあれば不具合。
/// - 中盤: 同じ評価関数・同じ深さの単純なαβ探索と比べる。MPC(浅い読みから枝を切る近似)を
///   使わずに動かすので、食い違いが1つでもあれば不具合。
/// - 全部取れる局面: 相手の石を全部取れる局面で、いちばん早く全部取る手を選ぶか(企画 P-11・P-12)。
/// 終局の値は OthelloAI と同じ決まり(空きマスを勝った側に数えた石数の差 × 64 ± 終局の空きマス)だが、
/// 比べる相手として独立させるため、ここで別に書いている。
///
/// 例: --search-check --positions 200
///     --search-check --weights x.bin     (評価ファイルを指定する)
/// </summary>
public static class SearchCheck
{
    /// <summary>中盤の探索で、終局の値をどの評価値よりも大きくするための基準</summary>
    private const int FinalScoreBase = 500_000_000;
    private const int Infinity = 2_000_000_000;
    /// <summary>終局の値で、石数の差に掛ける倍率</summary>
    private const int FinalValueScale = 64;
    /// <summary>終局の値の最大より大きい値(完全読みの窓に使う)</summary>
    private const int FinalValueLimit = 64 * FinalValueScale + 64;

    public static void Run(string[] args)
    {
        int index = Array.IndexOf(args, "--positions");
        int positions = index >= 0 && index + 1 < args.Length ? int.Parse(args[index + 1]) : 200;

        PatternEvaluator evaluatorFile = EvaluatorFile.LoadFromArgs(args, out string description);
        Console.WriteLine($"評価関数: {description}");
        // MPC は近似なので、使わずに動かす(使うと、正しい探索でも食い違いが出る)。並べ替えの指定は引数どおり
        string[] withoutMpc = args.Where(a => a != "--mpc").Append("--no-mpc").ToArray();
        OthelloAI.CreateFromArgs(evaluatorFile, withoutMpc, out string orderingDescription);
        Console.WriteLine($"並べ替え用の評価: {orderingDescription}");
        Func<OthelloAI> createAi = () => OthelloAI.CreateFromArgs(evaluatorFile, withoutMpc, out _);

        OthelloDebugLog.SuppressOutput = true;
        try
        {
            CheckEndgame(positions, empties: 12, seed: 7, createAi);
            CheckMidgame(positions, level: 3, seed: 11, createAi);
            CheckMidgame(positions, level: 6, seed: 13, createAi);
            CheckWipeout(Math.Max(10, positions / 4), seed: 17, createAi);
        }
        finally
        {
            OthelloDebugLog.SuppressOutput = false;
        }
    }

    /// <summary>
    /// 終盤の確認。単純な完全読みで全部の手の正しい値(最終の石数の差)を出し、
    /// OthelloAI が選んだ手が最善の値を持つかを見る。
    /// OthelloAI は、残り empties マスから完全読みに入るレベル(empties / 2)で動かす。
    /// </summary>
    private static void CheckEndgame(int count, int empties, int seed, Func<OthelloAI> createAi)
    {
        var random = new Random(seed);
        int mismatches = 0;
        int losingMismatches = 0;
        int speedOnlyMismatches = 0;
        long lostDiscs = 0;

        for (int checkedCount = 0; checkedCount < count;)
        {
            int level = empties / 2; // レベルNは残り2Nマスから完全読み
            if (!TryCreatePosition(random, 60 - empties, level, out MainGameData gameData, out BitBoard board)) continue;
            checkedCount++;

            int chosenMove = ChooseMove(gameData, createAi);
            (int bestValue, int chosenValue) = ScoreMoves(board, chosenMove, next => -Solve(next, -FinalValueLimit, FinalValueLimit));
            if (chosenValue == bestValue) continue;

            mismatches++;
            int lost = DiscDiffOf(bestValue) - DiscDiffOf(chosenValue);
            lostDiscs += lost;
            if (lost == 0) speedOnlyMismatches++;
            if (bestValue < 0) losingMismatches++;
        }

        string result = mismatches == 0 ? "OK" : "不具合あり";
        Console.WriteLine($"[{result}] 終盤(残り{empties}マス、完全読み): {count}局面中 {mismatches}局面で最善でない手"
                        + $"(うち最善でも負けの局面 {losingMismatches}、石数の差は同じで早さだけ違う局面 {speedOnlyMismatches}、損した石数の差の合計 {lostDiscs})");
    }

    /// <summary>
    /// 中盤の確認。同じ評価関数・同じ深さの単純なαβ探索で全部の手の値を出し、
    /// OthelloAI が選んだ手が最善の値を持つかを見る。
    /// </summary>
    private static void CheckMidgame(int count, int level, int seed, Func<OthelloAI> createAi)
    {
        int depth = AiLevel.MidgameDepth(level);
        var random = new Random(seed);
        OthelloAI evaluator = createAi();
        int mismatches = 0;

        for (int checkedCount = 0; checkedCount < count;)
        {
            int discs = random.Next(14, 40); // 序盤〜中盤(完全読みに入らない範囲)
            if (!TryCreatePosition(random, discs - 4, level, out MainGameData gameData, out BitBoard board)) continue;
            checkedCount++;

            int chosenMove = ChooseMove(gameData, createAi);
            (int bestValue, int chosenValue) = ScoreMoves(board, chosenMove,
                next => -AlphaBeta(next, depth - 1, -Infinity, Infinity, evaluator));
            if (chosenValue != bestValue) mismatches++;
        }

        string result = mismatches == 0 ? "OK" : "不具合あり";
        Console.WriteLine($"[{result}] 中盤(レベル{level}、深さ{depth}、MPCなし): {count}局面中 {mismatches}局面で最善でない手");
    }

    /// <summary>
    /// 全部取れる局面の確認(企画 P-11・P-12)。片方がいちばん多く裏返す手を打ち、もう片方がランダムに打つ対局で、
    /// 相手の石を全部取れる局面(単純な完全読みで、石数の差64・空きマスが残って終わる)を集める。
    /// OthelloAI の選んだ手で終わるまでの打つ手の数が、いちばん早く全部取る手と同じかを見る
    /// </summary>
    private static void CheckWipeout(int count, int seed, Func<OthelloAI> createAi)
    {
        const int MaxEmpties = 12; // 単純な完全読みで読み切れる範囲
        var random = new Random(seed);
        int found = 0, slower = 0, notWiped = 0;
        long games = 0;

        while (found < count && games < 200_000)
        {
            games++;
            var board = new BitBoard(0x0000_0008_1000_0000UL, 0x0000_0010_0800_0000UL);
            bool greedyToMove = random.Next(2) == 0;
            while (found < count)
            {
                ulong moves = board.GetMoves();
                if (moves == 0)
                {
                    var passed = new BitBoard(board.Opponent, board.Player);
                    if (passed.GetMoves() == 0) break;
                    board = passed;
                    greedyToMove = !greedyToMove;
                    continue;
                }

                int empties = board.EmptyCount;
                if (greedyToMove && empties <= MaxEmpties)
                {
                    int bestValue = Solve(board, -FinalValueLimit, FinalValueLimit);
                    if (DiscDiffOf(bestValue) == 64 && EmptiesLeftOf(bestValue) > 0)
                    {
                        found++;
                        int level = Math.Clamp((empties + 1) / 2, AiLevel.Min, AiLevel.Max); // 残り2Nマスから完全読み
                        int chosenMove = ChooseMove(PatternCheck.ToGameData(board, level), createAi);
                        int chosenValue = -Solve(board.DoMove(chosenMove), -FinalValueLimit, FinalValueLimit);
                        if (DiscDiffOf(chosenValue) != 64) notWiped++;
                        else if (chosenValue != bestValue) slower++;
                    }
                }

                int move = greedyToMove ? MostFlippingMove(board, moves) : RandomMove(random, moves);
                board = board.DoMove(move);
                greedyToMove = !greedyToMove;
            }
        }

        string result = found > 0 && slower == 0 && notWiped == 0 ? "OK" : "不具合あり";
        Console.WriteLine($"[{result}] 全部取れる局面(残り{MaxEmpties}マス以下): {found}局面中 全部取らなかった {notWiped}、"
                        + $"全部取ったが、いちばん早い手より遅い {slower}({games}局から集めた)");
    }

    /// <summary>いちばん多く石を裏返す手(同じなら番号の小さいマス)</summary>
    private static int MostFlippingMove(BitBoard board, ulong moves)
    {
        int best = -1, bestFlips = -1;
        for (ulong rest = moves; rest != 0; rest &= rest - 1)
        {
            int move = BitOperations.TrailingZeroCount(rest);
            int flips = BitOperations.PopCount(board.GetFlipped(move));
            if (flips > bestFlips) { best = move; bestFlips = flips; }
        }
        return best;
    }

    private static int RandomMove(Random random, ulong moves)
    {
        int pick = random.Next(BitOperations.PopCount(moves));
        ulong rest = moves;
        for (int i = 0; i < pick; i++) rest &= rest - 1;
        return BitOperations.TrailingZeroCount(rest);
    }

    /// <summary>OthelloAI に手を選ばせ、ビット位置で返す。毎回新しく作り、置換表などの持ち越しをなくす</summary>
    private static int ChooseMove(MainGameData gameData, Func<OthelloAI> createAi)
    {
        OthelloAI ai = createAi();
        (int row, int col) chosen = ai.AI(gameData, false);
        return BitBoard.CoordToBit(chosen.row, chosen.col);
    }

    /// <summary>全部の合法手を score で評価し、最善の値と、選ばれた手の値を返す</summary>
    private static (int bestValue, int chosenValue) ScoreMoves(BitBoard board, int chosenMove, Func<BitBoard, int> score)
    {
        int bestValue = int.MinValue;
        int chosenValue = int.MinValue;
        foreach (int move in BitBoard.GetMoveList(board.GetMoves()))
        {
            int value = score(board.DoMove(move));
            bestValue = Math.Max(bestValue, value);
            if (move == chosenMove) chosenValue = value;
        }
        return (bestValue, chosenValue);
    }

    /// <summary>単純な完全読み(置換表・手の並べ替えなし)。値は手番から見た終局の値(FinalValue)</summary>
    private static int Solve(BitBoard board, int alpha, int beta)
    {
        ulong moves = board.GetMoves();
        if (moves == 0)
        {
            var passed = new BitBoard(board.Opponent, board.Player);
            if (passed.GetMoves() == 0) return FinalValue(board);
            return -Solve(passed, -beta, -alpha);
        }

        int best = -FinalValueLimit;
        int currentAlpha = alpha;
        while (moves != 0)
        {
            int move = BitOperations.TrailingZeroCount(moves);
            moves &= moves - 1;
            best = Math.Max(best, -Solve(board.DoMove(move), -beta, -currentAlpha));
            currentAlpha = Math.Max(currentAlpha, best);
            if (currentAlpha >= beta) break;
        }
        return best;
    }

    /// <summary>単純なαβ探索(置換表・手の並べ替え・MPCなし)。終局は、どの評価値よりも大きい基準に終局の値を足す</summary>
    private static int AlphaBeta(BitBoard board, int depth, int alpha, int beta, OthelloAI evaluator)
    {
        ulong moves = board.GetMoves();
        if (moves == 0)
        {
            var passed = new BitBoard(board.Opponent, board.Player);
            if (passed.GetMoves() == 0) return MidgameFinalScore(board);
            if (depth <= 0) return evaluator.Evaluate(board);
            return -AlphaBeta(passed, depth - 1, -beta, -alpha, evaluator);
        }
        if (depth <= 0) return evaluator.Evaluate(board);

        int best = -Infinity;
        int currentAlpha = alpha;
        while (moves != 0)
        {
            int move = BitOperations.TrailingZeroCount(moves);
            moves &= moves - 1;
            best = Math.Max(best, -AlphaBeta(board.DoMove(move), depth - 1, -beta, -currentAlpha, evaluator));
            currentAlpha = Math.Max(currentAlpha, best);
            if (currentAlpha >= beta) break;
        }
        return best;
    }

    /// <summary>
    /// 終局の値(手番から見た値)。空きマスを勝った側の石として数えた石数の差 × 64 に、
    /// 勝ちなら終局の空きマスを足し、負けなら引く(石数の差が同じなら、勝ちは早いほど、負けは遅いほど高い)
    /// </summary>
    private static int FinalValue(BitBoard board)
    {
        int player = board.PlayerCount, opponent = board.OpponentCount;
        int empties = 64 - player - opponent;
        if (player == opponent) return 0;
        int discDiff = player > opponent ? player + empties - opponent : player - empties - opponent;
        return discDiff * FinalValueScale + Math.Sign(discDiff) * empties;
    }

    private static int MidgameFinalScore(BitBoard board)
    {
        int value = FinalValue(board);
        return value == 0 ? 0 : Math.Sign(value) * FinalScoreBase + value;
    }

    /// <summary>終局の値の、石数の差の部分</summary>
    private static int DiscDiffOf(int value) => value / FinalValueScale;

    /// <summary>終局の値の、終局の空きマスの部分</summary>
    private static int EmptiesLeftOf(int value) => Math.Abs(value - DiscDiffOf(value) * FinalValueScale);

    /// <summary>
    /// 初期局面からランダムに指定の手数だけ打った局面を作る。
    /// 途中で終局した場合と、できた局面で手番の側が打てない場合は false を返す。
    /// </summary>
    private static bool TryCreatePosition(Random random, int moves, int level,
                                          out MainGameData gameData, out BitBoard board)
    {
        gameData = new MainGameData
        {
            _tilesSize = 8, _tiles = new int[8, 8], _gameTurn = GameTurn.AI, _gameMode = GameMode.AIvsAI,
            _turnNum = 1, _turnCounter = 1, _aiLevel = level,
        };
        gameData._tiles[3, 3] = 2; gameData._tiles[3, 4] = 1;
        gameData._tiles[4, 3] = 1; gameData._tiles[4, 4] = 2;

        int placed = 0;
        int passes = 0;
        while (placed < moves)
        {
            List<(int x, int y)> validMoves = InstallationArea(gameData);
            if (validMoves.Count == 0)
            {
                if (++passes >= 2) { board = default; return false; }
                gameData = TurnChange(gameData);
                continue;
            }
            passes = 0;
            (int row, int col) move = validMoves[random.Next(validMoves.Count)];
            PlacePiece(move.row, move.col, gameData);
            gameData = TurnChange(gameData);
            gameData._turnCounter++;
            placed++;
        }

        board = BitBoard.FromMainGameData(gameData);
        return board.GetMoves() != 0;
    }
}

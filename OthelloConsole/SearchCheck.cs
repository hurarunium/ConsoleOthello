using System.Numerics;
using static OthelloSystem;

/// <summary>
/// 探索の確認(--search-check)。OthelloAI が選ぶ手が、工夫を入れない単純な探索の最善手と同じかを比べる。
/// 評価関数や探索を変えたときに、探索の不具合を入れていないかを確かめるために使う。
///
/// - 終盤: 完全読みは近似を使わないので、食い違いが1つでもあれば不具合。
/// - 中盤: 同じ評価関数・同じ深さの単純なαβ探索と比べる。OthelloAI は LMR(後ろの手を浅く読む近似)を
///   使うので、中盤の食い違いは近似によるものも含む。
///
/// 例: --search-check --positions 200
///     --search-check --eval pattern      (パターン評価で確かめる)
/// </summary>
public static class SearchCheck
{
    /// <summary>終局の値を、どの評価値よりも大きくするための倍率</summary>
    private const int FinalScoreScale = 10_000_000;
    private const int Infinity = 2_000_000_000;

    public static void Run(string[] args)
    {
        int index = Array.IndexOf(args, "--positions");
        int positions = index >= 0 && index + 1 < args.Length ? int.Parse(args[index + 1]) : 200;

        EvaluatorOption.Choice evaluator = EvaluatorOption.Parse(args);
        Console.WriteLine($"評価関数: {evaluator.Description}");

        OthelloDebugLog.SuppressOutput = true;
        try
        {
            CheckEndgame(positions, empties: 12, seed: 7, evaluator.CreateAi);
            CheckMidgame(positions, level: 3, seed: 11, evaluator.CreateAi);
            CheckMidgame(positions, level: 6, seed: 13, evaluator.CreateAi);
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
        long lostDiscs = 0;

        for (int checkedCount = 0; checkedCount < count;)
        {
            int level = empties / 2; // レベルNは残り2Nマスから完全読み
            if (!TryCreatePosition(random, 60 - empties, level, out MainGameData gameData, out BitBoard board)) continue;
            checkedCount++;

            int chosenMove = ChooseMove(gameData, createAi);
            (int bestValue, int chosenValue) = ScoreMoves(board, chosenMove, next => -Solve(next, -64, 64));
            if (chosenValue == bestValue) continue;

            mismatches++;
            lostDiscs += bestValue - chosenValue;
            if (bestValue < 0) losingMismatches++;
        }

        string result = mismatches == 0 ? "OK" : "不具合あり";
        Console.WriteLine($"[{result}] 終盤(残り{empties}マス、完全読み): {count}局面中 {mismatches}局面で最善でない手"
                        + $"(うち最善でも負けの局面 {losingMismatches}、損した石数の差の合計 {lostDiscs})");
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

        Console.WriteLine($"[参考] 中盤(レベル{level}、深さ{depth}): {count}局面中 {mismatches}局面で最善でない手(LMRの近似による食い違いを含む)");
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

    /// <summary>単純な完全読み(置換表・手の並べ替えなし)。値は手番から見た最終の石数の差</summary>
    private static int Solve(BitBoard board, int alpha, int beta)
    {
        ulong moves = board.GetMoves();
        if (moves == 0)
        {
            var passed = new BitBoard(board.Opponent, board.Player);
            if (passed.GetMoves() == 0) return FinalDiscDiff(board);
            return -Solve(passed, -beta, -alpha);
        }

        int best = -64;
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

    /// <summary>単純なαβ探索(置換表・手の並べ替え・LMRなし)。終局は石数の差に大きな倍率を掛ける</summary>
    private static int AlphaBeta(BitBoard board, int depth, int alpha, int beta, OthelloAI evaluator)
    {
        ulong moves = board.GetMoves();
        if (moves == 0)
        {
            var passed = new BitBoard(board.Opponent, board.Player);
            if (passed.GetMoves() == 0) return FinalDiscDiff(board) * FinalScoreScale;
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

    /// <summary>OthelloAI の終局の点数と同じく、空きマスは数えない石数の差</summary>
    private static int FinalDiscDiff(BitBoard board) => board.PlayerCount - board.OpponentCount;

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

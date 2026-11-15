using System.Numerics;
using static OthelloDebugLog;

/// <summary>
/// ビットボード + 反復深化 + 置換表 + 完全読み + PVS（NegaScout）による最強オセロAI
/// </summary>
public class OthelloAI
{
    /// <summary>評価関数(学習したパターン評価)</summary>
    private readonly PatternEvaluator _evaluator;

    public OthelloAI(PatternEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    private int _maxDepth;
    private int _recursionsCount;

    /// <summary>直前の1手で読んだ局面の数(探索の速さを比べるため)</summary>
    public int LastNodeCount => _recursionsCount;
    private int _abCutCount;           // αβカット発生回数
    private int _pvsResearchCount;     // PVS再探索回数
    private bool _perfSolveTriggered;  // 完全読みに移行したか
    private int _lastProgressLogNodes; // 完全読み進捗ログ用

    // Killer Move: 各深さで2手記録（βカットを起こした手）
    private int[,] _killerMoves = new int[0, 2];

    // History Heuristic: 各マスのカットスコア（深さ²を加算、反復深化ごとに老化）
    private readonly int[] _historyTable = new int[64];

    private readonly TranspositionTable _tt = new();

    // 完全読みに切り替える残り空きマス数。レベルから決める(AiLevel.PerfectSolveEmpties)
    private int _perfectSolveEmpties;

    // 探索の窓の無限大。どの評価値・終局の点数よりも外側にある必要がある
    private const int Inf = 1_000_000_000;

    // 終局の点数の基準(勝ち = WinScore + 石数差、負け = -WinScore - 石数差)。
    // 窓の端(±Inf)と重なると、2石差以上の勝ち負けが窓の外に出て、最善手を正しく選べなくなるため、Infの内側に置く
    private const int WinScore = Inf / 2;

    public (int x, int y) AI(MainGameData gameData, bool isDebug)
    {
        OthelloDebugLog._isDebug = isDebug;
        StartAILog(gameData._turnCounter);

        int bestMove = ChooseMove(BitBoard.FromMainGameData(gameData), gameData._aiLevel);
        StopAILog();
        return BitBoard.BitToCoord(bestMove);
    }

    /// <summary>
    /// 盤面(手番の側から見たもの)とレベルから、打つマスのビット位置を選ぶ。終盤の速さの測定(--endgame-bench)からも呼ぶ。
    /// forcePerfectSolve は、レベルに関係なく完全読みする(完全読みに入る上限を見直すための測定用)
    /// </summary>
    internal int ChooseMove(BitBoard board, int requestedLevel, bool forcePerfectSolve = false)
    {
        _recursionsCount = 0;
        _abCutCount = 0;
        _pvsResearchCount = 0;
        _perfSolveTriggered = false;
        _lastProgressLogNodes = 0;
        int level = AiLevel.Clamp(requestedLevel);
        _maxDepth = AiLevel.MidgameDepth(level);
        _perfectSolveEmpties = AiLevel.PerfectSolveEmpties(level);

        InfoLog($"AI思考開始 (レベル{level}: 中盤{_maxDepth}手読み・残り{_perfectSolveEmpties}マスから完全読み  残りマス={board.EmptyCount})");
        _tt.Clear();

        // Killer Move テーブル初期化
        _killerMoves = new int[_maxDepth + 2, 2];
        for (int d = 0; d < _maxDepth + 2; d++) { _killerMoves[d, 0] = -1; _killerMoves[d, 1] = -1; }

        // History Heuristic テーブルリセット
        Array.Clear(_historyTable, 0, 64);

        // 決めた深さを最後まで読む(途中で打ち切らない)。1手10秒以内に終わるようにするのは、探索を速くして達成する
        int bestMove = forcePerfectSolve || board.EmptyCount <= _perfectSolveEmpties
            ? SolvePerfectRoot(board)
            : IterativeDeepening(board, _maxDepth);

        DebugLog($"探索統計: αβカット={_abCutCount}  PVS再探索={_pvsResearchCount}  完全読み={(_perfSolveTriggered ? "あり" : "なし")}");
        DebugLog($"置換表: ヒット={_tt.HitCount} ({_tt.HitRate:F1}%)  ミス={_tt.MissCount}");

        GameLog($"AI評価\nレベル:{level} 探索深さ:{_maxDepth} 完全読み:{_perfSolveTriggered} 総計算手順回数：{_recursionsCount} 最良手:{BitBoard.BitToCoord(bestMove)}");
        return bestMove;
    }

    /// <summary>反復深化で maxDepth まで読み、最善手を返す</summary>
    private int IterativeDeepening(BitBoard board, int maxDepth)
    {
        int bestMove = -1;
        int prevScore = 0; // Aspiration Window 用（前の深さのスコア）

        for (int depth = 1; depth <= maxDepth; depth++)
        {
            for (int i = 0; i < 64; i++) _historyTable[i] >>= 1;

            int iterBest = -1;
            int score;

            // Aspiration Window: depth>=3 は前の深さのスコアを基準に探索窓を絞る
            // Delta=50 の根拠: 評価値の通常変動幅（50〜200程度）に対して適度な窓幅。
            // 小さすぎると再探索が頻発し、大きすぎると窓の効果が薄れる。
            const int AspirationDelta = 50;
            if (depth <= 2)
            {
                score = NegaScout(board, depth, -Inf, Inf, ref iterBest);
            }
            else
            {
                int lo = prevScore - AspirationDelta;
                int hi = prevScore + AspirationDelta;
                score = NegaScout(board, depth, lo, hi, ref iterBest);
                if (score <= lo)        // fail-low: 下限を広げて再探索
                {
                    iterBest = -1;
                    score = NegaScout(board, depth, -Inf, hi, ref iterBest);
                }
                else if (score >= hi)   // fail-high: 上限を広げて再探索
                {
                    iterBest = -1;
                    score = NegaScout(board, depth, lo, Inf, ref iterBest);
                }
            }
            prevScore = score;

            if (iterBest >= 0) bestMove = iterBest;

            InfoLog($"深さ {depth,2} 完了: 最善手={BitBoard.BitToCoord(bestMove)}  スコア={score,10}  ノード={_recursionsCount}");
        }
        return bestMove;
    }

    /// <summary>
    /// PVS（NegaScout）探索。置換表・完全読みを統合。
    /// board.Player = 現在の手番プレイヤーの石。
    /// </summary>
    private int NegaScout(BitBoard board, int depth, int alpha, int beta, ref int bestMove)
    {
        ulong hash = _tt.ComputeHash(board);
        // 置換表に書く値の種類(上限・下限・正確)は、探索を始める前のαで決める。ループ中に更新したαで決めると、βカットの値が「上限」として書かれてしまう
        int originalAlpha = alpha;

        // 置換表参照
        if (_tt.TryGet(hash, depth, alpha, beta, out int ttScore, out int ttBestMove))
        {
            if (bestMove == -1 && ttBestMove >= 0)
                bestMove = ttBestMove;
            return ttScore;
        }

        // 葉ノード
        if (depth <= 0)
        {
            _recursionsCount++;
            int eval = _evaluator.Evaluate(board);
            _tt.Store(hash, 0, eval, alpha, beta, -1);
            return eval;
        }

        ulong movesBit = board.GetMoves();

        // パスの処理
        if (movesBit == 0)
        {
            BitBoard passed = new BitBoard(board.Opponent, board.Player);
            if (passed.GetMoves() == 0)
            {
                int finalScore = FinalScore(board);
                _tt.Store(hash, depth, finalScore, alpha, beta, -1);
                return finalScore;
            }
            _recursionsCount++;
            int passScore = -NegaScout(new BitBoard(board.Opponent, board.Player), depth - 1, -beta, -alpha, ref bestMove);
            _tt.Store(hash, depth, passScore, alpha, beta, -1);
            return passScore;
        }

        // stackalloc でGCフリーな合法手バッファを確保
        Span<int> movesBuffer = stackalloc int[64];
        int moveCount = BitBoard.GetMoveArray(movesBit, movesBuffer);
        Span<int> moves = movesBuffer[..moveCount];

        // Move Ordering 1: 置換表の最善手を先頭にスワップ
        int insertPos = 0;
        if (ttBestMove >= 0)
        {
            for (int k = 0; k < moveCount; k++)
            {
                if (moves[k] == ttBestMove)
                {
                    moves[k] = moves[0]; moves[0] = ttBestMove;
                    insertPos = 1;
                    break;
                }
            }
        }

        // Move Ordering 2: Killer Move（TT手の直後に配置）
        int killer1 = depth < _killerMoves.GetLength(0) ? _killerMoves[depth, 0] : -1;
        int killer2 = depth < _killerMoves.GetLength(0) ? _killerMoves[depth, 1] : -1;
        if (killer1 >= 0 && killer1 != ttBestMove)
        {
            for (int k = insertPos; k < moveCount; k++)
            {
                if (moves[k] == killer1)
                {
                    if (k != insertPos) { moves[k] = moves[insertPos]; moves[insertPos] = killer1; }
                    insertPos++;
                    break;
                }
            }
        }
        if (killer2 >= 0 && killer2 != ttBestMove && killer2 != killer1)
        {
            for (int k = insertPos; k < moveCount; k++)
            {
                if (moves[k] == killer2)
                {
                    if (k != insertPos) { moves[k] = moves[insertPos]; moves[insertPos] = killer2; }
                    insertPos++;
                    break;
                }
            }
        }

        // Move Ordering 3: 残り手を History スコア降順で挿入ソート（手数が少ないためO(n²)で十分）
        for (int k = insertPos + 1; k < moveCount; k++)
        {
            int key = moves[k];
            int keyScore = _historyTable[key];
            int j = k - 1;
            while (j >= insertPos && _historyTable[moves[j]] < keyScore)
            {
                moves[j + 1] = moves[j];
                j--;
            }
            moves[j + 1] = key;
        }

        int localBestMove = moves[0];
        int bestScore = -Inf;

        for (int i = 0; i < moveCount; i++)
        {
            int move = moves[i];
            BitBoard next = board.DoMove(move);
            _recursionsCount++;

            int score;
            if (i == 0)
            {
                // PV手はフルウィンドウで探索
                int dummy = -1;
                score = -NegaScout(next, depth - 1, -beta, -alpha, ref dummy);
            }
            else
            {
                // LMR（Late Move Reductions）:
                // Move Ordering で後半の手（TT/Killer以外）は外れの可能性が高い。
                // depth-2 で Null Window 探索し、alpha を超えた場合のみ full depth で再探索する。
                // 条件: depth>=3 かつ insertPos（TT/Killer枠）より後の手
                int dummy = -1;
                bool canLMR = depth >= 3 && i >= Math.Max(2, insertPos);
                int lmrDepth = canLMR ? depth - 2 : depth - 1;

                score = -NegaScout(next, lmrDepth, -alpha - 1, -alpha, ref dummy);

                // LMR で alpha を超えた → full depth の Null Window で再確認
                if (canLMR && score > alpha)
                    score = -NegaScout(next, depth - 1, -alpha - 1, -alpha, ref dummy);

                // PVS再探索: Null Window を超えた場合のみ（この手が PV 候補）
                if (score > alpha && score < beta)
                {
                    _pvsResearchCount++;
                    score = -NegaScout(next, depth - 1, -beta, -alpha, ref dummy);
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                localBestMove = move;
                bestMove = move; // 子の局面では使われない(呼び出し側が捨てる ref を渡す)。ルートの最善手になる
            }

            alpha = Math.Max(alpha, score);
            if (alpha >= beta)
            {
                _abCutCount++;
                // Killer Move 更新（静寂手のみ：TT手でなければ登録）
                if (move != ttBestMove && depth < _killerMoves.GetLength(0))
                {
                    if (move != _killerMoves[depth, 0])
                    {
                        _killerMoves[depth, 1] = _killerMoves[depth, 0];
                        _killerMoves[depth, 0] = move;
                    }
                }
                // History Heuristic 更新
                _historyTable[move] += depth * depth;
                _tt.Store(hash, depth, bestScore, originalAlpha, beta, localBestMove);
                return bestScore;
            }
        }

        _tt.Store(hash, depth, bestScore, originalAlpha, beta, localBestMove);
        return bestScore;
    }

    // ---- 完全読み ----
    // 完全読みの値は、手番から見た最終の石数の差(-64〜64。空きマスは数えない)。
    // 中盤の探索の点数(WinScore + 石数差)とは別の尺度で、置換表は手ごとに空にするので混ざらない。

    /// <summary>空きマスがこの数以下になったら、置換表と並べ替えを使わない小さな探索に切り替える</summary>
    private const int SmallSolveEmpties = 6;
    /// <summary>空きマスがこの数以上の局面では、評価関数も使って手を並べる(未満は相手の打てる手の数だけで並べる)</summary>
    private const int EvalOrderingEmpties = 12;

    // 盤面を4つに分けたマスク(偶数理論: 空きマスが奇数個の区画から先に打つ)
    private const ulong QuadrantTopLeft = 0x0000_0000_0F0F_0F0FUL;
    private const ulong QuadrantTopRight = 0x0000_0000_F0F0_F0F0UL;
    private const ulong QuadrantBottomLeft = 0x0F0F_0F0F_0000_0000UL;
    private const ulong QuadrantBottomRight = 0xF0F0_F0F0_0000_0000UL;

    /// <summary>完全読みで最善手を求める</summary>
    private int SolvePerfectRoot(BitBoard board)
    {
        InfoLog($"残り{board.EmptyCount}マス → 完全読みモードへ切り替え");
        _perfSolveTriggered = true;

        // ルートは自分で手を並べて読み、最善手を確実に手元に残す
        Span<int> moves = stackalloc int[64];
        int moveCount = OrderSolveMoves(board, board.GetMoves(), -1, moves);

        int alpha = -65, beta = 65;
        int bestMove = moves[0];
        int bestScore = -65;
        for (int i = 0; i < moveCount; i++)
        {
            BitBoard next = board.DoMove(moves[i]);
            _recursionsCount++;
            int score;
            if (i == 0)
            {
                score = -Solve(next, -beta, -alpha);
            }
            else
            {
                score = -Solve(next, -alpha - 1, -alpha);
                if (score > alpha) score = -Solve(next, -beta, -alpha);
            }
            if (score > bestScore) { bestScore = score; bestMove = moves[i]; }
            alpha = Math.Max(alpha, score);
        }

        string result = bestScore > 0 ? $"勝ち(+{bestScore}石差)" : bestScore < 0 ? $"負け({bestScore}石差)" : "引き分け";
        InfoLog($"完全読み完了: 最善手={BitBoard.BitToCoord(bestMove)}  結果={result}  ノード={_recursionsCount}");
        return bestMove;
    }

    /// <summary>完全読み(置換表・ETC・手の並べ替えつきの NegaScout)。値は手番から見た最終の石数の差</summary>
    private int Solve(BitBoard board, int alpha, int beta)
    {
        int empties = board.EmptyCount;
        if (empties <= SmallSolveEmpties) return SolveSmall(board, alpha, beta, empties);

        // 100万局面ごとに進み具合を書く
        if (_recursionsCount - _lastProgressLogNodes >= 1_000_000)
        {
            _lastProgressLogNodes = _recursionsCount;
            InfoLog($"完全読み進捗: ノード={_recursionsCount}  残り{empties}マス");
        }

        ulong hash = _tt.ComputeHash(board);
        // 置換表に書く値の種類は、探索を始める前のαで決める
        int originalAlpha = alpha;
        if (_tt.TryGet(hash, 64, alpha, beta, out int ttScore, out int ttBestMove))
            return ttScore;

        ulong movesBit = board.GetMoves();
        if (movesBit == 0)
        {
            var passed = new BitBoard(board.Opponent, board.Player);
            if (passed.GetMoves() == 0) return FinalDiscDiff(board);
            _recursionsCount++;
            return -Solve(passed, -beta, -alpha);
        }

        Span<int> moves = stackalloc int[64];
        int moveCount = OrderSolveMoves(board, movesBit, ttBestMove, moves);

        // ETC(Enhanced Transposition Cutoff): 子の局面の置換表に「上限 S ≤ -β」があれば、親は β 以上で切れる
        for (int i = 0; i < moveCount; i++)
        {
            ulong childHash = _tt.ComputeHash(board.DoMove(moves[i]));
            if (_tt.TryGetRaw(childHash, 64, out TTEntryType childType, out int childScore)
                && childType == TTEntryType.UpperBound && -childScore >= beta)
            {
                _abCutCount++;
                _tt.Store(hash, 64, -childScore, originalAlpha, beta, moves[i]);
                return -childScore;
            }
        }

        int bestScore = -65;
        int localBestMove = moves[0];
        for (int i = 0; i < moveCount; i++)
        {
            BitBoard next = board.DoMove(moves[i]);
            _recursionsCount++;

            int score;
            if (i == 0)
            {
                score = -Solve(next, -beta, -alpha);
            }
            else
            {
                score = -Solve(next, -alpha - 1, -alpha);
                if (score > alpha && score < beta)
                {
                    _pvsResearchCount++;
                    score = -Solve(next, -beta, -alpha);
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                localBestMove = moves[i];
            }
            alpha = Math.Max(alpha, score);
            if (alpha >= beta)
            {
                _abCutCount++;
                break;
            }
        }

        _tt.Store(hash, 64, bestScore, originalAlpha, beta, localBestMove);
        return bestScore;
    }

    /// <summary>
    /// 完全読みで読む手の順番を決めて moves に書き、手の数を返す。
    /// 置換表の最善手を先頭にし、残りは「相手の打てる手が少ない手」を先にする(速さ優先)。
    /// 空きマスが多い局面では、評価関数(相手から見た値。小さいほど自分に良い)も足す
    /// </summary>
    private int OrderSolveMoves(BitBoard board, ulong movesBit, int ttBestMove, Span<int> moves)
    {
        Span<int> keys = stackalloc int[64];
        bool useEval = board.EmptyCount >= EvalOrderingEmpties;
        int count = 0;
        while (movesBit != 0)
        {
            int move = BitOperations.TrailingZeroCount(movesBit);
            movesBit &= movesBit - 1;

            int key;
            if (move == ttBestMove)
            {
                key = int.MinValue;
            }
            else
            {
                BitBoard next = board.DoMove(move);
                // 相手の打てる手の数(角は数えにくくするため2倍)。値が小さいほど先に読む
                ulong opponentMoves = next.GetMoves();
                key = (BitOperations.PopCount(opponentMoves) + BitOperations.PopCount(opponentMoves & CornerMask)) * 1024;
                if (useEval) key += _evaluator.Evaluate(next);
            }

            // 挿入ソート(手の数は多くても30程度なので十分)
            int j = count - 1;
            while (j >= 0 && keys[j] > key)
            {
                keys[j + 1] = keys[j];
                moves[j + 1] = moves[j];
                j--;
            }
            keys[j + 1] = key;
            moves[j + 1] = move;
            count++;
        }
        return count;
    }

    private const ulong CornerMask = 0x8100000000000081UL;

    /// <summary>
    /// 残りの少ない局面の完全読み。合法手を作らず、空きマスに順に置いてみる。
    /// 空きマスが奇数個の区画から先に打つ(偶数理論)。置換表は使わない(局面が多く、引く手間のほうが大きい)
    /// </summary>
    private int SolveSmall(BitBoard board, int alpha, int beta, int empties)
    {
        _recursionsCount++;
        ulong empty = ~(board.Player | board.Opponent);
        if (empties == 1) return SolveLastMove(board, empty);

        // 空きマスが奇数個の区画を先にする
        ulong odd = 0;
        if ((BitOperations.PopCount(empty & QuadrantTopLeft) & 1) != 0) odd |= QuadrantTopLeft;
        if ((BitOperations.PopCount(empty & QuadrantTopRight) & 1) != 0) odd |= QuadrantTopRight;
        if ((BitOperations.PopCount(empty & QuadrantBottomLeft) & 1) != 0) odd |= QuadrantBottomLeft;
        if ((BitOperations.PopCount(empty & QuadrantBottomRight) & 1) != 0) odd |= QuadrantBottomRight;

        int bestScore = -65;
        bool moved = false;
        for (int pass = 0; pass < 2; pass++)
        {
            ulong squares = pass == 0 ? empty & odd : empty & ~odd;
            while (squares != 0)
            {
                int pos = BitOperations.TrailingZeroCount(squares);
                squares &= squares - 1;
                ulong flipped = board.GetFlipped(pos);
                if (flipped == 0) continue;

                moved = true;
                var next = new BitBoard(board.Opponent ^ flipped, board.Player | flipped | (1UL << pos));
                int score = -SolveSmall(next, -beta, -alpha, empties - 1);
                if (score > bestScore)
                {
                    bestScore = score;
                    if (score > alpha)
                    {
                        alpha = score;
                        if (alpha >= beta) return bestScore;
                    }
                }
            }
        }
        if (moved) return bestScore;

        // 打てないときはパス。相手も打てなければ終局
        var passed = new BitBoard(board.Opponent, board.Player);
        if (passed.GetMoves() == 0) return FinalDiscDiff(board);
        return -SolveSmall(passed, -beta, -alpha, empties);
    }

    /// <summary>空きマスが1つの局面の値(打てる側が打ち、どちらも打てなければそのまま終局)</summary>
    private static int SolveLastMove(BitBoard board, ulong empty)
    {
        int pos = BitOperations.TrailingZeroCount(empty);
        int diff = board.PlayerCount - board.OpponentCount;
        int flipped = BitOperations.PopCount(board.GetFlipped(pos));
        if (flipped > 0) return diff + 2 * flipped + 1;

        int opponentFlipped = BitOperations.PopCount(new BitBoard(board.Opponent, board.Player).GetFlipped(pos));
        if (opponentFlipped > 0) return diff - 2 * opponentFlipped - 1;
        return diff;
    }

    /// <summary>終局の値(手番から見た石数の差。空きマスは数えない)</summary>
    private static int FinalDiscDiff(BitBoard board) => board.PlayerCount - board.OpponentCount;

    /// <summary>
    /// ゲーム終了時のスコア（現プレイヤー視点の駒数差）
    /// </summary>
    private static int FinalScore(BitBoard board)
    {
        int p = board.PlayerCount;
        int o = board.OpponentCount;
        if (p > o) return WinScore + (p - o);
        if (p < o) return -WinScore - (o - p);
        return 0;
    }

    /// <summary>評価関数だけを呼ぶ(探索の確認 SearchCheck で、同じ評価関数を使った単純な探索と比べるため)</summary>
    internal int Evaluate(BitBoard board) => _evaluator.Evaluate(board);
}

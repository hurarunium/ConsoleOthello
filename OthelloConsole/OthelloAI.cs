using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Numerics;
using static OthelloDebugLog;

/// <summary>
/// ビットボード + 反復深化 + 置換表 + 完全読み + PVS（NegaScout）による最強オセロAI
/// </summary>
public partial class OthelloAI
{
    /// <summary>評価関数(学習したパターン評価)</summary>
    private readonly PatternEvaluator _evaluator;

    /// <param name="transpositionTableSizeBits">置換表の件数を 2 の何乗にするか(標準は約800万件。ブラウザ版は小さくする)</param>
    public OthelloAI(PatternEvaluator evaluator, int transpositionTableSizeBits = TranspositionTable.DefaultSizeBits)
    {
        _evaluator = evaluator;
        _transpositionTableSizeBits = transpositionTableSizeBits;
        _tt = new TranspositionTable(transpositionTableSizeBits);
        _solveTt = _tt;
    }

    /// <summary>
    /// 中盤の探索で MPC を使うかの上書き。null ならレベルどおり(AiLevel.UsesMpc)。
    /// MPC あり・なしを比べるため(--mpc / --no-mpc)に使う
    /// </summary>
    public bool? MpcOverride { get; init; }

    /// <summary>MPC の余裕(誤差の標準偏差の何倍か)の上書き。null ならレベルどおり(AiLevel.MpcSelectivity)。切り方を選ぶため(--mpc-t)に使う</summary>
    public double? MpcSelectivityOverride { get; init; }

    /// <summary>いま選んでいる手で MPC を使うか(ChooseMove でレベルから決める)</summary>
    private bool _useMpc;
    /// <summary>いま選んでいる手の MPC の余裕</summary>
    private double _mpcSelectivity;
    /// <summary>MPC の浅い読みの最中か(浅い読みの中では MPC を使わない)</summary>
    private bool _inMpcProbe;
    /// <summary>反復深化でいま読んでいる深さ(ルートでは MPC を使わないため)</summary>
    private int _iterationDepth;
    private int _mpcCutCount;          // MPC で切った回数

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

    private readonly TranspositionTable _tt;
    private readonly int _transpositionTableSizeBits;
    /// <summary>
    /// 完全読み(Solve)が使う置換表。レベルでは中盤と同じ表(手ごとに空にする)。
    /// 持ち時間つきの探索では別の表にする(完全読みの値は中盤の評価値と尺度が違い、手をまたいで残すと混ざるため)
    /// </summary>
    private TranspositionTable _solveTt;

    // 完全読みに切り替える残り空きマス数。レベルから決める(AiLevel.PerfectSolveEmpties)
    private int _perfectSolveEmpties;

    // 探索の窓の無限大。どの評価値・終局の点数よりも外側にある必要がある
    private const int Inf = 1_000_000_000;

    // 終局の点数の基準(勝ち = WinScore + 終局の値、負け = -WinScore + 終局の値。終局の値は FinalValue)。
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
        _useMpc = MpcOverride ?? AiLevel.UsesMpc(level);
        _mpcSelectivity = MpcSelectivityOverride ?? AiLevel.MpcSelectivity(level);
        _mpcCutCount = 0;

        InfoLog($"AI思考開始 (レベル{level}: 中盤{_maxDepth}手読み・残り{_perfectSolveEmpties}マスから完全読み・MPC{(_useMpc ? $"あり({_mpcSelectivity:F2})" : "なし")}  残りマス={board.EmptyCount})");
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

        DebugLog($"探索統計: αβカット={_abCutCount}  PVS再探索={_pvsResearchCount}  MPCカット={_mpcCutCount}  完全読み={(_perfSolveTriggered ? "あり" : "なし")}");
        DebugLog($"置換表: ヒット={_tt.HitCount} ({_tt.HitRate:F1}%)  ミス={_tt.MissCount}");

        GameLog($"AI評価\nレベル:{level} 探索深さ:{_maxDepth} 完全読み:{_perfSolveTriggered} 総計算手順回数：{_recursionsCount} 最良手:{BitBoard.BitToCoord(bestMove)}");
        return bestMove;
    }

    /// <summary>
    /// 近似(MPC)なしで maxDepth まで反復深化し、深さ1〜maxDepth のルートの値を返す(添字 = 深さ。0 は使わない)。
    /// MPC の誤差を集める測定(--mpc-stats)で、浅い読みと深い読みの値を比べるために使う
    /// </summary>
    internal int[] RootValuesByDepth(BitBoard board, int maxDepth)
    {
        _recursionsCount = 0;
        _maxDepth = maxDepth;
        _useMpc = false;
        _tt.Clear();
        _killerMoves = new int[maxDepth + 2, 2];
        for (int d = 0; d < maxDepth + 2; d++) { _killerMoves[d, 0] = -1; _killerMoves[d, 1] = -1; }
        Array.Clear(_historyTable, 0, 64);

        var values = new int[maxDepth + 1];
        IterativeDeepening(board, maxDepth, values);
        return values;
    }

    /// <summary>
    /// MPC で、深さ deep の読みの代わりに使う浅い読みの深さ。深さの半分ほどにし、偶奇を deep にそろえる
    /// (オセロの評価値は、どちらの手番で読み終えるかで偏るため)
    /// </summary>
    internal static int MpcShallowDepth(int deep) => ((deep / 2) & ~1) + (deep & 1);

    /// <summary>終局まで読み切った値か(中盤の評価値ではなく、WinScore を含む点数か)</summary>
    internal static bool IsFinalScore(int score) => Math.Abs(score) >= WinScore / 2;

    /// <summary>反復深化で maxDepth まで読み、最善手を返す。valuesByDepth を渡すと、各深さのルートの値を書く</summary>
    private int IterativeDeepening(BitBoard board, int maxDepth, int[]? valuesByDepth = null)
    {
        int bestMove = -1;
        int prevScore = 0; // Aspiration Window 用（前の深さのスコア）
        EvalState rootState = PatternEvaluator.CreateState(board);

        for (int depth = 1; depth <= maxDepth; depth++)
        {
            if (!CanStartIteration(depth)) break; // 持ち時間の使い方のとき、残りが前の深さにかかった時間より短ければ入らない
            long iterationStart = Stopwatch.GetTimestamp();
            _iterationDepth = depth;
            for (int i = 0; i < 64; i++) _historyTable[i] >>= 1;

            int iterBest = -1;
            int score;

            // Aspiration Window: depth>=3 は前の深さのスコアを基準に探索窓を絞る
            // Delta=50 の根拠: 評価値の通常変動幅（50〜200程度）に対して適度な窓幅。
            // 小さすぎると再探索が頻発し、大きすぎると窓の効果が薄れる。
            const int AspirationDelta = 50;
            if (depth <= 2)
            {
                score = NegaScout(board, rootState, depth, -Inf, Inf, ref iterBest);
            }
            else
            {
                int lo = prevScore - AspirationDelta;
                int hi = prevScore + AspirationDelta;
                score = NegaScout(board, rootState, depth, lo, hi, ref iterBest);
                if (_timeUp) break; // 時間切れの深さは捨てる(値が正しくない)
                if (score <= lo)        // fail-low: 下限を広げて再探索
                {
                    InfoLog($"深さ {depth,2} 窓の下に外れた(値 {score} ≤ {lo}、ここまで ノード={_recursionsCount})。下限を広げて読み直す");
                    iterBest = -1;
                    score = NegaScout(board, rootState, depth, -Inf, hi, ref iterBest);
                    if (_timeUp) break;
                }
                else if (score >= hi)   // fail-high: 上限を広げて再探索
                {
                    InfoLog($"深さ {depth,2} 窓の上に外れた(値 {score} ≥ {hi}、ここまで ノード={_recursionsCount})。上限を広げて読み直す");
                    iterBest = -1;
                    score = NegaScout(board, rootState, depth, lo, Inf, ref iterBest);
                    if (_timeUp) break;
                }
            }
            if (_timeUp) break;
            prevScore = score;
            if (valuesByDepth != null) valuesByDepth[depth] = score;

            if (iterBest >= 0) bestMove = iterBest;
            FinishIteration(depth, Stopwatch.GetTimestamp() - iterationStart);

            InfoLog($"深さ {depth,2} 完了: 最善手={BitBoard.BitToCoord(bestMove)}  スコア={score,10}  ノード={_recursionsCount}");
        }
        return bestMove;
    }

    /// <summary>
    /// PVS（NegaScout）探索。置換表・完全読みを統合。
    /// board.Player = 現在の手番プレイヤーの石。
    /// </summary>
    private int NegaScout(BitBoard board, in EvalState state, int depth, int alpha, int beta, ref int bestMove)
    {
        if (IsTimeUp()) return 0; // 時間切れ(値は捨てられる)。持ち時間のない使い方(レベル)では常に false
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
            int eval = Evaluate(board, state);
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
            PatternEvaluator.Pass(state, out EvalState passedState);
            int passScore = -NegaScout(passed, passedState, depth - 1, -beta, -alpha, ref bestMove);
            if (_timeUp) return 0; // 時間切れの値を置換表に書かない
            _tt.Store(hash, depth, passScore, alpha, beta, -1);
            return passScore;
        }

        // MPC: 浅い読みの値から、深い読みが窓の外になると見込めるなら、読まずに切る(ルートでは使わない)
        if (_useMpc && !_inMpcProbe && depth >= MpcMinDepth && depth < _iterationDepth
            && TryMultiProbCut(board, state, depth, alpha, beta, out int mpcScore))
        {
            _mpcCutCount++;
            return mpcScore;
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

        if (Ordering != null && depth >= MidgameOrderingDepth)
        {
            // Move Ordering 2': 深い節点では、置換表の手の後ろを「並べ替え用の軽い評価 + 相手の打てる手の数」の小さい順に並べる
            OrderByLightEvaluation(board, state, moves, insertPos);
        }
        else
        {
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
        }

        int localBestMove = moves[0];
        int bestScore = -Inf;

        for (int i = 0; i < moveCount; i++)
        {
            int move = moves[i];
            ulong flipped = board.GetFlipped(move);
            BitBoard next = DoMove(board, move, flipped);
            PatternEvaluator.Play(state, move, flipped, out EvalState nextState);
            _recursionsCount++;

            int score;
            if (i == 0)
            {
                // PV手はフルウィンドウで探索
                int dummy = -1;
                score = -NegaScout(next, nextState, depth - 1, -beta, -alpha, ref dummy);
            }
            else
            {
                // 2手目以降は Null Window で「αより良いか」だけを確かめる
                int dummy = -1;
                score = -NegaScout(next, nextState, depth - 1, -alpha - 1, -alpha, ref dummy);

                // PVS再探索: Null Window を超えた場合のみ（この手が PV 候補）
                if (score > alpha && score < beta)
                {
                    _pvsResearchCount++;
                    score = -NegaScout(next, nextState, depth - 1, -beta, -alpha, ref dummy);
                }
            }

            if (_timeUp) return 0; // 時間切れの値を置換表に書かない
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

    // ---- MPC(Multi-ProbCut) ----
    // 深さ D の読みの値は、浅い読み(深さ d = MpcShallowDepth(D))の値 + 平均のずれ にほぼ等しく、
    // その誤差は正規分布に近い(標準偏差 σ)。浅い読みで「β + 余裕 以上」なら、深い読みも β 以上と見込んで切る(αも同じ)。
    // ずれと σ は、実戦の局面で集めた値(--mpc-stats)。

    /// <summary>MPC を使う最小の深さ(これより浅いと、浅い読みの手間が節約より大きい)</summary>
    private const int MpcMinDepth = 3;

    /// <summary>
    /// MPC を試す。切れたら true で、score に窓の端(β か α)を返す。
    /// 静的な評価値がすでに窓の外にあるときだけ、浅い読みを試す(浅い読みの無駄を減らすため)
    /// </summary>
    private bool TryMultiProbCut(BitBoard board, in EvalState state, int depth, int alpha, int beta, out int score)
    {
        score = 0;
        int shallow = MpcShallowDepth(depth);
        (double meanDiscs, double sigmaDiscs) = MpcError.Get(board.EmptyCount, depth);
        // 値は評価値の単位(石数 × Scale)
        int margin = (int)Math.Ceiling(_mpcSelectivity * sigmaDiscs * PatternEvaluator.Scale);
        int shift = (int)Math.Round(meanDiscs * PatternEvaluator.Scale);
        int eval = Evaluate(board, state);
        int dummy = -1;

        // 深い読み ≈ 浅い読み + shift。深い読み ≥ β と見込めるのは、浅い読み ≥ β − shift + margin のとき
        if (beta < WinScore / 2 && eval >= beta)
        {
            int probeBeta = beta - shift + margin;
            _inMpcProbe = true;
            int value = NegaScout(board, state, shallow, probeBeta - 1, probeBeta, ref dummy);
            _inMpcProbe = false;
            if (value >= probeBeta) { score = beta; return true; }
        }
        if (alpha > -WinScore / 2 && eval <= alpha)
        {
            int probeAlpha = alpha - shift - margin;
            _inMpcProbe = true;
            int value = NegaScout(board, state, shallow, probeAlpha, probeAlpha + 1, ref dummy);
            _inMpcProbe = false;
            if (value <= probeAlpha) { score = alpha; return true; }
        }
        return false;
    }

    // ---- 完全読み ----
    // 完全読みの値は、手番から見た終局の値(FinalValue。石数の差 × FinalValueScale ± 終局の空きマス)。
    // 中盤の探索の点数(WinScore + 終局の値)とは別の尺度で、置換表は手ごとに空にするので混ざらない。

    /// <summary>終局の値で、石数の差に掛ける倍率(終局の空きマスは60より少ないので、64倍すれば石数の差の順が崩れない)</summary>
    private const int FinalValueScale = 64;
    /// <summary>終局の値の最大(64対0で、空きマスが60残ったとしても超えない)</summary>
    private const int MaxFinalValue = 64 * FinalValueScale + 60;
    /// <summary>どの終局の値よりも小さい値(最善の値を探し始めるときに使う)</summary>
    private const int BelowAnyFinalValue = -MaxFinalValue - 1;
    /// <summary>空きマスがこの数以下になったら、置換表と並べ替えを使わない小さな探索に切り替える</summary>
    private const int SmallSolveEmpties = 6;
    /// <summary>空きマスがこの数以上の局面では、評価関数も使って手を並べる(未満は相手の打てる手の数だけで並べる)</summary>
    private const int EvalOrderingEmpties = 12;

    /// <summary>
    /// 中盤の手の並べ替え用の軽い評価(なければ、中盤は置換表・Killer・History だけで並べる)。番号は探索で持ち歩いている EvalState を使う。
    /// 完全読みの並べ替えには使わない(試すと、64か所の評価で並べるより読む局面が11〜24%増えた)
    /// </summary>
    public OrderingEvaluator? Ordering { get; init; }
    /// <summary>
    /// 中盤で、並べ替え用の軽い評価で手を並べ始める深さ(--ord-mid-depth。これより浅い節点は Killer と History で並べる)。
    /// レベル12の中盤で2〜6を比べ、4がいちばん速かった(浅くすると局面は減るが、並べる手間のほうが大きい)
    /// </summary>
    public int MidgameOrderingDepth { get; init; } = DefaultMidgameOrderingDepth;
    /// <summary>中盤の並べ替えで、相手の打てる手1つあたりに足す値(--ord-mobility。64・128・256を比べて64にした)</summary>
    public int MidgameOrderingMobilityWeight { get; init; } = DefaultMidgameOrderingMobilityWeight;

    private const int DefaultMidgameOrderingDepth = 4;
    private const int DefaultMidgameOrderingMobilityWeight = 64;

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
        EvalState rootState = PatternEvaluator.CreateState(board);
        int moveCount = OrderSolveMoves(board, rootState, board.GetMoves(), -1, moves);

        // 少なくとも1手は打つので、終局の値は「64石差 + 今の空きマス」を超えない(超えない値が見つかったら、ほかの手を読まずに済む)
        int bound = FinalValueBound(board.EmptyCount);
        int alpha = -bound - 1, beta = bound + 1;
        int bestMove = moves[0];
        int bestScore = BelowAnyFinalValue;
        for (int i = 0; i < moveCount; i++)
        {
            ulong flipped = board.GetFlipped(moves[i]);
            BitBoard next = DoMove(board, moves[i], flipped);
            PlayForSolve(rootState, moves[i], flipped, next.EmptyCount, out EvalState nextState);
            _recursionsCount++;
            int score;
            if (i == 0)
            {
                score = -Solve(next, nextState, -beta, -alpha);
            }
            else
            {
                score = -Solve(next, nextState, -alpha - 1, -alpha);
                if (score > alpha) score = -Solve(next, nextState, -beta, -alpha);
            }
            if (score > bestScore) { bestScore = score; bestMove = moves[i]; }
            alpha = Math.Max(alpha, score);
            if (alpha >= bound) break; // これより良い値はない
        }

        (int discDiff, int emptiesLeft) = SplitFinalValue(bestScore);
        string result = bestScore > 0 ? $"勝ち(+{discDiff}石差、空き{emptiesLeft}マスで終局)"
                      : bestScore < 0 ? $"負け({discDiff}石差、空き{emptiesLeft}マスで終局)" : "引き分け";
        InfoLog($"完全読み完了: 最善手={BitBoard.BitToCoord(bestMove)}  結果={result}  ノード={_recursionsCount}");
        return bestMove;
    }

    /// <summary>
    /// 完全読み(置換表・ETC・手の並べ替えつきの NegaScout)。値は手番から見た終局の値(FinalValue)。
    /// state(差分評価の番号)は、手の並べ替えに評価関数を使う残り EvalOrderingEmpties マス以上の局面でだけ正しい値が入っている
    /// </summary>
    private int Solve(BitBoard board, in EvalState state, int alpha, int beta)
    {
        // 値は ±FinalValueBound(空きマス) の外にならないので、窓をその範囲に絞る(いちばん良い勝ちが見つかったら、ほかの手を読まずに済む)
        int empties = board.EmptyCount;
        int bound = FinalValueBound(empties);
        if (beta > bound) beta = bound;
        if (alpha < -bound) alpha = -bound;
        if (alpha >= beta) return alpha;

        if (empties <= SmallSolveEmpties) return SolveSmall(board, alpha, beta, empties);
        if (IsTimeUp()) return 0; // 時間切れ(値は捨てられる)

        // 確定石で値の上限を絞る(勝ちを探しているときだけ)
        if (alpha > 0)
        {
            int upper = StabilityUpperBound(board, empties);
            if (upper <= alpha) return upper;
            if (upper < beta) beta = upper;
        }

        // 100万局面ごとに進み具合を書く
        if (_recursionsCount - _lastProgressLogNodes >= 1_000_000)
        {
            _lastProgressLogNodes = _recursionsCount;
            InfoLog($"完全読み進捗: ノード={_recursionsCount}  残り{empties}マス");
        }

        ulong hash = _solveTt.ComputeHash(board);
        // 置換表に書く値の種類は、探索を始める前のαで決める
        int originalAlpha = alpha;
        if (_solveTt.TryGet(hash, 64, alpha, beta, out int ttScore, out int ttBestMove))
            return ttScore;

        ulong movesBit = board.GetMoves();
        if (movesBit == 0)
        {
            var passed = new BitBoard(board.Opponent, board.Player);
            if (passed.GetMoves() == 0) return FinalValue(board);
            _recursionsCount++;
            PatternEvaluator.Pass(state, out EvalState passedState);
            return -Solve(passed, passedState, -beta, -alpha);
        }

        Span<int> moves = stackalloc int[64];
        int moveCount = OrderSolveMoves(board, state, movesBit, ttBestMove, moves);

        // ETC(Enhanced Transposition Cutoff): 子の局面の置換表に「上限 S ≤ -β」があれば、親は β 以上で切れる
        for (int i = 0; i < moveCount; i++)
        {
            ulong childHash = _solveTt.ComputeHash(board.DoMove(moves[i]));
            if (_solveTt.TryGetRaw(childHash, 64, out TTEntryType childType, out int childScore)
                && childType == TTEntryType.UpperBound && -childScore >= beta)
            {
                _abCutCount++;
                _solveTt.Store(hash, 64, -childScore, originalAlpha, beta, moves[i]);
                return -childScore;
            }
        }

        int bestScore = BelowAnyFinalValue;
        int localBestMove = moves[0];
        for (int i = 0; i < moveCount; i++)
        {
            ulong flipped = board.GetFlipped(moves[i]);
            BitBoard next = DoMove(board, moves[i], flipped);
            PlayForSolve(state, moves[i], flipped, empties - 1, out EvalState nextState);
            _recursionsCount++;

            int score;
            if (i == 0)
            {
                score = -Solve(next, nextState, -beta, -alpha);
            }
            else
            {
                score = -Solve(next, nextState, -alpha - 1, -alpha);
                if (score > alpha && score < beta)
                {
                    _pvsResearchCount++;
                    score = -Solve(next, nextState, -beta, -alpha);
                }
            }

            if (_timeUp) return 0; // 時間切れの値を置換表に書かない
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

        _solveTt.Store(hash, 64, bestScore, originalAlpha, beta, localBestMove);
        return bestScore;
    }

    /// <summary>
    /// 完全読みで読む手の順番を決めて moves に書き、手の数を返す。
    /// 置換表の最善手を先頭にし、残りは「相手の打てる手が少ない手」を先にする(速さ優先)。
    /// 空きマスが多い局面では、評価関数(相手から見た値。小さいほど自分に良い)も足す。残り16マス以上では2手読みの値を使う
    /// </summary>
    private int OrderSolveMoves(BitBoard board, in EvalState state, ulong movesBit, int ttBestMove, Span<int> moves)
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
                ulong flipped = board.GetFlipped(move);
                BitBoard next = DoMove(board, move, flipped);
                // 相手の打てる手の数(角は2倍に数える)。値が小さいほど先に読む
                ulong opponentMoves = next.GetMoves();
                key = (BitOperations.PopCount(opponentMoves) + BitOperations.PopCount(opponentMoves & CornerMask)) * OrderingMobilityWeight;
                if (useEval)
                {
                    PatternEvaluator.Play(state, move, flipped, out EvalState nextState);
                    key += board.EmptyCount >= ShallowSearchOrderingEmpties
                        ? OrderingSearch(next, nextState, 1, -Inf, Inf)
                        : Evaluate(next, nextState);
                }
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

    /// <summary>手の並べ替えで、相手の打てる手1つあたりに足す値(評価値は石数×32。実戦の局面で比べて決めた)</summary>
    private const int OrderingMobilityWeight = 128;
    /// <summary>空きマスがこの数以上の局面では、2手読み(子の局面から1手読み)の値で手を並べる</summary>
    private const int ShallowSearchOrderingEmpties = 16;

    /// <summary>
    /// 中盤の並べ替え: moves[from..] を、子の局面の「並べ替え用の軽い評価(相手から見た値) + 相手の打てる手の数 × 重み」の小さい順に並べる
    /// </summary>
    private void OrderByLightEvaluation(BitBoard board, in EvalState state, Span<int> moves, int from)
    {
        Span<int> keys = stackalloc int[64];
        int childEmpties = board.EmptyCount - 1;
        for (int k = from; k < moves.Length; k++)
        {
            int move = moves[k];
            ulong flipped = board.GetFlipped(move);
            BitBoard next = DoMove(board, move, flipped);
            PatternEvaluator.Play(state, move, flipped, out EvalState nextState);
            ulong opponentMoves = next.GetMoves();
            int key = Ordering!.Evaluate(nextState, childEmpties)
                    + (BitOperations.PopCount(opponentMoves) + BitOperations.PopCount(opponentMoves & CornerMask)) * MidgameOrderingMobilityWeight;

            // 挿入ソート(小さい順)
            int j = k - 1;
            while (j >= from && keys[j] > key)
            {
                keys[j + 1] = keys[j];
                moves[j + 1] = moves[j];
                j--;
            }
            keys[j + 1] = key;
            moves[j + 1] = move;
        }
    }

    /// <summary>手の並べ替えに使う浅い探索(評価のαβ。置換表は使わない)。値は手番から見た評価値</summary>
    private int OrderingSearch(BitBoard board, in EvalState state, int depth, int alpha, int beta)
    {
        if (depth <= 0) return Evaluate(board, state);
        ulong movesBit = board.GetMoves();
        if (movesBit == 0)
        {
            var passed = new BitBoard(board.Opponent, board.Player);
            if (passed.GetMoves() == 0) return FinalScore(board);
            PatternEvaluator.Pass(state, out EvalState passedState);
            return -OrderingSearch(passed, passedState, depth - 1, -beta, -alpha);
        }
        int best = -Inf;
        while (movesBit != 0)
        {
            int move = BitOperations.TrailingZeroCount(movesBit);
            movesBit &= movesBit - 1;
            ulong flipped = board.GetFlipped(move);
            PatternEvaluator.Play(state, move, flipped, out EvalState nextState);
            int score = -OrderingSearch(DoMove(board, move, flipped), nextState, depth - 1, -beta, -alpha);
            if (score > best)
            {
                best = score;
                if (score > alpha)
                {
                    alpha = score;
                    if (alpha >= beta) break;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// 残りの少ない局面の完全読み。合法手を作らず、空きマスに順に置いてみる。
    /// 空きマスが奇数個の区画から先に打つ(偶数理論)。置換表は使わない(局面が多く、引く手間のほうが大きい)
    /// </summary>
    private int SolveSmall(BitBoard board, int alpha, int beta, int empties)
    {
        _recursionsCount++;
        int bound = FinalValueBound(empties);
        if (beta > bound) beta = bound;
        if (alpha < -bound) alpha = -bound;
        if (alpha >= beta) return alpha;

        ulong empty = ~(board.Player | board.Opponent);
        if (empties == 1) return SolveLastMove(board, empty);

        if (alpha > 0 && empties >= SmallStabilityEmpties)
        {
            int upper = StabilityUpperBound(board, empties);
            if (upper <= alpha) return upper;
            if (upper < beta) beta = upper;
        }

        // 空きマスが奇数個の区画を先にする
        ulong odd = 0;
        if ((BitOperations.PopCount(empty & QuadrantTopLeft) & 1) != 0) odd |= QuadrantTopLeft;
        if ((BitOperations.PopCount(empty & QuadrantTopRight) & 1) != 0) odd |= QuadrantTopRight;
        if ((BitOperations.PopCount(empty & QuadrantBottomLeft) & 1) != 0) odd |= QuadrantBottomLeft;
        if ((BitOperations.PopCount(empty & QuadrantBottomRight) & 1) != 0) odd |= QuadrantBottomRight;

        int bestScore = BelowAnyFinalValue;
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
        if (passed.GetMoves() == 0) return FinalValue(board);
        return -SolveSmall(passed, -beta, -alpha, empties);
    }

    /// <summary>
    /// 空きマスが1つの局面の値(打てる側が打ち、どちらも打てなければそのまま終局)。
    /// 打てば空きマスは0になるので、値は石数の差 × FinalValueScale だけになる
    /// </summary>
    private static int SolveLastMove(BitBoard board, ulong empty)
    {
        int pos = BitOperations.TrailingZeroCount(empty);
        int diff = board.PlayerCount - board.OpponentCount;
        int flipped = BitOperations.PopCount(board.GetFlipped(pos));
        if (flipped > 0) return (diff + 2 * flipped + 1) * FinalValueScale;

        int opponentFlipped = BitOperations.PopCount(new BitBoard(board.Opponent, board.Player).GetFlipped(pos));
        if (opponentFlipped > 0) return (diff - 2 * opponentFlipped - 1) * FinalValueScale;
        return FinalValue(board);
    }

    /// <summary>
    /// 終局の値(手番から見た値。企画 P-11・P-12)。
    /// 石数の差は、残った空きマスを勝った側の石として数える(公式の決まりと同じ。相手の石を全部取れば、いつでも64石差)。
    /// 値 = 石数の差 × FinalValueScale +(勝ちなら +空きマス、負けなら −空きマス)。
    /// 石数の差がいちばん大事で、差が同じなら、勝つときは早く終わる(空きマスが多く残る)ほど高く、負けるときは長引くほど高い。
    /// 終局の局面だけで決まる値なので、どこから読んでも同じになり、置換表にもそのまま書ける
    /// </summary>
    private static int FinalValue(BitBoard board)
    {
        int player = board.PlayerCount;
        int opponent = board.OpponentCount;
        int empties = 64 - player - opponent;
        if (player > opponent) return (player + empties - opponent) * FinalValueScale + empties;
        if (player < opponent) return -(opponent + empties - player) * FinalValueScale - empties;
        return 0;
    }

    /// <summary>
    /// 空きマスが empties の局面から読んだ終局の値の上限(下限はこの −1 倍)。
    /// 石数の差は64まで、終局の空きマスは今の空きマスまで(今が終局なら今の空きマスと同じ)
    /// </summary>
    private static int FinalValueBound(int empties) => 64 * FinalValueScale + empties;

    /// <summary>
    /// 小さな探索(SolveSmall)で、確定石の上限を使う空きマスの下限。これより少ない局面では、確定石を数える手間のほうが大きい。
    /// 自己対局の残り20マス40局面で、使わない・2・4を比べ、4がいちばん速かった
    /// </summary>
    private const int SmallStabilityEmpties = 4;

    /// <summary>
    /// 確定石から見た、手番の終局の値の上限。相手の確定石が s 個なら、相手の石は終局で s 個以上残るので、
    /// 石数の差(空きマスは勝った側に数える)は 64 − 2s 以下になる。
    /// 勝ちなら終局の空きマスは今の空きマス以下なので、上限は (64 − 2s) × 64 + 空きマス。
    /// 一方的な局面で、同じ値の手が並んで窓に余裕がなくなっても、「これより上はない」とすぐにわかり、読む局面が大きく減る
    /// </summary>
    private static int StabilityUpperBound(BitBoard board, int empties)
    {
        int stable = BitOperations.PopCount(BitBoard.StableDiscs(board.Opponent, board.Player | board.Opponent));
        int maxDiscDiff = 64 - 2 * stable;
        // 差が0以下なら勝てないので、空きマスの分は足さない(負けの値は 差 × 64 − 空きマス ≤ 差 × 64)
        return maxDiscDiff > 0 ? maxDiscDiff * FinalValueScale + empties : maxDiscDiff * FinalValueScale;
    }

    /// <summary>終局の値を、石数の差(空きマスを勝った側に足したもの)と終局の空きマスに分ける(ログに出すため)</summary>
    internal static (int discDiff, int emptiesLeft) SplitFinalValue(int value)
    {
        // 勝ちは「差 × 64 + 空きマス」、負けは「差 × 64 − 空きマス」(空きマスは64より少ない)なので、0に向かって切り捨てれば差になる
        int discDiff = value / FinalValueScale;
        return (discDiff, Math.Abs(value - discDiff * FinalValueScale));
    }

    /// <summary>中盤の探索で終局に着いたときの点数。勝ち負けがわかった点数(WinScore)に、終局の値を足す</summary>
    private static int FinalScore(BitBoard board)
    {
        int value = FinalValue(board);
        if (value > 0) return WinScore + value;
        if (value < 0) return -WinScore + value;
        return 0;
    }

    /// <summary>
    /// 開発用のコマンド(ベンチ・測定・確認)の引数から AI を作る。中盤の並べ替え用の評価(--ordering / --no-ordering)と
    /// その調整(--ord-mid-depth・--ord-mobility)、MPC の上書き(--mpc / --no-mpc / --mpc-t)、置換表の大きさ(--tt-bits)、
    /// 持ち時間つきの使い方の設定(--solve-empties・--midgame-share・--our-book)を読む
    /// </summary>
    internal static OthelloAI CreateFromArgs(PatternEvaluator evaluator, string[] args, out string orderingDescription)
    {
        int IntArg(string name, int fallback)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? int.Parse(args[index + 1]) : fallback;
        }
        OrderingEvaluator? ordering = OrderingEvaluator.LoadFromArgs(args, out orderingDescription);
        var ai = new OthelloAI(evaluator, IntArg("--tt-bits", TranspositionTable.DefaultSizeBits))
        {
            Ordering = ordering,
            // 持ち時間つきの使い方(ChooseMoveTimed)の設定。--our-book で標準の場所の定石を使う(--book は Egaroucid の定石の指定に使っているため)
            EndgameSolveEmpties = IntArg("--solve-empties", 22),
            MidgameTimeShare = Array.IndexOf(args, "--midgame-share") is int shareIndex and >= 0 && shareIndex + 1 < args.Length
                ? double.Parse(args[shareIndex + 1], System.Globalization.CultureInfo.InvariantCulture) : 0.15,
            Book = args.Contains("--our-book") ? OpeningBook.LoadFromPath(null, out _) : null,
            MidgameOrderingDepth = IntArg("--ord-mid-depth", DefaultMidgameOrderingDepth),
            MidgameOrderingMobilityWeight = IntArg("--ord-mobility", DefaultMidgameOrderingMobilityWeight),
            MpcOverride = AiLevel.MpcOverrideFromArgs(args),
            MpcSelectivityOverride = AiLevel.MpcSelectivityFromArgs(args),
        };
        if (ordering != null) orderingDescription += $"、深さ{ai.MidgameOrderingDepth}以上・相手の手1つ {ai.MidgameOrderingMobilityWeight}";
        return ai;
    }

    /// <summary>
    /// 差分評価の確かめ(--verify-eval)。true なら、探索で評価するたびに盤面から作り直した値と比べ、違えば例外を投げる(とても遅くなる)
    /// </summary>
    public static bool VerifyIncrementalEval { get; set; }

    /// <summary>探索の中の評価: 持ち歩いた番号から引く</summary>
    private int Evaluate(BitBoard board, in EvalState state)
    {
        int value = _evaluator.Evaluate(state, board.EmptyCount);
        if (VerifyIncrementalEval && value != _evaluator.Evaluate(board))
            throw new InvalidOperationException($"差分評価の値 {value} が、盤面から作り直した値 {_evaluator.Evaluate(board)} と違います。");
        return value;
    }

    /// <summary>裏返る石がわかっているときの、手を打ったあとの盤面(手番が入れ替わる。BitBoard.DoMove と同じ)</summary>
    private static BitBoard DoMove(BitBoard board, int pos, ulong flipped)
        => new(board.Opponent ^ flipped, board.Player | flipped | (1UL << pos));

    /// <summary>
    /// 完全読みの子の局面の番号。手の並べ替えに評価関数を使う残りマス(EvalOrderingEmpties 以上)のときだけ作る。
    /// それより少ない局面では番号を使わないので、作らない(中身は不定)
    /// </summary>
    private static void PlayForSolve(in EvalState state, int pos, ulong flipped, int childEmpties, out EvalState child)
    {
        if (childEmpties >= EvalOrderingEmpties) PatternEvaluator.Play(state, pos, flipped, out child);
        else Unsafe.SkipInit(out child);
    }

    /// <summary>評価関数だけを呼ぶ(探索の確認 SearchCheck で、同じ評価関数を使った単純な探索と比べるため)</summary>
    internal int Evaluate(BitBoard board) => _evaluator.Evaluate(board);
}

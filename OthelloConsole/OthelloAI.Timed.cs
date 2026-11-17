using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using static OthelloDebugLog;

/// <summary>
/// 持ち時間つきの探索の結果。
/// Move は打つマスのビット位置(打てる手がなければ -1)。
/// CompletedDepth は中盤の読みで読み終えた最後の深さ。Solved は、中盤の読みで終局まで読み切った(CompletedDepth が残りマス数以上)こと。
/// EndgameOutcome は、終盤の読みで勝ち(1)・引き分け(0)・負け(-1)が分かったときの結果で、分からなかったら null。
/// FromBook は、定石で手を決めた(探索していない)こと。
/// </summary>
public readonly record struct TimedMoveResult(int Move, int CompletedDepth, bool Solved, bool TimedOut, long Nodes, TimeSpan Elapsed, int? EndgameOutcome = null, bool FromBook = false);

/// <summary>
/// 持ち時間で止まる使い方(企画 P-06。CodinGameのオセロの、1手150ms)。
/// レベル(ChooseMove)とは別の入口で、レベルの探索の結果は変えない。
/// - 反復深化で深さ1から読み、読み終えた深さの最善手だけを残す。時間が来たら、途中の深さは捨てる。
/// - 深さ1は必ず読み終える(最低でも1手は決める)。
/// - 前の深さにかかった時間より残りが短ければ、次の深さには入らない(読み終えられないため)。
/// - 残りマスが EndgameSolveEmpties 以下なら、持ち時間の MidgameTimeShare の分だけ中盤を読んだあと、残りの時間で
///   勝ち・引き分け・負けだけを読む(ランキングは勝敗なので、石差より先に勝敗を決める)。読み切れなければ、中盤の最善手を返す。
/// </summary>
public partial class OthelloAI
{
    /// <summary>時間を見る間隔(探索の呼び出しの回数。この値 + 1 回ごとに時計を見る)</summary>
    private const int TimeCheckMask = 255;

    /// <summary>
    /// 終盤の読み(勝敗)を試す残りマスの上限。これより空きマスが多い局面では、中盤の読みだけで打つ。
    /// 持ち時間150msで読み切れる範囲を測って決める(設計書 Phase 2)
    /// </summary>
    public int EndgameSolveEmpties { get; init; } = 22;

    /// <summary>終盤の読みを試す局面で、持ち時間のうち中盤の読みに使う割合(残りは勝敗の読みに使う)</summary>
    public double MidgameTimeShare { get; init; } = 0.15;

    /// <summary>
    /// 手と手の間で中盤の置換表を空にしないか。空にしない場合、前の手の読みの結果が次の手でも使える。
    /// 空にすると、置換表の大きさの分だけ、毎手時間がかかる
    /// </summary>
    public bool KeepTranspositionTable { get; init; }

    /// <summary>
    /// 序盤の定石(0〜7手目の全局面)。あれば、載っている局面は探索せずに定石の手を打つ。
    /// 持ち時間つきの使い方だけで使う(レベル1〜15は、確かめた強さを変えないため使わない)
    /// </summary>
    public OpeningBook? Book { get; init; }

    /// <summary>持ち時間つきの探索の最中か</summary>
    private bool _timedSearch;
    /// <summary>探索の中で時間を見ているか(持ち時間つきの探索の、深さ2以降と、終盤の読み)</summary>
    private bool _timeChecking;
    /// <summary>時間が来たか。立ったら、探索は値を捨てて一気に戻る</summary>
    private bool _timeUp;
    private long _deadlineTimestamp;
    private int _timeCheckCounter;
    private int _completedDepth;
    private long _lastIterationTicks;
    /// <summary>持ち時間つきの探索で使う、完全読み用の置換表(手をまたいで残す)</summary>
    private TranspositionTable? _timedSolveTt;

    /// <summary>
    /// 盤面(手番の側から見たもの)と持ち時間から、打つマスを選ぶ。レベルはなく、深さは持ち時間で決まる。
    /// 持ち時間は、この呼び出しの始まりから数える(置換表を空にする時間も含む)
    /// </summary>
    public TimedMoveResult ChooseMoveTimed(BitBoard board, TimeSpan budget)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        ulong movesBit = board.GetMoves();
        if (movesBit == 0) return new TimedMoveResult(-1, 0, false, false, 0, TimeSpan.Zero);
        if (Book != null && Book.TryGetMove(board, out int bookMove))
            return new TimedMoveResult(bookMove, 0, false, false, 0, Stopwatch.GetElapsedTime(startTimestamp), null, FromBook: true);
        if ((movesBit & (movesBit - 1)) == 0)
        {
            // 打てる手が1つだけなら、読まずにその手を返す
            return new TimedMoveResult(BitOperations.TrailingZeroCount(movesBit), 0, false, false, 0, Stopwatch.GetElapsedTime(startTimestamp));
        }

        _recursionsCount = 0;
        _abCutCount = 0;
        _pvsResearchCount = 0;
        _perfSolveTriggered = false;
        _lastProgressLogNodes = 0;
        _maxDepth = board.EmptyCount; // 終局まで読める深さが上限
        _useMpc = MpcOverride ?? false;
        _mpcSelectivity = MpcSelectivityOverride ?? AiLevel.MpcSelectivity(AiLevel.Max);
        _mpcCutCount = 0;
        if (!KeepTranspositionTable) _tt.Clear();

        _killerMoves = new int[_maxDepth + 2, 2];
        for (int d = 0; d < _maxDepth + 2; d++) { _killerMoves[d, 0] = -1; _killerMoves[d, 1] = -1; }
        Array.Clear(_historyTable, 0, 64);

        long hardDeadline = startTimestamp + (long)(budget.TotalSeconds * Stopwatch.Frequency);
        bool tryEndgame = board.EmptyCount <= EndgameSolveEmpties;
        // 終盤の読みを試すときは、中盤の読みを早めに切り上げる
        long midgameDeadline = tryEndgame
            ? startTimestamp + (long)(budget.TotalSeconds * MidgameTimeShare * Stopwatch.Frequency)
            : hardDeadline;

        _timedSearch = true;
        _timeChecking = false;
        _timeUp = false;
        _timeCheckCounter = 0;
        _completedDepth = 0;
        _lastIterationTicks = 0;
        _deadlineTimestamp = midgameDeadline;
        _timedSolveTt ??= new TranspositionTable(_transpositionTableSizeBits);
        _solveTt = _timedSolveTt;

        int bestMove;
        int? endgameOutcome = null;
        bool timedOut;
        try
        {
            InfoLog($"AI思考開始 (持ち時間{budget.TotalMilliseconds:F0}ms  残りマス={board.EmptyCount}  終盤の読み={(tryEndgame ? "あり" : "なし")})");
            bestMove = IterativeDeepening(board, _maxDepth);
            bool solvedByMidgame = _completedDepth >= board.EmptyCount;
            timedOut = _timeUp && !tryEndgame;

            if (tryEndgame && !solvedByMidgame)
            {
                // 中盤の読みの時間が終わった。残りの時間で、勝敗だけを読む
                _timeUp = false;
                _timeCheckCounter = 0;
                _deadlineTimestamp = hardDeadline;
                _timeChecking = true;
                int winLossMove = SolveWinLossRoot(board, out int outcome);
                if (winLossMove >= 0)
                {
                    endgameOutcome = outcome;
                    // 勝ち・引き分けなら、その手で打つ。負けと分かったときは、中盤の最善手で粘る
                    if (outcome >= 0) bestMove = winLossMove;
                }
                else
                {
                    timedOut = true; // 勝敗を読み切れなかった(中盤の最善手を返す)
                }
            }
        }
        finally
        {
            _timedSearch = false;
            _timeChecking = false;
            _timeUp = false; // 次にレベルで使うときに、時間切れの印を残さない
            _solveTt = _tt;
        }

        if (bestMove < 0) bestMove = BitOperations.TrailingZeroCount(movesBit); // 深さ1も読めなかったとき(起きない想定)
        bool solved = _completedDepth >= board.EmptyCount;
        return new TimedMoveResult(bestMove, _completedDepth, solved, timedOut, _recursionsCount, Stopwatch.GetElapsedTime(startTimestamp), endgameOutcome);
    }

    /// <summary>
    /// 勝ち・引き分け・負けだけを終局まで読み、手を選ぶ(窓を狭くして速く読む)。
    /// 勝つ手が見つかったらその手、なければ引き分けの手、すべて負けならどれかの手と outcome = -1 を返す。
    /// 時間切れで読み切れなかったら -1 を返す(outcome は意味を持たない)
    /// </summary>
    private int SolveWinLossRoot(BitBoard board, out int outcome)
    {
        outcome = -1;
        _perfSolveTriggered = true;
        Span<int> moves = stackalloc int[64];
        EvalState rootState = PatternEvaluator.CreateState(board);
        int moveCount = OrderSolveMoves(board, rootState, board.GetMoves(), -1, moves);

        int bestMove = moves[0];
        int bestOutcome = -2;
        for (int i = 0; i < moveCount; i++)
        {
            ulong flipped = board.GetFlipped(moves[i]);
            BitBoard next = DoMove(board, moves[i], flipped);
            PlayForSolve(rootState, moves[i], flipped, next.EmptyCount, out EvalState nextState);
            _recursionsCount++;
            // 相手の値が(-1, 1)の窓の外なら勝ち・負け、窓の中なら引き分け
            int score = -Solve(next, nextState, -1, 1);
            if (_timeUp) return -1;
            int moveOutcome = score > 0 ? 1 : score < 0 ? -1 : 0;
            if (moveOutcome > bestOutcome)
            {
                bestOutcome = moveOutcome;
                bestMove = moves[i];
                if (moveOutcome == 1) break; // 勝つ手が見つかったので、ほかの手は読まない
            }
        }
        outcome = bestOutcome;
        return bestMove;
    }

    /// <summary>
    /// 終局まで読んだ、手番から見た終局の値(確かめ用。石数の差 × 64 ± 空きマス。勝ちなら正、負けなら負、引き分けなら 0)。
    /// 完全読みの置換表を空にしてから読む
    /// </summary>
    internal int ExactValue(BitBoard board)
    {
        _tt.Clear();
        _solveTt = _tt;
        int bound = FinalValueBound(board.EmptyCount) + 1;
        return Solve(board, PatternEvaluator.CreateState(board), -bound, bound);
    }

    /// <summary>時間切れか。持ち時間のない使い方(レベル)では、時計を見ず常に false</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsTimeUp() => _timeChecking && CheckTimeUp();

    private bool CheckTimeUp()
    {
        if (_timeUp) return true;
        if ((++_timeCheckCounter & TimeCheckMask) != 0) return false;
        if (Stopwatch.GetTimestamp() >= _deadlineTimestamp) _timeUp = true;
        return _timeUp;
    }

    /// <summary>反復深化で、この深さに入ってよいか。持ち時間つきのときだけ、時間を見る(レベルは常に true)</summary>
    private bool CanStartIteration(int depth)
    {
        if (!_timedSearch) return true;
        _timeChecking = depth >= 2; // 深さ1は必ず読み終える
        if (depth < 3) return true;
        long remaining = _deadlineTimestamp - Stopwatch.GetTimestamp();
        return remaining > _lastIterationTicks;
    }

    /// <summary>反復深化で、深さを読み終えたときの記録(持ち時間つきのときだけ)</summary>
    private void FinishIteration(int depth, long ticks)
    {
        if (!_timedSearch) return;
        _completedDepth = depth;
        _lastIterationTicks = ticks;
    }
}

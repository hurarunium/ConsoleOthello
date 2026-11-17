using System.Diagnostics;
using System.Runtime.CompilerServices;
using static OthelloDebugLog;

/// <summary>
/// 持ち時間つきの探索の結果。
/// Move は打つマスのビット位置(打てる手がなければ -1)。
/// CompletedDepth は読み終えた最後の深さ。Solved は、終局まで読み切った(CompletedDepth が残りマス数以上)こと。
/// </summary>
public readonly record struct TimedMoveResult(int Move, int CompletedDepth, bool Solved, bool TimedOut, long Nodes, TimeSpan Elapsed);

/// <summary>
/// 持ち時間で止まる使い方(企画 P-06。CodinGameのオセロの、1手150ms)。
/// レベル(ChooseMove)とは別の入口で、レベルの探索の結果は変えない。
/// - 反復深化で深さ1から読み、読み終えた深さの最善手だけを残す。時間が来たら、途中の深さは捨てる。
/// - 深さ1は必ず読み終える(最低でも1手は決める)。
/// - 前の深さにかかった時間より残りが短ければ、次の深さには入らない(読み終えられないため)。
/// </summary>
public partial class OthelloAI
{
    /// <summary>時間を見る間隔(探索の呼び出しの回数。この値 + 1 回ごとに時計を見る)</summary>
    private const int TimeCheckMask = 255;

    /// <summary>持ち時間つきの探索の最中か</summary>
    private bool _timedSearch;
    /// <summary>探索の中で時間を見ているか(持ち時間つきの探索の、深さ2以降)</summary>
    private bool _timeChecking;
    /// <summary>時間が来たか。立ったら、探索は値を捨てて一気に戻る</summary>
    private bool _timeUp;
    private long _deadlineTimestamp;
    private int _timeCheckCounter;
    private int _completedDepth;
    private long _lastIterationTicks;

    /// <summary>
    /// 盤面(手番の側から見たもの)と持ち時間から、打つマスを選ぶ。レベルはなく、深さは持ち時間で決まる。
    /// 持ち時間は、この呼び出しの始まりから数える(置換表を空にする時間も含む)
    /// </summary>
    public TimedMoveResult ChooseMoveTimed(BitBoard board, TimeSpan budget)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        ulong movesBit = board.GetMoves();
        if (movesBit == 0) return new TimedMoveResult(-1, 0, false, false, 0, TimeSpan.Zero);
        if ((movesBit & (movesBit - 1)) == 0)
        {
            // 打てる手が1つだけなら、読まずにその手を返す
            return new TimedMoveResult(System.Numerics.BitOperations.TrailingZeroCount(movesBit), 0, false, false, 0, Stopwatch.GetElapsedTime(startTimestamp));
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
        _tt.Clear();

        _killerMoves = new int[_maxDepth + 2, 2];
        for (int d = 0; d < _maxDepth + 2; d++) { _killerMoves[d, 0] = -1; _killerMoves[d, 1] = -1; }
        Array.Clear(_historyTable, 0, 64);

        _timedSearch = true;
        _timeChecking = false;
        _timeUp = false;
        _timeCheckCounter = 0;
        _completedDepth = 0;
        _lastIterationTicks = 0;
        _deadlineTimestamp = startTimestamp + (long)(budget.TotalSeconds * Stopwatch.Frequency);

        int bestMove;
        try
        {
            InfoLog($"AI思考開始 (持ち時間{budget.TotalMilliseconds:F0}ms  残りマス={board.EmptyCount})");
            bestMove = IterativeDeepening(board, _maxDepth);
        }
        finally
        {
            _timedSearch = false;
            _timeChecking = false;
        }

        if (bestMove < 0) bestMove = System.Numerics.BitOperations.TrailingZeroCount(movesBit); // 深さ1も読めなかったとき(起きない想定)
        bool solved = _completedDepth >= board.EmptyCount;
        return new TimedMoveResult(bestMove, _completedDepth, solved, _timeUp, _recursionsCount, Stopwatch.GetElapsedTime(startTimestamp));
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

using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;

/// <summary>
/// ブラウザ版の入口。JS(Web Worker)から呼ぶ関数だけを置く。
/// 盤面のルール(合法手・裏返し・パス・終局)はJS側が持ち、ここではAIの手を選ぶだけにする。
/// </summary>
public static partial class OthelloWebApi
{
    private static OthelloAI? s_ai;
    private static long s_lastThinkMs;
    private static int s_lastNodes;

    // Main は .NET の起動に必要なだけで、何もしない(Worker から OthelloWebApi の関数を直接呼ぶ)
    public static void Main() { }

    /// <summary>
    /// 評価ファイル2つ(gzipを戻したあとの中身)を読み、AIを作る。ttSizeBits は置換表の件数(2 の何乗か)。
    /// 読み込みにかかった時間(ミリ秒)を返す
    /// </summary>
    [JSExport]
    public static int Initialize(byte[] patternEval, byte[] moveOrdering, int ttSizeBits)
    {
        var stopwatch = Stopwatch.StartNew();
        OthelloDebugLog.SuppressOutput = true; // 1手ごとの [INFO] をブラウザのコンソールに出さない
        PatternEvaluator evaluator = PatternEvaluator.Load(new MemoryStream(patternEval), EvaluatorFile.FileName);
        OrderingEvaluator ordering = OrderingEvaluator.Load(new MemoryStream(moveOrdering), OrderingEvaluator.FileName);
        s_ai = new OthelloAI(evaluator, ttSizeBits) { Ordering = ordering };
        return (int)stopwatch.ElapsedMilliseconds;
    }

    /// <summary>
    /// 盤面と手番から、AIが打つマスを選ぶ。
    /// board は64文字(a1, b1, …, h1, a2, … の順。'.' 空き、'0' 黒、'1' 白。CodinGameやEgaroucidと同じ並び)、
    /// sideToMove は手番の色('0' 黒 / '1' 白)。打つマスの番号(行 × 8 + 列)を返す。打てる手がなければ -1
    /// </summary>
    [JSExport]
    public static int ChooseMove(string board, string sideToMove, int level)
    {
        if (s_ai == null) throw new InvalidOperationException("Initialize を先に呼んでください。");
        if (board.Length != 64) throw new ArgumentException("盤面は64文字にしてください。", nameof(board));

        char me = sideToMove == "1" ? '1' : '0';
        char opponent = me == '0' ? '1' : '0';
        ulong player = 0, opponentStones = 0;
        for (int i = 0; i < 64; i++)
        {
            if (board[i] == me) player |= 1UL << i;
            else if (board[i] == opponent) opponentStones |= 1UL << i;
        }
        var bitBoard = new BitBoard(player, opponentStones);
        if (bitBoard.GetMoves() == 0) return -1;

        var stopwatch = Stopwatch.StartNew();
        int move = s_ai.ChooseMove(bitBoard, level);
        s_lastThinkMs = stopwatch.ElapsedMilliseconds;
        s_lastNodes = s_ai.LastNodeCount;
        return move;
    }

    /// <summary>直前の ChooseMove で考えた時間(ミリ秒)</summary>
    [JSExport]
    public static int LastThinkMs() => (int)s_lastThinkMs;

    /// <summary>直前の ChooseMove で読んだ局面の数</summary>
    [JSExport]
    public static int LastNodeCount() => s_lastNodes;
}

using System.Numerics;

/// <summary>
/// 置換表エントリの種類（αβウィンドウとの関係）
/// </summary>
public enum TTEntryType : byte
{
    Exact,       // 正確な評価値（ウィンドウ内）
    LowerBound,  // βカット発生（真の値 >= score）
    UpperBound,  // αカット発生（真の値 <= score）
}

/// <summary>
/// 置換表の1エントリ
/// </summary>
public struct TTEntry
{
    public ulong Hash;
    public int Score;
    public int BestMove;   // 最善手のビット位置（-1=なし）
    public byte Depth;
    public TTEntryType Type;
}

/// <summary>
/// 盤面のハッシュを用いた置換表。
/// 同一局面の再評価を防ぎ探索を大幅に高速化する。
/// </summary>
public class TranspositionTable
{
    // 約800万エントリ(1つ24バイトで約192MB)。約400万では、レベル15の中盤で1手に9,600万局面を読む局面があり、
    // 置換表が埋まって1手が10秒近くかかった(約800万で4.4秒。約1,600万にしても縮まなかった)
    // ブラウザ版はメモリが少ないので、小さい表を選べるようにしている(sizeBits)
    public const int DefaultSizeBits = 23;

    private readonly int _tableMask;
    private readonly TTEntry[] _table;

    /// <param name="sizeBits">表の件数を 2 の何乗にするか(標準 23 = 約800万件)</param>
    public TranspositionTable(int sizeBits = DefaultSizeBits)
    {
        if (sizeBits < 10 || sizeBits > 26) throw new ArgumentOutOfRangeException(nameof(sizeBits), sizeBits, "置換表の大きさは 2^10〜2^26 件にしてください。");
        _table = new TTEntry[1 << sizeBits];
        _tableMask = (1 << sizeBits) - 1;
    }

    // 統計カウンター
    public int HitCount { get; private set; }
    public int MissCount { get; private set; }
    public int Total => HitCount + MissCount;
    public float HitRate => Total == 0 ? 0f : (float)HitCount / Total * 100f;

    public void ResetStats()
    {
        HitCount = 0;
        MissCount = 0;
    }

    /// <summary>
    /// BitBoardからハッシュを計算する。石を1つずつ見るZobristハッシュより速いよう、
    /// 2つの64ビットの値を掛け算とずらしでよく混ぜる(SplitMix64 の仕上げと同じ混ぜ方)
    /// </summary>
    public ulong ComputeHash(BitBoard board)
    {
        ulong hash = Mix(board.Player ^ 0x9E3779B97F4A7C15UL) ^ BitOperations.RotateLeft(Mix(board.Opponent + 0xC2B2AE3D27D4EB4FUL), 32);
        return Mix(hash);
    }

    private static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    /// <summary>
    /// 置換表を引く。ヒットした場合は true を返し entry に結果を設定する
    /// </summary>
    public bool TryGet(ulong hash, int depth, int alpha, int beta, out int score, out int bestMove)
    {
        ref TTEntry entry = ref _table[hash & (ulong)_tableMask];
        score = 0;
        bestMove = entry.BestMove;

        if (entry.Hash != hash || entry.Depth < depth)
        {
            MissCount++;
            return false;
        }

        if (entry.Type == TTEntryType.Exact)
        {
            score = entry.Score;
            HitCount++;
            return true;
        }
        if (entry.Type == TTEntryType.LowerBound && entry.Score >= beta)
        {
            score = entry.Score;
            HitCount++;
            return true;
        }
        if (entry.Type == TTEntryType.UpperBound && entry.Score <= alpha)
        {
            score = entry.Score;
            HitCount++;
            return true;
        }
        MissCount++;
        return false;
    }

    /// <summary>
    /// 置換表にエントリを書き込む
    /// </summary>
    public void Store(ulong hash, int depth, int score, int alpha, int beta, int bestMove)
    {
        TTEntryType type;
        if (score <= alpha)
            type = TTEntryType.UpperBound;
        else if (score >= beta)
            type = TTEntryType.LowerBound;
        else
            type = TTEntryType.Exact;

        ref TTEntry entry = ref _table[hash & (ulong)_tableMask];
        // 深い探索結果を優先して上書き
        if (entry.Hash == 0 || entry.Depth <= depth)
        {
            entry.Hash = hash;
            entry.Score = score;
            entry.Depth = (byte)depth;
            entry.Type = type;
            entry.BestMove = bestMove;
        }
    }

    /// <summary>
    /// ETC（Enhanced Transposition Cutoff）用：αβの窓を見ずに置換表エントリを取得する。
    /// 子局面のカットオフ可能性を事前に検出するために使用する。
    /// minDepth より浅い探索の値は返さない(完全読みの中で、浅い中盤の探索の評価値を、正しい値と取り違えないため)
    /// </summary>
    public bool TryGetRaw(ulong hash, int minDepth, out TTEntryType type, out int score)
    {
        ref TTEntry entry = ref _table[hash & (ulong)_tableMask];
        if (entry.Hash == hash && entry.Depth >= minDepth)
        {
            type  = entry.Type;
            score = entry.Score;
            return true;
        }
        type  = default;
        score = 0;
        return false;
    }

    /// <summary>
    /// テーブルをクリアする（統計もリセット）
    /// </summary>
    public void Clear()
    {
        Array.Clear(_table, 0, _table.Length);
        ResetStats();
    }
}

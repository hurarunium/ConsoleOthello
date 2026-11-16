using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using System.Text.Json;

/// <summary>
/// パターン評価(企画 P-02 の準備、設計書 Phase 5)。
/// 盤面の部分(パターン)ごとに石の並びの点数を表で持ち、その合計を評価値にする。
///
/// - パターンは16種類。盤の8通りの回転・反転で写した置き場所のうち、マスの組が違うものを全部使う(各4か所、1局面64か所)
/// - 各マスは 空=0・手番の側=1・相手=2 の3通りで、並びを3進数の番号にして表を引く
/// - 同じマスの組に写る回転・反転(例: 辺の左右反転)は、表の番号を「代表の番号」にまとめて引く。
///   こうすると、盤を回転・反転しても評価値が必ず同じになる
/// - ゲームの進み具合(打った手の数)で15段階に分け、段階ごとに別の表と、段階ごとの定数(Bias)を持つ
/// - 評価値は「手番の側から見た最終の石数の差の見込み × Scale」(Scale=32 なら1石が32)
/// - 表は、代表でない番号にも代表の値を入れて持つ。評価のときは番号で直接引ける(代表の番号に直す手間がいらない)
/// - 探索では、64か所の番号を局面と一緒に持ち歩き(EvalState)、打った石と裏返した石の分だけ足し引きする(差分評価)
/// </summary>
public sealed class PatternEvaluator
{
    /// <summary>評価ファイルの識別子と形式の版</summary>
    private static readonly byte[] s_magic = "OTPE"u8.ToArray();
    private const int FormatVersion = 1;

    public const int StageCount = 15;
    /// <summary>評価値の倍率。表の値(16ビット整数)は「石数 × Scale」</summary>
    public const int Scale = 32;

    /// <summary>
    /// パターン16種類の元の形(マスの番号 = 行 × 8 + 列。a1=0、h1=7、a8=56)。
    /// ふらるが図で決めた形(library の resource ConsoleOthello-eval-patterns-16)。どれも盤の上に4か所ずつ置く
    /// </summary>
    private static readonly int[][] s_basePatterns =
    {
        Cells("a2 b2 c2 d2 e2 f2 g2 h2"),             //  1: 2列目
        Cells("b1 c1 d2 g2 e3 f4 g5 h6 h7"),          //  2: 斜め6 + Cマス2つ + Xマス
        Cells("a3 b3 c3 d3 e3 f3 g3 h3"),             //  3: 3列目
        Cells("a1 b1 c2 d3 e4 f5 g6 h7 h8"),          //  4: 斜め7 + 角2つ
        Cells("a4 b4 c4 d4 e4 f4 g4 h4"),             //  5: 4列目
        Cells("a1 b1 c1 a2 b2 c2 a3 b3 c3"),          //  6: 角の3×3
        Cells("d1 b2 e2 f3 g4 h5 g7"),                //  7: 斜め5 + Xマス2つ
        Cells("a1 b1 a2 b2 c3 d4 e5 f6 g7 h8"),       //  8: 斜め8 + 角の2×2
        Cells("a1 b1 c1 d1 e1 f1 g1 h1 b2 g2"),       //  9: 辺 + Xマス2つ
        Cells("a1 b1 c1 d1 a2 b2 c2 a3 b3 a4"),       // 10: 角の三角
        Cells("a1 c1 d1 e1 f1 h1 c2 d2 e2 f2"),       // 11: 辺の角と中央 + 2列目の中央
        Cells("a1 b1 a2 b2 c2 b3 c3 d3 c4 d4"),       // 12: 角から斜めの帯
        Cells("a1 b1 c1 d1 e1 f1 g1 h1 c2 f2"),       // 13: 辺 + 2列目の2マス
        Cells("a1 b1 c1 d1 e1 a2 b2 a3 a4 a5"),       // 14: 角から2辺へ伸びる形
        Cells("a1 b1 a2 b2 c2 d2 b3 c3 b4 d4"),       // 15: 角の不規則な形
        Cells("c1 d1 e1 f1 d2 e2 c3 d3 e3 f3"),       // 16: 辺の中央の形
    };

    /// <summary>パターンの種類ごとの、盤の上の置き場所(マスの番号の並び)。64か所</summary>
    private static readonly (int pattern, int[] cells)[] s_instances;
    /// <summary>置き場所ごとの、1段階ぶんの表(パターンの種類の順につなげたもの)の上の先頭位置</summary>
    private static readonly int[] s_instanceOffsets;
    /// <summary>
    /// 差分評価の重み。[マス × 64 + 置き場所] = そのマスが置き場所の何桁目か(3のべき乗)。マスを含まない置き場所は0。
    /// マス1つ分の64個は、128ビットのベクトル8本(16ビット × 8個ずつ)で読む
    /// </summary>
    private static readonly ushort[] s_cellWeights;
    /// <summary>パターンの種類ごとの、表の番号 → 代表の番号</summary>
    private static readonly int[][] s_canonical;
    private static readonly int[] s_tableSizes;

    /// <summary>
    /// [段階][パターンの種類の先頭位置 + 番号] の点数(石数 × Scale)。1段階ぶんは、パターンの種類の順に表をつなげたもの。
    /// 代表でない番号にも、代表の値が入っている
    /// </summary>
    private readonly short[][] _tables;
    /// <summary>[段階] の定数(石数 × Scale)</summary>
    private readonly short[] _bias;

    public static int PatternCount => s_basePatterns.Length;
    public static int InstanceCount => s_instances.Length;
    public static int TableSize(int pattern) => s_tableSizes[pattern];
    public static int CellCount(int pattern) => s_basePatterns[pattern].Length;

    static PatternEvaluator()
    {
        var instances = new List<(int, int[])>();
        s_canonical = new int[s_basePatterns.Length][];
        s_tableSizes = new int[s_basePatterns.Length];

        for (int p = 0; p < s_basePatterns.Length; p++)
        {
            int[] baseCells = s_basePatterns[p];
            s_tableSizes[p] = Pow3(baseCells.Length);

            // 8通りの回転・反転で写し、マスの組が初めて出たものだけを置き場所にする
            var seenSets = new HashSet<string>();
            var selfSymmetries = new List<int[]>(); // 元の形を、同じマスの組に写す回転・反転(並びの入れ替え方)
            for (int t = 0; t < 8; t++)
            {
                int[] mapped = baseCells.Select(cell => Transform(cell, t)).ToArray();
                string setKey = string.Join(",", mapped.OrderBy(c => c));
                if (seenSets.Add(setKey)) instances.Add((p, mapped));

                if (setKey == string.Join(",", baseCells.OrderBy(c => c)))
                    selfSymmetries.Add(mapped.Select(cell => Array.IndexOf(baseCells, cell)).ToArray());
            }
            s_canonical[p] = BuildCanonicalMap(baseCells.Length, selfSymmetries);
        }
        s_instances = instances.ToArray();
        if (s_instances.Length != InstanceSlots)
            throw new InvalidOperationException($"置き場所が{s_instances.Length}か所です。差分評価は{InstanceSlots}か所の前提で作っています。");

        s_instanceOffsets = new int[s_instances.Length];
        s_cellWeights = new ushort[64 * InstanceSlots];
        int offset = 0;
        var patternOffsets = new int[s_basePatterns.Length];
        for (int p = 0; p < s_basePatterns.Length; p++) { patternOffsets[p] = offset; offset += s_tableSizes[p]; }
        for (int i = 0; i < s_instances.Length; i++)
        {
            (int pattern, int[] cells) = s_instances[i];
            s_instanceOffsets[i] = patternOffsets[pattern];
            // 先頭のマスがいちばん上の桁(IndexOf と同じ並び)
            int weight = 1;
            for (int k = cells.Length - 1; k >= 0; k--)
            {
                s_cellWeights[cells[k] * InstanceSlots + i] = (ushort)weight;
                weight *= 3;
            }
        }
    }

    /// <summary>差分評価で持ち歩く番号の数(置き場所の数。16ビット × 64 = 128ビットのベクトル8本)</summary>
    public const int InstanceSlots = 64;

    private PatternEvaluator(short[][] tables, short[] bias)
    {
        _tables = tables;
        _bias = bias;
    }

    /// <summary>すべての点数が0の評価関数(学習の出発点、動作の確認用)</summary>
    public static PatternEvaluator CreateZero()
    {
        var tables = new short[StageCount][];
        for (int s = 0; s < StageCount; s++) tables[s] = new short[FeatureCountPerStage];
        return new PatternEvaluator(tables, new short[StageCount]);
    }

    /// <summary>
    /// 点数をランダムに入れた評価関数(対称性や保存・読み込みの確認用)。
    /// 評価では番号で直接引くので、代表でない番号には代表の値を入れる(入れないと対称性が壊れる)
    /// </summary>
    public static PatternEvaluator CreateRandom(int seed)
    {
        var random = new Random(seed);
        PatternEvaluator evaluator = CreateZero();
        for (int s = 0; s < StageCount; s++)
        {
            evaluator._bias[s] = (short)random.Next(-64, 65);
            for (int p = 0; p < s_basePatterns.Length; p++)
            {
                Span<short> table = evaluator._tables[s].AsSpan(s_featureOffsets[p], s_tableSizes[p]);
                // 代表の番号は、その番号以下の最小の番号なので、小さい順に埋めれば代表の値が先に決まっている
                for (int i = 0; i < table.Length; i++)
                {
                    int representative = s_canonical[p][i];
                    table[i] = representative == i ? (short)random.Next(-200, 201) : table[representative];
                }
            }
        }
        return evaluator;
    }

    /// <summary>打った手の数(60 − 空きマス)から段階を求める。4手ごとに1段階</summary>
    public static int StageOf(BitBoard board) => StageOfEmpties(board.EmptyCount);

    /// <summary>空きマスの数から段階を求める</summary>
    public static int StageOfEmpties(int empties) => Math.Min((60 - empties) / 4, StageCount - 1);

    /// <summary>手番の側(board.Player)から見た評価値(石数 × Scale)。64か所の番号を盤面から作り直す</summary>
    public int Evaluate(BitBoard board)
    {
        int stage = StageOf(board);
        short[] table = _tables[stage];
        int score = _bias[stage];
        for (int i = 0; i < s_instances.Length; i++)
            score += table[s_instanceOffsets[i] + IndexOf(board, s_instances[i].cells)];
        return score;
    }

    // ─── 差分評価(探索から使う入り口) ──────────────────────────

    /// <summary>盤面から、64か所の番号を手番の側・相手の側の両方から見て作る(探索の始まりで1回だけ使う)</summary>
    public static EvalState CreateState(BitBoard board)
    {
        var swapped = new BitBoard(board.Opponent, board.Player);
        EvalState state = default;
        for (int i = 0; i < s_instances.Length; i++)
        {
            state.Player[i] = (ushort)IndexOf(board, s_instances[i].cells);
            state.Opponent[i] = (ushort)IndexOf(swapped, s_instances[i].cells);
        }
        return state;
    }

    /// <summary>
    /// 手番の側が pos に打ち、flipped の石を裏返したあとの番号を作る(手番が入れ替わる)。
    /// 子の手番(= 親の相手)から見ると、打った石は相手の石(0 → 2)、裏返った石は自分 → 相手(1 → 2)。
    /// 子の相手(= 親の手番)から見ると、打った石は自分の石(0 → 1)、裏返った石は相手 → 自分(2 → 1)。
    /// なので、F = 裏返ったマスの重みの合計 として
    ///   子.Player = 親.Opponent + 2 × 重み[pos] + F、 子.Opponent = 親.Player + 重み[pos] − F
    /// 16ビットの足し引きは途中であふれても、答え(0〜59048)は正しくなる
    /// </summary>
    public static void Play(in EvalState parent, int pos, ulong flipped, out EvalState child)
    {
        Unsafe.SkipInit(out child);
        ref ushort weights = ref MemoryMarshal.GetArrayDataReference(s_cellWeights);
        ref ushort placed = ref Unsafe.Add(ref weights, pos * InstanceSlots);
        ref ushort parentPlayer = ref Unsafe.AsRef(in parent.Player[0]);
        ref ushort parentOpponent = ref Unsafe.AsRef(in parent.Opponent[0]);
        ref ushort childPlayer = ref child.Player[0];
        ref ushort childOpponent = ref child.Opponent[0];

        for (nuint k = 0; k < InstanceSlots; k += (nuint)Vector128<ushort>.Count)
        {
            Vector128<ushort> sum = Vector128<ushort>.Zero;
            for (ulong rest = flipped; rest != 0; rest &= rest - 1)
            {
                int cell = BitOperations.TrailingZeroCount(rest);
                sum += Vector128.LoadUnsafe(ref Unsafe.Add(ref weights, cell * InstanceSlots), k);
            }
            Vector128<ushort> place = Vector128.LoadUnsafe(ref placed, k);
            (Vector128.LoadUnsafe(ref parentOpponent, k) + place + place + sum).StoreUnsafe(ref childPlayer, k);
            (Vector128.LoadUnsafe(ref parentPlayer, k) + place - sum).StoreUnsafe(ref childOpponent, k);
        }
    }

    /// <summary>パスしたあとの番号(手番が入れ替わるだけ)</summary>
    public static void Pass(in EvalState parent, out EvalState child)
    {
        child.Player = parent.Opponent;
        child.Opponent = parent.Player;
    }

    /// <summary>持ち歩いた番号から、手番の側から見た評価値を求める(Evaluate(BitBoard) と同じ値)</summary>
    public int Evaluate(in EvalState state, int empties)
    {
        int stage = StageOfEmpties(empties);
        ref short table = ref MemoryMarshal.GetArrayDataReference(_tables[stage]);
        ref int offsets = ref MemoryMarshal.GetArrayDataReference(s_instanceOffsets);
        ref ushort indices = ref Unsafe.AsRef(in state.Player[0]);
        int score = _bias[stage];
        for (int i = 0; i < InstanceSlots; i++)
            score += Unsafe.Add(ref table, Unsafe.Add(ref offsets, i) + Unsafe.Add(ref indices, i));
        return score;
    }

    /// <summary>2つの番号の組が同じか(差分評価の確認用)</summary>
    public static bool SameIndices(in EvalState a, in EvalState b)
        => ((ReadOnlySpan<ushort>)a.Player).SequenceEqual(b.Player) && ((ReadOnlySpan<ushort>)a.Opponent).SequenceEqual(b.Opponent);

    /// <summary>置き場所の石の並びを3進数の番号にする(先頭のマスがいちばん上の桁)</summary>
    private static int IndexOf(BitBoard board, int[] cells)
    {
        ulong player = board.Player;
        ulong opponent = board.Opponent;
        int index = 0;
        foreach (int cell in cells)
        {
            int value = (int)((player >> cell) & 1) | (int)(((opponent >> cell) & 1) << 1);
            index = index * 3 + value;
        }
        return index;
    }

    // ─── 学習から使う入り口 ─────────────────────────────────────

    /// <summary>
    /// 1段階ぶんの表を、パターンの種類の順につなげた長さ(学習で使う重みの数)。
    /// 番号 = パターンの種類の先頭位置(FeatureOffset) + 代表の番号
    /// </summary>
    public static int FeatureCountPerStage { get; } = Enumerable.Range(0, s_basePatterns.Length).Sum(p => Pow3(s_basePatterns[p].Length));

    private static readonly int[] s_featureOffsets = Enumerable.Range(0, s_basePatterns.Length)
        .Select(p => Enumerable.Range(0, p).Sum(q => Pow3(s_basePatterns[q].Length))).ToArray();

    /// <summary>局面の64か所それぞれの、つなげた表の上の番号(代表の番号)を書き込む。destination の長さは InstanceCount 以上</summary>
    public static void WriteFeatureIndices(BitBoard board, Span<int> destination)
    {
        for (int i = 0; i < s_instances.Length; i++)
        {
            (int pattern, int[] cells) = s_instances[i];
            destination[i] = s_featureOffsets[pattern] + s_canonical[pattern][IndexOf(board, cells)];
        }
    }

    /// <summary>
    /// 学習した小数の重み(石数の単位)から作る。weights[段階] は長さ FeatureCountPerStage、bias[段階] は段階ごとの定数。
    /// 保存のために Scale を掛けて16ビット整数に丸める(はみ出す値は端にそろえる)
    /// </summary>
    public static PatternEvaluator FromWeights(float[][] weights, float[] bias)
    {
        PatternEvaluator evaluator = CreateZero();
        for (int s = 0; s < StageCount; s++)
        {
            evaluator._bias[s] = ToShort(bias[s]);
            for (int p = 0; p < s_basePatterns.Length; p++)
            {
                int offset = s_featureOffsets[p];
                Span<short> table = evaluator._tables[s].AsSpan(offset, s_tableSizes[p]);
                // 代表の番号でない場所も、代表の値で埋めておく(評価のときは番号で直接引くため)
                for (int i = 0; i < table.Length; i++) table[i] = ToShort(weights[s][offset + s_canonical[p][i]]);
            }
        }
        return evaluator;
    }

    private static short ToShort(float discs)
        => (short)Math.Clamp((int)MathF.Round(discs * Scale), short.MinValue, short.MaxValue);

    // ─── ファイルの保存・読み込み ────────────────────────────────

    /// <summary>
    /// 評価ファイル(pattern_eval.bin)を書く。並び: ヘッダー(識別子・版・段階の数・パターンの数・倍率)
    /// → パターンの定義(マスの数・マスの番号) → 段階ごとの定数 → 点数の表(段階1のパターン1から順に)
    /// </summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new BinaryWriter(File.Create(path)); // BinaryWriter はリトルエンディアンで書く
        writer.Write(s_magic);
        writer.Write(FormatVersion);
        writer.Write(StageCount);
        writer.Write(s_basePatterns.Length);
        writer.Write(Scale);
        foreach (int[] cells in s_basePatterns)
        {
            writer.Write(cells.Length);
            foreach (int cell in cells) writer.Write((byte)cell);
        }
        foreach (short bias in _bias) writer.Write(bias);
        // 1段階ぶんの表は、パターンの種類の順につなげて持っているので、そのまま書けばファイルの並びになる
        for (int s = 0; s < StageCount; s++)
            foreach (short value in _tables[s]) writer.Write(value);
    }

    /// <summary>
    /// 評価ファイルを読む。識別子・版・段階の数・パターンの定義・倍率が、このプログラムと一致しなければ例外を投げる
    /// (形を変えたプログラムで古いファイルを読み、気づかずに間違った評価をするのを防ぐ)
    /// </summary>
    public static PatternEvaluator Load(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (!reader.ReadBytes(s_magic.Length).SequenceEqual(s_magic))
            throw new InvalidDataException($"{path} は評価ファイル(OTPE)ではありません。");
        int version = reader.ReadInt32();
        if (version != FormatVersion)
            throw new InvalidDataException($"{path} の形式の版({version})が、このプログラム({FormatVersion})と違います。");
        int stageCount = reader.ReadInt32();
        int patternCount = reader.ReadInt32();
        int scale = reader.ReadInt32();
        if (stageCount != StageCount || patternCount != s_basePatterns.Length || scale != Scale)
            throw new InvalidDataException($"{path} の段階の数・パターンの数・倍率が、このプログラムと違います。");
        for (int p = 0; p < patternCount; p++)
        {
            int cellCount = reader.ReadInt32();
            byte[] cells = reader.ReadBytes(cellCount);
            if (!cells.Select(c => (int)c).SequenceEqual(s_basePatterns[p]))
                throw new InvalidDataException($"{path} の{p + 1}番目のパターンの形が、このプログラムと違います。");
        }

        PatternEvaluator evaluator = CreateZero();
        for (int s = 0; s < StageCount; s++) evaluator._bias[s] = reader.ReadInt16();
        for (int s = 0; s < StageCount; s++)
        {
            short[] table = evaluator._tables[s];
            for (int i = 0; i < table.Length; i++) table[i] = reader.ReadInt16();
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException($"{path} の末尾に、読まれないデータが残っています。");
        // 評価では番号で直接引くので、代表でない番号に代表と違う値が入っていると、盤を回転・反転したときに評価値が変わってしまう
        for (int s = 0; s < StageCount; s++)
            for (int p = 0; p < patternCount; p++)
            {
                ReadOnlySpan<short> table = evaluator._tables[s].AsSpan(s_featureOffsets[p], s_tableSizes[p]);
                for (int i = 0; i < table.Length; i++)
                    if (table[i] != table[s_canonical[p][i]])
                        throw new InvalidDataException($"{path} の段階{s + 1}・パターン{p + 1}の番号{i}の値が、代表の番号{s_canonical[p][i]}の値と違います。");
            }
        return evaluator;
    }

    /// <summary>2つの評価関数の点数がすべて同じか(保存と読み込みの確認用)</summary>
    public bool HasSameValues(PatternEvaluator other)
        => _bias.SequenceEqual(other._bias)
        && Enumerable.Range(0, StageCount).All(s => _tables[s].AsSpan().SequenceEqual(other._tables[s]));

    // ─── 準備の計算 ─────────────────────────────────────────────

    /// <summary>
    /// 表の番号 → 代表の番号 を作る。元の形を同じマスの組に写す回転・反転で並びを入れ替えた番号のうち、
    /// いちばん小さいものを代表にする
    /// </summary>
    private static int[] BuildCanonicalMap(int cellCount, List<int[]> selfSymmetries)
    {
        int size = Pow3(cellCount);
        var canonical = new int[size];
        var digits = new int[cellCount];
        for (int index = 0; index < size; index++)
        {
            int rest = index;
            for (int i = cellCount - 1; i >= 0; i--) { digits[i] = rest % 3; rest /= 3; }

            int best = int.MaxValue;
            foreach (int[] permutation in selfSymmetries)
            {
                // 写した並びの i 番目のマスは、元の並びの permutation[i] 番目のマス
                int mappedIndex = 0;
                for (int i = 0; i < cellCount; i++) mappedIndex = mappedIndex * 3 + digits[permutation[i]];
                best = Math.Min(best, mappedIndex);
            }
            canonical[index] = best;
        }
        return canonical;
    }

    /// <summary>
    /// マスの番号を、8通りの回転・反転(t = 0〜7)で写す。
    /// t の 4 の位: 行と列を入れ替える、1 の位: 上下を反転する、2 の位: 左右を反転する
    /// </summary>
    public static int Transform(int cell, int t)
    {
        int row = cell / 8, col = cell % 8;
        if ((t & 4) != 0) (row, col) = (col, row);
        if ((t & 1) != 0) row = 7 - row;
        if ((t & 2) != 0) col = 7 - col;
        return row * 8 + col;
    }

    /// <summary>ビットボードの盤面全体を、8通りの回転・反転(t = 0〜7)で写す</summary>
    public static BitBoard Transform(BitBoard board, int t)
    {
        ulong player = 0, opponent = 0;
        for (int cell = 0; cell < 64; cell++)
        {
            ulong to = 1UL << Transform(cell, t);
            if (((board.Player >> cell) & 1) != 0) player |= to;
            if (((board.Opponent >> cell) & 1) != 0) opponent |= to;
        }
        return new BitBoard(player, opponent);
    }

    private static int Pow3(int n)
    {
        int result = 1;
        for (int i = 0; i < n; i++) result *= 3;
        return result;
    }

    /// <summary>「a1 b1 ...」をマスの番号の並びにする</summary>
    private static int[] Cells(string text)
        => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
               .Select(n => (n[1] - '1') * 8 + (n[0] - 'a'))
               .ToArray();
}

/// <summary>
/// 評価ファイルの説明(pattern_eval.meta.json)。評価ファイルはバイナリで中身が読めないので、
/// 何から作ったファイルかを人が読める形で隣に置く
/// </summary>
public sealed class PatternEvaluatorMeta
{
    public string CreatedAt { get; set; } = "";
    public string Format { get; set; } = "OTPE v1";
    public int Stages { get; set; } = PatternEvaluator.StageCount;
    public int Scale { get; set; } = PatternEvaluator.Scale;
    public List<string> Data { get; set; } = new();
    public int Epochs { get; set; }
    /// <summary>検証用データでの誤差(石数の二乗平均平方根)。学習していないときは null</summary>
    public double? ValidationError { get; set; }

    // 人が読むファイルなので、日本語を \uXXXX に置き換えずにそのまま書く
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>評価ファイルの隣に置く説明のファイルのパス(pattern_eval.bin → pattern_eval.meta.json)</summary>
    public static string PathFor(string evaluatorPath) => Path.ChangeExtension(evaluatorPath, ".meta.json");

    public void Save(string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this, s_jsonOptions), new UTF8Encoding(false));

    public static PatternEvaluatorMeta? TryLoad(string path)
        => File.Exists(path) ? JsonSerializer.Deserialize<PatternEvaluatorMeta>(File.ReadAllText(path)) : null;
}

/// <summary>差分評価で持ち歩く、64か所の番号(16ビット × 64)</summary>
[InlineArray(PatternEvaluator.InstanceSlots)]
public struct PatternIndices
{
    private ushort _element0;
}

/// <summary>
/// 差分評価で局面と一緒に持ち歩く番号(256バイト)。
/// Player は手番の側から見た番号(手番 = 1、相手 = 2)、Opponent は相手の側から見た番号(相手 = 1、手番 = 2)
/// </summary>
public struct EvalState
{
    public PatternIndices Player;
    public PatternIndices Opponent;
}

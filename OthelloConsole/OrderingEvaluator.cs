using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// 中盤の手の並べ替え用の、軽いパターン評価(設計書 Phase 7)。
/// 評価関数(PatternEvaluator)の16種類のパターンのうち、選んだ数種類だけを使い、専用の表を引く。
/// 番号は探索で持ち歩いている EvalState をそのまま使うので、番号の更新の手間は増えず、表を引く回数だけが減る(64回 → 選んだ種類 × 4回)。
/// 値は評価関数と同じ単位(手番から見た最終の石数の差の見込み × Scale)。手の順番を決めるだけに使う。
/// 手元の Egaroucid は、完全読みの並べ替えに、評価関数のパターンのうち4種類だけを使う専用の表を持っている。
/// このAIでは完全読みでは逆に遅くなった(64か所の評価で並べるより局面が増えた)ので、中盤の深い節点の並べ替えにだけ使う。
/// パターンは 9・10・12・15(辺 + Xマス2つ・角の三角・角から斜めの帯・角の不規則な形)。4通りの組み合わせで、これがいちばん読む局面が少なかった
/// </summary>
public sealed class OrderingEvaluator
{
    private static readonly byte[] s_magic = "OTMO"u8.ToArray();
    private const int FormatVersion = 1;

    public const string FileName = "move_ordering.bin";
    public static string DefaultOutputPath => Path.Combine("weights", FileName);

    /// <summary>使うパターンの種類(0始まり。PatternEvaluator の並び)</summary>
    private readonly int[] _patterns;
    /// <summary>引く置き場所(EvalState の番号の位置)</summary>
    private readonly int[] _lanes;
    /// <summary>置き場所ごとの、1段階ぶんの表の上の先頭位置</summary>
    private readonly int[] _laneOffsets;
    /// <summary>[段階][使うパターンの種類の先頭位置 + 番号] の点数。代表でない番号にも代表の値が入っている</summary>
    private readonly short[][] _tables;
    private readonly short[] _bias;

    public IReadOnlyList<int> Patterns => _patterns;

    private OrderingEvaluator(int[] patterns, short[][] tables, short[] bias)
    {
        _patterns = patterns;
        _tables = tables;
        _bias = bias;

        var localOffsets = new Dictionary<int, int>();
        int offset = 0;
        foreach (int p in patterns) { localOffsets[p] = offset; offset += PatternEvaluator.TableSize(p); }

        var lanes = new List<int>();
        var laneOffsets = new List<int>();
        for (int i = 0; i < PatternEvaluator.InstanceCount; i++)
        {
            if (!localOffsets.TryGetValue(PatternEvaluator.InstancePattern(i), out int laneOffset)) continue;
            lanes.Add(i);
            laneOffsets.Add(laneOffset);
        }
        _lanes = lanes.ToArray();
        _laneOffsets = laneOffsets.ToArray();
    }

    /// <summary>1段階ぶんの表の長さ</summary>
    private static int TableLength(int[] patterns) => patterns.Sum(PatternEvaluator.TableSize);

    /// <summary>使うパターンの種類を確かめる(範囲内・重なりなし)</summary>
    public static int[] ValidatePatterns(IEnumerable<int> patterns)
    {
        int[] result = patterns.ToArray();
        if (result.Length == 0 || result.Distinct().Count() != result.Length || result.Any(p => p < 0 || p >= PatternEvaluator.PatternCount))
            throw new ArgumentException($"並べ替え用のパターンの指定が正しくありません(1〜{PatternEvaluator.PatternCount}の重ならない番号): {string.Join(",", result.Select(p => p + 1))}");
        return result;
    }

    /// <summary>
    /// 学習した小数の重み(石数の単位)から作る。weights[段階] は評価関数と同じ並び(長さ PatternEvaluator.FeatureCountPerStage)で、
    /// 使うパターンの部分だけを取り出す。代表でない番号にも代表の値を入れる
    /// </summary>
    public static OrderingEvaluator FromWeights(int[] patterns, float[][] weights, float[] bias)
    {
        patterns = ValidatePatterns(patterns);
        var tables = new short[PatternEvaluator.StageCount][];
        var shortBias = new short[PatternEvaluator.StageCount];
        for (int s = 0; s < PatternEvaluator.StageCount; s++)
        {
            shortBias[s] = ToShort(bias[s]);
            tables[s] = new short[TableLength(patterns)];
            int local = 0;
            foreach (int p in patterns)
            {
                int featureOffset = PatternEvaluator.FeatureOffset(p);
                for (int i = 0; i < PatternEvaluator.TableSize(p); i++)
                    tables[s][local + i] = ToShort(weights[s][featureOffset + PatternEvaluator.CanonicalIndex(p, i)]);
                local += PatternEvaluator.TableSize(p);
            }
        }
        return new OrderingEvaluator(patterns, tables, shortBias);
    }

    private static short ToShort(float discs)
        => (short)Math.Clamp((int)MathF.Round(discs * PatternEvaluator.Scale), short.MinValue, short.MaxValue);

    /// <summary>持ち歩いた番号から、手番の側から見た並べ替え用の値を求める</summary>
    public int Evaluate(in EvalState state, int empties)
    {
        int stage = PatternEvaluator.StageOfEmpties(empties);
        ref short table = ref MemoryMarshal.GetArrayDataReference(_tables[stage]);
        ref ushort indices = ref Unsafe.AsRef(in state.Player[0]);
        int score = _bias[stage];
        for (int k = 0; k < _lanes.Length; k++)
            score += Unsafe.Add(ref table, _laneOffsets[k] + Unsafe.Add(ref indices, _lanes[k]));
        return score;
    }

    /// <summary>盤面から求める(確認用。Evaluate(EvalState) と同じ値)</summary>
    public int Evaluate(BitBoard board) => Evaluate(PatternEvaluator.CreateState(board), board.EmptyCount);

    // ─── ファイルの保存・読み込み ────────────────────────────────

    /// <summary>
    /// 並べ替え用の評価ファイル(move_ordering.bin)を書く。並び: ヘッダー(識別子・版・段階の数・倍率)
    /// → 評価関数の16種類のパターンの定義(形が今と同じかを読み込みで確かめるため) → 使うパターンの番号
    /// → 段階ごとの定数 → 点数の表(段階1から順に、使うパターンの順につなげたもの)
    /// </summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(s_magic);
        writer.Write(FormatVersion);
        writer.Write(PatternEvaluator.StageCount);
        writer.Write(PatternEvaluator.Scale);
        writer.Write(PatternEvaluator.PatternCount);
        for (int p = 0; p < PatternEvaluator.PatternCount; p++)
        {
            ReadOnlySpan<int> cells = PatternEvaluator.PatternCells(p);
            writer.Write(cells.Length);
            foreach (int cell in cells) writer.Write((byte)cell);
        }
        writer.Write(_patterns.Length);
        foreach (int p in _patterns) writer.Write((byte)p);
        foreach (short bias in _bias) writer.Write(bias);
        foreach (short[] table in _tables)
            foreach (short value in table) writer.Write(value);
    }

    /// <summary>並べ替え用の評価ファイルを読む。形式や、評価関数のパターンの形が今と違えば例外を投げる</summary>
    public static OrderingEvaluator Load(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (!reader.ReadBytes(s_magic.Length).SequenceEqual(s_magic))
            throw new InvalidDataException($"{path} は並べ替え用の評価ファイル(OTMO)ではありません。");
        int version = reader.ReadInt32();
        if (version != FormatVersion)
            throw new InvalidDataException($"{path} の形式の版({version})が、このプログラム({FormatVersion})と違います。");
        if (reader.ReadInt32() != PatternEvaluator.StageCount || reader.ReadInt32() != PatternEvaluator.Scale)
            throw new InvalidDataException($"{path} の段階の数・倍率が、このプログラムと違います。");
        int patternCount = reader.ReadInt32();
        if (patternCount != PatternEvaluator.PatternCount)
            throw new InvalidDataException($"{path} の評価関数のパターンの数({patternCount})が、このプログラムと違います。");
        for (int p = 0; p < patternCount; p++)
        {
            byte[] cells = reader.ReadBytes(reader.ReadInt32());
            if (!cells.Select(c => (int)c).SequenceEqual(PatternEvaluator.PatternCells(p).ToArray()))
                throw new InvalidDataException($"{path} の{p + 1}番目のパターンの形が、このプログラムと違います。");
        }
        int[] patterns = ValidatePatterns(reader.ReadBytes(reader.ReadInt32()).Select(b => (int)b));

        var bias = new short[PatternEvaluator.StageCount];
        for (int s = 0; s < bias.Length; s++) bias[s] = reader.ReadInt16();
        var tables = new short[PatternEvaluator.StageCount][];
        for (int s = 0; s < tables.Length; s++)
        {
            tables[s] = new short[TableLength(patterns)];
            for (int i = 0; i < tables[s].Length; i++) tables[s][i] = reader.ReadInt16();
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException($"{path} の末尾に、読まれないデータが残っています。");
        return new OrderingEvaluator(patterns, tables, bias);
    }

    /// <summary>
    /// 開発用のコマンドの引数から読む。--no-ordering なら null(64か所の評価関数で並べる)。
    /// --ordering <パス> があればそのファイル、なければ標準の場所(exeの隣 → 今のフォルダの weights/)にあれば読む
    /// </summary>
    public static OrderingEvaluator? LoadFromArgs(string[] args, out string description)
    {
        description = "なし(評価関数で並べる)";
        if (args.Contains("--no-ordering")) return null;
        int index = Array.IndexOf(args, "--ordering");
        string? path = index >= 0 && index + 1 < args.Length ? args[index + 1] : FindDefaultPath();
        if (path == null) return null;
        if (!File.Exists(path)) throw new FileNotFoundException($"並べ替え用の評価ファイルが見つかりません: {Path.GetFullPath(path)}", path);
        OrderingEvaluator ordering = Load(path);
        description = $"{Path.GetFileName(path)}(パターン {string.Join(",", ordering.Patterns.Select(p => p + 1))})";
        return ordering;
    }

    /// <summary>標準の場所にあるファイル(なければ null)</summary>
    public static string? FindDefaultPath()
    {
        string besideExe = Path.Combine(AppContext.BaseDirectory, "weights", FileName);
        if (File.Exists(besideExe)) return besideExe;
        return File.Exists(DefaultOutputPath) ? DefaultOutputPath : null;
    }
}

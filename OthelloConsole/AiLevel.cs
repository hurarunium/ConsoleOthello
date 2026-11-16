/// <summary>
/// AIのレベル(企画 P-06)。レベルは1〜15の整数で、Egaroucidのlv.1〜10と同じ決まりで読む。
/// - 中盤: N手先まで読む
/// - 終盤: 残りマスが 2N 以下になったら完全読み(ただし上限 PerfectSolveEmptiesCap マス)
/// どのレベルでも、決めた深さを最後まで読む(途中で打ち切らない)。
/// 1手の思考時間は TargetThinkTime 以内に終わるよう、探索を速くして達成する(Phase 7までの目標)。
/// </summary>
public static class AiLevel
{
    public const int Min = 1;
    public const int Max = 15;
    /// <summary>GUI版・コンソール版で最初に選ばれているレベル(以前の「普通」と同じく中盤6手読み)</summary>
    public const int Default = 6;

    /// <summary>
    /// 完全読みに入る残りマスの数の上限。レベル11〜15はここで頭打ちになる。
    /// 10秒以内に終わるかを見て、Phase 7で調整する
    /// </summary>
    public const int PerfectSolveEmptiesCap = 20;

    /// <summary>
    /// 1手の思考時間の目標(企画の制約: 全レベルで10秒以内)。探索を打ち切る時間ではなく、
    /// この時間以内に読み終えられるよう速くするための目安。ベンチで、超えた手の数を数える
    /// </summary>
    public static readonly TimeSpan TargetThinkTime = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 中盤の探索で LMR(後ろの手を浅く読む近似)を使い始めるレベル。
    /// LMR を使うと見落としが出て弱くなる(レベル5対lv.5で、LMR ありは33.0%、なしは45.1%、各400局)。
    /// 一方、LMR がないと深いレベルで1手10秒に収まらない(レベル14は最大31秒)。
    /// LMR なしでも1手が十分短いレベル11まで(1局で最大0.8秒)は使わず、それより上だけ使う
    /// </summary>
    public const int LmrMinLevel = 12;

    /// <summary>選べるレベルの一覧(1〜15)</summary>
    public static IReadOnlyList<int> All { get; } = Enumerable.Range(Min, Max - Min + 1).ToList();

    /// <summary>中盤で読む手数</summary>
    public static int MidgameDepth(int level) => Clamp(level);

    /// <summary>そのレベルで、中盤の探索に LMR を使うか</summary>
    public static bool UsesLmr(int level) => Clamp(level) >= LmrMinLevel;

    /// <summary>
    /// 開発用のコマンドの引数から、LMR の決まりの上書きを読む(--lmr で必ず使う、--no-lmr で使わない、どちらもなければ null = レベルどおり)
    /// </summary>
    public static bool? LmrOverrideFromArgs(string[] args)
        => args.Contains("--no-lmr") ? false : args.Contains("--lmr") ? true : null;

    /// <summary>完全読みに入る残りマスの数</summary>
    public static int PerfectSolveEmpties(int level) => Math.Min(2 * Clamp(level), PerfectSolveEmptiesCap);

    public static int Clamp(int level) => Math.Clamp(level, Min, Max);

    /// <summary>入力された文字列をレベルとして読む。範囲外や数字でないときは false</summary>
    public static bool TryParse(string? text, out int level)
        => int.TryParse(text?.Trim(), out level) && level >= Min && level <= Max;

    /// <summary>
    /// 4段階だったころの強さの名前を、中盤で読む手数が同じレベルに読み替える(古い棋譜を読むため)。
    /// 当時はどの強さも残り18マスから完全読みしていたので、終盤の読み方は当時と違う
    /// </summary>
    public static int FromLegacyStrengthName(string name) => name.ToLowerInvariant() switch
    {
        "beginner" => 3,
        "normal" => 6,
        "expert" => 10,
        "professional" => 14,
        _ => throw new ArgumentException($"強さの名前「{name}」は読み替えられません。"),
    };
}

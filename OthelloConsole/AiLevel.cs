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
    /// 中盤の探索で MPC(Multi-ProbCut。浅い読みの値から、深い読みの結果を見込んで枝を切る近似)を使い始めるレベル。
    /// 以前は LMR(後ろの手を浅く読む近似)を使っていたが、見落としで弱くなっていた
    /// (レベル5対lv.5で、LMR ありは33.0%、なしは45.1%、各400局)。強いオセロAI(Logistello・Edax・Egaroucid)にならい、LMR はやめた。
    /// 近似なしでも1手が十分短いレベル11まで(1局で最大0.8秒)は使わず、深く読むレベル12以上だけ MPC で速くする
    /// </summary>
    public const int MpcMinLevel = 12;

    /// <summary>選べるレベルの一覧(1〜15)</summary>
    public static IReadOnlyList<int> All { get; } = Enumerable.Range(Min, Max - Min + 1).ToList();

    /// <summary>中盤で読む手数</summary>
    public static int MidgameDepth(int level) => Clamp(level);

    /// <summary>そのレベルで、中盤の探索に MPC を使うか</summary>
    public static bool UsesMpc(int level) => Clamp(level) >= MpcMinLevel;

    /// <summary>
    /// MPC で枝を切るときの余裕(誤差の標準偏差の何倍か)。大きいほど切る枝が減り、正確だが遅い。
    /// 正規分布なら 1.5 倍で約87%、2.5 倍で約99% の確からしさで切る。
    /// レベル12対lv.12の各100局で、1.0 は41.0%、1.5 は44.0%、2.0 は44.5%、2.5 は47.0%(MPCなしは22局で45.5%、LMR は35.0%)。
    /// 2.5 でも1局分の思考時間はMPCなしの約20分の1なので、いちばん慎重な 2.5 にした
    /// </summary>
    public static double MpcSelectivity(int level) => 2.5;

    /// <summary>
    /// 開発用のコマンドの引数から、MPC の決まりの上書きを読む(--mpc で必ず使う、--no-mpc で使わない、どちらもなければ null = レベルどおり)
    /// </summary>
    public static bool? MpcOverrideFromArgs(string[] args)
        => args.Contains("--no-mpc") ? false : args.Contains("--mpc") ? true : null;

    /// <summary>開発用のコマンドの引数から、MPC の余裕の上書きを読む(--mpc-t 1.8 など。なければ null = レベルどおり)</summary>
    public static double? MpcSelectivityFromArgs(string[] args)
    {
        int index = Array.IndexOf(args, "--mpc-t");
        return index >= 0 && index + 1 < args.Length ? double.Parse(args[index + 1], System.Globalization.CultureInfo.InvariantCulture) : null;
    }

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

/// <summary>
/// 起動の引数 --eval legacy|pattern と --weights で、どの評価関数のAIを作るかを決める(設計書 Phase 5〜6 の間だけ使う)。
/// Phase 6で今の評価関数(legacy)を消すときに、この切り替えも取り除く。
///
/// 例: --eval legacy                    今の評価関数(weights/best_weights.json)
///     --eval pattern                   パターン評価(weights/pattern_eval.bin。なければすべて0の重み)
///     --eval pattern --weights x.bin   パターン評価(指定したファイル)
/// </summary>
public static class EvaluatorOption
{
    public const string LegacyWeightsPath = "weights/best_weights.json";
    public const string DefaultPatternPath = "weights/pattern_eval.bin";

    /// <summary>どの評価関数で動かすかと、記録に残す説明</summary>
    public sealed record Choice(string Description, Func<OthelloAI> CreateAi);

    public static Choice Parse(string[] args)
    {
        string eval = ValueOf(args, "--eval") ?? "legacy";
        switch (eval)
        {
            case "legacy":
            {
                bool exists = File.Exists(LegacyWeightsPath);
                WeightSet weights = exists ? WeightSet.Load(LegacyWeightsPath) : WeightSet.Default();
                return new Choice(exists ? "legacy:best_weights.json" : "legacy:default", () => new OthelloAI(weights));
            }
            case "pattern":
            {
                string path = ValueOf(args, "--weights") ?? DefaultPatternPath;
                if (!File.Exists(path))
                {
                    if (ValueOf(args, "--weights") != null)
                        throw new FileNotFoundException($"評価ファイルが見つかりません: {path}", path);
                    Console.WriteLine($"[注意] {path} がないので、すべて0の重みのパターン評価で動かします。");
                    PatternEvaluator zero = PatternEvaluator.CreateZero();
                    return new Choice("pattern:zero", () => new OthelloAI(zero));
                }
                PatternEvaluator evaluator = PatternEvaluator.Load(path);
                string createdAt = PatternEvaluatorMeta.TryLoad(PatternEvaluatorMeta.PathFor(path))?.CreatedAt ?? "作成日時不明";
                return new Choice($"pattern:{Path.GetFileName(path)}({createdAt})", () => new OthelloAI(evaluator));
            }
            default:
                throw new ArgumentException($"--eval は legacy か pattern にしてください(指定: {eval})。");
        }
    }

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

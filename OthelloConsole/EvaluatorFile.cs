/// <summary>
/// 学習したパターン評価のファイル(weights/pattern_eval.bin)を読む。
/// GUI版・コンソール版・ベンチ・分析・確認は、どれもここから評価関数を受け取る。
/// </summary>
public static class EvaluatorFile
{
    public const string FileName = "pattern_eval.bin";

    /// <summary>学習で書き出すときの標準の場所(今いるフォルダの weights/)</summary>
    public static string DefaultOutputPath => Path.Combine("weights", FileName);

    /// <summary>
    /// 評価ファイルの場所。exeの隣の weights/ を先に探し(exeをダブルクリックして起動したとき)、
    /// なければ今いるフォルダの weights/ を探す(dotnet run で起動したとき)
    /// </summary>
    public static string FindDefaultPath()
    {
        string besideExe = Path.Combine(AppContext.BaseDirectory, "weights", FileName);
        return File.Exists(besideExe) ? besideExe : DefaultOutputPath;
    }

    /// <summary>
    /// 評価ファイルを読む。path が null なら標準の場所から読む。
    /// description には、棋譜や記録に残す説明(ファイル名と、説明のファイルに書いた作成日時)を返す
    /// </summary>
    public static PatternEvaluator Load(string? path, out string description)
    {
        string resolved = path ?? FindDefaultPath();
        if (!File.Exists(resolved))
            throw new FileNotFoundException($"評価ファイルが見つかりません: {Path.GetFullPath(resolved)}(--train-pattern で作ってください)", resolved);

        PatternEvaluator evaluator = PatternEvaluator.Load(resolved);
        string createdAt = PatternEvaluatorMeta.TryLoad(PatternEvaluatorMeta.PathFor(resolved))?.CreatedAt ?? "作成日時不明";
        description = $"{Path.GetFileName(resolved)}({createdAt})";
        return evaluator;
    }

    /// <summary>起動の引数の --weights <パス>(なければ標準の場所)から読む</summary>
    public static PatternEvaluator LoadFromArgs(string[] args, out string description)
    {
        int index = Array.IndexOf(args, "--weights");
        string? path = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        return Load(path, out description);
    }
}

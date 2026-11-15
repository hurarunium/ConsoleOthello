using System.Diagnostics;

/// <summary>
/// パターン評価の学習(--train-pattern、企画 P-03、設計書 Phase 6)。
///
/// 局面ごとに「46か所の表の値 + 段階の定数」の合計が、目標の値(手番から見た最終の石数の差)に近づくように、
/// 表の値を少しずつ動かす(確率的勾配降下法)。1局面ごとに
///   誤差 = 目標の値 − 予測、 使われた46か所の値と段階の定数 += 学習率 × 誤差
/// だけ動かす。局面の順番は周回ごとに混ぜ、学習率は周回とともに小さくする。
/// 段階(打った手の数を4手ごとに分けたもの)ごとに表が別なので、局面は自分の段階の表だけを動かし、段階ごとに別のスレッドで学習する。
/// (全部の局面の誤差をまとめて一度に動かすやり方は、46か所が同じ向きに一斉に動いて発散したので使わない)
///
/// 例: --train-pattern --egaroucid ../external/egaroucid-data/Egaroucid_Train_Data.zip
///                     --wthor ../external/wthor --records Data/collected/game_records.jsonl
///                     --epochs 30 --out weights/pattern_eval.bin
///     --limit 2000000 で、Egaroucidのデータを先頭から指定の数だけ使う(試しに学習するとき)
/// </summary>
public static class PatternTrainer
{
    /// <summary>検証用に取り分ける割合(Egaroucidのデータだけから取り分ける)</summary>
    private const double ValidationRate = 0.02;
    /// <summary>周回ごとに学習率を小さくする割合(n周目の学習率 = 最初の学習率 ÷ (1 + 割合 × (n − 1)))</summary>
    private const float LearningRateDecay = 0.1f;

    public static void Run(string[] args)
    {
        string? egaroucidPath = ValueOf(args, "--egaroucid");
        string? wthorPath = ValueOf(args, "--wthor");
        string? recordsPath = ValueOf(args, "--records");
        string outPath = ValueOf(args, "--out") ?? EvaluatorFile.DefaultOutputPath;
        int epochs = int.Parse(ValueOf(args, "--epochs") ?? "30");
        long limit = long.Parse(ValueOf(args, "--limit") ?? "0");
        // 1局面の誤差を46か所で分け合うので、1か所あたりの歩幅は小さくする(46 × 学習率 が1より十分小さい値)
        float learningRate = float.Parse(ValueOf(args, "--learning-rate") ?? "0.002");
        if (egaroucidPath == null && wthorPath == null && recordsPath == null)
            throw new ArgumentException("--egaroucid・--wthor・--records の少なくとも1つで、教師データを指定してください。");

        var stopwatch = Stopwatch.StartNew();
        var dataNames = new List<string>();
        var training = new List<TrainingSample>();
        var validation = new List<TrainingSample>();

        if (egaroucidPath != null)
        {
            long index = 0, before = training.Count;
            foreach (TrainingSample sample in TrainingData.LoadEgaroucid(egaroucidPath, limit))
            {
                // 50局面に1つを検証用にする(読む順で決めるので、毎回同じ局面が検証用になる)
                if (index++ % (long)(1 / ValidationRate) == 0) validation.Add(sample);
                else training.Add(sample);
            }
            dataNames.Add($"Egaroucid教師データ {Path.GetFileName(egaroucidPath)}: 学習用 {training.Count - before:N0}局面・検証用 {validation.Count:N0}局面");
            Console.WriteLine($"読み込み: {dataNames[^1]}({stopwatch.Elapsed.TotalSeconds:F0}秒)");
        }
        if (wthorPath != null)
        {
            int before = training.Count;
            training.AddRange(TrainingData.LoadWthor(wthorPath));
            dataNames.Add($"WTHOR {Path.GetFileName(wthorPath.TrimEnd('/'))}: {training.Count - before:N0}局面(残り{TrainingData.WthorMinEmpties}マス以上、24マス空きからの理論値)");
            Console.WriteLine($"読み込み: {dataNames[^1]}({stopwatch.Elapsed.TotalSeconds:F0}秒)");
        }
        if (recordsPath != null)
        {
            int before = training.Count;
            training.AddRange(TrainingData.LoadGameRecords(recordsPath));
            dataNames.Add($"展示の棋譜 {Path.GetFileName(recordsPath)}: {training.Count - before:N0}局面(最終の石数の差)");
            Console.WriteLine($"読み込み: {dataNames[^1]}({stopwatch.Elapsed.TotalSeconds:F0}秒)");
        }

        // 段階ごとに分ける(局面は自分の段階の表だけを動かすので、段階ごとに独立に学習できる)
        List<TrainingSample>[] trainingByStage = SplitByStage(training);
        List<TrainingSample>[] validationByStage = SplitByStage(validation);
        training.Clear();
        training.TrimExcess();

        int features = PatternEvaluator.FeatureCountPerStage;
        var weights = new float[PatternEvaluator.StageCount][];
        var bias = new float[PatternEvaluator.StageCount];
        for (int s = 0; s < PatternEvaluator.StageCount; s++) weights[s] = new float[features];

        Console.WriteLine($"学習: {epochs}周、最初の学習率 {learningRate}(周回ごとに小さくする)");
        double lastValidationError = double.NaN;
        for (int epoch = 1; epoch <= epochs; epoch++)
        {
            // 段階ごとに、別々のスレッドで1周する
            float epochRate = learningRate / (1 + LearningRateDecay * (epoch - 1));
            double[] trainSquaredError = new double[PatternEvaluator.StageCount];
            int currentEpoch = epoch;
            Parallel.For(0, PatternEvaluator.StageCount, s =>
                trainSquaredError[s] = RunEpoch(trainingByStage[s], weights[s], ref bias[s], epochRate, seed: currentEpoch * 100 + s));

            long trainCount = trainingByStage.Sum(l => (long)l.Count);
            double trainError = Math.Sqrt(trainSquaredError.Sum() / Math.Max(1, trainCount));
            lastValidationError = ValidationError(validationByStage, weights, bias);
            Console.WriteLine($"[{epoch,3}/{epochs}] 学習用の誤差 {trainError:F3}石  検証用の誤差 {lastValidationError:F3}石  ({stopwatch.Elapsed.TotalSeconds:F0}秒)");
        }

        PatternEvaluator evaluator = PatternEvaluator.FromWeights(weights, bias);
        evaluator.Save(outPath);
        new PatternEvaluatorMeta
        {
            CreatedAt = DateTimeOffset.Now.ToString("o"),
            Data = dataNames,
            Epochs = epochs,
            ValidationError = double.IsNaN(lastValidationError) ? null : Math.Round(lastValidationError, 3),
        }.Save(PatternEvaluatorMeta.PathFor(outPath));
        Console.WriteLine($"保存: {Path.GetFullPath(outPath)}(説明: {PatternEvaluatorMeta.PathFor(outPath)})");
    }

    /// <summary>1段階ぶんのデータを、混ぜた順番で1周し、1局面ごとに重みを動かす。動かす前の誤差の二乗の合計を返す</summary>
    private static double RunEpoch(List<TrainingSample> samples, float[] weights, ref float bias, float learningRate, int seed)
    {
        // 局面の順番を混ぜる(同じ種なら同じ順番になるので、同じ条件の学習は同じ結果になる)
        int[] order = Enumerable.Range(0, samples.Count).ToArray();
        new Random(seed).Shuffle(order);

        double squaredError = 0;
        Span<int> indices = stackalloc int[PatternEvaluator.InstanceCount];
        foreach (int i in order)
        {
            TrainingSample sample = samples[i];
            PatternEvaluator.WriteFeatureIndices(sample.Board, indices);
            float predicted = bias;
            foreach (int index in indices) predicted += weights[index];
            float residual = sample.Target - predicted;
            squaredError += (double)residual * residual;

            float step = learningRate * residual;
            foreach (int index in indices) weights[index] += step;
            bias += step;
        }
        return squaredError;
    }

    /// <summary>検証用データでの誤差(石数の二乗平均平方根)。検証用データがなければ NaN</summary>
    private static double ValidationError(List<TrainingSample>[] byStage, float[][] weights, float[] bias)
    {
        double squaredError = 0;
        long count = 0;
        Span<int> indices = stackalloc int[PatternEvaluator.InstanceCount];
        for (int s = 0; s < byStage.Length; s++)
        {
            foreach (TrainingSample sample in byStage[s])
            {
                PatternEvaluator.WriteFeatureIndices(sample.Board, indices);
                float predicted = bias[s];
                foreach (int index in indices) predicted += weights[s][index];
                double residual = sample.Target - predicted;
                squaredError += residual * residual;
                count++;
            }
        }
        return count == 0 ? double.NaN : Math.Sqrt(squaredError / count);
    }

    private static List<TrainingSample>[] SplitByStage(List<TrainingSample> samples)
    {
        var byStage = Enumerable.Range(0, PatternEvaluator.StageCount).Select(_ => new List<TrainingSample>()).ToArray();
        foreach (TrainingSample sample in samples) byStage[PatternEvaluator.StageOf(sample.Board)].Add(sample);
        return byStage;
    }

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

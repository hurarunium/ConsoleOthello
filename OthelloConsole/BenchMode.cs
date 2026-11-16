using System.Diagnostics;
using System.Text;
using System.Text.Json;
using static OthelloSystem;

/// <summary>ベンチの条件。起動の引数から作る。</summary>
public sealed class BenchOptions
{
    public int EgaroucidLevel { get; init; } = 1;
    /// <summary>自分のAIのレベル(1〜15)</summary>
    public int OurLevel { get; init; } = AiLevel.Default;

    /// <summary>対局数。同じ序盤を先手・後手で1局ずつ打つため、偶数にそろえる</summary>
    public int Games { get; init; } = 20;
    /// <summary>序盤にランダムに打つ手数(両者合わせて)。同じ対局ばかりになるのを防ぐ</summary>
    public int OpeningMoves { get; init; } = 8;
    /// <summary>序盤の局面集を作る乱数の種。同じ値なら、毎回同じ序盤の局面集になる</summary>
    public int Seed { get; init; } = 1;
    public int EgaroucidThreads { get; init; } = 1;
    public bool EgaroucidUsesBook { get; init; }
    public string EgaroucidPath { get; init; } = "../external/egaroucid/Egaroucid-console_v7.8.2/bin/Egaroucid_for_Console.out";
    public string OutputDirectory { get; init; } = "bench-results";

    /// <summary>
    /// 起動の引数を読み取る。
    /// 例: --bench --our-level 5 --level 5 --games 40 --opening-moves 8 --seed 1
    /// (--level は Egaroucid のレベル、--our-level は自分のAIのレベル。評価ファイルは --weights で変えられる。標準は weights/pattern_eval.bin。
    ///  LMR はレベルどおり(AiLevel.UsesLmr)。--lmr で必ず使い、--no-lmr で使わずに打つ)
    /// </summary>
    public static BenchOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            int index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length) throw new ArgumentException($"{name} の値がありません。");
            return args[index + 1];
        }

        int games = int.Parse(Value("--games") ?? "20");
        if (games <= 0) throw new ArgumentException("--games は1以上にしてください。");

        var defaults = new BenchOptions();
        return new BenchOptions
        {
            EgaroucidLevel = int.Parse(Value("--level") ?? "1"),
            OurLevel = AiLevel.TryParse(Value("--our-level") ?? AiLevel.Default.ToString(), out int ourLevel)
                ? ourLevel
                : throw new ArgumentException($"--our-level は{AiLevel.Min}〜{AiLevel.Max}にしてください。"),
            Games = games % 2 == 0 ? games : games + 1,
            OpeningMoves = int.Parse(Value("--opening-moves") ?? "8"),
            Seed = int.Parse(Value("--seed") ?? "1"),
            EgaroucidThreads = int.Parse(Value("--threads") ?? "1"),
            EgaroucidUsesBook = args.Contains("--book"),
            EgaroucidPath = Value("--egaroucid") ?? defaults.EgaroucidPath,
            OutputDirectory = Value("--out") ?? defaults.OutputDirectory,
        };
    }
}

/// <summary>ベンチの1局分の結果。games.jsonl に1行ずつ書く。</summary>
public sealed class BenchGameResult
{
    public int Index { get; set; }
    /// <summary>序盤のランダムな手順(例: f5d6c3...)</summary>
    public string Opening { get; set; } = "";
    /// <summary>自分のAIの色("black" / "white")</summary>
    public string OurColor { get; set; } = "";
    /// <summary>自分のAIから見た結果("win" / "loss" / "draw")</summary>
    public string Result { get; set; } = "";
    public int OurDiscs { get; set; }
    public int EgaroucidDiscs { get; set; }
    /// <summary>パスを含まない手順。Egaroucidの play にそのまま渡せる</summary>
    public string Transcript { get; set; } = "";
    public int OurMoveCount { get; set; }
    public long OurThinkMsTotal { get; set; }
    public long OurThinkMsMax { get; set; }
    /// <summary>自分のAIの思考時間が、目標(AiLevel.TargetThinkTime)を超えた手の数</summary>
    public int OurMovesOverTarget { get; set; }
    public int EgaroucidMoveCount { get; set; }
    public long EgaroucidThinkMsTotal { get; set; }
}

/// <summary>ベンチ全体のまとめ。summary.json に書く。</summary>
public sealed class BenchSummary
{
    public string StartedAt { get; set; } = "";
    public string EndedAt { get; set; } = "";
    public int OurLevel { get; set; }
    public string OurWeights { get; set; } = "";
    /// <summary>中盤の探索で LMR を使ったか(レベルどおり。--lmr / --no-lmr で上書きできる)</summary>
    public bool OurUsesLmr { get; set; } = true;
    public int EgaroucidLevel { get; set; }
    public bool EgaroucidUsesBook { get; set; }
    public int EgaroucidThreads { get; set; }
    public int Games { get; set; }
    public int OpeningMoves { get; set; }
    public int Seed { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Draws { get; set; }
    /// <summary>勝率。引き分けは0.5勝として数える</summary>
    public double WinRate { get; set; }
    /// <summary>自分のAIの石数 − Egaroucidの石数 の平均</summary>
    public double AverageDiscDiff { get; set; }
    public double OurThinkMsAverage { get; set; }
    public long OurThinkMsMax { get; set; }
    /// <summary>自分のAIの思考時間が、目標(AiLevel.TargetThinkTime)を超えた手の数(全局の合計)</summary>
    public int OurMovesOverTarget { get; set; }
    public double EgaroucidThinkMsAverage { get; set; }
}

/// <summary>
/// 自分のAIとEgaroucidを対戦させて、強さを測る(企画 P-01)。
/// 序盤の局面集を乱数の種から作り、同じ局面で先手・後手を入れ替えて2局ずつ打つ。
/// </summary>
public static class BenchMode
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public static void Run(string[] args)
    {
        BenchOptions options = BenchOptions.Parse(args);
        bool? lmrOverride = AiLevel.LmrOverrideFromArgs(args);
        bool useLmr = lmrOverride ?? AiLevel.UsesLmr(options.OurLevel);
        var ai = new OthelloAI(EvaluatorFile.LoadFromArgs(args, out string weightsSource)) { LmrOverride = lmrOverride };

        DateTimeOffset startedAt = DateTimeOffset.Now;
        Directory.CreateDirectory(options.OutputDirectory);
        // 同じ秒に複数のベンチを並べて動かしても重ならないよう、乱数の種とプロセスの番号も入れる
        string baseName = $"bench-{startedAt:yyyyMMdd-HHmmss}-L{options.OurLevel}-lv{options.EgaroucidLevel}"
                        + $"-s{options.Seed}-p{Environment.ProcessId}";
        string gamesPath = Path.Combine(options.OutputDirectory, baseName + "-games.jsonl");
        string summaryPath = Path.Combine(options.OutputDirectory, baseName + "-summary.json");

        Console.WriteLine($"ベンチ開始: 自分のAI=レベル{options.OurLevel}({weightsSource}、LMR{(useLmr ? "あり" : "なし")}{(lmrOverride == null ? "" : "(指定)")})  Egaroucid lv.{options.EgaroucidLevel}"
                        + $"(定石の本={(options.EgaroucidUsesBook ? "あり" : "なし")}, スレッド={options.EgaroucidThreads})"
                        + $"  {options.Games}局  序盤のランダム手={options.OpeningMoves}  種={options.Seed}");

        List<List<(int row, int col)>> openings = CreateOpenings(options.Games / 2, options.OpeningMoves, options.Seed);
        var results = new List<BenchGameResult>();

        OthelloDebugLog.SuppressOutput = true;
        try
        {
            using var egaroucid = new EgaroucidClient(options.EgaroucidPath, options.EgaroucidLevel,
                                                      options.EgaroucidThreads, options.EgaroucidUsesBook);
            // BOMを付けない(JSONLを読む道具の多くは、先頭のBOMをエラーにする)
            using var gamesWriter = new StreamWriter(gamesPath, append: false, new UTF8Encoding(false));

            for (int i = 0; i < options.Games; i++)
            {
                List<(int row, int col)> opening = openings[i / 2];
                int ourTurnNum = i % 2 == 0 ? 1 : 2; // 偶数局目は自分が黒、奇数局目は自分が白
                BenchGameResult result = PlayGame(ai, egaroucid, options.OurLevel, opening, ourTurnNum);
                result.Index = i + 1;
                results.Add(result);

                gamesWriter.WriteLine(JsonSerializer.Serialize(result));
                gamesWriter.Flush(); // 途中で止めても、そこまでの結果が残るようにする
                PrintProgress(results, options.Games);
            }
        }
        finally
        {
            OthelloDebugLog.SuppressOutput = false;
        }

        BenchSummary summary = Summarize(results, options, weightsSource, startedAt);
        summary.OurUsesLmr = useLmr;
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, s_jsonOptions));
        PrintSummary(summary);
        Console.WriteLine($"結果: {Path.GetFullPath(gamesPath)}");
        Console.WriteLine($"まとめ: {Path.GetFullPath(summaryPath)}");
    }

    /// <summary>
    /// 序盤の局面集を作る。初期局面からランダムな合法手を指定の手数だけ打った手順を、count個返す。
    /// 同じ手順が重ならないようにする。
    /// </summary>
    private static List<List<(int row, int col)>> CreateOpenings(int count, int openingMoves, int seed)
    {
        var random = new Random(seed);
        var openings = new List<List<(int row, int col)>>();
        var seen = new HashSet<string>();
        const int MaxAttemptsPerOpening = 1000;

        for (int attempt = 0; openings.Count < count; attempt++)
        {
            if (attempt > count * MaxAttemptsPerOpening)
                throw new InvalidOperationException("重ならない序盤の手順を必要な数だけ作れませんでした。--opening-moves を増やしてください。");

            MainGameData gameData = CreateInitialGameData(AiLevel.Default);
            var moves = new List<(int row, int col)>();
            for (int m = 0; m < openingMoves; m++)
            {
                List<(int x, int y)> validMoves = InstallationArea(gameData);
                if (validMoves.Count == 0) break; // 序盤でパスが出る手順は使わない
                (int row, int col) move = validMoves[random.Next(validMoves.Count)];
                PlacePiece(move.row, move.col, gameData);
                gameData = TurnChange(gameData);
                moves.Add(move);
            }
            if (moves.Count != openingMoves) continue;
            if (seen.Add(GameTranscript.Format(moves))) openings.Add(moves);
        }
        return openings;
    }

    /// <summary>1局を最後まで打ち、自分のAIから見た結果を返す</summary>
    private static BenchGameResult PlayGame(OthelloAI ai, EgaroucidClient egaroucid, int level,
                                            List<(int row, int col)> opening, int ourTurnNum)
    {
        MainGameData gameData = CreateInitialGameData(level);
        var transcript = new List<(int row, int col)>();
        var result = new BenchGameResult
        {
            Opening = GameTranscript.Format(opening),
            OurColor = ourTurnNum == 1 ? "black" : "white",
        };

        foreach ((int row, int col) move in opening)
        {
            PlacePiece(move.row, move.col, gameData);
            gameData = TurnChange(gameData);
            gameData._turnCounter++;
            transcript.Add(move);
        }

        int passCount = 0;
        var stopwatch = new Stopwatch();
        while (passCount < 2)
        {
            if (InstallationArea(gameData).Count == 0)
            {
                passCount++;
                gameData = TurnChange(gameData);
                continue;
            }
            passCount = 0;

            bool isOurTurn = gameData._turnNum == ourTurnNum;
            stopwatch.Restart();
            (int row, int col) chosen = isOurTurn
                ? ai.AI(gameData, false)
                : egaroucid.GetMove(gameData._tiles, gameData._turnNum);
            stopwatch.Stop();

            if (isOurTurn)
            {
                result.OurMoveCount++;
                result.OurThinkMsTotal += stopwatch.ElapsedMilliseconds;
                result.OurThinkMsMax = Math.Max(result.OurThinkMsMax, stopwatch.ElapsedMilliseconds);
                if (stopwatch.Elapsed > AiLevel.TargetThinkTime) result.OurMovesOverTarget++;
            }
            else
            {
                result.EgaroucidMoveCount++;
                result.EgaroucidThinkMsTotal += stopwatch.ElapsedMilliseconds;
            }

            if (!InstallationArea(gameData).Contains(chosen))
            {
                string who = isOurTurn ? "自分のAI" : "Egaroucid";
                throw new InvalidOperationException(
                    $"{who}が合法でない手 {GameRecord.ToNotation(chosen.row, chosen.col)} を返しました(手順: {GameTranscript.Format(transcript)})。");
            }

            PlacePiece(chosen.row, chosen.col, gameData);
            gameData = TurnChange(gameData);
            gameData._turnCounter++;
            transcript.Add(chosen);
        }

        var (black, white) = Counting(gameData._tiles);
        result.OurDiscs = ourTurnNum == 1 ? black : white;
        result.EgaroucidDiscs = ourTurnNum == 1 ? white : black;
        result.Result = result.OurDiscs > result.EgaroucidDiscs ? "win"
                      : result.OurDiscs < result.EgaroucidDiscs ? "loss"
                      : "draw";
        result.Transcript = GameTranscript.Format(transcript);
        return result;
    }

    /// <summary>初期局面(黒が先手)のゲームデータを作る</summary>
    private static MainGameData CreateInitialGameData(int level)
    {
        var gameData = new MainGameData
        {
            _tilesSize = 8,
            _tiles = new int[8, 8],
            _gameTurn = GameTurn.AI,
            _gameMode = GameMode.AIvsAI,
            _turnNum = 1,
            _turnCounter = 1,
            _aiLevel = level,
        };
        gameData._tiles[3, 3] = 2; gameData._tiles[3, 4] = 1;
        gameData._tiles[4, 3] = 1; gameData._tiles[4, 4] = 2;
        return gameData;
    }

    private static void PrintProgress(List<BenchGameResult> results, int totalGames)
    {
        BenchGameResult last = results[^1];
        int wins = results.Count(r => r.Result == "win");
        int losses = results.Count(r => r.Result == "loss");
        int draws = results.Count(r => r.Result == "draw");
        string average = last.OurMoveCount > 0 ? $"{last.OurThinkMsTotal / last.OurMoveCount}" : "-";
        Console.WriteLine($"[{last.Index,4}/{totalGames}] {last.OurColor,-5} {last.Result,-4} {last.OurDiscs,2}-{last.EgaroucidDiscs,-2}"
                        + $"  思考 平均{average}ms 最大{last.OurThinkMsMax}ms {AiLevel.TargetThinkTime.TotalSeconds}秒超え{last.OurMovesOverTarget}手   通算 {wins}勝{losses}敗{draws}分");
    }

    private static BenchSummary Summarize(List<BenchGameResult> results, BenchOptions options,
                                          string weightsSource, DateTimeOffset startedAt)
    {
        int wins = results.Count(r => r.Result == "win");
        int losses = results.Count(r => r.Result == "loss");
        int draws = results.Count(r => r.Result == "draw");
        int ourMoves = results.Sum(r => r.OurMoveCount);
        int egaroucidMoves = results.Sum(r => r.EgaroucidMoveCount);

        return new BenchSummary
        {
            StartedAt = startedAt.ToString("o"),
            EndedAt = DateTimeOffset.Now.ToString("o"),
            OurLevel = options.OurLevel,
            OurWeights = weightsSource,
            EgaroucidLevel = options.EgaroucidLevel,
            EgaroucidUsesBook = options.EgaroucidUsesBook,
            EgaroucidThreads = options.EgaroucidThreads,
            Games = results.Count,
            OpeningMoves = options.OpeningMoves,
            Seed = options.Seed,
            Wins = wins,
            Losses = losses,
            Draws = draws,
            WinRate = Math.Round((wins + draws * 0.5) / results.Count, 4),
            AverageDiscDiff = Math.Round(results.Average(r => r.OurDiscs - r.EgaroucidDiscs), 2),
            OurThinkMsAverage = ourMoves > 0 ? Math.Round((double)results.Sum(r => r.OurThinkMsTotal) / ourMoves, 1) : 0,
            OurThinkMsMax = results.Max(r => r.OurThinkMsMax),
            OurMovesOverTarget = results.Sum(r => r.OurMovesOverTarget),
            EgaroucidThinkMsAverage = egaroucidMoves > 0 ? Math.Round((double)results.Sum(r => r.EgaroucidThinkMsTotal) / egaroucidMoves, 1) : 0,
        };
    }

    private static void PrintSummary(BenchSummary summary)
    {
        Console.WriteLine();
        Console.WriteLine($"=== まとめ: レベル{summary.OurLevel} vs Egaroucid lv.{summary.EgaroucidLevel} ===");
        Console.WriteLine($"{summary.Wins}勝 {summary.Losses}敗 {summary.Draws}分  勝率 {summary.WinRate:P1}  平均石数差 {summary.AverageDiscDiff:+0.00;-0.00;0}");
        Console.WriteLine($"自分のAIの思考時間: 平均 {summary.OurThinkMsAverage}ms  最大 {summary.OurThinkMsMax}ms  {AiLevel.TargetThinkTime.TotalSeconds}秒を超えた手 {summary.OurMovesOverTarget}手");
        Console.WriteLine($"Egaroucidの思考時間: 平均 {summary.EgaroucidThinkMsAverage}ms");
    }
}

using System.Text;
using System.Text.Json;
using static OthelloSystem;

/// <summary>
/// 棋譜の分析(--analyze、企画 P-04)。
/// 棋譜の各局面で、Egaroucidに全部の合法手を評価させ、打った手が最善手からどれだけ損したかを出す。
/// AIが打った手については、今のAI(同じ強さ)ならどの手を選ぶかも並べ、悪手が直っているかを見る。
///
/// f5d6形式の棋譜(.txt、1行1局)も読める(企画 P-08)。f5d6には人間かAIかの情報がないので、両方の色の手を分析する。
///
/// 例: --analyze Data/collected/game_records.jsonl          (人間が勝った局をすべて)
///     --analyze Data/collected/game_records.jsonl --index 39 --level 18
///     --analyze data/game_records.txt                       (全部の局。今のAIはレベル15)
///     --analyze data/game_records.txt --our-level 5          (今のAIをレベル5にする)
///     --analyze data/game_records.txt --threads 4            (速くなるが、評価値が少しぶれる)
/// </summary>
public static class AnalyzeMode
{
    /// <summary>この石数以上損した手を「悪手」として目立たせる</summary>
    private const int BlunderThreshold = 4;
    /// <summary>OthelloAI が完全読みに入る残りマスの数(OthelloAI.PerfectSolveThreshold と同じ)</summary>
    private const int PerfectSolveEmpties = 18;
    private const string DefaultEgaroucidPath = "../external/egaroucid/Egaroucid-console_v7.8.2/bin/Egaroucid_for_Console.out";
    private const string WeightsPath = "weights/best_weights.json";
    /// <summary>f5d6形式の棋譜を分析するときの、今のAIのレベルの標準(--our-level で変えられる)</summary>
    private const int DefaultTranscriptAiLevel = AiLevel.Max;

    /// <summary>1手分の分析結果</summary>
    private sealed record MoveAnalysis(
        int Turn, string Color, string Actor, string Played, int PlayedValue,
        string Best, int BestValue, int Empties, string? CurrentAiMove, int? CurrentAiValue)
    {
        public int Loss => BestValue - PlayedValue;
        public int? CurrentAiLoss => CurrentAiValue.HasValue ? BestValue - CurrentAiValue.Value : null;
    }

    public static void Run(string[] args)
    {
        int fileIndex = Array.IndexOf(args, "--analyze");
        if (fileIndex < 0 || fileIndex + 1 >= args.Length)
            throw new ArgumentException("--analyze のあとに棋譜ファイル(game_records.jsonl)を指定してください。");
        string recordsPath = args[fileIndex + 1];
        int level = int.Parse(ValueOf(args, "--level") ?? "18");
        // f5d6形式の棋譜にはAIのレベルが残っていないので、今のAIのレベルを指定する。JSONLでは、指定があれば棋譜のレベルの代わりに使う
        string? ourLevelText = ValueOf(args, "--our-level");
        int? ourLevel = ourLevelText == null ? null
                      : AiLevel.TryParse(ourLevelText, out int parsedLevel) ? parsedLevel
                      : throw new ArgumentException($"--our-level は{AiLevel.Min}〜{AiLevel.Max}にしてください。");
        string egaroucidPath = ValueOf(args, "--egaroucid") ?? DefaultEgaroucidPath;
        // 複数スレッドで読むと読む順番が毎回変わり、評価値が少しぶれる。同じ棋譜からいつも同じ報告を出すため、標準は1スレッド
        int threads = int.Parse(ValueOf(args, "--threads") ?? "1");
        HashSet<int> indexes = args.Select((arg, i) => (arg, i))
                                   .Where(pair => pair.arg == "--index" && pair.i + 1 < args.Length)
                                   .Select(pair => int.Parse(args[pair.i + 1]))
                                   .ToHashSet();

        bool isTranscriptFile = Path.GetExtension(recordsPath).Equals(".txt", StringComparison.OrdinalIgnoreCase);
        // JSONLは、指定がなければ人間が勝った局だけ。f5d6形式は勝ち負けがわからないので、指定がなければ全部の局
        List<(int lineNumber, GameRecord record)> games = (isTranscriptFile ? LoadTranscripts(recordsPath, ourLevel ?? DefaultTranscriptAiLevel) : LoadGames(recordsPath))
            .Where(game => indexes.Count > 0 ? indexes.Contains(game.lineNumber)
                                             : isTranscriptFile || game.record.Result == "win")
            .ToList();
        if (games.Count == 0)
        {
            Console.WriteLine("分析する対局がありません。");
            return;
        }

        var weights = File.Exists(WeightsPath) ? WeightSet.Load(WeightsPath) : WeightSet.Default();
        var report = new StringBuilder();
        report.AppendLine($"# 棋譜の分析({DateTimeOffset.Now:yyyy-MM-dd HH:mm})");
        report.AppendLine();
        report.AppendLine($"- 棋譜: `{recordsPath}`");
        report.AppendLine($"- 評価: Egaroucid lv.{level}(定石の本なし、{threads}スレッド)。値は、その手を打った側から見た最終の石数の差の見込み");
        report.AppendLine($"- 損: 最善手の値 − 打った手の値。{BlunderThreshold}以上を悪手とする");
        report.AppendLine($"- 今のAI: 棋譜と同じレベル(--our-level の指定があればそのレベル)の、今のコードのAI(重み: {(File.Exists(WeightsPath) ? "best_weights.json" : "default")})が選ぶ手");
        report.AppendLine($"- 4段階だったころの棋譜は、中盤で読む手数が同じレベルに読み替える(Beginner→3、Normal→6、Expert→10、Professional→14)。当時はどれも残り18マスから完全読みしていたので、終盤の読み方は当時と違う");
        report.AppendLine();

        OthelloDebugLog.SuppressOutput = true;
        try
        {
            using var egaroucid = new EgaroucidClient(egaroucidPath, level, threads, useBook: false);
            foreach ((int lineNumber, GameRecord record) in games)
            {
                int gameLevel = ourLevel ?? LevelOf(record);
                Console.WriteLine($"分析中: {lineNumber}行目(今のAIはレベル{gameLevel}、{record.Moves.Count}手)");
                List<MoveAnalysis> moves = AnalyzeGame(record, gameLevel, egaroucid, weights);
                AppendGameReport(report, lineNumber, record, moves);
            }
        }
        finally
        {
            OthelloDebugLog.SuppressOutput = false;
        }

        Directory.CreateDirectory("bench-results");
        string reportPath = Path.Combine("bench-results", $"analyze-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine(report.ToString());
        Console.WriteLine($"報告: {Path.GetFullPath(reportPath)}");
    }

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>棋譜ファイルを読む。行番号(1始まり)と一緒に返す</summary>
    private static IEnumerable<(int lineNumber, GameRecord record)> LoadGames(string path)
    {
        int lineNumber = 0;
        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            GameRecord? record = JsonSerializer.Deserialize<GameRecord>(line);
            if (record != null) yield return (lineNumber, record);
        }
    }

    /// <summary>
    /// f5d6形式の棋譜を読む。行番号(1始まり)と一緒に返す。
    /// 人間かAIかはわからないので Actor は空にし、AIのレベルは aiLevel にする
    /// </summary>
    private static IEnumerable<(int lineNumber, GameRecord record)> LoadTranscripts(string path, int aiLevel)
    {
        int lineNumber = 0;
        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            GameTranscript.ReplayResult replay = GameTranscript.Replay(line);
            var (black, white) = Counting(replay.FinalTiles);
            var record = new GameRecord
            {
                AiLevel = aiLevel,
                BlackCount = black,
                WhiteCount = white,
                Transcript = line.Trim(),
                Moves = replay.Steps.Select((step, i) => new MoveRecord
                {
                    Turn = i + 1,
                    Color = step.TurnNum == 1 ? "black" : "white",
                    IsPass = step.IsPass,
                    Row = step.Row,
                    Col = step.Col,
                    Notation = step.IsPass ? "pass" : GameRecord.ToNotation(step.Row, step.Col),
                }).ToList(),
            };
            yield return (lineNumber, record);
        }
    }

    /// <summary>棋譜のAIのレベル。4段階だったころの棋譜は、強さの名前をレベルに読み替える</summary>
    private static int LevelOf(GameRecord record)
        => record.AiLevel > 0 ? record.AiLevel : AiLevel.FromLegacyStrengthName(record.AiStrength);

    /// <summary>1局を初手から並べ直し、パス以外の各手を分析する</summary>
    private static List<MoveAnalysis> AnalyzeGame(GameRecord record, int aiLevel, EgaroucidClient egaroucid, WeightSet weights)
    {
        MainGameData gameData = CreateInitialGameData(aiLevel);
        // この作りでは、AIが先手のとき白から打ち始めるので、1手目の色で手番を決める
        if (record.Moves.Count > 0 && record.Moves[0].Color == "white") gameData._turnNum = 2;

        var results = new List<MoveAnalysis>();
        foreach (MoveRecord move in record.Moves)
        {
            int moverTurnNum = move.Color == "black" ? 1 : 2;
            if (gameData._turnNum != moverTurnNum)
                throw new InvalidDataException($"{move.Turn}手目: 棋譜の手番({move.Color})と、並べ直した盤面の手番が合いません。");

            if (move.IsPass)
            {
                gameData = TurnChange(gameData);
                continue;
            }

            List<(int x, int y)> legalMoves = InstallationArea(gameData);
            if (!legalMoves.Contains((move.Row, move.Col)))
                throw new InvalidDataException($"{move.Turn}手目: {move.Notation} は合法手ではありません。");

            Dictionary<(int row, int col), int> values = egaroucid.GetMoveValues(gameData._tiles, gameData._turnNum, legalMoves.Count);
            KeyValuePair<(int row, int col), int> best = values.MaxBy(pair => pair.Value);

            string? currentAiMove = null;
            int? currentAiValue = null;
            // 人間の手は今のAIと比べない。f5d6形式の棋譜(Actor が空)は、両方の色の手を比べる
            if (move.Actor != "human")
            {
                // 置換表などを持ち越さないよう、毎回新しく作る
                (int row, int col) chosen = new OthelloAI(weights).AI(gameData.Clone(), false);
                currentAiMove = GameRecord.ToNotation(chosen.row, chosen.col);
                currentAiValue = values[chosen];
            }

            int empties = 64 - CountDiscs(gameData._tiles);
            results.Add(new MoveAnalysis(move.Turn, move.Color, move.Actor, move.Notation, values[(move.Row, move.Col)],
                                         GameRecord.ToNotation(best.Key.row, best.Key.col), best.Value, empties,
                                         currentAiMove, currentAiValue));

            PlacePiece(move.Row, move.Col, gameData);
            gameData = TurnChange(gameData);
            gameData._turnCounter++;
        }
        return results;
    }

    private static void AppendGameReport(StringBuilder report, int lineNumber, GameRecord record, List<MoveAnalysis> moves)
    {
        bool isTranscript = record.Moves.All(m => m.Actor == "");
        string players = isTranscript ? "f5d6形式(人間かAIかは不明)"
                       : $"{(record.AiLevel > 0 ? $"レベル{record.AiLevel}" : $"{record.AiStrength}(レベル{LevelOf(record)}に読み替え)")}、人間は{(record.HumanColor == "black" ? "黒" : "白")}";
        string startedAt = record.StartedAt.Length >= 16 ? $"({record.StartedAt[..16]})" : "";
        report.AppendLine($"## {lineNumber}行目: {players}、結果 黒{record.BlackCount} − 白{record.WhiteCount}{startedAt}");
        report.AppendLine();

        // 表に出すのは、今のAIと比べる手(AIの手。f5d6形式なら全部の手)
        List<MoveAnalysis> targetMoves = moves.Where(m => m.Actor != "human").ToList();
        if (isTranscript)
        {
            foreach (string color in new[] { "black", "white" })
            {
                List<MoveAnalysis> colorMoves = moves.Where(m => m.Color == color).ToList();
                report.AppendLine($"- {(color == "black" ? "黒" : "白")}の損の合計: {colorMoves.Sum(m => m.Loss)}石"
                                + $"({colorMoves.Count}手、悪手{colorMoves.Count(m => m.Loss >= BlunderThreshold)}手)");
            }
            report.AppendLine($"- 今のAI(レベル{record.AiLevel})なら: 損の合計 {targetMoves.Sum(m => m.CurrentAiLoss ?? 0)}石"
                            + $"(悪手 {targetMoves.Count(m => m.CurrentAiLoss >= BlunderThreshold)}手)");
        }
        else
        {
            List<MoveAnalysis> humanMoves = moves.Where(m => m.Actor == "human").ToList();
            List<MoveAnalysis> blunders = targetMoves.Where(m => m.Loss >= BlunderThreshold).ToList();
            report.AppendLine($"- AIの損の合計: {targetMoves.Sum(m => m.Loss)}石({targetMoves.Count}手、悪手{blunders.Count}手。"
                            + $"うち完全読みの範囲〈残り{PerfectSolveEmpties}マス以下〉 {blunders.Count(m => m.Empties <= PerfectSolveEmpties)}手)");
            report.AppendLine($"- 今のAIなら: 損の合計 {targetMoves.Sum(m => m.CurrentAiLoss ?? 0)}石"
                            + $"(悪手 {targetMoves.Count(m => m.CurrentAiLoss >= BlunderThreshold)}手)");
            report.AppendLine($"- 人間の損の合計: {humanMoves.Sum(m => m.Loss)}石({humanMoves.Count}手)");
        }
        report.AppendLine();
        report.AppendLine("| 手 | 色 | 空き | 打った手 | 値 | 最善手 | 値 | 損 | 今のAIの手 | 損 |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (MoveAnalysis m in targetMoves)
        {
            string mark = m.Loss >= BlunderThreshold ? " **" : "";
            string markEnd = m.Loss >= BlunderThreshold ? "**" : "";
            report.AppendLine($"| {m.Turn} | {(m.Color == "black" ? "黒" : "白")} | {m.Empties} | {m.Played} | {m.PlayedValue:+0;-0;0} "
                            + $"| {m.Best} | {m.BestValue:+0;-0;0} |{mark}{m.Loss}{markEnd} | {m.CurrentAiMove} | {m.CurrentAiLoss} |");
        }
        report.AppendLine();
    }

    private static int CountDiscs(int[,] tiles)
    {
        var (black, white) = Counting(tiles);
        return black + white;
    }

    private static MainGameData CreateInitialGameData(int aiLevel)
    {
        var gameData = new MainGameData
        {
            _tilesSize = 8, _tiles = new int[8, 8], _gameTurn = GameTurn.AI, _gameMode = GameMode.AIvsAI,
            _turnNum = 1, _turnCounter = 1, _aiLevel = aiLevel,
        };
        gameData._tiles[3, 3] = 2; gameData._tiles[3, 4] = 1;
        gameData._tiles[4, 3] = 1; gameData._tiles[4, 4] = 2;
        return gameData;
    }
}

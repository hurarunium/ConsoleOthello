using System.Diagnostics;
using static OthelloSystem;

/// <summary>
/// パターン評価の確認(--pattern-check、設計書 Phase 5)。
/// - 置き場所の数
/// - 対称性: 盤を8通りに回転・反転しても評価値が同じか(点数はランダムに入れて確かめる)
/// - 保存と読み込み: 書いたファイルを読み込むと同じ値に戻るか。違うファイルは読み込みで止まるか
/// - 速さ: 1秒あたりの評価の回数を、今の評価関数と比べる
/// パターン評価の形を変えたら、毎回これを通す。
///
/// 例: --pattern-check --positions 2000
/// </summary>
public static class PatternCheck
{
    public static void Run(string[] args)
    {
        int index = Array.IndexOf(args, "--positions");
        int positions = index >= 0 && index + 1 < args.Length ? int.Parse(args[index + 1]) : 2000;

        List<BitBoard> boards = CreatePositions(positions, seed: 21);
        Console.WriteLine($"確かめる局面: {boards.Count}(序盤〜終盤をランダムに作った局面)");

        CheckInstances();
        CheckSymmetry(boards);
        CheckSaveAndLoad(boards);
        CheckSpeed(boards);
        CheckSearchSpeed(boards);
    }

    private static void CheckInstances()
    {
        bool ok = PatternEvaluator.InstanceCount == 46;
        Console.WriteLine($"[{(ok ? "OK" : "不具合あり")}] 置き場所: {PatternEvaluator.InstanceCount}か所(パターン{PatternEvaluator.PatternCount}種類。期待は46か所)");
    }

    private static void CheckSymmetry(List<BitBoard> boards)
    {
        PatternEvaluator evaluator = PatternEvaluator.CreateRandom(seed: 5);
        int mismatches = 0;
        foreach (BitBoard board in boards)
        {
            int expected = evaluator.Evaluate(board);
            for (int t = 1; t < 8; t++)
                if (evaluator.Evaluate(PatternEvaluator.Transform(board, t)) != expected) mismatches++;
        }
        Console.WriteLine($"[{(mismatches == 0 ? "OK" : "不具合あり")}] 対称性: {boards.Count}局面 × 7通りの回転・反転のうち、評価値が変わったもの {mismatches}");
    }

    private static void CheckSaveAndLoad(List<BitBoard> boards)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pattern-check-{Environment.ProcessId}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "pattern_eval.bin");
            PatternEvaluator original = PatternEvaluator.CreateRandom(seed: 9);
            original.Save(path);
            PatternEvaluator loaded = PatternEvaluator.Load(path);

            bool sameValues = original.HasSameValues(loaded);
            bool sameEvaluations = boards.All(board => original.Evaluate(board) == loaded.Evaluate(board));
            long size = new FileInfo(path).Length;
            Console.WriteLine($"[{(sameValues && sameEvaluations ? "OK" : "不具合あり")}] 保存と読み込み: 点数が同じ={sameValues}、評価値が同じ={sameEvaluations}、ファイルの大きさ {size:N0}バイト");

            // 違うファイルを読み込んだら止まるか(識別子を壊す)
            byte[] bytes = File.ReadAllBytes(path);
            bytes[0] = (byte)'X';
            string brokenPath = Path.Combine(directory, "broken.bin");
            File.WriteAllBytes(brokenPath, bytes);
            bool rejected;
            try { PatternEvaluator.Load(brokenPath); rejected = false; }
            catch (InvalidDataException) { rejected = true; }
            Console.WriteLine($"[{(rejected ? "OK" : "不具合あり")}] 識別子の違うファイルの読み込み: {(rejected ? "エラーで止まった" : "読み込めてしまった")}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckSpeed(List<BitBoard> boards)
    {
        PatternEvaluator pattern = PatternEvaluator.CreateRandom(seed: 5);
        OthelloAI legacy = new OthelloAI(File.Exists(EvaluatorOption.LegacyWeightsPath)
            ? WeightSet.Load(EvaluatorOption.LegacyWeightsPath) : WeightSet.Default());
        const int Rounds = 200;

        long checksum = 0;
        var stopwatch = Stopwatch.StartNew();
        for (int r = 0; r < Rounds; r++) foreach (BitBoard board in boards) checksum += legacy.Evaluate(board);
        double legacyRate = Rounds * boards.Count / stopwatch.Elapsed.TotalSeconds;

        stopwatch.Restart();
        for (int r = 0; r < Rounds; r++) foreach (BitBoard board in boards) checksum += pattern.Evaluate(board);
        double patternRate = Rounds * boards.Count / stopwatch.Elapsed.TotalSeconds;

        Console.WriteLine($"[参考] 速さ: 今の評価関数 {legacyRate / 1e6:F2}百万回/秒、パターン評価 {patternRate / 1e6:F2}百万回/秒"
                        + $"(パターン評価は今の {patternRate / legacyRate:F2}倍){(checksum == 0 ? "" : "")}");
    }

    /// <summary>
    /// 探索の速さ: 中盤の局面をレベル8で読ませ、1秒あたりに読んだ局面の数を比べる。
    /// 評価関数が違うと読む局面の数も変わるので、かかった時間ではなく、1秒あたりの局面の数で比べる
    /// </summary>
    private static void CheckSearchSpeed(List<BitBoard> boards)
    {
        const int Level = 8;
        List<BitBoard> midgame = boards.Where(b => b.EmptyCount > AiLevel.PerfectSolveEmpties(Level) && b.EmptyCount < 50).Take(30).ToList();
        OthelloAI legacy = new OthelloAI(File.Exists(EvaluatorOption.LegacyWeightsPath)
            ? WeightSet.Load(EvaluatorOption.LegacyWeightsPath) : WeightSet.Default());
        OthelloAI pattern = new OthelloAI(PatternEvaluator.CreateRandom(seed: 5));

        (double nodesPerSecond, double secondsPerMove) Measure(OthelloAI ai)
        {
            long nodes = 0;
            var stopwatch = Stopwatch.StartNew();
            foreach (BitBoard board in midgame)
            {
                ai.AI(ToGameData(board, Level), false);
                nodes += ai.LastNodeCount;
            }
            return (nodes / stopwatch.Elapsed.TotalSeconds, stopwatch.Elapsed.TotalSeconds / midgame.Count);
        }

        OthelloDebugLog.SuppressOutput = true;
        try
        {
            var (legacyNps, legacySec) = Measure(legacy);
            var (patternNps, patternSec) = Measure(pattern);
            Console.WriteLine($"[参考] 探索の速さ(レベル{Level}、中盤{midgame.Count}局面): 今の評価関数 {legacyNps / 1e6:F2}百万局面/秒(1手 平均{legacySec * 1000:F0}ms)、"
                            + $"パターン評価 {patternNps / 1e6:F2}百万局面/秒(1手 平均{patternSec * 1000:F0}ms。点数はランダム)。パターン評価は今の {patternNps / legacyNps:F2}倍");
        }
        finally
        {
            OthelloDebugLog.SuppressOutput = false;
        }
    }

    /// <summary>ビットボード(手番の側 = Player)を、黒の番のゲームデータにする</summary>
    private static MainGameData ToGameData(BitBoard board, int level)
    {
        var gameData = new MainGameData
        {
            _tilesSize = 8, _tiles = new int[8, 8], _gameTurn = GameTurn.AI, _gameMode = GameMode.AIvsAI,
            _turnNum = 1, _turnCounter = 1, _aiLevel = level,
        };
        board.ToTiles(gameData._tiles, 1);
        return gameData;
    }

    /// <summary>初期局面からランダムに打った局面を作る(打つ手数を4〜56にばらつかせる)</summary>
    private static List<BitBoard> CreatePositions(int count, int seed)
    {
        var random = new Random(seed);
        var boards = new List<BitBoard>();
        while (boards.Count < count)
        {
            var gameData = new MainGameData
            {
                _tilesSize = 8, _tiles = new int[8, 8], _gameTurn = GameTurn.AI, _gameMode = GameMode.AIvsAI,
                _turnNum = 1, _turnCounter = 1,
            };
            gameData._tiles[3, 3] = 2; gameData._tiles[3, 4] = 1;
            gameData._tiles[4, 3] = 1; gameData._tiles[4, 4] = 2;

            int moves = random.Next(4, 57);
            bool finished = false;
            for (int i = 0; i < moves && !finished; i++)
            {
                List<(int x, int y)> valid = InstallationArea(gameData);
                if (valid.Count == 0)
                {
                    gameData = TurnChange(gameData);
                    finished = InstallationArea(gameData).Count == 0;
                    continue;
                }
                (int row, int col) move = valid[random.Next(valid.Count)];
                PlacePiece(move.row, move.col, gameData);
                gameData = TurnChange(gameData);
            }
            if (!finished) boards.Add(BitBoard.FromMainGameData(gameData));
        }
        return boards;
    }
}

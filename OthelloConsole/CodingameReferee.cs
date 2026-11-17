using System.Diagnostics;
using System.Globalization;
using System.Text;

/// <summary>
/// 手元の審判(--codingame-referee。企画 P-04、設計書 Phase 3)。
/// ボット(--codingame で動く本プログラム)のプロセスを2つ起動し、CodinGameのオセロの審判
/// (MultiStruct/Othello の Referee.java)と同じ入力を標準入力へ送り、返事までの時間を**審判側の時計**で測る
/// (プロセスの入出力も含む)。最初の1手は2000ms、2手目からは150ms。超えたら、その場で負け。不正な手も負け。
///
/// 公式の審判と同じ決まり: 黒がプレイヤー0で先手、手は小文字にしたうえで空白を除いて合法手と比べる、
/// 打てないときは相手がもう一度打つ(打てない側には入力を送らない)、石が多いほうが勝ち(空きマスは数えない)。
/// 違い: 合法手の並びは、行ごとの順(公式は石を置いた順)。序盤を変えるため、最初の数手は審判がランダムに打つ。
/// 最初の1手の時計は、ボットのプロセスを起動したときから数える(起動と評価の読み込みも含めて測る)。
///
/// 例: --codingame-referee --games 100
///     --codingame-referee --games 20 --limit-ms 100       (サーバーが遅い場合に備えて、短い制限でも守れるかを見る)
///     --codingame-referee --a-args "--turn-budget-ms 110"  (ボットAへ渡す引数)
/// </summary>
public static class CodingameReferee
{
    private static readonly (int dx, int dy)[] Directions =
        { (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1) };

    public static void Run(string[] args)
    {
        int games = int.Parse(ValueOf(args, "--games") ?? "100");
        double limitMs = double.Parse(ValueOf(args, "--limit-ms") ?? "150", CultureInfo.InvariantCulture);
        double firstLimitMs = double.Parse(ValueOf(args, "--first-limit-ms") ?? "2000", CultureInfo.InvariantCulture);
        int openingPlies = int.Parse(ValueOf(args, "--opening-plies") ?? "8");
        string[] botArgsA = SplitArgs(ValueOf(args, "--a-args"));
        string[] botArgsB = SplitArgs(ValueOf(args, "--b-args"));
        string[] passThrough = PassThroughArgs(args);

        Console.WriteLine($"制限時間 {limitMs:F0}ms(最初の1手は起動から {firstLimitMs:F0}ms)、{games}局、最初の{openingPlies}手は審判がランダムに打つ");
        Console.WriteLine($"ボットの引数 A: {string.Join(' ', passThrough.Concat(botArgsA))}  B: {string.Join(' ', passThrough.Concat(botArgsB))}");

        var statsA = new BotStats("A");
        var statsB = new BotStats("B");
        int aWins = 0, bWins = 0, draws = 0, failures = 0;

        for (int game = 0; game < games; game++)
        {
            bool aIsFirst = game % 2 == 0; // 先手(プレイヤー0・黒)
            var random = new Random(7000 + game / 2);
            GameResult result = PlayGame(random, openingPlies, limitMs, firstLimitMs,
                                         aIsFirst ? passThrough.Concat(botArgsA).ToArray() : passThrough.Concat(botArgsB).ToArray(),
                                         aIsFirst ? passThrough.Concat(botArgsB).ToArray() : passThrough.Concat(botArgsA).ToArray(),
                                         aIsFirst ? statsA : statsB, aIsFirst ? statsB : statsA);
            // result.Winner は 0(先手)・1(後手)・-1(引き分け)
            int aSide = aIsFirst ? 0 : 1;
            if (result.Winner == -1) draws++;
            else if (result.Winner == aSide) aWins++;
            else bWins++;
            if (result.Failure != null) { failures++; Console.WriteLine($"  第{game + 1}局: {result.Failure}"); }
            if ((game + 1) % 10 == 0 || game + 1 == games)
                Console.WriteLine($"  {game + 1}局まで: A {aWins}勝 B {bWins}勝 引き分け {draws}  時間切れ・不正な手 {failures}局");
        }

        Console.WriteLine($"結果: A {aWins}勝 B {bWins}勝 引き分け {draws}  時間切れ・不正な手で終わった対局 {failures}");
        statsA.Print(limitMs);
        statsB.Print(limitMs);
        Console.WriteLine(failures == 0 ? "[OK] すべての対局で、制限時間を守り、合法手を返した" : $"[NG] 制限時間を超えた、または不正な手を返した対局が {failures} あった");
        if (failures != 0) Environment.ExitCode = 1;
    }

    // ---- 1局 ----

    private sealed record GameResult(int Winner, string? Failure);

    private static GameResult PlayGame(Random random, int openingPlies, double limitMs, double firstLimitMs,
                                       string[] firstBotArgs, string[] secondBotArgs, BotStats firstStats, BotStats secondStats)
    {
        var grid = NewGrid();
        using var first = new BotProcess(0, firstBotArgs);
        using var second = new BotProcess(1, secondBotArgs);
        BotProcess[] bots = { first, second };
        BotStats[] stats = { firstStats, secondStats };

        int current = 0;
        List<(int x, int y)> legal = Actions(grid, current);
        int plies = 0;

        while (true)
        {
            string chosen;
            BotProcess bot = bots[current];

            if (plies < openingPlies)
            {
                (int x, int y) pick = legal[random.Next(legal.Count)];
                chosen = Name(pick);
            }
            else
            {
                // 入力を作る(公式の sendInputs と同じ順)
                var lines = new List<string>();
                if (!bot.InitSent)
                {
                    lines.Add(current.ToString());
                    lines.Add("8");
                    bot.InitSent = true;
                }
                for (int y = 0; y < 8; y++)
                {
                    var row = new StringBuilder();
                    for (int x = 0; x < 8; x++) row.Append(grid[y, x] < 0 ? '.' : (char)('0' + grid[y, x]));
                    lines.Add(row.ToString());
                }
                if (bot.Expert) lines.Add(bots[1 - current].LastMoves);
                lines.Add(legal.Count.ToString());
                foreach ((int x, int y) action in legal) lines.Add(Name(action));

                bool isFirstTurn = bot.TurnsPlayed == 0;
                double limit = isFirstTurn ? firstLimitMs : limitMs;
                string? output = bot.Execute(lines, limit, isFirstTurn, out double elapsedMs);
                bot.TurnsPlayed++;
                stats[current].Add(elapsedMs, isFirstTurn);

                if (output == null || elapsedMs > limit)
                {
                    stats[current].Timeouts++;
                    return new GameResult(1 - current, $"プレイヤー{current}が時間切れ({elapsedMs:F1}ms > {limit:F0}ms、{(isFirstTurn ? "最初の1手" : $"{bot.TurnsPlayed}手目")})");
                }

                // 公式と同じ読み方: MSG より前だけを取り、EXPERT を外し、空白を除いて小文字にする
                string move = output.Split("MSG")[0];
                if (move.Contains("EXPERT")) { move = move.Replace("EXPERT", ""); bot.Expert = true; }
                chosen = new string(move.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
                bot.LastMoves += chosen + ";";

                if (!legal.Any(a => Name(a) == chosen))
                {
                    stats[current].Invalid++;
                    return new GameResult(1 - current, $"プレイヤー{current}が不正な手「{output.Trim()}」({bot.TurnsPlayed}手目)");
                }
            }

            (int x, int y) cell = legal.First(a => Name(a) == chosen);
            Apply(grid, cell, current);
            plies++;

            // 相手の石が全部なくなったら、その場で勝ち
            if (Count(grid, 1 - current) == 0) return new GameResult(current, null);

            List<(int x, int y)> next = Actions(grid, 1 - current);
            if (next.Count > 0)
            {
                current = 1 - current;
                legal = next;
                bots[current].LastMoves = "";
            }
            else
            {
                next = Actions(grid, current);
                if (next.Count > 0)
                {
                    legal = next; // 相手はパス。同じ側がもう一度打つ
                    bots[1 - current].LastMoves = "pass;";
                }
                else
                {
                    int c0 = Count(grid, 0), c1 = Count(grid, 1);
                    return new GameResult(c0 > c1 ? 0 : c1 > c0 ? 1 : -1, null);
                }
            }
        }
    }

    // ---- ボットのプロセス ----

    private sealed class BotProcess : IDisposable
    {
        private readonly Process _process;
        private readonly long _startTimestamp;
        public bool InitSent, Expert;
        public int TurnsPlayed;
        public string LastMoves = "";

        public BotProcess(int index, string[] botArgs)
        {
            var info = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // dotnet run / dotnet xxx.dll で動いているときは、dll の場所も渡す
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
                info.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
            info.ArgumentList.Add("--codingame");
            foreach (string arg in botArgs) info.ArgumentList.Add(arg);

            _startTimestamp = Stopwatch.GetTimestamp();
            _process = Process.Start(info)!;
            _process.ErrorDataReceived += (_, _) => { }; // 標準エラーは読み捨てる(詰まらないように)
            _process.BeginErrorReadLine();
        }

        /// <summary>
        /// 入力を送って、返事の1行を待つ。返事までの時間(ms)を elapsedMs に入れる。
        /// 制限を大きく過ぎても返事が来なければ null(時間は制限の数倍まで測る)。
        /// 最初の1手は、プロセスを起動したときから数える
        /// </summary>
        public string? Execute(List<string> lines, double limitMs, bool fromProcessStart, out double elapsedMs)
        {
            long clockStart = fromProcessStart ? _startTimestamp : Stopwatch.GetTimestamp();
            Task<string?> read = _process.StandardOutput.ReadLineAsync();
            try
            {
                _process.StandardInput.Write(string.Join("\n", lines) + "\n");
                _process.StandardInput.Flush();
            }
            catch (IOException)
            {
                elapsedMs = Stopwatch.GetElapsedTime(clockStart).TotalMilliseconds;
                return null; // ボットが終了していた
            }

            double waitMs = Math.Max(limitMs * 3, limitMs + 1000) - Stopwatch.GetElapsedTime(clockStart).TotalMilliseconds;
            bool done = read.Wait(TimeSpan.FromMilliseconds(Math.Max(1, waitMs)));
            elapsedMs = Stopwatch.GetElapsedTime(clockStart).TotalMilliseconds;
            return done ? read.Result : null;
        }

        public void Dispose()
        {
            try { _process.StandardInput.Close(); } catch (IOException) { }
            if (!_process.WaitForExit(2000)) _process.Kill(true);
            _process.Dispose();
        }
    }

    // ---- 統計 ----

    private sealed class BotStats
    {
        private readonly string _name;
        private readonly List<double> _times = new();
        private readonly List<double> _firstTurnTimes = new();
        private readonly double[] _earlyMax = new double[6]; // 2手目以降の、最初の数手の最大(JITの暖機の影響を見る)
        private readonly int[] _earlyCount = new int[6];
        public int Timeouts, Invalid;

        public BotStats(string name) => _name = name;

        public void Add(double elapsedMs, bool firstTurn)
        {
            if (firstTurn) { _firstTurnTimes.Add(elapsedMs); return; }
            _times.Add(elapsedMs);
        }

        public void Print(double limitMs)
        {
            var sorted = _times.OrderBy(t => t).ToList();
            double Percentile(double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];
            Console.WriteLine($"{_name}: 2手目以降 {sorted.Count}手  平均 {(sorted.Count == 0 ? 0 : sorted.Average()):F1}ms  上位50% {Percentile(0.5):F1}ms  上位99% {Percentile(0.99):F1}ms  最大 {(sorted.Count == 0 ? 0 : sorted.Max()):F1}ms  制限({limitMs:F0}ms)超え {sorted.Count(t => t > limitMs)}");
            Console.WriteLine($"{_name}: 最初の1手(起動から) {_firstTurnTimes.Count}回  平均 {(_firstTurnTimes.Count == 0 ? 0 : _firstTurnTimes.Average()):F0}ms  最大 {(_firstTurnTimes.Count == 0 ? 0 : _firstTurnTimes.Max()):F0}ms  時間切れ {Timeouts}  不正な手 {Invalid}");
        }
    }

    // ---- 盤面(公式の審判と同じ考え方の、独立した小さな実装。ボットの内部の盤面は使わない) ----

    private static int[,] NewGrid()
    {
        var grid = new int[8, 8];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) grid[y, x] = -1;
        for (int y = 3; y <= 4; y++)
            for (int x = 3; x <= 4; x++)
                grid[y, x] = x == y ? 1 : 0; // 公式: (x == y) なら白(1)、そうでなければ黒(0)
        return grid;
    }

    private static string Name((int x, int y) cell) => $"{(char)('a' + cell.x)}{cell.y + 1}";

    private static int Count(int[,] grid, int player)
    {
        int n = 0;
        foreach (int v in grid) if (v == player) n++;
        return n;
    }

    /// <summary>その側が打てるマス(行ごとの順)</summary>
    private static List<(int x, int y)> Actions(int[,] grid, int player)
    {
        var actions = new List<(int x, int y)>();
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                if (grid[y, x] < 0 && FlippedCount(grid, x, y, player) > 0) actions.Add((x, y));
        return actions;
    }

    private static int FlippedCount(int[,] grid, int x0, int y0, int player)
    {
        int total = 0;
        foreach ((int dx, int dy) in Directions)
        {
            int x = x0 + dx, y = y0 + dy, run = 0;
            while (x is >= 0 and < 8 && y is >= 0 and < 8 && grid[y, x] == 1 - player) { run++; x += dx; y += dy; }
            if (run > 0 && x is >= 0 and < 8 && y is >= 0 and < 8 && grid[y, x] == player) total += run;
        }
        return total;
    }

    private static void Apply(int[,] grid, (int x, int y) cell, int player)
    {
        foreach ((int dx, int dy) in Directions)
        {
            int x = cell.x + dx, y = cell.y + dy, run = 0;
            while (x is >= 0 and < 8 && y is >= 0 and < 8 && grid[y, x] == 1 - player) { run++; x += dx; y += dy; }
            if (run > 0 && x is >= 0 and < 8 && y is >= 0 and < 8 && grid[y, x] == player)
            {
                for (int i = 1; i <= run; i++) grid[cell.y + dy * i, cell.x + dx * i] = player;
            }
        }
        grid[cell.y, cell.x] = player;
    }

    // ---- 引数 ----

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string[] SplitArgs(string? text)
        => string.IsNullOrWhiteSpace(text) ? Array.Empty<string>() : text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>ボットにそのまま渡す引数(評価ファイルの指定)</summary>
    private static string[] PassThroughArgs(string[] args)
    {
        string? weights = ValueOf(args, "--weights");
        return weights == null ? Array.Empty<string>() : new[] { "--weights", weights };
    }
}

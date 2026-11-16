using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

/// <summary>
/// MPC(Multi-ProbCut)の誤差を集める測定(--mpc-stats、設計書 Phase 7)。
/// WTHOR の実戦の局面を、近似(MPC)なしで深さ1〜最大まで反復深化し、
/// 各深さのルートの値から「浅い読み(深さ d)の値と、深い読み(深さ D)の値の差」を、空きマスの段階ごとに集める。
/// MPC は、浅い読みの値から深い読みの値を見込んで枝を切るので、この差の散らばり(標準偏差)が、切るときの余裕の元になる。
///
/// 例: --mpc-stats --wthor ../external/wthor/WTH_2020.wtb --empties 20,24,28,32,36,40,44,48,52 --positions 40 --max-depth 12
///     --out mpc-stats.csv  (結果を CSV にも書く)
/// 局面ごとに独立して読むので、CPU のコアを並べて使う(思考時間は測らない)。
/// </summary>
public static class MpcStats
{
    public static void Run(string[] args)
    {
        string wthorPath = ValueOf(args, "--wthor") ?? "../external/wthor/WTH_2020.wtb";
        int[] emptiesList = (ValueOf(args, "--empties") ?? "20,24,28,32,36,40,44,48,52").Split(',').Select(int.Parse).ToArray();
        int positions = int.Parse(ValueOf(args, "--positions") ?? "40");
        int maxDepth = int.Parse(ValueOf(args, "--max-depth") ?? "12");
        int threads = int.Parse(ValueOf(args, "--threads") ?? Math.Max(1, Environment.ProcessorCount - 2).ToString());
        string? outPath = ValueOf(args, "--out");

        PatternEvaluator evaluator = EvaluatorFile.LoadFromArgs(args, out string description);
        Console.WriteLine($"評価関数: {description}");
        Console.WriteLine($"棋譜: {wthorPath}  空きマス {string.Join(",", emptiesList)}  各{positions}局面  深さ1〜{maxDepth}  {threads}スレッド");

        // (空きマス, 局面) の組を作る。1局から1局面だけ取る(同じ局の続きの局面ばかりにならないように)
        var jobs = new List<(int empties, BitBoard board)>();
        foreach (int empties in emptiesList)
        {
            var seen = new HashSet<(ulong, ulong)>();
            int count = 0;
            foreach (TrainingSample sample in TrainingData.LoadWthor(wthorPath, minEmpties: empties))
            {
                if (TrainingData.EmptyCount(sample) != empties) continue;
                var board = new BitBoard(sample.Player, sample.Opponent);
                if (board.GetMoves() == 0 || !seen.Add((board.Player, board.Opponent))) continue;
                jobs.Add((empties, board));
                if (++count >= positions) break;
            }
        }

        // 結果: 空きマス → 局面ごとの [深さ0(評価関数そのもの), 深さ1, …, maxDepth] の値
        var results = new ConcurrentBag<(int empties, int[] values)>();
        var stopwatch = Stopwatch.StartNew();
        int done = 0;
        OthelloDebugLog.SuppressOutput = true;
        try
        {
            var aiPool = new ThreadLocal<OthelloAI>(() => new OthelloAI(evaluator));
            Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = threads }, job =>
            {
                OthelloAI ai = aiPool.Value!;
                int[] values = ai.RootValuesByDepth(job.board, maxDepth);
                values[0] = ai.Evaluate(job.board);
                // 終局まで読み切った深さがある局面は、評価値の誤差にならないので使わない
                if (!values.Skip(1).Any(OthelloAI.IsFinalScore)) results.Add((job.empties, values));
                int finished = Interlocked.Increment(ref done);
                if (finished % 20 == 0)
                    Console.Error.WriteLine($"  {finished}/{jobs.Count}局面  {stopwatch.Elapsed.TotalMinutes:F1}分");
            });
        }
        finally
        {
            OthelloDebugLog.SuppressOutput = false;
        }

        // 段階・浅い深さ d・深い深さ D ごとに、差(深い − 浅い)の平均と標準偏差を出す(値は石数。評価値 ÷ Scale)
        var csv = new StringBuilder("empties,shallow,deep,n,mean,sd\n");
        Console.WriteLine();
        Console.WriteLine("差 = 深い読みの値 − 浅い読みの値(石数)。浅い深さは MPC で使う「深さの半分ほど・偶奇をそろえた」値");
        foreach (int empties in emptiesList)
        {
            var group = results.Where(r => r.empties == empties).Select(r => r.values).ToList();
            Console.WriteLine($"残り{empties}マス({group.Count}局面)");
            for (int deep = 1; deep <= maxDepth; deep++)
            {
                var line = new StringBuilder($"  深さ{deep,2}:");
                for (int shallow = 0; shallow < deep; shallow++)
                {
                    double[] diffs = group.Select(v => (v[deep] - v[shallow]) / (double)PatternEvaluator.Scale).ToArray();
                    if (diffs.Length < 2) continue;
                    double mean = diffs.Average();
                    double sd = Math.Sqrt(diffs.Sum(x => (x - mean) * (x - mean)) / (diffs.Length - 1));
                    csv.AppendLine($"{empties},{shallow},{deep},{diffs.Length},{mean:F3},{sd:F3}");
                    if (shallow == OthelloAI.MpcShallowDepth(deep) || shallow == 0)
                        line.Append($"  d{shallow}: 平均{mean,6:F2} 標準偏差{sd,5:F2}");
                }
                Console.WriteLine(line);
            }
        }
        Console.WriteLine($"時間: {stopwatch.Elapsed.TotalMinutes:F1}分");
        if (outPath != null)
        {
            File.WriteAllText(outPath, csv.ToString());
            Console.WriteLine($"CSV: {outPath}");
        }
    }

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

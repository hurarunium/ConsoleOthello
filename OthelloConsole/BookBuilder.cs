using System.Diagnostics;
using System.Numerics;

/// <summary>
/// 定石を作る・確かめる開発用のコマンド(企画 P-05、設計書 Phase 4)。
///
/// --build-book: 8手目の局面(約6.7万)を1つずつ深く読んで評価値を出し、ミニマックスで0〜7手目の最善手を決めて、定石のファイルを作る。
///   --depth N        8手目の局面を何手読むか(標準 12)
///   --threads N      並べて読む数(標準 すべてのコア)
///   --out パス       出力先(標準 weights/opening_book.bin)
///   --measure N      N局面(8手目から等間隔に選ぶ)を、深さ 8・10・12・14 で読んで、1局面あたりの時間を出して終わる
///   読んだ値は、1分ごとに途中経過のファイル(出力先と同じフォルダの opening_book_leaves_d{深さ}.bin)へ書く。
///   止めても(Ctrl+C でも)、同じ深さで動かし直すと、続きから読む。
/// --book-check: 定石のファイルを確かめる(局面の数、初期局面から7手目までの全局面が載っていること、返す手がすべて合法手であること、対称の変換)。
///   --book パス      確かめるファイル(標準 weights/opening_book.bin)
/// </summary>
public static class BookBuilder
{
    private const int Unset = int.MinValue;
    private const int LeafPly = OpeningBook.BookPlies; // 8手目の局面が、評価する葉

    public static void Run(string[] args)
    {
        OthelloDebugLog.SuppressOutput = true;
        try
        {
            if (args.Contains("--book-check")) Check(args);
            else Build(args);
        }
        finally
        {
            OthelloDebugLog.SuppressOutput = false;
        }
    }

    // ---- 作る ----

    private static void Build(string[] args)
    {
        int depth = int.Parse(ValueOf(args, "--depth") ?? "12");
        int threads = int.Parse(ValueOf(args, "--threads") ?? Environment.ProcessorCount.ToString());
        string outPath = ValueOf(args, "--out") ?? OpeningBook.DefaultPath;
        PatternEvaluator evaluator = EvaluatorFile.LoadFromArgs(args, out string description);
        OrderingEvaluator? ordering = OrderingEvaluator.LoadFromArgs(args, out string orderingDescription);
        Console.WriteLine($"評価関数: {description}  並べ替え用の評価: {orderingDescription}");

        var stopwatch = Stopwatch.StartNew();
        var levels = OpeningBook.EnumerateLevels(LeafPly);
        Console.WriteLine($"局面の数(手数ごと): {string.Join("、", levels.Select((l, ply) => $"{ply}手目 {l.Length}"))}  数え上げ {stopwatch.ElapsedMilliseconds}ms");
        var leaves = levels[LeafPly];

        string? measure = ValueOf(args, "--measure");
        if (measure != null)
        {
            Measure(evaluator, ordering, leaves, int.Parse(measure));
            return;
        }

        string checkpointPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, $"opening_book_leaves_d{depth}.bin");
        int[] values = LoadCheckpoint(checkpointPath, depth, leaves) ?? NewValues(leaves.Length);
        int[] pending = Enumerable.Range(0, leaves.Length).Where(i => values[i] == Unset).ToArray();
        Console.WriteLine($"8手目の局面 {leaves.Length}、深さ {depth}、並列 {threads}、読み済み {leaves.Length - pending.Length}、これから {pending.Length}  途中経過: {checkpointPath}");

        bool cancelled = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled = true; Console.WriteLine("止めています(途中経過を書いて終わります)..."); };

        if (pending.Length > 0) EvaluateLeaves(evaluator, ordering, leaves, values, pending, depth, threads, checkpointPath, () => cancelled);
        SaveCheckpoint(checkpointPath, depth, leaves, values);
        if (cancelled || values.Any(v => v == Unset))
        {
            Console.WriteLine($"途中です(読み済み {values.Count(v => v != Unset)} / {leaves.Length})。同じ引数で動かし直すと、続きから読みます。");
            return;
        }

        OpeningBook book = MinimaxBook(levels, values, out int rootValue, out int rootMove);
        book.Save(outPath);
        Console.WriteLine($"定石を書きました: {outPath}  {book.PositionCount}局面  初期局面の評価値(先手から) {rootValue / (double)PatternEvaluator.Scale:F2}石  最善手 {BitBoard.BitToCoord(rootMove)}  (合計 {stopwatch.Elapsed:hh\\:mm\\:ss})");
    }

    /// <summary>8手目の局面の評価値を、並べて読む。途中経過を1分ごとに書く</summary>
    private static void EvaluateLeaves(PatternEvaluator evaluator, OrderingEvaluator? ordering, (ulong player, ulong opponent)[] leaves, int[] values,
                                       int[] pending, int depth, int threads, string checkpointPath, Func<bool> isCancelled)
    {
        var stopwatch = Stopwatch.StartNew();
        long done = 0;
        long nextSaveMs = 60_000;
        object sync = new();
        var options = new ParallelOptions { MaxDegreeOfParallelism = threads };
        Parallel.For(0, pending.Length, options,
            () => new OthelloAI(evaluator, 21) { Ordering = ordering },
            (k, state, ai) =>
            {
                if (isCancelled()) { state.Stop(); return ai; }
                int leaf = pending[k];
                values[leaf] = EvaluateLeaf(ai, leaves[leaf], depth);
                long finished = Interlocked.Increment(ref done);
                if (stopwatch.ElapsedMilliseconds >= Volatile.Read(ref nextSaveMs))
                {
                    lock (sync)
                    {
                        if (stopwatch.ElapsedMilliseconds >= nextSaveMs)
                        {
                            nextSaveMs = stopwatch.ElapsedMilliseconds + 60_000;
                            SaveCheckpoint(checkpointPath, depth, leaves, values);
                            double perLeafMs = stopwatch.ElapsedMilliseconds / (double)finished;
                            TimeSpan eta = TimeSpan.FromMilliseconds(perLeafMs * (pending.Length - finished));
                            Console.WriteLine($"  {finished} / {pending.Length}  経過 {stopwatch.Elapsed:hh\\:mm\\:ss}  残り約 {eta:hh\\:mm\\:ss}  (1局面あたり 並列で {perLeafMs:F0}ms)");
                        }
                    }
                }
                return ai;
            },
            _ => { });
        Console.WriteLine($"8手目の局面の読みが終わりました(今回 {done} 局面、{stopwatch.Elapsed:hh\\:mm\\:ss})");
    }

    /// <summary>
    /// 手番の側から見た、その局面を depth 手読んだ評価値(中盤の読み。近似なし)。8手目の局面は残り52マスで、終局には届かない
    /// </summary>
    private static int EvaluateLeaf(OthelloAI ai, (ulong player, ulong opponent) position, int depth)
        => ai.RootValuesByDepth(new BitBoard(position.player, position.opponent), depth)[depth];

    /// <summary>8手目の局面から後ろ向きに、各局面で「子の局面の評価値(相手から見たもの)を反転して最大の手」を選ぶ</summary>
    private static OpeningBook MinimaxBook((ulong player, ulong opponent)[][] levels, int[] leafValues, out int rootValue, out int rootMove)
    {
        var nodeValues = new int[LeafPly + 1][];
        nodeValues[LeafPly] = leafValues;
        var moves = new byte[LeafPly][];
        for (int ply = LeafPly - 1; ply >= 0; ply--)
        {
            var level = levels[ply];
            nodeValues[ply] = new int[level.Length];
            moves[ply] = new byte[level.Length];
            for (int i = 0; i < level.Length; i++)
            {
                var board = new BitBoard(level[i].player, level[i].opponent);
                ulong movesBit = board.GetMoves();
                int best = int.MinValue, bestMove = -1;
                while (movesBit != 0)
                {
                    int move = BitOperations.TrailingZeroCount(movesBit);
                    movesBit &= movesBit - 1;
                    int child = OpeningBook.ChildIndex(levels[ply + 1], level[i], move);
                    if (child < 0) throw new InvalidOperationException($"{ply}手目の局面の子が、{ply + 1}手目の一覧にありません。");
                    int value = -nodeValues[ply + 1][child];
                    if (value > best) { best = value; bestMove = move; }
                }
                if (bestMove < 0) throw new InvalidOperationException($"{ply}手目の局面に打てる手がありません。");
                nodeValues[ply][i] = best;
                moves[ply][i] = (byte)bestMove;
            }
        }
        rootValue = nodeValues[0][0];
        rootMove = moves[0][0];
        return OpeningBook.Create(levels, moves);
    }

    // ---- 時間の測定 ----

    /// <summary>8手目の局面から等間隔に count 個を選び、深さごとに1局面あたりの時間を測る(1つずつ・1スレッド)</summary>
    private static void Measure(PatternEvaluator evaluator, OrderingEvaluator? ordering, (ulong player, ulong opponent)[] leaves, int count)
    {
        var ai = new OthelloAI(evaluator, 21) { Ordering = ordering };
        int step = Math.Max(1, leaves.Length / count);
        var sample = Enumerable.Range(0, count).Select(i => leaves[Math.Min(leaves.Length - 1, i * step)]).ToArray();
        EvaluateLeaf(ai, sample[0], 8); // 暖機
        Console.WriteLine($"8手目の局面 {count} 個の、1局面あたりの時間(1スレッド)");
        Console.WriteLine("深さ | 平均ms | 最大ms | 全局面(6.7万)を1スレッドで読む時間の見込み");
        foreach (int depth in new[] { 8, 10, 12, 14, 16 })
        {
            double sum = 0, max = 0;
            foreach (var position in sample)
            {
                long start = Stopwatch.GetTimestamp();
                EvaluateLeaf(ai, position, depth);
                double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                sum += ms; max = Math.Max(max, ms);
                if (sum > 120_000) break; // 1つの深さで2分を超えたら打ち切る
            }
            double average = sum / count;
            Console.WriteLine($"{depth,4} | {average,6:F0} | {max,6:F0} | {TimeSpan.FromMilliseconds(average * leaves.Length):hh\\:mm\\:ss}");
        }
    }

    // ---- 途中経過のファイル ----

    private static void SaveCheckpoint(string path, int depth, (ulong player, ulong opponent)[] leaves, int[] values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        using (var stream = File.Create(temp))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("OBL1"u8.ToArray());
            writer.Write(depth);
            writer.Write(leaves.Length);
            writer.Write(LeavesChecksum(leaves));
            foreach (int value in values) writer.Write(value);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static int[]? LoadCheckpoint(string path, int depth, (ulong player, ulong opponent)[] leaves)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("OBL1"u8) || reader.ReadInt32() != depth || reader.ReadInt32() != leaves.Length
            || reader.ReadUInt64() != LeavesChecksum(leaves))
        {
            Console.WriteLine($"途中経過のファイル {path} は、深さか局面の並びが合わないので使いません。");
            return null;
        }
        var values = new int[leaves.Length];
        for (int i = 0; i < values.Length; i++) values[i] = reader.ReadInt32();
        return values;
    }

    private static int[] NewValues(int count)
    {
        var values = new int[count];
        Array.Fill(values, Unset);
        return values;
    }

    private static ulong LeavesChecksum((ulong player, ulong opponent)[] leaves)
    {
        ulong hash = 14695981039346656037UL;
        foreach ((ulong player, ulong opponent) in leaves)
        {
            hash = (hash ^ player) * 1099511628211UL;
            hash = (hash ^ opponent) * 1099511628211UL;
        }
        return hash;
    }

    // ---- 確かめる ----

    private static void Check(string[] args)
    {
        string path = ValueOf(args, "--book") ?? OpeningBook.FindDefaultPath();
        int failures = 0;

        // 対称の変換が、1マスずつ動かした結果と同じか
        var random = new Random(3);
        int transformMismatches = 0;
        for (int i = 0; i < 2000; i++)
        {
            ulong bits = ((ulong)random.NextInt64() << 1) ^ (ulong)random.NextInt64();
            for (int t = 0; t < OpeningBook.SymmetryCount; t++)
                if (OpeningBook.Transform(t, bits) != NaiveTransform(t, bits) || OpeningBook.Transform(t, OpeningBook.Transform(t, bits)) != bits) transformMismatches++;
        }
        Console.WriteLine($"対称の変換(2000個の盤面×4種類): 食い違い {transformMismatches} {(transformMismatches == 0 ? "[OK]" : "[NG]")}");
        if (transformMismatches != 0) failures++;

        OpeningBook book;
        try { book = OpeningBook.Load(path); }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or FileNotFoundException)
        {
            Console.WriteLine($"[NG] 定石を読めません: {path}: {e.Message}");
            Environment.ExitCode = 1;
            return;
        }
        var levels = OpeningBook.EnumerateLevels(OpeningBook.BookPlies - 1);
        Console.WriteLine($"定石: {path}  手を持つ局面 {book.PositionCount}(数え上げた局面の合計 {levels.Sum(l => l.Length)})  {(book.PositionCount == levels.Sum(l => l.Length) ? "[OK]" : "[NG]")}");
        if (book.PositionCount != levels.Sum(l => l.Length)) failures++;

        // 初期局面から7手目まで、元の向きのまま全局面を数えて、すべて定石で手が引け、その手が合法手か
        var current = new HashSet<(ulong, ulong)> { OpeningBook.InitialPosition };
        for (int ply = 0; ply < OpeningBook.BookPlies; ply++)
        {
            int missing = 0;
            var next = new HashSet<(ulong, ulong)>();
            foreach ((ulong player, ulong opponent) in current)
            {
                var board = new BitBoard(player, opponent);
                if (!book.TryGetMove(board, out int move)) { missing++; continue; }
                BitBoard after = board.DoMove(move);
                _ = after;
                ulong moves = board.GetMoves();
                while (moves != 0)
                {
                    int m = BitOperations.TrailingZeroCount(moves);
                    moves &= moves - 1;
                    BitBoard child = board.DoMove(m);
                    next.Add((child.Player, child.Opponent));
                }
            }
            Console.WriteLine($"  {ply}手目: 元の向きの局面 {current.Count}  定石で手を引けなかった局面 {missing} {(missing == 0 ? "[OK]" : "[NG]")}");
            if (missing != 0) failures++;
            current = next;
        }

        Console.WriteLine(failures == 0 ? "[OK] 定石は、0〜7手目のすべての局面で、合法手を返す" : $"[NG] 食い違い {failures} 件");
        if (failures != 0) Environment.ExitCode = 1;
    }

    /// <summary>1マスずつ動かして、対称を掛ける(ビット位置 = y*8+x)。確かめ用</summary>
    private static ulong NaiveTransform(int t, ulong bits)
    {
        ulong result = 0;
        for (int i = 0; i < 64; i++)
        {
            if (((bits >> i) & 1UL) == 0) continue;
            int x = i % 8, y = i / 8;
            (int nx, int ny) = t switch
            {
                0 => (x, y),
                1 => (y, x),
                2 => (7 - x, 7 - y),
                _ => (7 - y, 7 - x),
            };
            result |= 1UL << (ny * 8 + nx);
        }
        return result;
    }

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

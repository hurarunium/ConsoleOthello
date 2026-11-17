using System.Buffers.Binary;
using System.Numerics;

/// <summary>
/// 序盤の定石(企画 P-05、設計書 Phase 4)。初期局面から0〜7手目の全局面(8手目は評価用)に、最善手を1つずつ持つ。
///
/// - 局面は、初期局面を変えない4つの対称(そのまま・斜めの反転・180度の回転・もう一方の斜めの反転)で同じ局面にまとめる。
///   まとめた局面(Player, Opponent の組が最小のもの)を「正規の向き」と呼ぶ。
/// - 局面の一覧は、初期局面から0〜7手目まで全部の手を数え上げ、正規の向きにして並べ替えて作る(起動のたびに数え直す。数千〜1万局面)。
///   ファイルには局面を持たず、その並び順に、最善手(正規の向きでのビット位置、1バイト)だけを持つ。
///   数え上げの結果が変わると食い違うので、ファイルには局面の並びの検査値も入れる。
/// - 引くときは、いまの盤面を正規の向きにし、並びを二分探索して手を取り、元の向きに戻す。
/// 定石には黒・白の区別がない(手番の側が Player)。最初の8手にパスはないので、手数 = 60 - 空きマスで引ける。
/// </summary>
public sealed class OpeningBook
{
    /// <summary>手を持つ局面の手数(0〜7手目)。8手目の局面は、定石を作るときの評価にだけ使う</summary>
    public const int BookPlies = 8;

    public const string FileName = "opening_book.bin";
    /// <summary>作るときの標準の出力先(今いるフォルダの weights/)</summary>
    public static string DefaultPath => Path.Combine("weights", FileName);

    /// <summary>定石ファイルの場所。exeの隣の weights/ を先に探し、なければ今いるフォルダの weights/(評価ファイルと同じ探し方)</summary>
    public static string FindDefaultPath()
    {
        string besideExe = Path.Combine(AppContext.BaseDirectory, "weights", FileName);
        return File.Exists(besideExe) ? besideExe : DefaultPath;
    }

    private static readonly byte[] s_magic = "OBK1"u8.ToArray();

    private readonly (ulong player, ulong opponent)[][] _keys;
    private readonly byte[][] _moves;

    private OpeningBook((ulong, ulong)[][] keys, byte[][] moves)
    {
        _keys = keys;
        _moves = moves;
    }

    /// <summary>手を持つ局面の数(0〜7手目の合計)</summary>
    public int PositionCount => _keys.Take(BookPlies).Sum(level => level.Length);

    // ---- 対称 ----

    /// <summary>初期局面を変えない4つの対称。0: そのまま、1: 斜めの反転(x と y を入れ替える)、2: 180度の回転、3: もう一方の斜めの反転</summary>
    public const int SymmetryCount = 4;

    /// <summary>盤面(ビット列)に対称 t を掛ける。どの対称も、2回掛けると元に戻る(戻すときも同じ t を掛ける)</summary>
    public static ulong Transform(int t, ulong bits) => t switch
    {
        0 => bits,
        1 => FlipDiagonal(bits),
        2 => Reverse(bits),
        _ => Reverse(FlipDiagonal(bits)),
    };

    /// <summary>x と y を入れ替える(ビット位置 = y*8+x なので、盤の主対角線での反転)</summary>
    internal static ulong FlipDiagonal(ulong x)
    {
        const ulong k1 = 0x5500550055005500UL, k2 = 0x3333000033330000UL, k4 = 0x0f0f0f0f00000000UL;
        ulong t = k4 & (x ^ (x << 28));
        x ^= t ^ (t >> 28);
        t = k2 & (x ^ (x << 14));
        x ^= t ^ (t >> 14);
        t = k1 & (x ^ (x << 7));
        x ^= t ^ (t >> 7);
        return x;
    }

    /// <summary>ビット位置 i を 63-i に移す(盤の180度の回転)</summary>
    internal static ulong Reverse(ulong x)
    {
        x = ((x >> 1) & 0x5555555555555555UL) | ((x & 0x5555555555555555UL) << 1);
        x = ((x >> 2) & 0x3333333333333333UL) | ((x & 0x3333333333333333UL) << 2);
        x = ((x >> 4) & 0x0F0F0F0F0F0F0F0FUL) | ((x & 0x0F0F0F0F0F0F0F0FUL) << 4);
        return BinaryPrimitives.ReverseEndianness(x);
    }

    /// <summary>4つの対称のうち、(Player, Opponent) が最小になる向きにする。そのときの対称を t に返す</summary>
    public static (ulong player, ulong opponent) Canonical(ulong player, ulong opponent, out int t)
    {
        (ulong, ulong) best = (player, opponent);
        t = 0;
        for (int s = 1; s < SymmetryCount; s++)
        {
            var candidate = (Transform(s, player), Transform(s, opponent));
            if (candidate.CompareTo(best) < 0) { best = candidate; t = s; }
        }
        return best;
    }

    // ---- 局面の数え上げ ----

    /// <summary>黒が先手の初期局面(手番の側 = 黒が Player)</summary>
    public static (ulong player, ulong opponent) InitialPosition => ((1UL << 28) | (1UL << 35), (1UL << 27) | (1UL << 36));

    /// <summary>
    /// 初期局面から 0〜maxPly 手目までの全局面を、正規の向きにして数え上げる。手数ごとに、並べ替えた局面の配列を返す
    /// </summary>
    public static (ulong player, ulong opponent)[][] EnumerateLevels(int maxPly)
    {
        var levels = new (ulong, ulong)[maxPly + 1][];
        (ulong p0, ulong o0) = InitialPosition;
        levels[0] = new[] { Canonical(p0, o0, out _) };
        for (int ply = 1; ply <= maxPly; ply++)
        {
            var next = new HashSet<(ulong, ulong)>();
            foreach ((ulong player, ulong opponent) in levels[ply - 1])
            {
                var board = new BitBoard(player, opponent);
                ulong moves = board.GetMoves();
                while (moves != 0)
                {
                    int move = BitOperations.TrailingZeroCount(moves);
                    moves &= moves - 1;
                    BitBoard child = board.DoMove(move);
                    next.Add(Canonical(child.Player, child.Opponent, out _));
                }
            }
            var sorted = next.ToArray();
            Array.Sort(sorted);
            levels[ply] = sorted;
        }
        return levels;
    }

    /// <summary>手数 ply の正規の向きの局面 position から、手 move を打ったあとの局面の、次の手数の並びでの番号</summary>
    public static int ChildIndex((ulong player, ulong opponent)[] nextLevel, (ulong player, ulong opponent) position, int move)
    {
        BitBoard child = new BitBoard(position.player, position.opponent).DoMove(move);
        (ulong, ulong) key = Canonical(child.Player, child.Opponent, out _);
        return Array.BinarySearch(nextLevel, key);
    }

    /// <summary>局面の並びの検査値(FNV-1a)。数え上げの結果が、定石を作ったときと同じかを確かめる</summary>
    private static ulong Checksum((ulong player, ulong opponent)[][] levels)
    {
        ulong hash = 14695981039346656037UL;
        for (int ply = 0; ply < BookPlies; ply++)
        {
            foreach ((ulong player, ulong opponent) in levels[ply])
            {
                hash = (hash ^ player) * 1099511628211UL;
                hash = (hash ^ opponent) * 1099511628211UL;
            }
        }
        return hash;
    }

    // ---- 引く ----

    /// <summary>
    /// 盤面(手番の側から見たもの)の定石の手を探す。0〜7手目の局面で、載っていれば true。
    /// 返す手は元の向きでのビット位置で、必ず合法手(そうでなければ false)
    /// </summary>
    public bool TryGetMove(BitBoard board, out int move)
    {
        move = -1;
        int ply = 60 - board.EmptyCount;
        if (ply < 0 || ply >= BookPlies) return false;

        (ulong player, ulong opponent) key = Canonical(board.Player, board.Opponent, out int t);
        int index = Array.BinarySearch(_keys[ply], key);
        if (index < 0) return false;

        ulong actualMove = Transform(t, 1UL << _moves[ply][index]);
        if (BitOperations.PopCount(actualMove) != 1) return false;
        int candidate = BitOperations.TrailingZeroCount(actualMove);
        if (((board.GetMoves() >> candidate) & 1UL) == 0) return false;
        move = candidate;
        return true;
    }

    // ---- 作る・保存・読み込み ----

    /// <summary>
    /// 数え上げた局面の並びと、各局面の最善手(正規の向きでのビット位置。0〜7手目)から定石を作る
    /// </summary>
    public static OpeningBook Create((ulong, ulong)[][] levels, byte[][] moves) => new(levels, moves);

    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        using (var stream = File.Create(temp))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(s_magic);
            writer.Write(BookPlies);
            for (int ply = 0; ply < BookPlies; ply++) writer.Write(_keys[ply].Length);
            writer.Write(Checksum(_keys));
            for (int ply = 0; ply < BookPlies; ply++) writer.Write(_moves[ply]);
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// 定石のファイルを読む。局面は数え上げ直して、ファイルの局面の数と検査値が合うことを確かめる(合わなければ例外)
    /// </summary>
    public static OpeningBook Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        byte[] magic = reader.ReadBytes(4);
        if (!magic.AsSpan().SequenceEqual(s_magic)) throw new InvalidDataException("定石のファイルではありません。");
        int plies = reader.ReadInt32();
        if (plies != BookPlies) throw new InvalidDataException($"手数が合いません(ファイル {plies}、プログラム {BookPlies})。");
        var counts = new int[plies];
        for (int ply = 0; ply < plies; ply++) counts[ply] = reader.ReadInt32();
        ulong checksum = reader.ReadUInt64();

        var levels = EnumerateLevels(BookPlies - 1);
        for (int ply = 0; ply < plies; ply++)
        {
            if (levels[ply].Length != counts[ply]) throw new InvalidDataException($"{ply}手目の局面の数が合いません(ファイル {counts[ply]}、数え上げ {levels[ply].Length})。");
        }
        if (Checksum(levels) != checksum) throw new InvalidDataException("局面の並びの検査値が合いません(数え上げの決まりが変わっています)。");

        var moves = new byte[plies][];
        for (int ply = 0; ply < plies; ply++)
        {
            moves[ply] = reader.ReadBytes(counts[ply]);
            if (moves[ply].Length != counts[ply]) throw new InvalidDataException($"{ply}手目の手のデータが途中で終わっています(必要 {counts[ply]}バイト、実際 {moves[ply].Length}バイト)。");
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("定石のファイルの末尾に、読まれないデータが残っています。");

        // 手のデータが壊れていても使い始める前に分かるよう、すべての局面で、手が合法手かを確かめる(数千局面なので一瞬)
        for (int ply = 0; ply < plies; ply++)
        {
            for (int i = 0; i < levels[ply].Length; i++)
            {
                int move = moves[ply][i];
                var board = new BitBoard(levels[ply][i].player, levels[ply][i].opponent);
                if (move >= 64 || ((board.GetMoves() >> move) & 1UL) == 0)
                    throw new InvalidDataException($"{ply}手目の{i}番目の局面の手({move})が、合法手ではありません。");
            }
        }
        return new OpeningBook(levels, moves);
    }

    /// <summary>起動の引数の --book &lt;パス&gt; か、標準の場所から読む。--no-book か、ファイルがなければ null(その理由を reason に入れる)</summary>
    public static OpeningBook? LoadFromArgs(string[] args, out string reason)
    {
        if (args.Contains("--no-book")) { reason = "--no-book"; return null; }
        int index = Array.IndexOf(args, "--book");
        string path = index >= 0 && index + 1 < args.Length ? args[index + 1] : FindDefaultPath();
        if (!File.Exists(path)) { reason = $"{path} がありません"; return null; }
        try
        {
            OpeningBook book = Load(path);
            reason = $"{path}({book.PositionCount}局面)";
            return book;
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            reason = $"{path} を読めません: {e.Message}";
            return null;
        }
    }
}

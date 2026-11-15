using System.IO.Compression;
using System.Numerics;
using System.Text.Json;

/// <summary>学習の1局面: 手番の側から見た盤面と、目標の値(手番から見た最終の石数の差)</summary>
public readonly record struct TrainingSample(ulong Player, ulong Opponent, float Target)
{
    public BitBoard Board => new(Player, Opponent);
}

/// <summary>
/// 教師データの読み込み(企画 P-03、設計書 Phase 6)。どれも「局面 + 目標の値」にそろえる。
/// - Egaroucid: 1行 = 64文字の盤面(a1, b1, …, h8 の順。X=手番の側、O=相手、-=空き) + 空白 + 評価値(手番から見た最終の石数の差)
/// - WTHOR: 人同士の大会の棋譜(.wtb)。残り24マス以上の局面に、24マス空きからの理論値をつける
/// - 展示の棋譜: JSONL(GameRecord)または f5d6 形式の .txt。最終の石数の差をつける
/// </summary>
public static class TrainingData
{
    /// <summary>
    /// Egaroucidの教師データを読む。path は zip・フォルダ・txt のどれでもよい。
    /// limit を超えたら読むのをやめる(0なら全部)
    /// </summary>
    public static IEnumerable<TrainingSample> LoadEgaroucid(string path, long limit = 0)
    {
        long count = 0;
        foreach (TextReader reader in OpenTextFiles(path))
        {
            using (reader)
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length < 66) continue;
                    yield return ParseEgaroucidLine(line);
                    if (limit > 0 && ++count >= limit) yield break;
                }
            }
        }
    }

    private static TrainingSample ParseEgaroucidLine(string line)
    {
        ulong player = 0, opponent = 0;
        for (int cell = 0; cell < 64; cell++)
        {
            char c = line[cell];
            if (c == 'X') player |= 1UL << cell;
            else if (c == 'O') opponent |= 1UL << cell;
            else if (c != '-') throw new InvalidDataException($"Egaroucidの教師データに読めない文字があります: {line}");
        }
        return new TrainingSample(player, opponent, int.Parse(line.AsSpan(65)));
    }

    /// <summary>zip の中の .txt、フォルダの中の .txt、1つの .txt を、名前の順に開く</summary>
    private static IEnumerable<TextReader> OpenTextFiles(string path)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using ZipArchive zip = ZipFile.OpenRead(path);
            foreach (ZipArchiveEntry entry in zip.Entries.Where(e => e.Name.EndsWith(".txt")).OrderBy(e => e.FullName))
                yield return new StreamReader(entry.Open());
        }
        else if (Directory.Exists(path))
        {
            foreach (string file in Directory.GetFiles(path, "*.txt", SearchOption.AllDirectories).OrderBy(f => f))
                yield return new StreamReader(file);
        }
        else
        {
            yield return new StreamReader(path);
        }
    }

    /// <summary>WTHORで目標の値をつける局面の、空きマスの数の下限(理論値は24マス空きで計算されている)</summary>
    public const int WthorMinEmpties = 24;

    /// <summary>
    /// WTHORの棋譜(.wtb)を読む。path はファイルかフォルダ。
    /// 形式: 16バイトのヘッダー(作成日・対局の数〈4バイト〉・…・盤の大きさ・理論値の深さ)のあと、1局68バイト
    /// (大会・黒・白の番号〈各2バイト〉、黒の石数、黒の理論的な石数、60手〈1手 = 行×10 + 列。0は終わり〉)
    /// </summary>
    /// minEmpties は、返す局面の空きマスの数の下限(学習では WthorMinEmpties。終盤の速さを測るときは小さくする)
    public static IEnumerable<TrainingSample> LoadWthor(string path, int minEmpties = WthorMinEmpties)
    {
        IEnumerable<string> files = Directory.Exists(path)
            ? Directory.GetFiles(path, "*.wtb").OrderBy(f => f)
            : new[] { path };
        foreach (string file in files)
        {
            byte[] bytes = File.ReadAllBytes(file);
            int gameCount = BitConverter.ToInt32(bytes, 4);
            int boardSize = bytes[12];
            if (boardSize != 8 && boardSize != 0)
                throw new InvalidDataException($"{file} は8×8の棋譜ではありません(盤の大きさ {boardSize})。");
            if (16 + gameCount * 68 > bytes.Length)
                throw new InvalidDataException($"{file} の長さが、ヘッダーの対局の数({gameCount})と合いません。");

            for (int g = 0; g < gameCount; g++)
            {
                int offset = 16 + g * 68;
                int theoreticalBlack = bytes[offset + 7];
                // 理論値は黒の石数(空きマスは勝った側に数える)。石数の差に直す
                float blackDiff = 2 * theoreticalBlack - 64;
                var moves = new List<int>();
                for (int m = 0; m < 60; m++)
                {
                    int code = bytes[offset + 8 + m];
                    if (code == 0) break;
                    int row = code / 10, col = code % 10;
                    if (row < 1 || row > 8 || col < 1 || col > 8)
                        throw new InvalidDataException($"{file} の{g + 1}局目に読めない手({code})があります。");
                    moves.Add((row - 1) * 8 + (col - 1));
                }
                foreach (TrainingSample sample in ReplayWithLabel(moves, blackDiff, minEmpties))
                    yield return sample;
            }
        }
    }

    /// <summary>
    /// 展示の棋譜(JSONL または f5d6 形式の .txt)を読み、最後まで打った局の各局面に、最終の石数の差をつける
    /// </summary>
    public static IEnumerable<TrainingSample> LoadGameRecords(string path)
    {
        bool isTranscript = path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string transcript;
            if (isTranscript)
            {
                transcript = line.Trim();
            }
            else
            {
                GameRecord? record = JsonSerializer.Deserialize<GameRecord>(line);
                if (record == null || record.Result == "aborted" || record.Winner == "") continue;
                List<(int row, int col)> played = record.Moves.Where(m => !m.IsPass).Select(m => (m.Row, m.Col)).ToList();
                if (record.Moves.Count > 0 && record.Moves[0].Color == "white") played = GameTranscript.ConvertWhiteFirst(played);
                transcript = GameTranscript.Format(played);
            }

            GameTranscript.ReplayResult replay;
            try { replay = GameTranscript.Replay(transcript); }
            catch (InvalidDataException) { continue; } // オセロとして成り立たない局は使わない
            if (!replay.IsFinished) continue;

            var (black, white) = OthelloSystem.Counting(replay.FinalTiles);
            List<int> moves = replay.Steps.Where(s => !s.IsPass).Select(s => (s.Row - 1) * 8 + (s.Col - 1)).ToList();
            foreach (TrainingSample sample in ReplayWithLabel(moves, black - white, minEmpties: 0))
                yield return sample;
        }
    }

    /// <summary>
    /// 手順(マスの番号)を初期局面から並べ直し、空きマスが minEmpties 以上の各局面(手番の側から見た盤面)に、
    /// 手番から見た目標の値をつけて返す。blackDiff は黒から見た最終の石数の差
    /// </summary>
    private static IEnumerable<TrainingSample> ReplayWithLabel(List<int> moves, float blackDiff, int minEmpties)
    {
        // 初期局面(黒の番): d5・e4 が黒、d4・e5 が白
        ulong black = (1UL << 28) | (1UL << 35);
        ulong white = (1UL << 27) | (1UL << 36);
        var board = new BitBoard(black, white);
        bool blackToMove = true;

        foreach (int move in moves)
        {
            if (board.GetMoves() == 0)
            {
                board = new BitBoard(board.Opponent, board.Player); // パス
                blackToMove = !blackToMove;
            }
            if ((board.GetMoves() & (1UL << move)) == 0) yield break; // 合法でない手が出たら、その局はそこまで

            if (board.EmptyCount >= minEmpties)
                yield return new TrainingSample(board.Player, board.Opponent, blackToMove ? blackDiff : -blackDiff);

            board = board.DoMove(move);
            blackToMove = !blackToMove;
        }
    }

    /// <summary>空きマスの数(学習の段階分けと同じ数え方)</summary>
    public static int EmptyCount(TrainingSample sample) => 64 - BitOperations.PopCount(sample.Player | sample.Opponent);
}

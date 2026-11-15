using System.Text;
using System.Text.Json;
using static OthelloSystem;

/// <summary>
/// 棋譜(JSONL)を、f5d6形式(1行1局)に変換する(--convert-records、企画 P-09)。
/// 最後まで打った局だけを変換する。白が先手で始まった局は、黒が先手の手順に直してから書く。
/// 変換した各局は並べ直して、最後まで打てることと、最後の石数が元の棋譜と合うことを確かめる。
/// 合法でない手を含むなど、オセロとして成り立たない局は変換せず、行番号と理由を表示する。
///
/// 例: --convert-records Data/collected/game_records.jsonl
///     --convert-records Data/collected/game_records.jsonl --out Data/collected/game_records.txt
/// </summary>
public static class ConvertRecordsMode
{
    public static void Run(string[] args)
    {
        int index = Array.IndexOf(args, "--convert-records");
        if (index < 0 || index + 1 >= args.Length)
            throw new ArgumentException("--convert-records のあとに棋譜ファイル(JSONL)を指定してください。");
        string inputPath = args[index + 1];
        int outIndex = Array.IndexOf(args, "--out");
        string outputPath = outIndex >= 0 && outIndex + 1 < args.Length
            ? args[outIndex + 1]
            : Path.ChangeExtension(inputPath, ".txt");

        var lines = new List<string>();
        int skipped = 0, convertedFromWhiteFirst = 0, lineNumber = 0;
        var invalidGames = new List<string>();
        foreach (string line in File.ReadLines(inputPath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            GameRecord record = JsonSerializer.Deserialize<GameRecord>(line)
                ?? throw new InvalidDataException($"{lineNumber}行目を読めませんでした。");

            // 途中でやめた局(結果がない局)は、手順が途中で終わるので変換しない
            if (record.Result == "aborted" || record.Winner == "")
            {
                skipped++;
                continue;
            }

            bool isWhiteFirst = record.Moves.Count > 0 && record.Moves[0].Color == "white";
            List<(int row, int col)> moves = record.Moves.Where(m => !m.IsPass).Select(m => (m.Row, m.Col)).ToList();
            if (isWhiteFirst)
            {
                moves = GameTranscript.ConvertWhiteFirst(moves);
                convertedFromWhiteFirst++;
            }

            string? problem = FindProblem(record, moves, isWhiteFirst);
            if (problem != null)
            {
                invalidGames.Add($"{lineNumber}行目: {problem}");
                if (isWhiteFirst) convertedFromWhiteFirst--;
                continue;
            }
            lines.Add(GameTranscript.Format(moves));
        }

        File.WriteAllLines(outputPath, lines, new UTF8Encoding(false));
        Console.WriteLine($"{lines.Count}局を変換しました(うち白が先手から直した局 {convertedFromWhiteFirst}局)。"
                        + $"途中でやめた局 {skipped}局は変換していません。");
        if (invalidGames.Count > 0)
        {
            Console.WriteLine($"オセロとして成り立たない局 {invalidGames.Count}局は変換していません:");
            foreach (string invalidGame in invalidGames) Console.WriteLine($"  {invalidGame}");
        }
        Console.WriteLine($"出力: {Path.GetFullPath(outputPath)}");
    }

    /// <summary>
    /// 変換した手順を並べ直し、最後まで打てることと、最後の石数が元の棋譜と合うことを確かめる。
    /// 白が先手から直した局は、黒と白の石数が入れ替わる。問題がなければ null、あればその理由を返す。
    /// </summary>
    private static string? FindProblem(GameRecord record, List<(int row, int col)> moves, bool isWhiteFirst)
    {
        GameTranscript.ReplayResult replay;
        try
        {
            replay = GameTranscript.Replay(moves);
        }
        catch (InvalidDataException exception)
        {
            return exception.Message;
        }
        if (!replay.IsFinished)
            return "変換した手順では対局が終わりません。";

        var (black, white) = Counting(replay.FinalTiles);
        (int expectedBlack, int expectedWhite) = isWhiteFirst
            ? (record.WhiteCount, record.BlackCount)
            : (record.BlackCount, record.WhiteCount);
        if (black != expectedBlack || white != expectedWhite)
            return $"最後の石数が元の棋譜と合いません(変換後 黒{black}・白{white}、期待 黒{expectedBlack}・白{expectedWhite})。";
        return null;
    }
}

using System.Diagnostics;
using System.Globalization;

/// <summary>
/// CodinGameのオセロ(othello-1)と同じ、標準入力・標準出力のやり取りで動くモード(--codingame。企画 P-04、設計書 Phase 3)。
/// 公式の審判(MultiStruct/Othello の Referee.java)の入出力に合わせている。
///
/// 入力:
///   最初の1回だけ: 自分のid(0が黒・1が白)、盤の大きさ(8)
///   毎ターン: 盤面8行('0'=黒、'1'=白、'.'=空き。上の行から)、合法手の数、合法手(例: d3)
///   打てないときは、そのターンの入力は来ない(審判がパスにする)
/// 出力: 打つマス(例: d3)の1行。「d3 MSG 文字列」でメッセージも出せる。盤面の情報は毎ターン来るので、EXPERT は使わない。
/// 時間: 最初の1手は2000ms、2手目からは150ms。超えるとその場で負け。
///
/// 標準出力には手以外を出さない(ログは出さない)。持ち時間は、手元で測った余裕を引いた値(--turn-budget-ms、--first-budget-ms)。
/// 例: dotnet run -c Release -- --codingame --turn-budget-ms 100
/// </summary>
public static class CodingameBot
{
    public static void Run(string[] args)
    {
        double turnBudgetMs = double.Parse(ValueOf(args, "--turn-budget-ms") ?? "100", CultureInfo.InvariantCulture);
        double firstBudgetMs = double.Parse(ValueOf(args, "--first-budget-ms") ?? "1000", CultureInfo.InvariantCulture);
        int ttBits = int.Parse(ValueOf(args, "--tt-bits") ?? "20");

        OthelloDebugLog.SuppressOutput = true;
        PatternEvaluator evaluator = EvaluatorFile.LoadFromArgs(args, out _);
        OpeningBook? book = OpeningBook.LoadFromArgs(args, out string bookDescription);
        Console.Error.WriteLine($"定石: {(book != null ? bookDescription : $"なし({bookDescription})")}");
        var ai = new OthelloAI(evaluator, ttBits) { Book = book };

        TextReader input = Console.In;
        TextWriter output = Console.Out;
        int myId = -1;
        bool firstTurn = true;

        while (true)
        {
            string? line = input.ReadLine();
            if (line == null) return; // 対局が終わった
            long turnStart = Stopwatch.GetTimestamp();

            if (myId < 0)
            {
                myId = int.Parse(line.Trim());
                _ = int.Parse(ReadRequired(input).Trim()); // 盤の大きさ(8)
                line = ReadRequired(input);
            }

            var rows = new string[8];
            rows[0] = line;
            for (int y = 1; y < 8; y++) rows[y] = ReadRequired(input);
            int actionCount = int.Parse(ReadRequired(input).Trim());
            var legalActions = new List<string>(actionCount);
            for (int i = 0; i < actionCount; i++) legalActions.Add(ReadRequired(input).Trim());

            BitBoard board = ParseBoard(rows, myId);

            // 最初の1手の時計は、プロセスを起動したときから数える(起動と評価の読み込みも含めて守る)
            double spentMs = firstTurn
                ? (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds
                : Stopwatch.GetElapsedTime(turnStart).TotalMilliseconds;
            double limitMs = firstTurn ? firstBudgetMs : turnBudgetMs;
            bool isFirstTurn = firstTurn;
            firstTurn = false;

            TimedMoveResult result = ai.ChooseMoveTimed(board, TimeSpan.FromMilliseconds(Math.Max(1.0, limitMs - spentMs)));

            // 最初の1手が定石で決まったときは、2000msの残りでJITの暖機をする(最初の探索が、暖機なしの150msで始まらないように)
            if (isFirstTurn && result.FromBook && !args.Contains("--no-warmup"))
            {
                double leftMs = firstBudgetMs - (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds - 250;
                if (leftMs > 50) ai.WarmUp(TimeSpan.FromMilliseconds(Math.Min(600, leftMs)));
            }

            string move = result.Move >= 0 ? ActionName(result.Move) : "";
            if (!legalActions.Contains(move))
            {
                // 起きない想定。審判の合法手と食い違ったら、不正な手で負けないよう、合法手の先頭を打つ
                Console.Error.WriteLine($"合法手と食い違い: 選んだ手={move} 合法手={string.Join(",", legalActions)}");
                move = legalActions[0];
            }
            output.WriteLine($"{move} MSG {(result.FromBook ? "book" : $"d{result.CompletedDepth}")}");
            output.Flush();
        }
    }

    /// <summary>盤面8行から、手番の側(自分)を Player とする BitBoard を作る</summary>
    internal static BitBoard ParseBoard(string[] rows, int myId)
    {
        ulong player = 0, opponent = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                char c = rows[y][x];
                if (c == '.') continue;
                ulong bit = 1UL << (y * 8 + x);
                if (c - '0' == myId) player |= bit; else opponent |= bit;
            }
        }
        return new BitBoard(player, opponent);
    }

    /// <summary>ビット位置を、CodinGameの手の表記(列を a〜h、行を 1〜8)にする。例: 19 → d3</summary>
    internal static string ActionName(int pos) => $"{(char)('a' + pos % 8)}{pos / 8 + 1}";

    private static string ReadRequired(TextReader input) => input.ReadLine() ?? throw new EndOfStreamException("入力が途中で終わった");

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

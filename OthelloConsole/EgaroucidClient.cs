using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Egaroucid for Console を子プロセスとして起動し、盤面を渡して手を受け取る。
/// GTPではなく、Egaroucidの通常のコマンド(setboard / go)を -quiet で使う。
/// 毎手 setboard で盤面を丸ごと送るため、パスや手順の食い違いでずれることがない。
/// </summary>
public sealed partial class EgaroucidClient : IDisposable
{
    /// <summary>1手を待つ上限。Egaroucidが止まったときにベンチ全体が固まらないようにする。</summary>
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromMinutes(3);

    private readonly Process _process;
    private bool _isDisposed;

    public int Level { get; }
    public bool UsesBook { get; }

    /// <param name="executablePath">Egaroucid_for_Console.out のパス</param>
    /// <param name="level">Egaroucidのレベル(0〜60)</param>
    /// <param name="threads">Egaroucidが使うスレッド数</param>
    /// <param name="useBook">定石の本を使うか。使わないほうが、レベルだけで強さが決まる</param>
    public EgaroucidClient(string executablePath, int level, int threads, bool useBook)
    {
        string fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Egaroucidが見つかりません: {fullPath}", fullPath);

        Level = level;
        UsesBook = useBook;

        var arguments = new List<string> { "-quiet", "-l", level.ToString(), "-t", threads.ToString() };
        if (!useBook) arguments.Add("-nobook");

        var startInfo = new ProcessStartInfo(fullPath)
        {
            // 評価ファイル(resources/)を実行ファイルからの相対パスで読むため、同じフォルダで起動する
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Egaroucidを起動できませんでした。");
        // 標準エラーは使わないが、バッファが詰まって止まらないよう読み捨てる
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
    }

    /// <summary>
    /// 盤面と手番を渡し、Egaroucidが選んだ手を返す。
    /// 呼び出し側は、手番の側に合法手があることを確かめてから呼ぶ(パスの局面では呼ばない)。
    /// </summary>
    /// <param name="tiles">盤面。tiles[行, 列]、0=空、1=黒、2=白</param>
    /// <param name="turnNum">手番(1=黒、2=白)</param>
    /// <returns>1始まりの(行, 列)</returns>
    public (int row, int col) GetMove(int[,] tiles, int turnNum)
    {
        _process.StandardInput.WriteLine($"setboard {ToBoardString(tiles)} {(turnNum == 1 ? 'B' : 'W')}");
        _process.StandardInput.WriteLine("go");
        _process.StandardInput.Flush();

        while (true)
        {
            string? line = _process.StandardOutput.ReadLineAsync().WaitAsync(MoveTimeout).GetAwaiter().GetResult();
            if (line == null)
                throw new InvalidOperationException("Egaroucidが途中で終了しました。");

            // -quiet では、プロンプト("> ")のあとに手(例: f6)だけが出力される
            Match match = MoveRegex().Match(line);
            if (match.Success)
            {
                string move = match.Groups[1].Value.ToLowerInvariant();
                int col = move[0] - 'a' + 1;
                int row = move[1] - '0';
                return (row, col);
            }
        }
    }

    /// <summary>盤面をEgaroucidの setboard の形式(a1, b1, …, h8 の順に B/W/-)にする</summary>
    private static string ToBoardString(int[,] tiles)
    {
        var builder = new StringBuilder(64);
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                builder.Append(tiles[row, col] switch { 1 => 'B', 2 => 'W', _ => '-' });
            }
        }
        return builder.ToString();
    }

    [GeneratedRegex(@"([a-hA-H][1-8])\s*$")]
    private static partial Regex MoveRegex();

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.WriteLine("quit");
                _process.StandardInput.Flush();
                if (!_process.WaitForExit(3000)) _process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // すでに終了している場合は何もしない
        }
        _process.Dispose();
    }
}

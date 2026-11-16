using System.Text;
using static OthelloSystem;

/// <summary>
/// f5d6形式の棋譜(企画 P-07・P-08・P-09)。
/// 黒が先手の対局を、パスを書かずに手の表記(列の英字の小文字 + 行の数字)だけで並べた1行(例: f5d6c3d3c4)。
/// Egaroucidの play と同じ書き方。
/// </summary>
public static class GameTranscript
{
    /// <summary>f5d6形式の棋譜を、1局1行で書き足すファイル。棋譜(JSONL)と同じく、exeと同じフォルダの data/ に置く</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "data", "game_records.txt");

    /// <summary>並べ直した1手分。パスは、手番の側に合法手がないときに自動で補ったもの</summary>
    public sealed record ReplayStep(int TurnNum, bool IsPass, int Row, int Col);

    /// <summary>並べ直した結果。Steps はパスを含む全部の手、FinalTiles は最後の盤面</summary>
    public sealed record ReplayResult(IReadOnlyList<ReplayStep> Steps, int[,] FinalTiles, bool IsFinished);

    /// <summary>手の並び(1始まりの行・列、パスは含めない)を、f5d6形式の文字列にする</summary>
    public static string Format(IEnumerable<(int row, int col)> moves)
        => string.Concat(moves.Select(move => GameRecord.ToNotation(move.row, move.col)));

    /// <summary>
    /// f5d6形式の文字列を読み、初期局面(黒が先手)から並べ直す。
    /// 手番の側に合法手がなければパスを補う。合法でない手や、読めない文字があれば例外を投げる。
    /// </summary>
    public static ReplayResult Replay(string transcript)
    {
        string text = transcript.Trim().ToLowerInvariant();
        if (text.Length % 2 != 0)
            throw new FormatException($"f5d6形式の長さが奇数です: {transcript}");

        var moves = new List<(int row, int col)>();
        for (int i = 0; i < text.Length; i += 2)
        {
            char file = text[i];
            char rank = text[i + 1];
            if (file < 'a' || file > 'h' || rank < '1' || rank > '8')
                throw new FormatException($"{i / 2 + 1}手目の「{text.Substring(i, 2)}」は、f5d6形式の手ではありません。");
            moves.Add((rank - '0', file - 'a' + 1));
        }
        return Replay(moves);
    }

    /// <summary>手の並び(パスは含めない)を、初期局面(黒が先手)から並べ直す</summary>
    public static ReplayResult Replay(IReadOnlyList<(int row, int col)> moves)
    {
        MainGameData gameData = CreateInitialGameData();
        var steps = new List<ReplayStep>();

        for (int i = 0; i < moves.Count; i++)
        {
            (int row, int col) move = moves[i];
            if (InstallationArea(gameData).Count == 0)
            {
                // 手番の側が打てないのでパスを補う。相手も打てなければ、もう終局している
                gameData = TurnChange(gameData);
                if (InstallationArea(gameData).Count == 0)
                    throw new InvalidDataException($"{i + 1}手目の {GameRecord.ToNotation(move.row, move.col)} の前に、対局が終わっています。");
                steps.Add(new ReplayStep(3 - gameData._turnNum, IsPass: true, 0, 0));
            }

            if (!InstallationArea(gameData).Contains(move))
                throw new InvalidDataException($"{i + 1}手目の {GameRecord.ToNotation(move.row, move.col)} は合法手ではありません。");

            steps.Add(new ReplayStep(gameData._turnNum, IsPass: false, move.row, move.col));
            PlacePiece(move.row, move.col, gameData);
            gameData = TurnChange(gameData);
        }

        bool isFinished = InstallationArea(gameData).Count == 0
                       && InstallationArea(TurnChange(gameData.Clone())).Count == 0;
        return new ReplayResult(steps, gameData._tiles, isFinished);
    }

    /// <summary>
    /// 白が先手で始まった手順を、黒が先手の手順に直す(企画 P-09)。
    /// 石の色をすべて入れ替え、盤面を左右に反転する。初期配置の色を入れ替えると、
    /// 左右反転した初期配置と同じになるので、手順の意味は変わらない(最後の黒と白の石数が入れ替わる)。
    /// 色は手番で決まるので、手の位置を左右に反転するだけでよい。
    /// </summary>
    public static List<(int row, int col)> ConvertWhiteFirst(IEnumerable<(int row, int col)> moves)
        => moves.Select(move => (move.row, 9 - move.col)).ToList();

    /// <summary>
    /// 1局分のf5d6形式の棋譜を、game_records.txt の末尾に1行で書き足す。
    /// 対局が止まらないよう、例外は外に出さずfalseを返す(GameRecord.Append と同じ考え)。
    /// </summary>
    public static bool Append(string transcript)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, transcript + Environment.NewLine, new UTF8Encoding(false));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static MainGameData CreateInitialGameData()
    {
        var gameData = new MainGameData
        {
            _tilesSize = 8, _tiles = new int[8, 8], _gameTurn = GameTurn.AI, _gameMode = GameMode.AIvsAI,
            _turnNum = 1, _turnCounter = 1,
        };
        gameData._tiles[3, 3] = 2; gameData._tiles[3, 4] = 1;
        gameData._tiles[4, 3] = 1; gameData._tiles[4, 4] = 2;
        return gameData;
    }
}

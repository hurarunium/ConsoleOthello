using System.Text.Json;

/// <summary>
/// 1手分の記録。パスも1手として残す。
/// </summary>
public class MoveRecord
{
    public int    Turn    { get; set; }  // 何手目か(1始まり。パスも数える)
    public string Color   { get; set; } = "";  // "black" / "white"
    public string Actor   { get; set; } = "";  // "human" / "ai"
    public bool   IsPass  { get; set; }
    public int    Row     { get; set; }  // 1始まり。パスのときは0
    public int    Col     { get; set; }  // 1始まり。パスのときは0
    public string Notation { get; set; } = "";  // 一般的な表記(例: f5)。パスは "pass"
    public long?  ThinkMs { get; set; }  // AIが考えた時間(ミリ秒)。人間の手はnull
}

/// <summary>
/// 1局分の棋譜。対局が終わる(または途中でやめる)たびに1行のJSONとして書き足す。
/// </summary>
public class GameRecord
{
    public string StartedAt  { get; set; } = "";  // ISO8601
    public string EndedAt    { get; set; } = "";  // ISO8601
    public string GameMode   { get; set; } = "";
    /// <summary>4段階だったころの強さの名前(Beginnerなど)。2026-11-15より前の棋譜だけにある。新しい棋譜は AiLevel を使う</summary>
    public string AiStrength { get; set; } = "";
    /// <summary>AIのレベル(1〜15)。古い棋譜では0</summary>
    public int    AiLevel    { get; set; }
    public string HumanColor { get; set; } = "";  // "black" / "white"。人間がいないモードでは ""
    public string Winner     { get; set; } = "";  // "black" / "white" / "draw"。途中でやめたら ""
    public string Result     { get; set; } = "";  // 人間から見た結果: "win" / "loss" / "draw"(プレイヤー対AIのみ)。途中でやめたら "aborted"
    public int    BlackCount { get; set; }
    public int    WhiteCount { get; set; }
    public string WeightsSource { get; set; } = "";  // 評価ファイルの名前と作成日時(例: "pattern_eval.bin(2026-11-15T23:35…)")。古い棋譜は "best_weights.json" / "default"
    public List<MoveRecord> Moves { get; set; } = new();
    /// <summary>f5d6形式の手順(パスは書かない。黒が先手)。2026-11-15より前の棋譜にはない</summary>
    public string Transcript { get; set; } = "";

    /// <summary>棋譜を書き足すファイル。起動のしかたに左右されないよう、exeと同じフォルダの data/ に置く。</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "data", "game_records.jsonl");

    /// <summary>
    /// この棋譜を1行のJSONとしてファイルの末尾に書き足す。
    /// 展示中に保存の失敗で対局が止まらないよう、例外は外に出さずfalseを返す。
    /// </summary>
    public bool Append()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string line = JsonSerializer.Serialize(this) + Environment.NewLine;
            File.AppendAllText(FilePath, line);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>1始まりの(行, 列)を一般的な表記(列の英字 + 行の数字。例: f5)にする。</summary>
    public static string ToNotation(int row, int col) => $"{(char)('a' + col - 1)}{row}";
}

using System.Text.Json;

/// <summary>プレイヤー対AIの累積戦績。JSONで保存・読み込みできる。</summary>
public class BattleRecord
{
    public int Wins   { get; set; }
    public int Losses { get; set; }
    public int Draws  { get; set; }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(this, options));
    }

    public static BattleRecord Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<BattleRecord>(File.ReadAllText(path)) ?? new BattleRecord();
        }
        catch
        {
            return new BattleRecord();
        }
    }
}

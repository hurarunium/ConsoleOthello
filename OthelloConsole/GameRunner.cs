using static OthelloSystem;

/// <summary>
/// 画面に何も出さずに、2つのAIを最後まで対局させる(設計書 Phase 7 の自己対戦で、局面を集めるために使う)。
/// </summary>
public static class GameRunner
{
    /// <summary>
    /// black(黒・先手)と white(白)で1局を打ち、最終の石数を返す。
    /// 序盤の openingMoves 手は random でランダムに打つ(同じ対局ばかりになるのを防ぐ)。
    /// </summary>
    /// <returns>(黒の最終の石数, 白の最終の石数)</returns>
    public static (int black, int white) Play(OthelloAI black, OthelloAI white, int level, int openingMoves, Random random)
    {
        MainGameData gameData = CreateInitialGameData(level);

        OthelloDebugLog.SuppressOutput = true;
        try
        {
            int passCount = 0;
            while (passCount < 2)
            {
                List<(int x, int y)> validMoves = InstallationArea(gameData);
                if (validMoves.Count == 0)
                {
                    passCount++;
                    gameData = TurnChange(gameData);
                    continue;
                }
                passCount = 0;

                (int row, int col) move = gameData._turnCounter <= openingMoves
                    ? validMoves[random.Next(validMoves.Count)]
                    : (gameData._turnNum == 1 ? black : white).AI(gameData, false);
                PlacePiece(move.row, move.col, gameData);
                gameData = TurnChange(gameData);
                gameData._turnCounter++;
            }
        }
        finally
        {
            OthelloDebugLog.SuppressOutput = false;
        }

        return Counting(gameData._tiles);
    }

    /// <summary>初期局面(黒が先手)のゲームデータを作る</summary>
    private static MainGameData CreateInitialGameData(int level)
    {
        var gameData = new MainGameData
        {
            _tilesSize = 8, _tiles = new int[8, 8], _gameTurn = GameTurn.AI, _gameMode = GameMode.AIvsAI,
            _turnNum = 1, _turnCounter = 1, _aiLevel = level,
        };
        gameData._tiles[3, 3] = 2; gameData._tiles[3, 4] = 1;
        gameData._tiles[4, 3] = 1; gameData._tiles[4, 4] = 2;
        return gameData;
    }
}

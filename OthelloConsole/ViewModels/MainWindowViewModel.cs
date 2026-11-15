using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OthelloConsole.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    // ─── ゲーム状態 ────────────────────────────────────────────
    private MainGameData         _gameData;
    private readonly OthelloAI   _ai;
    private List<(int x, int y)> _validMoves          = new();
    private int                   _consecutivePassCount = 0;
    private (int row, int col)    _lastMove            = (-1, -1);

    // ─── 棋譜の記録(学習用データ集め) ─────────────────────────────
    private readonly string _weightsSource;
    private GameRecord?     _currentRecord;

    // ─── 対局の番号 ─────────────────────────────────────────────
    // 「ゲーム開始」のたびに増やす。AIの思考やパスの待ちのあとで番号が変わっていたら、
    // 新しい対局が始まっているので、前の対局の手を打たずに捨てる(前の対局の手が新しい盤面に打たれるのを防ぐ)
    private int _gameNumber;

    // ─── バインディングプロパティ ───────────────────────────────
    [ObservableProperty] private int    _blackScore;
    [ObservableProperty] private int    _whiteScore;
    [ObservableProperty] private string _currentTurnText = "ゲーム待機中";
    [ObservableProperty] private string _statusMessage   = "ゲームを設定して「ゲーム開始」を押してください";
    [ObservableProperty] private bool   _isPlayerTurn    = false;
    [ObservableProperty] private bool   _isGameRunning   = false;
    [ObservableProperty] private bool   _isAiThinking    = false;

    // ─── パス通知 ───────────────────────────────────────────────
    [ObservableProperty] private bool   _isPassNotification  = false;
    [ObservableProperty] private string _passNotificationText = "";

    // ─── AI 戦績（PlayervsAI 累積） ─────────────────────────────
    private const string RecordsPath = "data/battle_records.json";
    private enum GameResult { None, Win, Loss, Draw }
    private GameResult _lastGameResult = GameResult.None;
    [ObservableProperty] private int  _playerWins   = 0;
    [ObservableProperty] private int  _playerLosses = 0;
    [ObservableProperty] private int  _playerDraws  = 0;
    [ObservableProperty] private bool _canUndoLastResult = false;

    // ─── 設定プロパティ ─────────────────────────────────────────
    [ObservableProperty] private GameMode   _selectedGameMode   = GameMode.PlayervsAI;
    [ObservableProperty] private AIStrength _selectedAiStrength = AIStrength.Normal;
    [ObservableProperty] private bool       _isPlayerFirst      = true;

    public bool IsPlayerVsAiMode => SelectedGameMode == GameMode.PlayervsAI;

    // ─── CanExecute 変更通知 ────────────────────────────────────
    partial void OnIsPlayerTurnChanged(bool value)  => CellClickCommand.NotifyCanExecuteChanged();
    partial void OnIsGameRunningChanged(bool value) => CellClickCommand.NotifyCanExecuteChanged();
    partial void OnIsAiThinkingChanged(bool value)
    {
        CellClickCommand.NotifyCanExecuteChanged();
        NewGameCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedGameModeChanged(GameMode value) => OnPropertyChanged(nameof(IsPlayerVsAiMode));

    // ─── コレクション ───────────────────────────────────────────
    public ObservableCollection<CellViewModel> Cells { get; } =
        new(Enumerable.Range(0, 64).Select(i => new CellViewModel(i)));

    public IReadOnlyList<GameMode>   GameModes   { get; } = Enum.GetValues<GameMode>().ToList();
    public IReadOnlyList<AIStrength> AiStrengths { get; } = Enum.GetValues<AIStrength>().ToList();

    public MainWindowViewModel()
    {
        bool hasBestWeights = File.Exists("weights/best_weights.json");
        _weightsSource = hasBestWeights ? "best_weights.json" : "default";
        _ai = new OthelloAI(
            hasBestWeights
                ? WeightSet.Load("weights/best_weights.json")
                : WeightSet.Default());

        var record = BattleRecord.Load(RecordsPath);
        PlayerWins   = record.Wins;
        PlayerLosses = record.Losses;
        PlayerDraws  = record.Draws;
    }

    // ─── UndoLastResult コマンド ────────────────────────────────
    [RelayCommand]
    private void UndoLastResult()
    {
        switch (_lastGameResult)
        {
            case GameResult.Win:  PlayerWins--;   break;
            case GameResult.Loss: PlayerLosses--; break;
            case GameResult.Draw: PlayerDraws--;  break;
        }
        _lastGameResult   = GameResult.None;
        CanUndoLastResult = false;
        new BattleRecord { Wins = PlayerWins, Losses = PlayerLosses, Draws = PlayerDraws }.Save(RecordsPath);
    }

    // ─── NewGame コマンド ────────────────────────────────────────
    // AIの思考中は押せない。思考中に新しい対局を始めると、前の対局のAIと新しい対局のAIが
    // 同じ OthelloAI(置換表)を同時に使ってしまうため
    [RelayCommand(CanExecute = nameof(CanStartNewGame))]
    private async Task NewGame()
    {
        _gameNumber++;

        // 対局の途中で「ゲーム開始」を押されたら、そこまでの棋譜を途中終了として残す
        if (IsGameRunning && _currentRecord is { Moves.Count: > 0 })
        {
            var (b, w) = OthelloSystem.Counting(_gameData._tiles);
            FinishRecord("aborted", "", b, w);
        }

        _consecutivePassCount = 0;
        _lastMove             = (-1, -1);
        _lastGameResult       = GameResult.None;
        CanUndoLastResult     = false;

        bool aiGoesFirst = SelectedGameMode == GameMode.AIvsAI
                        || (SelectedGameMode == GameMode.PlayervsAI && !IsPlayerFirst);

        _gameData = new MainGameData
        {
            _tilesSize   = 8,
            _gameMode    = SelectedGameMode,
            _AIStrength  = SelectedAiStrength,
            _gameTurn    = aiGoesFirst ? GameTurn.AI    : GameTurn.Player,
            _turnNum     = 1, // どのモードでも黒から打ち始める。人間が後攻なら、AIが黒・人間が白
            _turnCounter = 1,
            _tiles       = new int[8, 8]
        };

        int mid = 4; // 8 / 2
        _gameData._tiles[mid - 1, mid - 1] = 2;
        _gameData._tiles[mid - 1, mid]     = 1;
        _gameData._tiles[mid,     mid - 1] = 1;
        _gameData._tiles[mid,     mid]     = 2;

        _currentRecord = new GameRecord
        {
            StartedAt     = DateTimeOffset.Now.ToString("o"),
            GameMode      = SelectedGameMode.ToString(),
            AiStrength    = SelectedAiStrength.ToString(),
            HumanColor    = SelectedGameMode == GameMode.PlayervsAI ? HumanColorName() : "",
            WeightsSource = _weightsSource,
        };

        IsGameRunning = true;
        SyncBoardToUI();
        SyncScoreToUI();

        await ProcessNextTurnAsync();
    }

    // ─── CellClick コマンド ──────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanClickCell))]
    private async Task CellClick(CellViewModel cell)
    {
        if (!_validMoves.Any(m => m.x == cell.Row && m.y == cell.Col))
            return;

        await PlaceAndAdvanceAsync(cell.Row, cell.Col);
    }

    private bool CanStartNewGame() => !IsAiThinking;

    private bool CanClickCell(CellViewModel? _)
        => IsPlayerTurn && IsGameRunning && !IsAiThinking;

    // ─── 石を置いてターンを進める ─────────────────────────────────
    private async Task PlaceAndAdvanceAsync(int row, int col, long? thinkMs = null)
    {
        IsPlayerTurn = false;

        RecordMove(isPass: false, row, col, thinkMs);
        OthelloSystem.PlacePiece(row, col, _gameData);
        _lastMove = (row, col);
        _consecutivePassCount = 0;

        _gameData = OthelloSystem.TurnChange(_gameData);
        _gameData._turnCounter++;

        SyncBoardToUI();
        SyncScoreToUI();

        if (CheckGameOver()) return;

        await ProcessNextTurnAsync();
    }

    // ─── 次ターン制御 ───────────────────────────────────────────
    private async Task ProcessNextTurnAsync()
    {
        _validMoves = OthelloSystem.InstallationArea(_gameData);

        if (_validMoves.Count == 0)
        {
            _consecutivePassCount++;
            RecordMove(isPass: true, 0, 0, null);
            string passer = _gameData._turnNum == 1 ? "黒" : "白";
            StatusMessage        = $"{passer} は置ける場所がありません。パスします。";
            CurrentTurnText      = $"{passer} パス";
            PassNotificationText = $"{passer} はパスします！";
            IsPassNotification   = true;

            if (CheckGameOver()) return;

            int gameNumber = _gameNumber;
            await Task.Delay(1500);
            if (gameNumber != _gameNumber) return; // 待っている間に新しい対局が始まった
            IsPassNotification = false;

            _gameData = OthelloSystem.TurnChange(_gameData);
            _gameData._turnCounter++;

            await ProcessNextTurnAsync();
            return;
        }

        _consecutivePassCount = 0;
        SyncValidMovesToUI();

        if (IsHumanTurn())
        {
            IsPlayerTurn = true;
            string player = _gameData._turnNum == 1 ? "黒（あなた）" : "白（あなた）";
            CurrentTurnText = $"{player} の番";
            StatusMessage   = "石を置く場所をクリックしてください";
        }
        else
        {
            await ProcessAiTurnAsync();
        }
    }

    // ─── AI ターン（非同期） ─────────────────────────────────────
    private async Task ProcessAiTurnAsync()
    {
        IsPlayerTurn = false;
        IsAiThinking = true;
        string color    = _gameData._turnNum == 1 ? "黒" : "白";
        CurrentTurnText = $"AI（{color}）が思考中...";
        StatusMessage   = "しばらくお待ちください";

        int gameNumber = _gameNumber;
        (int x, int y) aiMove;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var snapshot = _gameData.Clone();
            aiMove = await Task.Run(() => _ai.AI(snapshot, isDebug: false));
        }
        finally
        {
            IsAiThinking = false;
        }
        long thinkMs = stopwatch.ElapsedMilliseconds;
        if (gameNumber != _gameNumber) return; // 考えている間に新しい対局が始まった

        CurrentTurnText = $"AI（{color}）が ({aiMove.x}, {aiMove.y}) に置きました";
        StatusMessage   = "";
        await Task.Delay(600);
        if (gameNumber != _gameNumber) return; // 待っている間に新しい対局が始まった

        await PlaceAndAdvanceAsync(aiMove.x, aiMove.y, thinkMs);
    }

    // ─── ゲーム終了判定 ─────────────────────────────────────────
    private bool CheckGameOver()
    {
        bool boardFull  = !_gameData._tiles.Cast<int>().Any(t => t == 0);
        bool doublePass = _consecutivePassCount >= 2;

        if (!boardFull && !doublePass) return false;

        IsPassNotification = false;
        IsGameRunning      = false;
        IsPlayerTurn       = false;

        var (black, white) = OthelloSystem.Counting(_gameData._tiles);
        BlackScore = black;
        WhiteScore = white;

        string result = black > white ? "黒の勝ち！"
                      : white > black ? "白の勝ち！"
                      :                 "引き分け！";
        CurrentTurnText = "ゲーム終了";
        StatusMessage   = $"{result}（黒 {black} vs 白 {white}）";

        string winner = black > white ? "black" : white > black ? "white" : "draw";
        string humanColor = _currentRecord?.HumanColor ?? "";
        string humanResult = humanColor == "" ? ""
                           : winner == "draw" ? "draw"
                           : winner == humanColor ? "win" : "loss";
        FinishRecord(humanResult, winner, black, white);

        if (_gameData._gameMode == GameMode.PlayervsAI)
        {
            if      (humanResult == "win")  { PlayerWins++;   _lastGameResult = GameResult.Win; }
            else if (humanResult == "loss") { PlayerLosses++; _lastGameResult = GameResult.Loss; }
            else                            { PlayerDraws++;  _lastGameResult = GameResult.Draw; }

            new BattleRecord { Wins = PlayerWins, Losses = PlayerLosses, Draws = PlayerDraws }
                .Save(RecordsPath);
            CanUndoLastResult = true;
        }

        return true;
    }

    // ─── 棋譜の記録 ─────────────────────────────────────────────
    /// <summary>いまの盤面の手番から、人間の色("black" / "white")を求める。</summary>
    private string HumanColorName()
    {
        int humanTurnNum = _gameData._gameTurn == GameTurn.Player ? _gameData._turnNum : 3 - _gameData._turnNum;
        return humanTurnNum == 1 ? "black" : "white";
    }

    /// <summary>いまの手番の1手(またはパス)を棋譜に足す。石を置く前・手番を変える前に呼ぶ。</summary>
    private void RecordMove(bool isPass, int row, int col, long? thinkMs)
    {
        if (_currentRecord is null) return;

        _currentRecord.Moves.Add(new MoveRecord
        {
            Turn     = _currentRecord.Moves.Count + 1,
            Color    = _gameData._turnNum == 1 ? "black" : "white",
            Actor    = IsHumanTurn() ? "human" : "ai",
            IsPass   = isPass,
            Row      = row,
            Col      = col,
            Notation = isPass ? "pass" : GameRecord.ToNotation(row, col),
            ThinkMs  = thinkMs,
        });
    }

    /// <summary>棋譜に結果を書いてファイルへ書き足し、記録中の棋譜を手放す。</summary>
    private void FinishRecord(string result, string winner, int black, int white)
    {
        if (_currentRecord is null) return;

        _currentRecord.EndedAt    = DateTimeOffset.Now.ToString("o");
        _currentRecord.Result     = result;
        _currentRecord.Winner     = winner;
        _currentRecord.BlackCount = black;
        _currentRecord.WhiteCount = white;
        _currentRecord.Transcript = GameTranscript.Format(
            _currentRecord.Moves.Where(m => !m.IsPass).Select(m => (m.Row, m.Col)));
        _currentRecord.Append();
        // f5d6形式の棋譜は、最後まで打った対局だけを残す(途中でやめた局は手順が途中で終わるため)
        if (result != "aborted") GameTranscript.Append(_currentRecord.Transcript);
        _currentRecord = null;
    }

    // ─── 現在ターンが人間か ──────────────────────────────────────
    private bool IsHumanTurn() => _gameData._gameMode switch
    {
        GameMode.PlayervsPlayer => true,
        GameMode.PlayervsAI     => _gameData._gameTurn == GameTurn.Player,
        GameMode.AIvsAI         => false,
        _                       => false
    };

    // ─── UI 同期ヘルパー ─────────────────────────────────────────
    private void SyncBoardToUI()
    {
        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                var cell = Cells[i * 8 + j];
                cell.Value      = _gameData._tiles[i, j];
                cell.IsLastMove = (i + 1 == _lastMove.row && j + 1 == _lastMove.col);
                cell.IsValidMove = false;
            }
        }
    }

    private void SyncValidMovesToUI()
    {
        foreach (var c in Cells) c.IsValidMove = false;
        // InstallationArea は 1ベース座標 (x=row, y=col) を返す
        foreach (var (x, y) in _validMoves)
            Cells[(x - 1) * 8 + (y - 1)].IsValidMove = true;
    }

    private void SyncScoreToUI()
    {
        var (black, white) = OthelloSystem.Counting(_gameData._tiles);
        BlackScore = black;
        WhiteScore = white;
    }
}

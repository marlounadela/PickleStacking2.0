namespace PickleStacking.Models;

public sealed class SessionState
{
    public int CourtsCount { get; set; } = 2;
    public bool IsActive { get; set; }
    public bool IsPaused { get; set; }
    public GameMode Mode { get; set; } = GameMode.Doubles;
    public int GameCounter { get; set; }
    public bool FirstRoundCompleted { get; set; }
    public MatchCategory NextMatchCategory { get; set; } = MatchCategory.WinWin;
}

public enum GameMode
{
    Singles,
    Doubles
}

public enum MatchCategory
{
    WinWin,
    LossLoss
}
namespace PickleStacking.Models;

public sealed class Game
{
    public int Number { get; set; }
    public int CourtNumber { get; set; }
    public Team TeamA { get; set; } = new();
    public Team TeamB { get; set; } = new();
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
    public string WinningTeam { get; set; } = "A";
    public string Mode { get; set; } = "Doubles";
}
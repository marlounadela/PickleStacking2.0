namespace PickleStacking.Models;

public sealed class NextUpPreview
{
    public int CourtNumber { get; set; }
    public List<Player> TeamA { get; set; } = new();
    public List<Player> TeamB { get; set; } = new();
    public string GroupLabel { get; set; } = string.Empty;

    public string TeamAName => string.Join(" / ", TeamA.Select(p => p.Name));
    public string TeamBName => string.Join(" / ", TeamB.Select(p => p.Name));
}

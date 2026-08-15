namespace PickleStacking.Models;

public sealed class Team
{
    public List<Player> Players { get; set; } = new();
    public string Label { get; set; } = string.Empty;
    public string DisplayName => string.Join(" / ", Players.Select(p => p.Name));
}
namespace PickleStacking.Models;

public sealed class Court
{
    public int Number { get; set; }
    public Team? TeamA { get; set; }
    public Team? TeamB { get; set; }
    public DateTime StartedAt { get; set; }
    public bool IsActive => TeamA != null && TeamB != null;
    public string DisplayName => $"Court {Number}";
}
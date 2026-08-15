using System.Text.Json.Serialization;

namespace PickleStacking.Models;

public enum PlayerStatus
{
    Waiting,
    Playing,
    Win,
    Loss,
    Next
}

public sealed class Player
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public int EntryOrder { get; set; }
    public PlayerStatus Status { get; set; } = PlayerStatus.Waiting;
    public int GamesPlayed { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public DateTime LastPlayedTime { get; set; } = DateTime.MinValue;
    public List<string> PreviousPartners { get; set; } = new();
    public List<string> PreviousOpponents { get; set; } = new();

    [JsonIgnore]
    public string StatusLabel => Status switch
    {
        PlayerStatus.Waiting => "WAITING",
        PlayerStatus.Playing => "PLAYING",
        PlayerStatus.Win => "WIN",
        PlayerStatus.Loss => "LOSS",
        PlayerStatus.Next => "NEXT",
        _ => "WAITING"
    };

    [JsonIgnore]
    public string StatusCss => Status switch
    {
        PlayerStatus.Waiting => "badge-waiting",
        PlayerStatus.Playing => "badge-playing",
        PlayerStatus.Win => "badge-win",
        PlayerStatus.Loss => "badge-loss",
        PlayerStatus.Next => "badge-next",
        _ => "badge-waiting"
    };
}
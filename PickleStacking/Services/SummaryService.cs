using PickleStacking.Models;

namespace PickleStacking.Services;

public sealed class PlayerRanking
{
    public int Rank { get; set; }
    public string PlayerId { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public int GamesPlayed { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public double WinRate { get; set; }
    public string WinRateDisplay => $"{WinRate:P1}";
}

public sealed class SessionSummary
{
    public DateTime? SessionDate { get; set; }
    public int PlayerCount { get; set; }
    public int CourtCount { get; set; }
    public string Mode { get; set; } = "Doubles";
    public int TotalGames { get; set; }
    public TimeSpan? Duration { get; set; }
    public int TotalWins { get; set; }
    public int TotalLosses { get; set; }
    public List<PlayerRanking> Rankings { get; set; } = new();
    public List<Award> Awards { get; set; } = new();
}

public sealed class Award
{
    public string Title { get; set; } = string.Empty;
    public string Emoji { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public sealed class SummaryService
{
    private readonly StackingService stacking;

    public SummaryService(StackingService stacking)
    {
        this.stacking = stacking;
    }

    public SessionSummary BuildSummary()
    {
        var summary = new SessionSummary();
        var history = stacking.History.ToList();
        var players = stacking.Players.ToList();

        summary.PlayerCount = players.Count;
        summary.CourtCount = stacking.Session.CourtsCount;
        summary.Mode = stacking.Session.Mode == GameMode.Singles ? "Singles" : "Doubles";
        summary.TotalGames = history.Count;

        // Session date/time from first completed game
        if (history.Count > 0)
        {
            var first = history.Min(g => g.StartedAt);
            var last = history.Max(g => g.CompletedAt);
            summary.SessionDate = first.ToLocalTime();
            summary.Duration = last - first;
        }

        // Total wins/losses from player stats
        summary.TotalWins = players.Sum(p => p.Wins);
        summary.TotalLosses = players.Sum(p => p.Losses);

        // Build rankings from actual player stats
        var rankings = players
            .Select(p => new PlayerRanking
            {
                PlayerId = p.Id,
                PlayerName = p.Name,
                GamesPlayed = p.GamesPlayed,
                Wins = p.Wins,
                Losses = p.Losses,
                WinRate = p.GamesPlayed > 0 ? (double)p.Wins / p.GamesPlayed : 0
            })
            .OrderByDescending(r => r.WinRate)      // 1. Win Rate
            .ThenByDescending(r => r.Wins)          // 2. Wins
            .ThenByDescending(r => r.GamesPlayed)   // 3. Games Played
            .ThenBy(r => r.PlayerName, StringComparer.OrdinalIgnoreCase) // deterministic tie-breaker
            .ToList();

        // Assign ranks (handle ties by giving same rank to identical stats)
        for (var i = 0; i < rankings.Count; i++)
        {
            if (i > 0 &&
                rankings[i].WinRate == rankings[i - 1].WinRate &&
                rankings[i].Wins == rankings[i - 1].Wins &&
                rankings[i].GamesPlayed == rankings[i - 1].GamesPlayed)
            {
                rankings[i].Rank = rankings[i - 1].Rank;
            }
            else
            {
                rankings[i].Rank = i + 1;
            }
        }

        summary.Rankings = rankings;

        // Build awards
        summary.Awards = BuildAwards(rankings);

        return summary;
    }

    private static List<Award> BuildAwards(List<PlayerRanking> rankings)
    {
        var awards = new List<Award>();
        var awardedPlayerIds = new HashSet<string>();

        // Only award if there are players with games played
        var participants = rankings.Where(r => r.GamesPlayed > 0).ToList();
        if (participants.Count == 0)
            return awards;

        // Champion
        var champion = participants.FirstOrDefault();
        if (champion != null)
        {
            awards.Add(new Award
            {
                Title = "Champion",
                Emoji = "🏆",
                PlayerName = champion.PlayerName,
                Detail = $"{champion.Wins}W / {champion.Losses}L — {champion.WinRate:P1}"
            });
            awardedPlayerIds.Add(champion.PlayerId);
        }

        // Runner-Up
        var runnerUp = participants.FirstOrDefault(r => !awardedPlayerIds.Contains(r.PlayerId));
        if (runnerUp != null)
        {
            awards.Add(new Award
            {
                Title = "Runner-Up",
                Emoji = "🥈",
                PlayerName = runnerUp.PlayerName,
                Detail = $"{runnerUp.Wins}W / {runnerUp.Losses}L — {runnerUp.WinRate:P1}"
            });
            awardedPlayerIds.Add(runnerUp.PlayerId);
        }

        // Third Place
        var third = participants.FirstOrDefault(r => !awardedPlayerIds.Contains(r.PlayerId));
        if (third != null)
        {
            awards.Add(new Award
            {
                Title = "Third Place",
                Emoji = "🥉",
                PlayerName = third.PlayerName,
                Detail = $"{third.Wins}W / {third.Losses}L — {third.WinRate:P1}"
            });
            awardedPlayerIds.Add(third.PlayerId);
        }

        // Most Wins (only if different from top 3)
        var mostWins = participants
            .Where(r => !awardedPlayerIds.Contains(r.PlayerId))
            .OrderByDescending(r => r.Wins)
            .ThenByDescending(r => r.WinRate)
            .FirstOrDefault();
        if (mostWins != null && mostWins.Wins > 0)
        {
            awards.Add(new Award
            {
                Title = "Most Wins",
                Emoji = "🔥",
                PlayerName = mostWins.PlayerName,
                Detail = $"{mostWins.Wins} wins"
            });
            awardedPlayerIds.Add(mostWins.PlayerId);
        }

        // Highest Win Rate (only if different from top 3 and Most Wins)
        var highestWinRate = participants
            .Where(r => !awardedPlayerIds.Contains(r.PlayerId) && r.GamesPlayed > 0)
            .OrderByDescending(r => r.WinRate)
            .ThenByDescending(r => r.Wins)
            .FirstOrDefault();
        if (highestWinRate != null && highestWinRate.WinRate > 0)
        {
            awards.Add(new Award
            {
                Title = "Highest Win Rate",
                Emoji = "📈",
                PlayerName = highestWinRate.PlayerName,
                Detail = $"{highestWinRate.WinRate:P1} win rate"
            });
            awardedPlayerIds.Add(highestWinRate.PlayerId);
        }

        // Most Games Played (only if different from top 3 and other awards)
        var mostGames = participants
            .Where(r => !awardedPlayerIds.Contains(r.PlayerId))
            .OrderByDescending(r => r.GamesPlayed)
            .ThenByDescending(r => r.WinRate)
            .FirstOrDefault();
        if (mostGames != null && mostGames.GamesPlayed > 0)
        {
            awards.Add(new Award
            {
                Title = "Most Games Played",
                Emoji = "🎯",
                PlayerName = mostGames.PlayerName,
                Detail = $"{mostGames.GamesPlayed} games"
            });
            awardedPlayerIds.Add(mostGames.PlayerId);
        }

        // Fair Play / Participation - only if there are players with 0 wins but games played
        var fairPlay = participants
            .Where(r => !awardedPlayerIds.Contains(r.PlayerId) && r.Wins == 0 && r.GamesPlayed > 0)
            .OrderByDescending(r => r.GamesPlayed)
            .FirstOrDefault();
        if (fairPlay != null)
        {
            awards.Add(new Award
            {
                Title = "Fair Play",
                Emoji = "🤝",
                PlayerName = fairPlay.PlayerName,
                Detail = $"{fairPlay.GamesPlayed} games played"
            });
            awardedPlayerIds.Add(fairPlay.PlayerId);
        }

        return awards;
    }
}
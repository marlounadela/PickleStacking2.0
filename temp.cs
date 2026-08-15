using System.Text.Json;
using Microsoft.JSInterop;
using PickleStacking.Models;

namespace PickleStacking.Services;

public sealed class StackingService
{
    private const string StorageKey = "pickle-stacking-state";
    private const int MaxCombinationPool = 16;

    private readonly IJSRuntime js;
    private readonly List<Player> players = new();
    private readonly List<Court> courts = new();
    private readonly List<Game> history = new();
    private readonly SessionState session = new();
    private int nextEntryOrder;
    private bool initialized;

    public StackingService(IJSRuntime js)
    {
        this.js = js;
    }

    public IReadOnlyList<Player> Players => players.OrderBy(p => p.EntryOrder).ToArray();
    public IReadOnlyList<Court> Courts => courts.OrderBy(c => c.Number).ToArray();
    public IReadOnlyList<Game> History => history.OrderByDescending(g => g.Number).ToArray();
    public SessionState Session => session;

    // GLOBAL FIFO NEXT TO PLAY queue - fixed order once teams are selected
    private readonly List<NextUpPreview> nextToPlayQueue = new();

    // Waiting queue: players who are waiting (Waiting or Next status, not Playing)
    public IReadOnlyList<Player> WaitingQueue => players
        .Where(p => p.Status != PlayerStatus.Playing)
        .OrderBy(p => p.EntryOrder)
        .ToArray();

    public bool AllPlayersHavePlayed => players.Count > 0 && players.All(p => p.GamesPlayed > 0);

    // ------------------------------------------------------------------
    // Initialization / persistence
    // ------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        if (initialized)
            return;
        initialized = true;

        try
        {
            var json = await js.InvokeAsync<string>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrEmpty(json))
            {
                var state = JsonSerializer.Deserialize<PersistedState>(json);
                if (state != null)
                {
                    players.Clear();
                    players.AddRange(state.Players);
                    session.CourtsCount = Math.Clamp(state.Session.CourtsCount, 1, 10);
                    session.IsActive = state.Session.IsActive;
                    session.IsPaused = state.Session.IsPaused;
                    session.Mode = state.Session.Mode;
                    session.GameCounter = state.Session.GameCounter;
                    nextEntryOrder = state.NextEntryOrder;
                    history.Clear();
                    history.AddRange(state.History);

                    // Restore NEXT TO PLAY queue if present
                    if (state.NextToPlayQueue != null)
                    {
                        nextToPlayQueue.Clear();
                        nextToPlayQueue.AddRange(state.NextToPlayQueue);
                    }
                    else
                    {
                        // Initialize empty queue if not persisted
                        nextToPlayQueue.Clear();
                    }
                }
            }
        }
        catch
        {
            // Ignore storage errors; start fresh.
        }

        EnsureCourts();
        RebuildCourtsFromPlayers();
    }

    public async Task SaveAsync()
    {
        try
        {
            var state = new PersistedState
            {
                Players = players,
                Session = session,
                History = history,
                NextEntryOrder = nextEntryOrder,
                NextToPlayQueue = nextToPlayQueue.ToList()
            };
            var json = JsonSerializer.Serialize(state);
            await js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
        }
        catch
        {
            // Ignore storage errors.
        }
    }

    private void Persist() => _ = SaveAsync();

    // ------------------------------------------------------------------
    // Player management
    // ------------------------------------------------------------------

    public void AddPlayer(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Player name cannot be empty.");

        name = name.Trim();
        if (players.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A player named '{name}' already exists.");

        var player = new Player
        {
            Name = name,
            EntryOrder = nextEntryOrder++,
            Status = PlayerStatus.Waiting
        };
        players.Add(player);
        Persist();
    }

    public void RemovePlayer(string playerId)
    {
        var player = players.FirstOrDefault(p => p.Id == playerId);
        if (player == null)
            return;

        if (player.Status == PlayerStatus.Playing)
            throw new InvalidOperationException($"'{player.Name}' is currently playing and cannot be removed.");

        players.Remove(player);
        Persist();
    }

    public void RenamePlayer(string playerId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("Player name cannot be empty.");

        newName = newName.Trim();
        var player = players.FirstOrDefault(p => p.Id == playerId);
        if (player == null)
            return;

        if (players.Any(p => p.Id != playerId && string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A player named '{newName}' already exists.");

        // Update the existing player record in place.
        // Since Player is a reference type, this updates the name everywhere
        // the player object is referenced (courts, queue, history, etc.).
        player.Name = newName;
        Persist();
    }

    // ------------------------------------------------------------------
    // Session configuration
    // ------------------------------------------------------------------

    public void ChangeMode(GameMode mode)
    {
        if (session.IsActive)
            throw new InvalidOperationException("Game mode cannot be changed while a session is active.");

        session.Mode = mode;
        Persist();
    }

    public void ChangeCourts(int count)
    {
        if (session.IsActive)
            throw new InvalidOperationException("Court count cannot be changed while a session is active.");

        session.CourtsCount = Math.Clamp(count, 1, 10);
        EnsureCourts();
        Persist();
    }

    public void StartSession()
    {
        if (session.IsActive)
            throw new InvalidOperationException("A session is already active.");

        var needed = session.Mode == GameMode.Singles ? 2 : 4;
        if (players.Count < needed)
            throw new InvalidOperationException(
                $"Not enough players to start. {session.Mode} requires at least {needed} players.");

        session.IsActive = true;
        session.IsPaused = false;
        ResetPlayerStatuses();
        AssignInitialGames();
        Persist();
    }

    public void PauseSession()
    {
        if (!session.IsActive)
            return;
        session.IsPaused = !session.IsPaused;
        if (!session.IsPaused)
            AssignNextPlayers();
        Persist();
    }

    public void ResetSession()
    {
        session.IsActive = false;
        session.IsPaused = false;
        session.GameCounter = 0;
        history.Clear();

        foreach (var court in courts)
        {
            court.TeamA = null;
            court.TeamB = null;
            court.StartedAt = default;
        }

        foreach (var player in players)
        {
            player.Status = PlayerStatus.Waiting;
            player.GamesPlayed = 0;
            player.Wins = 0;
            player.Losses = 0;
            player.PreviousPartners.Clear();
            player.PreviousOpponents.Clear();
            player.LastPlayedTime = DateTime.MinValue;
        }

        // Clear the NEXT TO PLAY queue on reset
        nextToPlayQueue.Clear();
        Persist();
    }

    // ------------------------------------------------------------------
    // Game results
    // ------------------------------------------------------------------

    public void RecordResult(int courtNumber, bool teamAWon)
    {
        var court = courts.FirstOrDefault(c => c.Number == courtNumber);
        if (court == null || court.TeamA == null || court.TeamB == null)
            return;

        var teamAPlayers = court.TeamA.Players.ToArray();
        var teamBPlayers = court.TeamB.Players.ToArray();
        var winners = teamAWon ? teamAPlayers : teamBPlayers;
        var losers = teamAWon ? teamBPlayers : teamAPlayers;

        foreach (var p in winners)
        {
            p.Wins++;
            p.GamesPlayed++;
            p.Status = PlayerStatus.Win;
            p.LastPlayedTime = DateTime.UtcNow;
            RecordPartners(p, winners);
            RecordOpponents(p, losers);
        }

        foreach (var p in losers)
        {
            p.Losses++;
            p.GamesPlayed++;
            p.Status = PlayerStatus.Loss;
            p.LastPlayedTime = DateTime.UtcNow;
            RecordPartners(p, losers);
            RecordOpponents(p, winners);
        }

        session.GameCounter++;
        history.Add(new Game
        {
            Number = session.GameCounter,
            CourtNumber = court.Number,
            TeamA = court.TeamA,
            TeamB = court.TeamB,
            StartedAt = court.StartedAt,
            CompletedAt = DateTime.UtcNow,
            WinningTeam = teamAWon ? "A" : "B",
            Mode = session.Mode == GameMode.Singles ? "Singles" : "Doubles"
        });

        court.TeamA = null;
        court.TeamB = null;
        court.StartedAt = default;

        // After game finishes, assign next players
        AssignNextPlayers();
        Persist();
    }

    // ------------------------------------------------------------------
    // Helper: Record partners and opponents
    // ------------------------------------------------------------------

    private static void RecordPartners(Player player, IReadOnlyList<Player> teammates)
    {
        foreach (var teammate in teammates)
        {
            if (teammate.Id == player.Id)
                continue;
            if (!player.PreviousPartners.Contains(teammate.Id))
                player.PreviousPartners.Add(teammate.Id);
        }
    }

    private static void RecordOpponents(Player player, IReadOnlyList<Player> opponents)
    {
        foreach (var opponent in opponents)
        {
            if (!player.PreviousOpponents.Contains(opponent.Id))
                player.PreviousOpponents.Add(opponent.Id);
        }
    }

    // ------------------------------------------------------------------
    // Assignment
    // ------------------------------------------------------------------

    private void AssignInitialGames()
    {
        var ordered = players.OrderBy(p => p.EntryOrder).ToList();
        var needed = session.Mode == GameMode.Singles ? 2 : 4;
        var index = 0;

        foreach (var court in courts)
        {
            if (ordered.Count - index < needed)
                break;

            var teamA = ordered.Skip(index).Take(needed / 2).ToList();
            var teamB = ordered.Skip(index + needed / 2).Take(needed / 2).ToList();
            AssignCourt(court, teamA, teamB);
            index += needed;
        }

        // After initial assignment, build the NEXT TO PLAY queue
        BuildNextToPlayQueue();
    }

    private void BuildNextToPlayQueue()
    {
        // Clear any existing queue
        nextToPlayQueue.Clear();

        var needed = session.Mode == GameMode.Singles ? 2 : 4;
        var maxQueuedMatchups = session.CourtsCount;

        // During first round (not all players have played), prioritize unplayed players,
        // but allow played players (Win/Loss) as fallback when needed
        if (!AllPlayersHavePlayed)
        {
            var eligible = players
                .Where(p => p.Status != PlayerStatus.Playing)
                .OrderBy(p => p.GamesPlayed) // 0 games (unplayed) first
                .ThenBy(p => p.EntryOrder)   // FIFO within same game count
                .ToList();

            // Select teams in FIFO order - only build as many as court count
            var remaining = eligible.ToList();
            while (remaining.Count >= needed && nextToPlayQueue.Count < maxQueuedMatchups)
            {
                var selected = remaining.Take(needed).ToList();
                var half = needed / 2;
                List<Player> teamA;
                List<Player> teamB;

                if (needed == 4)
                {
                    // Apply partner/opponent rotation to avoid same teams in consecutive rounds
                    var bestSplit = FindBestDoublesSplit(selected);
                    teamA = bestSplit.Item1;
                    teamB = bestSplit.Item2;
                }
                else
                {
                    teamA = selected.Take(half).ToList();
                    teamB = selected.Skip(half).Take(half).ToList();
                }

                nextToPlayQueue.Add(new NextUpPreview
                {
                    CourtNumber = nextToPlayQueue.Count + 1,
                    TeamA = teamA,
                    TeamB = teamB,
                    GroupLabel = "FIFO"
                });

                // Mark players as Next (queued)
                foreach (var p in selected)
                {
                    p.Status = PlayerStatus.Next;
                    remaining.Remove(p);
                }
            }
        }
        else
        {
            // After everyone has played once, use the existing stacking algorithm
            var eligible = players
                .Where(p => p.Status != PlayerStatus.Playing)
                .OrderBy(p => p.GamesPlayed)
                .ThenBy(p => p.EntryOrder)
                .ToList();

            var remaining = eligible.ToList();
            while (remaining.Count >= needed && nextToPlayQueue.Count < maxQueuedMatchups)
            {
                var selected = SelectPlayersForCourtGlobal(remaining, needed);
                if (selected == null)
                    break;

                var half = needed / 2;
                nextToPlayQueue.Add(new NextUpPreview
                {
                    CourtNumber = nextToPlayQueue.Count + 1,
                    TeamA = selected.Take(half).ToList(),
                    TeamB = selected.Skip(half).Take(half).ToList(),
                    GroupLabel = DetermineGroupLabel(selected)
                });

                // Mark players as Next (queued)
                foreach (var p in selected)
                {
                    p.Status = PlayerStatus.Next;
                    remaining.Remove(p);
                }
            }
        }
    }

    private string DetermineGroupLabel(List<Player> selected)
    {
        // Determine if the team is WIN or LOSS based group
        // This is based on the majority status of the players
        var winCount = selected.Count(p => p.Status == PlayerStatus.Win);
        var lossCount = selected.Count(p => p.Status == PlayerStatus.Loss);
        
        if (winCount >= lossCount)
            return "WIN";
        return "LOSS";
    }

    private void AssignNextPlayers()
    {
        if (!session.IsActive || session.IsPaused)
            return;

        var openCourts = courts.Where(c => !c.IsActive).OrderBy(c => c.Number).ToList();
        if (openCourts.Count == 0)
            return;

        // If we have teams in the NEXT TO PLAY queue, assign them to vacant courts
        if (nextToPlayQueue.Count > 0)
        {
            AssignQueuedTeamsToCourts(openCourts);
            return;
        }

        // No queued teams - build a new queue and assign
        BuildNextToPlayQueue();
        if (nextToPlayQueue.Count > 0)
        {
            AssignQueuedTeamsToCourts(openCourts);
        }
    }

    private void AssignQueuedTeamsToCourts(List<Court> openCourts)
    {
        var neededPerCourt = session.Mode == GameMode.Singles ? 2 : 4;

        // Use separate court index - queue is FIFO (always take from front)
        var courtIndex = 0;
        while (courtIndex < openCourts.Count && nextToPlayQueue.Count > 0)
        {
            var preview = nextToPlayQueue[0];
            var half = neededPerCourt / 2;

            // Get the actual players from the preview
            var teamAPlayers = preview.TeamA.ToList();
            var teamBPlayers = preview.TeamB.ToList();

            // Mark players as playing
            foreach (var p in teamAPlayers.Concat(teamBPlayers))
                p.Status = PlayerStatus.Playing;

            AssignCourt(openCourts[courtIndex], teamAPlayers, teamBPlayers);

            // Remove this team from the front of the queue
            nextToPlayQueue.RemoveAt(0);

            // After removing, generate the next team and append to END of queue
            // Find remaining eligible players (not playing, not already in queue)
            var queuedPlayerIds = nextToPlayQueue
                .SelectMany(q => q.TeamA.Concat(q.TeamB))
                .Select(p => p.Id)
                .ToHashSet();

            // During first round: prefer unplayed players (Waiting), then fall back to played players if needed
            var remainingEligible = AllPlayersHavePlayed
                ? players
                    .Where(p => p.Status != PlayerStatus.Playing && !queuedPlayerIds.Contains(p.Id))
                    .ToList()
                : players
                    .Where(p => p.Status != PlayerStatus.Playing && !queuedPlayerIds.Contains(p.Id))
                    .OrderBy(p => p.GamesPlayed) // unplayed (0 games) first, then played
                    .ThenBy(p => p.EntryOrder)
                    .ToList();

            // Check if we can generate a new team
            if (remainingEligible.Count >= neededPerCourt)
            {
                List<Player>? newSelected = null;

                // During first round, use FIFO for unplayed players first
                if (!AllPlayersHavePlayed)
                {
                    newSelected = remainingEligible
                        .OrderBy(p => p.GamesPlayed) // 0 games (unplayed) first
                        .ThenBy(p => p.EntryOrder)
                        .Take(neededPerCourt)
                        .ToList();
                }
                else
                {
                    // After first round, use existing stacking algorithm
                    newSelected = SelectPlayersForCourtGlobal(remainingEligible, neededPerCourt);
                }

                if (newSelected != null && newSelected.Count == neededPerCourt)
                {
                    var newHalf = neededPerCourt / 2;
                    List<Player> newTeamA;
                    List<Player> newTeamB;

                    if (neededPerCourt == 4)
                    {
                        // Use partner/opponent rotation to form teams
                        var bestSplit = FindBestDoublesSplit(newSelected);
                        newTeamA = bestSplit.Item1;
                        newTeamB = bestSplit.Item2;
                    }
                    else
                    {
                        newTeamA = newSelected.Take(newHalf).ToList();
                        newTeamB = newSelected.Skip(newHalf).Take(newHalf).ToList();
                    }

                    nextToPlayQueue.Add(new NextUpPreview
                    {
                        CourtNumber = nextToPlayQueue.Count + 1,
                        TeamA = newTeamA,
                        TeamB = newTeamB,
                        GroupLabel = AllPlayersHavePlayed ? DetermineGroupLabel(newSelected) : "FIFO"
                    });

                    // Mark newly queued players as Next
                    foreach (var p in newSelected)
                    {
                        p.Status = PlayerStatus.Next;
                    }

                    // Persist the updated queue
                    Persist();
                }
            }

            courtIndex++;
        }
    }

    // ------------------------------------------------------------------
    // Core selection algorithm - GLOBAL approach
    // ------------------------------------------------------------------

    /// <summary>
    /// Select players for a single court from the eligible pool, prioritizing:
    /// 1. Fewest GamesPlayed
    /// 2. WIN vs WIN or LOSS vs LOSS compatibility
    /// 3. Longest waiting time
    /// 4. Partner rotation
    /// 5. Avoid repeated opponents
    /// 6. FIFO when otherwise equal
    /// </summary>
    private List<Player>? SelectPlayersForCourtGlobal(List<Player> eligible, int needed)
    {
        if (eligible.Count < needed)
            return null;

        // Phase 1: Sort by GamesPlayed ascending (dominant factor), then wait time descending
        var sorted = eligible
            .OrderBy(p => p.GamesPlayed)
            .ThenByDescending(p => WaitMinutes(p))
            .ToList();

        // Phase 2: Try to form same-result groups (WIN vs WIN or LOSS vs LOSS)
        var result = SelectResultBasedGlobal(sorted, needed);
        if (result != null)
            return result;

        // Phase 3: Fall back to lowest GamesPlayed overall (mixed is last resort)
        return PickBestCombination(sorted.Take(MaxCombinationPool).ToList(), needed);
    }

    /// <summary>
    /// Select players for ALL courts at once, ensuring global fairness.
    /// </summary>
    private List<Player>? SelectPlayersForAllCourts(List<Player> eligible, int numCourts, int neededPerCourt)
    {
        var totalNeeded = numCourts * neededPerCourt;
        if (eligible.Count < totalNeeded)
            return null;

        // Sort all eligible players by GamesPlayed ascending, then wait time descending
        var sorted = eligible
            .OrderBy(p => p.GamesPlayed)
            .ThenByDescending(p => WaitMinutes(p))
            .ToList();

        // We need to select totalNeeded players
        // First, try to form same-result groups at each game-count threshold
        var result = SelectResultBasedGlobal(sorted, totalNeeded);
        if (result != null && result.Count == totalNeeded)
            return result;

        // Fall back: just take the totalNeeded players with fewest games
        // But still try to maintain WIN/LOSS grouping where possible
        var fallback = sorted.Take(totalNeeded).ToList();
        return fallback;
    }

    /// <summary>
    /// Select a group of players prioritizing same-result (WIN/WIN or LOSS/LOSS) matchups.
    /// </summary>
    private List<Player>? SelectResultBasedGlobal(List<Player> pool, int count)
    {
        // STEP 1: Sort by GamesPlayed ascending (dominant), then wait time descending
        var sorted = pool
            .OrderBy(p => p.GamesPlayed)
            .ThenByDescending(p => WaitMinutes(p))
            .ToList();

        // STEP 2: Expand the pool gradually from the lowest game count
        var gameCounts = sorted.Select(p => p.GamesPlayed).Distinct().OrderBy(g => g).ToList();

        foreach (var threshold in gameCounts)
        {
            var thresholdPool = sorted.Where(p => p.GamesPlayed <= threshold).ToList();
            if (thresholdPool.Count < count)
                continue;

            // Try WIN group first.
            var winGroup = thresholdPool.Where(p => p.Status == PlayerStatus.Win).ToList();
            if (winGroup.Count >= count)
            {
                var selected = PickBestCombination(winGroup, count);
                if (selected != null && selected.Count == count)
                    return selected;
            }

            // Try LOSS group.
            var lossGroup = thresholdPool.Where(p => p.Status == PlayerStatus.Loss).ToList();
            if (lossGroup.Count >= count)
            {
                var selected = PickBestCombination(lossGroup, count);
                if (selected != null && selected.Count == count)
                    return selected;
            }
        }

        // No same-result group found within reasonable game-count bounds.
        // Fall back to the lowest-game players overall (mixed is a last resort).
        return PickBestCombination(sorted, count);
    }

    /// <summary>
    /// Pick the best combination of players based on fairness score (GamesPlayed dominant), 
    /// with WIN/LOSS grouping preference.
    /// </summary>
    private List<Player>? PickBestCombination(List<Player> candidates, int count)
    {
        if (candidates.Count < count)
            return null;

        // Bound the pool for performance while preserving fairness.
        var pool = candidates
            .OrderBy(p => p.GamesPlayed)
            .ThenByDescending(p => WaitMinutes(p))
            .Take(MaxCombinationPool)
            .ToList();

        if (pool.Count < count)
            pool = candidates;

        // Try to find a same-result group first
        var result = TryFindSameResultGroup(pool, count);
        if (result != null)
            return result;

        // Last resort: just return the best scoring combination
        return count == 2 ? PickBestSingles(pool) : PickBestDoubles(pool);
    }

    /// <summary>
    /// Try to find a same-result (WIN/WIN or LOSS/LOSS) group from the candidates.
    /// </summary>
    private List<Player>? TryFindSameResultGroup(List<Player> candidates, int count)
    {
        // Separate by WIN/LOSS status
        var winCandidates = candidates.Where(p => p.Status == PlayerStatus.Win).ToList();
        var lossCandidates = candidates.Where(p => p.Status == PlayerStatus.Loss).ToList();

        // Try WIN group
        if (winCandidates.Count >= count)
        {
            var selected = count == 2 ? PickBestSingles(winCandidates) : PickBestDoubles(winCandidates);
            if (selected != null && selected.Count == count)
                return selected;
        }

        // Try LOSS group
        if (lossCandidates.Count >= count)
        {
            var selected = count == 2 ? PickBestSingles(lossCandidates) : PickBestDoubles(lossCandidates);
            if (selected != null && selected.Count == count)
                return selected;
        }

        return null;
    }

    private List<Player> PickBestSingles(List<Player> candidates)
    {
        List<Player>? best = null;
        var bestScore = double.MaxValue;

        foreach (var combo in Combinations(candidates, 2))
        {
            var score = ScoreSingles(combo);
            if (score < bestScore)
            {
                bestScore = score;
                best = combo;
            }
        }

        return best ?? candidates.Take(2).ToList();
    }

    private List<Player> PickBestDoubles(List<Player> candidates)
    {
        List<Player>? best = null;
        var bestScore = double.MaxValue;

        foreach (var combo in Combinations(candidates, 4))
        {
            // Three ways to split four players into two teams of two.
            var splits = new[]
            {
                new[] { new[] { combo[0], combo[1] }, new[] { combo[2], combo[3] } },
                new[] { new[] { combo[0], combo[2] }, new[] { combo[1], combo[3] } },
                new[] { new[] { combo[0], combo[3] }, new[] { combo[1], combo[2] } }
            };

            foreach (var split in splits)
            {
                var score = ScoreDoubles(split[0], split[1]);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = split[0].Concat(split[1]).ToList();
                }
            }
        }

        return best ?? candidates.Take(4).ToList();
    }

    /// <summary>
    /// Find the best split of 4 players into two teams of 2, avoiding repeated partners/opponents.
    /// </summary>
    private (List<Player> TeamA, List<Player> TeamB) FindBestDoublesSplit(List<Player> fourPlayers)
    {
        if (fourPlayers.Count != 4)
        {
            var half = fourPlayers.Count / 2;
            return (fourPlayers.Take(half).ToList(), fourPlayers.Skip(half).ToList());
        }

        var a = fourPlayers[0];
        var b = fourPlayers[1];
        var c = fourPlayers[2];
        var d = fourPlayers[3];

        var splits = new[]
        {
            new[] { new[] { a, b }, new[] { c, d } },
            new[] { new[] { a, c }, new[] { b, d } },
            new[] { new[] { a, d }, new[] { b, c } }
        };

        (List<Player>, List<Player>)? best = null;
        var bestScore = double.MaxValue;

        foreach (var split in splits)
        {
            var score = ScoreDoubles(split[0], split[1]);
            if (score < bestScore)
            {
                bestScore = score;
                best = (split[0].ToList(), split[1].ToList());
            }
        }

        return best ?? (new List<Player> { a, b }, new List<Player> { c, d });
    }

    private double ScoreSingles(IReadOnlyList<Player> combo)
    {
        var score = 0.0;
        var a = combo[0];
        var b = combo[1];

        // Opponent avoidance is a minor tiebreaker, never overrides game-count fairness.
        if (a.PreviousOpponents.Contains(b.Id))
            score += 10;
        if (b.PreviousOpponents.Contains(a.Id))
            score += 10;

        score += FairnessScore(combo);
        return score;
    }

    private double ScoreDoubles(IReadOnlyList<Player> teamA, IReadOnlyList<Player> teamB)
    {
        var score = 0.0;

        // Partner rotation: avoid recent partners (minor tiebreaker).
        if (teamA[0].PreviousPartners.Contains(teamA[1].Id))
            score += 20;
        if (teamB[0].PreviousPartners.Contains(teamB[1].Id))
            score += 20;

        // Opponent rotation: avoid recent opponents (minor tiebreaker).
        foreach (var a in teamA)
        {
            foreach (var b in teamB)
            {
                if (a.PreviousOpponents.Contains(b.Id))
                    score += 10;
                if (b.PreviousOpponents.Contains(a.Id))
                    score += 10;
            }
        }

        score += FairnessScore(teamA.Concat(teamB));
        return score;
    }

    private double FairnessScore(IEnumerable<Player> selected)
    {
        var score = 0.0;
        foreach (var p in selected)
        {
            // GamesPlayed is the DOMINANT factor.
            // A 1-game difference (1000) can never be outweighed by
            // partner rotation (20) or opponent avoidance (10).
            score += p.GamesPlayed * 1000;

            // Longer wait is better (tiebreaker).
            score -= WaitMinutes(p) * 1.0;
        }
        return score;
    }

    private static double WaitMinutes(Player p)
    {
        if (p.LastPlayedTime == DateTime.MinValue)
            return 100000;
        return (DateTime.UtcNow - p.LastPlayedTime).TotalMinutes;
    }

    // ------------------------------------------------------------------
    // Court assignment
    // ------------------------------------------------------------------

    private void AssignCourt(Court court, List<Player> teamA, List<Player> teamB)
    {
        court.TeamA = new Team { Label = "A", Players = teamA };
        court.TeamB = new Team { Label = "B", Players = teamB };
        court.StartedAt = DateTime.UtcNow;

        foreach (var p in teamA.Concat(teamB))
            p.Status = PlayerStatus.Playing;
    }

    /// <summary>
    /// Manually repair a court by swapping two players within that court.
    /// Only rearranges the players already assigned to the court.
    /// Does NOT modify the queue, other courts, player stats, or history.
    /// </summary>
    public void RepairCourt(int courtNumber, string playerIdA, string playerIdB)
    {
        if (string.IsNullOrEmpty(playerIdA) || string.IsNullOrEmpty(playerIdB))
            return;
        if (playerIdA == playerIdB)
            return;

        var court = courts.FirstOrDefault(c => c.Number == courtNumber);
        if (court == null || court.TeamA == null || court.TeamB == null)
            return;

        // Collect the four slots on this court
        var slots = new List<(int TeamIndex, int PlayerIndex, Player Player)>();
        for (var t = 0; t < 2; t++)
        {
            var team = t == 0 ? court.TeamA : court.TeamB;
            for (var i = 0; i < team.Players.Count; i++)
                slots.Add((t, i, team.Players[i]));
        }

        var slotA = slots.FirstOrDefault(s => s.Player.Id == playerIdA);
        var slotB = slots.FirstOrDefault(s => s.Player.Id == playerIdB);
        if (slotA.Player == null || slotB.Player == null)
            return;

        // Swap the player references between the two slots
        var teamA = court.TeamA;
        var teamB = court.TeamB;

        if (slotA.TeamIndex == 0)
            teamA.Players[slotA.PlayerIndex] = slotB.Player;
        else
            teamB.Players[slotA.PlayerIndex] = slotB.Player;

        if (slotB.TeamIndex == 0)
            teamA.Players[slotB.PlayerIndex] = slotA.Player;
        else
            teamB.Players[slotB.PlayerIndex] = slotA.Player;

        // Only the court arrangement changes. Queue, other courts, stats, and history are untouched.
        Persist();
    }

    // ------------------------------------------------------------------
    // Next-up preview (for NEXT TO PLAY section)
    // ------------------------------------------------------------------

    public IReadOnlyList<NextUpPreview> GetNextUp()
    {
        // Return the global NEXT TO PLAY queue
        return nextToPlayQueue.ToList();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private void EnsureCourts()
    {
        if (courts.Count == session.CourtsCount)
            return;

        courts.Clear();
        for (var i = 1; i <= session.CourtsCount; i++)
            courts.Add(new Court { Number = i });
    }

    private void RebuildCourtsFromPlayers()
    {
        EnsureCourts();
        foreach (var court in courts)
        {
            court.TeamA = null;
            court.TeamB = null;
            court.StartedAt = default;
        }

        var playing = players.Where(p => p.Status == PlayerStatus.Playing).OrderBy(p => p.EntryOrder).ToList();
        var needed = session.Mode == GameMode.Singles ? 2 : 4;
        var courtIndex = 0;
        var index = 0;

        while (index < playing.Count && courtIndex < courts.Count)
        {
            var half = needed / 2;
            var teamA = playing.Skip(index).Take(half).ToList();
            var teamB = playing.Skip(index + half).Take(half).ToList();

            if (teamA.Count != half || teamB.Count != half)
                break;

            var court = courts[courtIndex];
            court.TeamA = new Team { Label = "A", Players = teamA };
            court.TeamB = new Team { Label = "B", Players = teamB };
            court.StartedAt = DateTime.UtcNow;
            index += needed;
            courtIndex++;
        }
    }

    private void ResetPlayerStatuses()
    {
        foreach (var player in players)
            player.Status = PlayerStatus.Waiting;
    }

    private static IEnumerable<List<Player>> Combinations(List<Player> source, int k)
    {
        var result = new List<List<Player>>();
        var combo = new Player[k];
        Combine(source, combo, 0, 0, k, result);
        return result;
    }

    private static void Combine(List<Player> source, Player[] combo, int start, int index, int k, List<List<Player>> result)
    {
        if (index == k)
        {
            result.Add(combo.ToList());
            return;
        }

        for (var i = start; i <= source.Count - (k - index); i++)
        {
            combo[index] = source[i];
            Combine(source, combo, i + 1, index + 1, k, result);
        }
    }

    private sealed class PersistedState
    {
        public List<Player> Players { get; set; } = new();
        public SessionState Session { get; set; } = new();
        public List<Game> History { get; set; } = new();
        public int NextEntryOrder { get; set; }
        public List<NextUpPreview> NextToPlayQueue { get; set; } = new();
    }
}

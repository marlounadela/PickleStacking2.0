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

    // GLOBAL NEXT TO PLAY queue - reflects actual algorithm selections
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
                    // Safely restore players - discard if null or empty
                    players.Clear();
                    if (state.Players != null && state.Players.Count > 0)
                    {
                        players.AddRange(state.Players);
                    }

                    // Safely restore session state with defaults
                    session.CourtsCount = Math.Clamp(state.Session?.CourtsCount ?? 2, 1, 10);
                    session.IsActive = state.Session?.IsActive ?? false;
                    session.IsPaused = state.Session?.IsPaused ?? false;
                    session.Mode = state.Session?.Mode ?? GameMode.Doubles;
                    session.GameCounter = state.Session?.GameCounter ?? 0;
                    session.FirstRoundCompleted = state.Session?.FirstRoundCompleted ?? false;
                    session.NextMatchCategory = state.Session?.NextMatchCategory ?? MatchCategory.WinWin;

                    nextEntryOrder = state.NextEntryOrder > 0 ? state.NextEntryOrder : 1;
                    history.Clear();
                    if (state.History != null)
                    {
                        history.AddRange(state.History);
                    }

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
                // If state is null or json is empty, start with clean defaults
            }
        }
        catch
        {
            // Ignore storage errors; start fresh with defaults
        }

        // Always ensure courts are built with safe defaults
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

        // If a session is active, rebuild the queue so the new player
        // (with 0 games) gets priority per the fairness algorithm.
        if (session.IsActive && !session.IsPaused)
        {
            RebuildQueueForNewPlayer();
        }

        Persist();
    }

    /// <summary>
    /// Rebuild the NEXT TO PLAY queue when a new player is added mid-session,
    /// ensuring the new player (0 games) gets priority per the fairness algorithm.
    /// </summary>
    private void RebuildQueueForNewPlayer()
    {
        // Only rebuild if there are queued players
        if (nextToPlayQueue.Count == 0)
            return;

        // Reset queued players back to Waiting
        foreach (var preview in nextToPlayQueue)
        {
            foreach (var p in preview.TeamA.Concat(preview.TeamB))
            {
                if (p.Status == PlayerStatus.Next)
                    p.Status = PlayerStatus.Waiting;
            }
        }

        // Clear and rebuild the queue
        nextToPlayQueue.Clear();
        BuildNextToPlayQueue();
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
        session.FirstRoundCompleted = false;
        session.NextMatchCategory = MatchCategory.WinWin;
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
        session.FirstRoundCompleted = false;
        session.NextMatchCategory = MatchCategory.WinWin;
        history.Clear();

        foreach (var court in courts)
        {
            court.TeamA = null;
            court.TeamB = null;
            court.StartedAt = default;
            court.BecameAvailableAt = DateTime.MinValue;
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

    private void ResetPlayerStatuses()
    {
        foreach (var player in players)
            player.Status = PlayerStatus.Waiting;
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

        // Mark court as available with timestamp for dynamic ordering
        court.TeamA = null;
        court.TeamB = null;
        court.StartedAt = default;
        court.BecameAvailableAt = DateTime.UtcNow;

        // Check if first round is now complete
        if (!session.FirstRoundCompleted && AllPlayersHavePlayed)
        {
            session.FirstRoundCompleted = true;
        }

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
        // FIRST ROUND: FIFO ONLY - use player entry/order sequence
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

        // Get eligible players (not playing, not already in queue)
        var eligible = players
            .Where(p => p.Status != PlayerStatus.Playing)
            .ToList();

        var remaining = eligible.ToList();
        while (remaining.Count >= needed && nextToPlayQueue.Count < maxQueuedMatchups)
        {
            List<Player>? selected = null;

            if (!session.FirstRoundCompleted)
            {
                // FIRST ROUND: FIFO ONLY - use entry order, prioritize unplayed players
                selected = remaining
                    .OrderBy(p => p.GamesPlayed) // 0 games (unplayed) first
                    .ThenBy(p => p.EntryOrder)   // FIFO within same game count
                    .Take(needed)
                    .ToList();
            }
            else
            {
                // AFTER FIRST ROUND: Use fairness + standing-based algorithm
                // Determine the required match category for this queue slot
                var category = GetNextMatchCategoryForQueueSlot(nextToPlayQueue.Count);
                selected = SelectPlayersForMatch(remaining, needed, category);
            }

            if (selected == null || selected.Count != needed)
                break;

            var half = needed / 2;
            List<Player> teamA;
            List<Player> teamB;

            if (needed == 4)
            {
                // Apply partner/opponent rotation to form teams
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
                GroupLabel = session.FirstRoundCompleted ? DetermineGroupLabel(selected) : "FIFO"
            });

            // Mark players as Next (queued)
            foreach (var p in selected)
            {
                p.Status = PlayerStatus.Next;
                remaining.Remove(p);
            }
        }
    }

    /// <summary>
    /// Determine the match category for a queue slot based on the current NextMatchCategory
    /// and how many queue slots have been built so far.
    /// </summary>
    private MatchCategory GetNextMatchCategoryForQueueSlot(int queueSlotIndex)
    {
        // The queue alternates starting from the current NextMatchCategory
        var current = session.NextMatchCategory;
        if (queueSlotIndex % 2 == 0)
            return current;
        return current == MatchCategory.WinWin ? MatchCategory.LossLoss : MatchCategory.WinWin;
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

        // Get open courts sorted by availability order (BecameAvailableAt ascending, then court number as tiebreaker)
        var openCourts = courts
            .Where(c => !c.IsActive)
            .OrderBy(c => c.BecameAvailableAt)
            .ThenBy(c => c.Number)
            .ToList();

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

            // Toggle NextMatchCategory after assigning a match to a court
            ToggleNextMatchCategory();

            // Remove this team from the front of the queue
            nextToPlayQueue.RemoveAt(0);

            // After removing, generate the next team and append to END of queue
            // Find remaining eligible players (not playing, not already in queue)
            var queuedPlayerIds = nextToPlayQueue
                .SelectMany(q => q.TeamA.Concat(q.TeamB))
                .Select(p => p.Id)
                .ToHashSet();

            // Check if we can generate a new team
            var remainingEligible = players
                .Where(p => p.Status != PlayerStatus.Playing && !queuedPlayerIds.Contains(p.Id))
                .ToList();

            // Check if we can generate a new team
            if (remainingEligible.Count >= neededPerCourt)
            {
                List<Player>? newSelected = null;

                if (!session.FirstRoundCompleted)
                {
                    // FIRST ROUND: FIFO ONLY
                    newSelected = remainingEligible
                        .OrderBy(p => p.GamesPlayed) // 0 games (unplayed) first
                        .ThenBy(p => p.EntryOrder)
                        .Take(neededPerCourt)
                        .ToList();
                }
                else
                {
                    // After first round: use fairness + standing algorithm
                    // Determine the next match category based on the current NextMatchCategory
                    var category = session.NextMatchCategory;
                    newSelected = SelectPlayersForMatch(remainingEligible, neededPerCourt, category);
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
                        GroupLabel = session.FirstRoundCompleted ? DetermineGroupLabel(newSelected) : "FIFO"
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
    // Core selection algorithm - FAIR STANDING + DYNAMIC COURT MATCHING
    // ------------------------------------------------------------------

    /// <summary>
    /// Select players for a single court from the eligible pool, using the
    /// 8-priority hierarchy:
    /// P1: Fewest GamesPlayed
    /// P2: Correct WIN/WIN or LOSS/LOSS category
    /// P3: Similar current standing
    /// P4: Similar Win Rate
    /// P5: Avoid repeated partners
    /// P6: Avoid repeated opponents
    /// P7: Waiting time
    /// P8: FIFO/order as final tie-breaker
    /// </summary>
    private List<Player>? SelectPlayersForMatch(List<Player> eligible, int needed, MatchCategory category)
    {
        if (eligible.Count < needed)
            return null;

        // P1: Sort by GamesPlayed ascending (dominant factor)
        var sorted = eligible
            .OrderBy(p => p.GamesPlayed)
            .ThenBy(p => p.EntryOrder)
            .ToList();

        // Expand the pool gradually from the lowest game count.
        // The scoring function handles the full 8-priority hierarchy:
        // P1: GamesPlayed (1000 per game) - DOMINANT
        // P2: Category match (500 per wrong status)
        // P3-P8: Standing, Win Rate, Partner/Opponent rotation, Waiting, FIFO
        var gameCounts = sorted.Select(p => p.GamesPlayed).Distinct().OrderBy(g => g).ToList();

        foreach (var threshold in gameCounts)
        {
            var thresholdPool = sorted.Where(p => p.GamesPlayed <= threshold).ToList();
            if (thresholdPool.Count < needed)
                continue;

            var selected = PickBestCombination(thresholdPool, needed, category);
            if (selected != null && selected.Count == needed)
                return selected;
        }

        // Last resort: pick the lowest-game players overall
        return PickBestCombination(sorted, needed, category);
    }

    /// <summary>
    /// Pick the best combination of players based on the 8-priority hierarchy.
    /// </summary>
    private List<Player>? PickBestCombination(List<Player> candidates, int count, MatchCategory category)
    {
        if (candidates.Count < count)
            return null;

        // Bound the pool for performance while preserving fairness.
        var pool = candidates
            .OrderBy(p => p.GamesPlayed)
            .ThenBy(p => p.EntryOrder)
            .Take(MaxCombinationPool)
            .ToList();

        if (pool.Count < count)
            pool = candidates;

        // Use the scoring function which implements the full 8-priority hierarchy.
        // The scoring function naturally prefers same-category players (P2) via the
        // 500-point penalty, but GamesPlayed (P1, 1000 per game) always dominates.
        return count == 2 ? PickBestSingles(pool, category) : PickBestDoubles(pool, category);
    }

    /// <summary>
    /// Try to find a same-result (WIN/WIN or LOSS/LOSS) group from the candidates.
    /// </summary>
    private List<Player>? TryFindSameResultGroup(List<Player> candidates, int count, MatchCategory category)
    {
        // Separate by WIN/LOSS status
        var winCandidates = candidates.Where(p => p.Status == PlayerStatus.Win).ToList();
        var lossCandidates = candidates.Where(p => p.Status == PlayerStatus.Loss).ToList();

        // Try the required category first
        if (category == MatchCategory.WinWin && winCandidates.Count >= count)
        {
            var selected = count == 2 ? PickBestSingles(winCandidates, category) : PickBestDoubles(winCandidates, category);
            if (selected != null && selected.Count == count)
                return selected;
        }
        else if (category == MatchCategory.LossLoss && lossCandidates.Count >= count)
        {
            var selected = count == 2 ? PickBestSingles(lossCandidates, category) : PickBestDoubles(lossCandidates, category);
            if (selected != null && selected.Count == count)
                return selected;
        }

        // Try the other category as fallback
        if (category == MatchCategory.WinWin && lossCandidates.Count >= count)
        {
            var selected = count == 2 ? PickBestSingles(lossCandidates, category) : PickBestDoubles(lossCandidates, category);
            if (selected != null && selected.Count == count)
                return selected;
        }
        else if (category == MatchCategory.LossLoss && winCandidates.Count >= count)
        {
            var selected = count == 2 ? PickBestSingles(winCandidates, category) : PickBestDoubles(winCandidates, category);
            if (selected != null && selected.Count == count)
                return selected;
        }

        return null;
    }

    private List<Player> PickBestSingles(List<Player> candidates, MatchCategory category)
    {
        List<Player>? best = null;
        var bestScore = double.MaxValue;

        foreach (var combo in Combinations(candidates, 2))
        {
            var score = ScoreSingles(combo, category);
            if (score < bestScore)
            {
                bestScore = score;
                best = combo;
            }
        }

        return best ?? candidates.Take(2).ToList();
    }

    private List<Player> PickBestDoubles(List<Player> candidates, MatchCategory category)
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
                var score = ScoreDoubles(split[0], split[1], category);
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
            var score = ScoreDoubles(split[0], split[1], session.NextMatchCategory);
            if (score < bestScore)
            {
                bestScore = score;
                best = (split[0].ToList(), split[1].ToList());
            }
        }

        return best ?? (new List<Player> { a, b }, new List<Player> { c, d });
    }

    private double ScoreSingles(IReadOnlyList<Player> combo, MatchCategory category)
    {
        var score = 0.0;
        var a = combo[0];
        var b = combo[1];

        // P1: GamesPlayed fairness (DOMINANT - 1000 per game difference)
        score += (a.GamesPlayed + b.GamesPlayed) * 1000;

        // P2: Category match (WIN/WIN or LOSS/LOSS)
        var requiredStatus = category == MatchCategory.WinWin ? PlayerStatus.Win : PlayerStatus.Loss;
        if (a.Status != requiredStatus)
            score += 500;
        if (b.Status != requiredStatus)
            score += 500;

        // P3: Similar standing (smaller difference is better)
        var standingDiff = Math.Abs(a.Wins - b.Wins) + Math.Abs(a.Losses - b.Losses);
        score += standingDiff * 50;

        // P4: Similar Win Rate
        var winRateA = a.GamesPlayed > 0 ? (double)a.Wins / a.GamesPlayed : 0;
        var winRateB = b.GamesPlayed > 0 ? (double)b.Wins / b.GamesPlayed : 0;
        score += Math.Abs(winRateA - winRateB) * 100;

        // P6: Opponent rotation (minor tiebreaker)
        if (a.PreviousOpponents.Contains(b.Id))
            score += 10;
        if (b.PreviousOpponents.Contains(a.Id))
            score += 10;

        // P7: Waiting time (longer wait is better)
        score -= WaitMinutes(a) * 0.1;
        score -= WaitMinutes(b) * 0.1;

        // P8: FIFO/order as final tie-breaker
        score += (a.EntryOrder + b.EntryOrder) * 0.001;

        return score;
    }

    private double ScoreDoubles(IReadOnlyList<Player> teamA, IReadOnlyList<Player> teamB, MatchCategory category)
    {
        var score = 0.0;
        var all = teamA.Concat(teamB).ToList();

        // P1: GamesPlayed dominance (DOMINANT - 1000 per game)
        score += all.Sum(p => p.GamesPlayed) * 1000;

        // P2: Correct category (WIN/WIN or LOSS/LOSS)
        var requiredStatus = category == MatchCategory.WinWin ? PlayerStatus.Win : PlayerStatus.Loss;
        foreach (var p in all)
        {
            if (p.Status != requiredStatus)
                score += 500;
        }

        // P3: Similar standing (smaller spread is better)
        var wins = all.Select(p => p.Wins).ToList();
        var losses = all.Select(p => p.Losses).ToList();
        var winSpread = wins.Max() - wins.Min();
        var lossSpread = losses.Max() - losses.Min();
        score += (winSpread + lossSpread) * 50;

        // P4: Similar Win Rate
        var winRates = all.Select(p => p.GamesPlayed > 0 ? (double)p.Wins / p.GamesPlayed : 0).ToList();
        var winRateSpread = winRates.Max() - winRates.Min();
        score += winRateSpread * 100;

        // P5: Partner rotation (avoid recent partners - minor tiebreaker)
        if (teamA[0].PreviousPartners.Contains(teamA[1].Id))
            score += 20;
        if (teamB[0].PreviousPartners.Contains(teamB[1].Id))
            score += 20;

        // P6: Opponent rotation (avoid recent opponents - minor tiebreaker)
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

        // P7: Waiting time (longer wait is better)
        foreach (var p in all)
        {
            score -= WaitMinutes(p) * 0.1;
        }

        // P8: FIFO/order as final tiebreaker
        score += all.Sum(p => p.EntryOrder) * 0.001;

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
        court.BecameAvailableAt = DateTime.MinValue;

        foreach (var p in teamA.Concat(teamB))
            p.Status = PlayerStatus.Playing;
    }

    private void ToggleNextMatchCategory()
    {
        session.NextMatchCategory = session.NextMatchCategory == MatchCategory.WinWin
            ? MatchCategory.LossLoss
            : MatchCategory.WinWin;
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
            court.BecameAvailableAt = DateTime.MinValue;
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

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

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
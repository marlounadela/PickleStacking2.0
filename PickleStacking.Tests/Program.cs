using Microsoft.JSInterop;
using PickleStacking.Models;
using PickleStacking.Services;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("Scenario 1: 4 players / 1 doubles court", Scenario1),
    ("Scenario 2: 8 players / 2 doubles courts", Scenario2),
    ("Scenario 3: 20+ players / multiple courts", Scenario3),
    ("Scenario 4: Singles", Scenario4),
    ("Scenario 5: Doubles", Scenario5),
    ("Scenario 6: WIN vs WIN / LOSS vs LOSS after first round", Scenario6),
    ("Scenario 7: FIFO initial assignment", Scenario7),
    ("Scenario 8: Partner rotation", Scenario8),
    ("Scenario 9: Repeated opponent avoidance", Scenario9),
    ("Scenario 10: Multiple courts finish at different times", Scenario10),
    ("Scenario 11: Add players while games active", Scenario11),
    ("Scenario 12: Pause / resume", Scenario12),
    ("Scenario 13: Reset", Scenario13),
    ("Scenario 14: localStorage recovery", Scenario14),
    ("Scenario 15: 10 courts", Scenario15),
    ("Scenario 16: Game-count fairness - low-game players prioritized", Scenario16),
    ("Scenario 17: Same-result courts - WIN vs WIN / LOSS vs LOSS", Scenario17),
    ("Scenario 18: Late player with 0 games gets priority", Scenario18),
    ("Scenario 19: Court repair - swap players within a court", Scenario19),
    ("Scenario 20: Court repair - independent per court", Scenario20),
    ("Scenario 21: Court repair - game result uses repaired teams", Scenario21),
    ("Scenario 22: Player rename preserves identity and stats", Scenario22),
    ("Scenario 23: Player rename updates everywhere", Scenario23),
    ("Scenario 24: Court repair does not alter queue or other courts", Scenario24)
};

var passed = 0;
var failed = 0;

foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL  {test.Name}");
        Console.WriteLine($"      {ex.Message}");
        failed++;
    }
}

Console.WriteLine();
Console.WriteLine($"Results: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

// ---------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------

static StackingService NewService(FakeJSRuntime? js = null)
{
    js ??= new FakeJSRuntime();
    return new StackingService(js);
}

static void AddPlayers(StackingService svc, int count, string prefix = "P")
{
    for (var i = 1; i <= count; i++)
        svc.AddPlayer($"{prefix}{i}");
}

static string CourtMatch(StackingService svc, int courtNumber)
{
    var court = svc.Courts.First(c => c.Number == courtNumber);
    if (!court.IsActive)
        return "(empty)";
    return $"{court.TeamA!.DisplayName} vs {court.TeamB!.DisplayName}";
}

static string[] CourtPlayerNames(StackingService svc, int courtNumber)
{
    var court = svc.Courts.First(c => c.Number == courtNumber);
    if (!court.IsActive)
        return Array.Empty<string>();
    return court.TeamA!.Players.Concat(court.TeamB!.Players).Select(p => p.Name).OrderBy(n => n).ToArray();
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception($"Assertion failed: {message}");
}

static void AssertCourt(StackingService svc, int courtNumber, string expected)
{
    var actual = CourtMatch(svc, courtNumber);
    Assert(actual == expected, $"Court {courtNumber}: expected '{expected}' but got '{actual}'");
}

static void AssertCourtPlayers(StackingService svc, int courtNumber, params string[] expectedNames)
{
    var actual = CourtPlayerNames(svc, courtNumber);
    var expected = expectedNames.OrderBy(n => n).ToArray();
    Assert(actual.SequenceEqual(expected),
        $"Court {courtNumber}: expected players [{string.Join(", ", expected)}] but got [{string.Join(", ", actual)}]");
}

// ---------------------------------------------------------------------
// Scenario 1: 4 players / 1 doubles court
// ---------------------------------------------------------------------

static async Task Scenario1()
{
    var svc = NewService();
    svc.ChangeCourts(1);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 4);

    svc.StartSession();
    AssertCourtPlayers(svc, 1, "P1", "P2", "P3", "P4");

    svc.RecordResult(1, true); // Team A (P1/P2) wins

    Assert(svc.Players.First(p => p.Name == "P1").Wins == 1, "P1 should have 1 win");
    Assert(svc.Players.First(p => p.Name == "P3").Losses == 1, "P3 should have 1 loss");
    Assert(svc.History.Count == 1, "History should have 1 game");
    Assert(svc.Players.All(p => p.GamesPlayed == 1), "All players should have played 1 game");

    // With only 4 players, the next game must reuse all 4.
    AssertCourtPlayers(svc, 1, "P1", "P2", "P3", "P4");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 2: 8 players / 2 doubles courts
// ---------------------------------------------------------------------

static async Task Scenario2()
{
    var svc = NewService();
    svc.ChangeCourts(2);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 8);

    svc.StartSession();
    AssertCourtPlayers(svc, 1, "P1", "P2", "P3", "P4");
    AssertCourtPlayers(svc, 2, "P5", "P6", "P7", "P8");

    // Court 1 finishes: Team A (P1/P2) wins
    svc.RecordResult(1, true);
    // Court 1 should re-fill with the 4 eligible players (P1-P4, since P5-P8 are playing)
    AssertCourtPlayers(svc, 1, "P1", "P2", "P3", "P4");

    // Court 2 finishes: Team A (P5/P6) wins
    svc.RecordResult(2, true);

    // Court 1 was re-filled with P1-P4. Court 2 can only be filled with P5-P8 (the only eligible players).
    AssertCourtPlayers(svc, 2, "P5", "P6", "P7", "P8");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 3: 20+ players / multiple courts
// ---------------------------------------------------------------------

static async Task Scenario3()
{
    var svc = NewService();
    svc.ChangeCourts(4);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 24);

    svc.StartSession();
    Assert(svc.Courts.Count(c => c.IsActive) == 4, "4 courts should be active");
    Assert(svc.WaitingQueue.Count() == 8, "8 players should be waiting");

    // Finish all 4 courts
    for (var i = 1; i <= 4; i++)
        svc.RecordResult(i, i % 2 == 0);

    // 4 more courts should fill from the 8 waiting (first-timers via FIFO)
    Assert(svc.Courts.Count(c => c.IsActive) == 4, "4 courts should be active again");

    // The 8 first-timers (P17-P24) should now be playing
    var playingNames = svc.Courts
        .Where(c => c.IsActive)
        .SelectMany(c => c.TeamA!.Players.Concat(c.TeamB!.Players))
        .Select(p => p.Name)
        .ToArray();

    Assert(playingNames.Contains("P17") && playingNames.Contains("P24"),
        "First-timers P17-P24 should be playing");
    Assert(svc.Players.Count(p => p.Status == PlayerStatus.Playing) == 16,
        "16 players should be playing (8 original + 8 first-timers)");
    Assert(svc.Players.Count(p => p.GamesPlayed == 0 && p.Status != PlayerStatus.Playing) == 0,
        "No non-playing players should have 0 games");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 4: Singles
// ---------------------------------------------------------------------

static async Task Scenario4()
{
    var svc = NewService();
    svc.ChangeCourts(2);
    svc.ChangeMode(GameMode.Singles);
    AddPlayers(svc, 4);

    svc.StartSession();
    AssertCourtPlayers(svc, 1, "P1", "P2");
    AssertCourtPlayers(svc, 2, "P3", "P4");

    svc.RecordResult(1, true);
    // Court 1 re-fills with P1, P2 (the only eligible players)
    AssertCourtPlayers(svc, 1, "P1", "P2");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 5: Doubles
// ---------------------------------------------------------------------

static async Task Scenario5()
{
    var svc = NewService();
    svc.ChangeCourts(1);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 4);

    svc.StartSession();
    AssertCourtPlayers(svc, 1, "P1", "P2", "P3", "P4");
    Assert(svc.Session.Mode == GameMode.Doubles, "Mode should be doubles");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 6: WIN vs WIN / LOSS vs LOSS after first round
// ---------------------------------------------------------------------

static async Task Scenario6()
{
    var svc = NewService();
    svc.ChangeCourts(2);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 8);

    svc.StartSession();
    svc.RecordResult(1, true); // P1/P2 win, P3/P4 lose
    svc.RecordResult(2, true); // P5/P6 win, P7/P8 lose

    // After both courts finish, all 8 have played.
    // Court 1 was re-filled with P1-P4 (FIFO, before all had played).
    // Court 2 can only be filled with P5-P8 (the only eligible players).
    AssertCourtPlayers(svc, 2, "P5", "P6", "P7", "P8");
    var winGroup = new[] { "P1", "P2", "P5", "P6" }.OrderBy(n => n).ToArray();
    var lossGroup = new[] { "P3", "P4", "P7", "P8" }.OrderBy(n => n).ToArray();

    // Verify WIN/LOSS grouping works when all players are eligible simultaneously.
    // Reset and re-run with all courts finishing at once.
    var svc2 = NewService();
    svc2.ChangeCourts(2);
    svc2.ChangeMode(GameMode.Doubles);
    AddPlayers(svc2, 8);
    svc2.StartSession();

    // Simulate both courts finishing at the same time by recording results
    // but pausing between to prevent immediate re-fill.
    svc2.PauseSession();
    svc2.RecordResult(1, true);
    svc2.RecordResult(2, true);
    svc2.PauseSession(); // resume

    // Now all 8 have played and both courts are empty.
    // Court 1 should get WIN group, court 2 should get LOSS group.
    var c1 = CourtPlayerNames(svc2, 1);
    var c2 = CourtPlayerNames(svc2, 2);
    Assert(c1.SequenceEqual(winGroup), "Court 1 should be all winners");
    Assert(c2.SequenceEqual(lossGroup), "Court 2 should be all losers");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 7: FIFO initial assignment
// ---------------------------------------------------------------------

static async Task Scenario7()
{
    var svc = NewService();
    svc.ChangeCourts(2);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 8);

    svc.StartSession();
    AssertCourtPlayers(svc, 1, "P1", "P2", "P3", "P4");
    AssertCourtPlayers(svc, 2, "P5", "P6", "P7", "P8");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 8: Partner rotation
// ---------------------------------------------------------------------

static async Task Scenario8()
{
    var svc = NewService();
    svc.ChangeCourts(2);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 8);

    svc.StartSession();
    // Game 1: P1/P2 vs P3/P4, P5/P6 vs P7/P8
    svc.RecordResult(1, true); // P1,P2 win
    svc.RecordResult(2, true); // P5,P6 win

    // Next round: WIN group P1,P2,P5,P6 on court 1.
    // Partner rotation should avoid P1+P2 and P5+P6 being partners again.
    var court1 = svc.Courts.First(c => c.Number == 1);
    var teamA = court1.TeamA!.Players.Select(p => p.Name).OrderBy(n => n).ToArray();
    var teamB = court1.TeamB!.Players.Select(p => p.Name).OrderBy(n => n).ToArray();

    var partnerPairs = new[]
    {
        string.Join("+", teamA),
        string.Join("+", teamB)
    };

    Assert(!partnerPairs.Contains("P1+P2"), "P1 and P2 should not be partners again");
    Assert(!partnerPairs.Contains("P5+P6"), "P5 and P6 should not be partners again");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 9: Repeated opponent avoidance
// ---------------------------------------------------------------------

static async Task Scenario9()
{
    var svc = NewService();
    svc.ChangeCourts(2);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 8);

    svc.StartSession();
    // Game 1: P1/P2 vs P3/P4, P5/P6 vs P7/P8
    svc.RecordResult(1, true); // P1,P2 win
    svc.RecordResult(2, true); // P5,P6 win

    // Next round: WIN group P1,P2,P5,P6 on court 1.
    // Opponent avoidance: P1 should not face P2 again (they were opponents in game 1).
    var court1 = svc.Courts.First(c => c.Number == 1);
    var teamA = court1.TeamA!.Players.Select(p => p.Name).ToArray();
    var teamB = court1.TeamB!.Players.Select(p => p.Name).ToArray();

    var p1Team = teamA.Contains("P1") ? teamA : teamB;
    var p2Team = teamA.Contains("P2") ? teamA : teamB;
    Assert(!p1Team.SequenceEqual(p2Team), "P1 and P2 should not be on the same team (they were opponents)");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 10: Multiple courts finish at different times
// ---------------------------------------------------------------------

static async Task Scenario10()
{
    var svc = NewService();
    svc.ChangeCourts(2);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 8);

    svc.StartSession();
    // Court 1 finishes first
    svc.RecordResult(1, true);
    // Court 1 re-fills with P1-P4 (only eligible players)
    AssertCourtPlayers(svc, 1, "P1", "P2", "P3", "P4");
    AssertCourtPlayers(svc, 2, "P5", "P6", "P7", "P8");

    // Court 2 finishes later
    svc.RecordResult(2, true);
    Assert(svc.Courts.Count(c => c.IsActive) == 2, "Both courts should be active again");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 11: Add players while games active
// ---------------------------------------------------------------------

static async Task Scenario11()
{
    var svc = NewService();
    svc.ChangeCourts(1);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 4);

    svc.StartSession();
    // Add a new player while game is active
    svc.AddPlayer("NewPlayer");
    Assert(svc.WaitingQueue.Any(p => p.Name == "NewPlayer"), "New player should be in the waiting queue");

    // Finish the game
    svc.RecordResult(1, true);

    // New player (0 games) should get priority via FIFO for their first game
    var court1 = svc.Courts.First(c => c.Number == 1);
    var names = court1.TeamA!.Players.Concat(court1.TeamB!.Players).Select(p => p.Name).ToArray();
    Assert(names.Contains("NewPlayer"), "New player should be assigned their first game");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 12: Pause / resume
// ---------------------------------------------------------------------

static async Task Scenario12()
{
    var svc = NewService();
    svc.ChangeCourts(1);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 8);

    svc.StartSession();
    svc.PauseSession();
    Assert(svc.Session.IsPaused, "Session should be paused");

    // Finish game while paused - no new assignment should happen
    svc.RecordResult(1, true);
    AssertCourt(svc, 1, "(empty)");

    // Resume
    svc.PauseSession(); // toggles back
    Assert(!svc.Session.IsPaused, "Session should be resumed");
    Assert(svc.Courts.Count(c => c.IsActive) == 1, "Court should be filled after resume");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 13: Reset
// ---------------------------------------------------------------------

static async Task Scenario13()
{
    var svc = NewService();
    svc.ChangeCourts(2);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 8);

    svc.StartSession();
    svc.RecordResult(1, true);
    svc.RecordResult(2, true);
    Assert(svc.History.Count == 2, "History should have 2 games");

    svc.ResetSession();
    Assert(!svc.Session.IsActive, "Session should be inactive after reset");
    Assert(svc.History.Count == 0, "History should be empty after reset");
    Assert(svc.Players.All(p => p.GamesPlayed == 0 && p.Wins == 0 && p.Losses == 0), "Player stats should be reset");
    Assert(svc.Courts.All(c => !c.IsActive), "No courts should be active after reset");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 14: localStorage recovery
// ---------------------------------------------------------------------

static async Task Scenario14()
{
    var js = new FakeJSRuntime();

    // Direct test: verify FakeJS works
    js.SetItem("test-key", "test-value");
    await js.InvokeVoidAsync("localStorage.setItem", "test-key2", "test-value2");

    var svc1 = NewService(js);
    svc1.ChangeCourts(2);
    svc1.ChangeMode(GameMode.Doubles);
    AddPlayers(svc1, 8);
    svc1.StartSession();
    svc1.RecordResult(1, true);
    svc1.RecordResult(2, true);

    // Explicitly save to ensure persistence completes
    await svc1.SaveAsync();

    // New service sharing the same storage
    var svc2 = NewService(js);
    await svc2.InitializeAsync();

    Assert(svc2.Players.Count == 8, $"Players should be restored (got {svc2.Players.Count})");
    Assert(svc2.Session.IsActive, "Session should be restored as active");
    Assert(svc2.History.Count == 2, "History should be restored");
    Assert(svc2.Players.First(p => p.Name == "P1").Wins == 1, "P1 wins should be restored");
    Assert(svc2.Courts.Count(c => c.IsActive) == 2, "Active courts should be restored");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 16: Game-count fairness - low-game players prioritized
// ---------------------------------------------------------------------

static async Task Scenario16()
{
    var svc = NewService();
    svc.ChangeCourts(1);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 8);

    svc.StartSession();
    // Court 1: P1/P2 vs P3/P4 (P5-P8 waiting)
    svc.RecordResult(1, true); // P1,P2 win, P3,P4 lose

    // Court 1 re-fills with first-timers P5-P8 (0 games get priority)
    AssertCourtPlayers(svc, 1, "P5", "P6", "P7", "P8");

    // Continue until all 8 players have played at least once.
    for (var round = 0; round < 3; round++)
    {
        svc.RecordResult(1, true);
    }

    // All 8 players should have played roughly the same number of games.
    var games = svc.Players.Select(p => p.GamesPlayed).ToArray();
    var max = games.Max();
    var min = games.Min();
    Assert(max - min <= 2, $"Game count spread should be small, got max={max}, min={min}, values=[{string.Join(",", games)}]");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 17: Same-result courts - WIN vs WIN / LOSS vs LOSS
// ---------------------------------------------------------------------

static async Task Scenario17()
{
    var svc = NewService();
    svc.ChangeCourts(2);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 8);

    svc.StartSession();
    // Finish both courts simultaneously (pause to prevent re-fill)
    svc.PauseSession();
    svc.RecordResult(1, true); // P1,P2 win, P3,P4 lose
    svc.RecordResult(2, true); // P5,P6 win, P7,P8 lose

    // Capture result groups BEFORE resume (players have Win/Loss status now).
    var winners = svc.Players.Where(p => p.Status == PlayerStatus.Win).Select(p => p.Name).OrderBy(n => n).ToArray();
    var losers = svc.Players.Where(p => p.Status == PlayerStatus.Loss).Select(p => p.Name).OrderBy(n => n).ToArray();

    // Resume → assigns courts.
    svc.PauseSession();

    // Both courts should be filled with same-result groups.
    foreach (var court in svc.Courts.Where(c => c.IsActive))
    {
        var courtPlayers = court.TeamA!.Players.Concat(court.TeamB!.Players).Select(p => p.Name).ToArray();
        var allWin = courtPlayers.All(n => winners.Contains(n));
        var allLoss = courtPlayers.All(n => losers.Contains(n));
        Assert(allWin || allLoss,
            $"Court {court.Number} should be all WIN or all LOSS, got [{string.Join(",", courtPlayers)}]");
    }
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 18: Late player with 0 games gets priority
// ---------------------------------------------------------------------

static async Task Scenario18()
{
    var svc = NewService();
    svc.ChangeCourts(1);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 4);

    svc.StartSession();
    // Play several rounds so existing players have multiple games.
    for (var i = 0; i < 3; i++)
        svc.RecordResult(1, true);

    // Add a late player with 0 games.
    svc.AddPlayer("LatePlayer");

    // Finish the current game.
    svc.RecordResult(1, true);

    // The late player (0 games) must be in the next game.
    var court1 = svc.Courts.First(c => c.Number == 1);
    var names = court1.TeamA!.Players.Concat(court1.TeamB!.Players).Select(p => p.Name).ToArray();
    Assert(names.Contains("LatePlayer"), "Late player with 0 games should be prioritized");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 15: 10 courts
// ---------------------------------------------------------------------

static async Task Scenario15()
{
    var svc = NewService();
    svc.ChangeCourts(10);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 40);

    svc.StartSession();
    Assert(svc.Courts.Count == 10, "Should have 10 courts");
    Assert(svc.Courts.Count(c => c.IsActive) == 10, "All 10 courts should be active");
    Assert(svc.WaitingQueue.Count() == 0, "No players should be waiting");

    // Finish all courts
    for (var i = 1; i <= 10; i++)
        svc.RecordResult(i, true);

    Assert(svc.History.Count == 10, "History should have 10 games");
    Assert(svc.Players.All(p => p.GamesPlayed == 1), "All players should have played once");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 19: Court repair - swap players within a court
// ---------------------------------------------------------------------

static async Task Scenario19()
{
    var svc = NewService();
    svc.ChangeCourts(1);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 4);

    svc.StartSession();
    // Original: P1/P2 vs P3/P4
    AssertCourt(svc, 1, "P1 / P2 vs P3 / P4");

    // Repair: swap P3 into P2's position → P1/P3 vs P2/P4
    var p2 = svc.Players.First(p => p.Name == "P2");
    var p3 = svc.Players.First(p => p.Name == "P3");
    svc.RepairCourt(1, p2.Id, p3.Id);

    AssertCourt(svc, 1, "P1 / P3 vs P2 / P4");

    // All four original players remain, no duplicates
    AssertCourtPlayers(svc, 1, "P1", "P2", "P3", "P4");

    // Player stats unchanged
    Assert(svc.Players.All(p => p.GamesPlayed == 0 && p.Wins == 0 && p.Losses == 0),
        "Player stats should be unchanged after repair");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 20: Court repair - independent per court
// ---------------------------------------------------------------------

static async Task Scenario20()
{
    var svc = NewService();
    svc.ChangeCourts(2);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 8);

    svc.StartSession();
    // Court 1: P1/P2 vs P3/P4, Court 2: P5/P6 vs P7/P8
    AssertCourt(svc, 1, "P1 / P2 vs P3 / P4");
    AssertCourt(svc, 2, "P5 / P6 vs P7 / P8");

    // Repair Court 1 only: swap P3 into P2's position
    var p2 = svc.Players.First(p => p.Name == "P2");
    var p3 = svc.Players.First(p => p.Name == "P3");
    svc.RepairCourt(1, p2.Id, p3.Id);

    // Court 1 repaired
    AssertCourt(svc, 1, "P1 / P3 vs P2 / P4");

    // Court 2 unchanged
    AssertCourt(svc, 2, "P5 / P6 vs P7 / P8");

    // No cross-court duplication
    var court1Players = CourtPlayerNames(svc, 1);
    var court2Players = CourtPlayerNames(svc, 2);
    Assert(!court1Players.Intersect(court2Players).Any(), "No player should appear on both courts");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 21: Court repair - game result uses repaired teams
// ---------------------------------------------------------------------

static async Task Scenario21()
{
    var svc = NewService();
    svc.ChangeCourts(1);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 4);

    svc.StartSession();
    // Original: P1/P2 vs P3/P4
    // Repair: swap P3 into P2's position → P1/P3 vs P2/P4
    var p2 = svc.Players.First(p => p.Name == "P2");
    var p3 = svc.Players.First(p => p.Name == "P3");
    svc.RepairCourt(1, p2.Id, p3.Id);

    AssertCourt(svc, 1, "P1 / P3 vs P2 / P4");

    // Team A (P1/P3) wins
    svc.RecordResult(1, true);

    Assert(svc.Players.First(p => p.Name == "P1").Wins == 1, "P1 should have 1 win");
    Assert(svc.Players.First(p => p.Name == "P3").Wins == 1, "P3 should have 1 win");
    Assert(svc.Players.First(p => p.Name == "P2").Losses == 1, "P2 should have 1 loss");
    Assert(svc.Players.First(p => p.Name == "P4").Losses == 1, "P4 should have 1 loss");

    // History should reflect the repaired teams
    var game = svc.History.First();
    Assert(game.TeamA.DisplayName == "P1 / P3", "History Team A should be P1 / P3");
    Assert(game.TeamB.DisplayName == "P2 / P4", "History Team B should be P2 / P4");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 22: Player rename preserves identity and stats
// ---------------------------------------------------------------------

static async Task Scenario22()
{
    var svc = NewService();
    svc.ChangeCourts(1);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 4);

    svc.StartSession();
    svc.RecordResult(1, true); // P1/P2 win, P3/P4 lose

    var p1 = svc.Players.First(p => p.Name == "P1");
    var originalId = p1.Id;
    var originalWins = p1.Wins;
    var originalGames = p1.GamesPlayed;
    var originalEntryOrder = p1.EntryOrder;

    // Rename P1 → "John Smith"
    svc.RenamePlayer(p1.Id, "John Smith");

    var renamed = svc.Players.First(p => p.Id == originalId);
    Assert(renamed.Name == "John Smith", "Player name should be updated");
    Assert(renamed.Id == originalId, "Player identity should be preserved");
    Assert(renamed.Wins == originalWins, "Player wins should be preserved");
    Assert(renamed.GamesPlayed == originalGames, "Player games should be preserved");
    Assert(renamed.EntryOrder == originalEntryOrder, "Player entry order should be preserved");

    // No duplicate player created
    Assert(svc.Players.Count == 4, "Player count should remain 4");
    Assert(svc.Players.Count(p => p.Id == originalId) == 1, "Only one player with the original ID");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 23: Player rename updates everywhere
// ---------------------------------------------------------------------

static async Task Scenario23()
{
    var svc = NewService();
    svc.ChangeCourts(1);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 4);

    svc.StartSession();
    // P1/P2 vs P3/P4 on court 1
    var p1 = svc.Players.First(p => p.Name == "P1");
    svc.RenamePlayer(p1.Id, "John Smith");

    // Court should show the new name
    AssertCourt(svc, 1, "John Smith / P2 vs P3 / P4");

    // Record result and check history
    svc.RecordResult(1, true);
    var game = svc.History.First();
    Assert(game.TeamA.DisplayName.Contains("John Smith"), "History should show the renamed player");

    // Player list should show the new name
    Assert(svc.Players.Any(p => p.Name == "John Smith"), "Player list should show the renamed player");
    Assert(!svc.Players.Any(p => p.Name == "P1"), "Old name should not exist");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Scenario 24: Court repair does not alter queue or other courts
// ---------------------------------------------------------------------

static async Task Scenario24()
{
    var svc = NewService();
    svc.ChangeCourts(2);
    svc.ChangeMode(GameMode.Doubles);
    AddPlayers(svc, 12);

    svc.StartSession();
    // Court 1: P1/P2 vs P3/P4, Court 2: P5/P6 vs P7/P8
    // Next Up: P9/P10 vs P11/P12
    var nextUpBefore = svc.GetNextUp().Select(n => $"{n.TeamAName} vs {n.TeamBName}").ToArray();
    var waitingBefore = svc.WaitingQueue.Select(p => p.Name).OrderBy(n => n).ToArray();

    // Repair Court 1
    var p2 = svc.Players.First(p => p.Name == "P2");
    var p3 = svc.Players.First(p => p.Name == "P3");
    svc.RepairCourt(1, p2.Id, p3.Id);

    AssertCourt(svc, 1, "P1 / P3 vs P2 / P4");

    // Court 2 unchanged
    AssertCourt(svc, 2, "P5 / P6 vs P7 / P8");

    // Next Up unchanged
    var nextUpAfter = svc.GetNextUp().Select(n => $"{n.TeamAName} vs {n.TeamBName}").ToArray();
    Assert(nextUpBefore.SequenceEqual(nextUpAfter), "Next Up should be unchanged after repair");

    // Waiting Queue unchanged
    var waitingAfter = svc.WaitingQueue.Select(p => p.Name).OrderBy(n => n).ToArray();
    Assert(waitingBefore.SequenceEqual(waitingAfter), "Waiting Queue should be unchanged after repair");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------------
// Fake IJSRuntime
// ---------------------------------------------------------------------

public sealed class FakeJSRuntime : IJSRuntime
{
    public Dictionary<string, string> Storage { get; } = new();

    public void SetItem(string key, string value)
    {
        Storage[key] = value;
    }

    public string? GetItem(string key)
    {
        Storage.TryGetValue(key, out var value);
        return value;
    }

    public ValueTask<TResult> InvokeAsync<TResult>(string identifier, object?[]? args)
    {
        if (identifier == "localStorage.getItem")
        {
            var key = args?[0]?.ToString() ?? "";
            Storage.TryGetValue(key, out var value);
            return ValueTask.FromResult((TResult)(object)(value ?? ""));
        }

        if (identifier == "localStorage.setItem")
        {
            var key = args?[0]?.ToString() ?? "";
            var value = args?[1]?.ToString() ?? "";
            Storage[key] = value;
            return ValueTask.FromResult<TResult>(default!);
        }

        return ValueTask.FromResult<TResult>(default!);
    }

    public ValueTask<TResult> InvokeAsync<TResult>(string identifier, CancellationToken cancellationToken, object?[]? args)
        => InvokeAsync<TResult>(identifier, args);

    public ValueTask InvokeVoidAsync(string identifier, object?[]? args)
    {
        if (identifier == "localStorage.setItem")
        {
            var key = args?[0]?.ToString() ?? "";
            var value = args?[1]?.ToString() ?? "";
            Storage[key] = value;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask InvokeVoidAsync(string identifier, CancellationToken cancellationToken, object?[]? args)
        => InvokeVoidAsync(identifier, args);
}
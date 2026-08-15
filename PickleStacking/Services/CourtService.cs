using PickleStacking.Models;

namespace PickleStacking.Services;

public sealed class CourtService
{
    private readonly StackingService stacking;

    public CourtService(StackingService stacking)
    {
        this.stacking = stacking;
    }

    public IReadOnlyList<Court> All => stacking.Courts;

    public void SetCount(int count) => stacking.ChangeCourts(count);

    public void RecordResult(int courtNumber, bool teamAWon) => stacking.RecordResult(courtNumber, teamAWon);

    public void Repair(int courtNumber, string playerIdA, string playerIdB) => stacking.RepairCourt(courtNumber, playerIdA, playerIdB);
}
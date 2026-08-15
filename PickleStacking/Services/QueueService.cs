using PickleStacking.Models;

namespace PickleStacking.Services;

public sealed class QueueService
{
    private readonly StackingService stacking;

    public QueueService(StackingService stacking)
    {
        this.stacking = stacking;
    }

    public IEnumerable<Player> Waiting => stacking.WaitingQueue;

    public IReadOnlyList<NextUpPreview> NextUp => stacking.GetNextUp();
}
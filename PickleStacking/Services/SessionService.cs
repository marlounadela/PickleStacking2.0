using PickleStacking.Models;

namespace PickleStacking.Services;

public sealed class SessionService
{
    private readonly StackingService stacking;

    public SessionService(StackingService stacking)
    {
        this.stacking = stacking;
    }

    public SessionState State => stacking.Session;

    public void Start() => stacking.StartSession();

    public void Pause() => stacking.PauseSession();

    public void Reset() => stacking.ResetSession();

    public void SetMode(GameMode mode) => stacking.ChangeMode(mode);

    public void SetCourts(int count) => stacking.ChangeCourts(count);
}
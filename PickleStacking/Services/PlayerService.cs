using PickleStacking.Models;

namespace PickleStacking.Services;

public sealed class PlayerService
{
    private readonly StackingService stacking;

    public PlayerService(StackingService stacking)
    {
        this.stacking = stacking;
    }

    public IReadOnlyList<Player> All => stacking.Players;

    public void Add(string name) => stacking.AddPlayer(name);

    public void Remove(string playerId) => stacking.RemovePlayer(playerId);

    public void Rename(string playerId, string newName) => stacking.RenamePlayer(playerId, newName);

    public Player? Find(string playerId) => stacking.Players.FirstOrDefault(p => p.Id == playerId);
}
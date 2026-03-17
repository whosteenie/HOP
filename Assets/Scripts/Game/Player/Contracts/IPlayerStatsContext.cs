using Game.Match;

namespace Game.Player.Contracts {
    public interface IPlayerStatsContext {
        MatchPlayerStateProxy PlayerState { get; }
        float ObservedServerMovementSpeed { get; }
        ulong OwnerClientId { get; }
    }
}

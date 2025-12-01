using System.Collections.Generic;
using Unity.Services.Multiplayer;

namespace Network.Events {
    /// <summary>
    /// Event published when the player list changes in a session.
    /// </summary>
    public class PlayersChangedEvent : GameEvent {
        public readonly IReadOnlyList<IReadOnlyPlayer> Players;

        public PlayersChangedEvent(IReadOnlyList<IReadOnlyPlayer> players) {
            Players = players;
        }
    }

    /// <summary>
    /// Event published when a relay join code becomes available.
    /// </summary>
    public class RelayCodeAvailableEvent : GameEvent {
        public readonly string Code;

        public RelayCodeAvailableEvent(string code) {
            Code = code;
        }
    }

    /// <summary>
    /// Event published when the host disconnects.
    /// </summary>
    public class HostDisconnectedEvent : GameEvent {
    }

    /// <summary>
    /// Event published when the lobby is reset.
    /// </summary>
    public class LobbyResetEvent : GameEvent {
    }

    /// <summary>
    /// Event published when a session is joined.
    /// </summary>
    public class SessionJoinedEvent : GameEvent {
        public readonly string Code;

        public SessionJoinedEvent(string code) {
            Code = code;
        }
    }
}


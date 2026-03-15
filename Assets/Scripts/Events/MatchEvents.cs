namespace Network.Events {
    /// <summary>
    /// Event published when a match starts.
    /// </summary>
    public class MatchStartedEvent : GameEvent {
    }

    /// <summary>
    /// Event published when a match ends.
    /// </summary>
    public class MatchEndedEvent : GameEvent {
    }

    /// <summary>
    /// Event published during pre-match countdown.
    /// </summary>
    public class PreMatchCountdownEvent : GameEvent {
        public readonly int Seconds;

        public PreMatchCountdownEvent(int seconds) {
            Seconds = seconds;
        }
    }

    /// <summary>
    /// Event published while pre-match is waiting for all players before countdown starts.
    /// </summary>
    public class PreMatchWaitingForPlayersEvent : GameEvent {
        public readonly bool IsWaiting;

        public PreMatchWaitingForPlayersEvent(bool isWaiting) {
            IsWaiting = isWaiting;
        }
    }

    /// <summary>
    /// Event published when match time is updated.
    /// </summary>
    public class MatchTimeUpdatedEvent : GameEvent {
        public readonly int SecondsRemaining;

        public MatchTimeUpdatedEvent(int secondsRemaining) {
            SecondsRemaining = secondsRemaining;
        }
    }

    /// <summary>
    /// Event published when post-match sequence starts.
    /// </summary>
    public class PostMatchStartedEvent : GameEvent {
    }
}


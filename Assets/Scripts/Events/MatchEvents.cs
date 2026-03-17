namespace Events {
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

    public class MatchProgressionResolvedEvent : GameEvent {
        public readonly ulong PlayerClientId;
        public readonly int MatchCompletionXp;
        public readonly int BonusXp;
        public readonly bool DidWin;
        public readonly bool DidLose;
        public readonly string GamemodeId;
        public readonly int Placement;
        public readonly bool RecordMatchCompletion;
        public readonly float AverageSpeed;

        public MatchProgressionResolvedEvent(ulong playerClientId, int matchCompletionXp, int bonusXp, bool didWin,
            bool didLose, string gamemodeId, int placement, bool recordMatchCompletion, float averageSpeed) {
            PlayerClientId = playerClientId;
            MatchCompletionXp = matchCompletionXp;
            BonusXp = bonusXp;
            DidWin = didWin;
            DidLose = didLose;
            GamemodeId = gamemodeId;
            Placement = placement;
            RecordMatchCompletion = recordMatchCompletion;
            AverageSpeed = averageSpeed;
        }
    }

    public class MatchKingTimeAwardedEvent : GameEvent {
        public readonly ulong PlayerClientId;
        public readonly float Seconds;

        public MatchKingTimeAwardedEvent(ulong playerClientId, float seconds) {
            PlayerClientId = playerClientId;
            Seconds = seconds;
        }
    }

    public class RequestShowPostMatchXpEvent : GameEvent {
    }

    /// <summary>
     /// Event published when a player's podium visuals have been snapped and any dependent presentation should resync.
     /// </summary>
    public class PodiumVisualsSnappedEvent : GameEvent {
        public readonly ulong PlayerNetworkObjectId;

        public PodiumVisualsSnappedEvent(ulong playerNetworkObjectId) {
            PlayerNetworkObjectId = playerNetworkObjectId;
        }
    }
}


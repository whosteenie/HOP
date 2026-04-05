namespace Events {
    public enum MatchLifecycleState : byte {
        Initializing,
        WaitingForPlayers,
        Countdown,
        Active,
        PostMatch
    }

    public class MatchLifecycleStateChangedEvent : GameEvent {
        public readonly MatchLifecycleState Previous;
        public readonly MatchLifecycleState Current;

        public MatchLifecycleStateChangedEvent(MatchLifecycleState previous, MatchLifecycleState current) {
            Previous = previous;
            Current = current;
        }
    }

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

    public class PostMatchPodiumPrepareRequestedEvent : GameEvent {
        public readonly ulong PlayerClientId;

        public PostMatchPodiumPrepareRequestedEvent(ulong playerClientId) {
            PlayerClientId = playerClientId;
        }
    }

    public class PostMatchResetVelocityRequestedEvent : GameEvent {
        public readonly ulong PlayerClientId;

        public PostMatchResetVelocityRequestedEvent(ulong playerClientId) {
            PlayerClientId = playerClientId;
        }
    }

    public class PostMatchTeleportRequestedEvent : GameEvent {
        public readonly ulong PlayerClientId;
        public readonly UnityEngine.Vector3 Position;
        public readonly UnityEngine.Quaternion Rotation;

        public PostMatchTeleportRequestedEvent(ulong playerClientId, UnityEngine.Vector3 position,
            UnityEngine.Quaternion rotation) {
            PlayerClientId = playerClientId;
            Position = position;
            Rotation = rotation;
        }
    }

    public class PostMatchSnapVisualsRequestedEvent : GameEvent {
        public readonly ulong PlayerClientId;

        public PostMatchSnapVisualsRequestedEvent(ulong playerClientId) {
            PlayerClientId = playerClientId;
        }
    }

    public class PostMatchWorldModelVisibilityEvent : GameEvent {
        public readonly ulong PlayerClientId;
        public readonly bool Visible;

        public PostMatchWorldModelVisibilityEvent(ulong playerClientId, bool visible) {
            PlayerClientId = playerClientId;
            Visible = visible;
        }
    }

    public class PostMatchGameplayCameraEvent : GameEvent {
        public readonly ulong PlayerClientId;
        public readonly bool Active;

        public PostMatchGameplayCameraEvent(ulong playerClientId, bool active) {
            PlayerClientId = playerClientId;
            Active = active;
        }
    }

    public class PostMatchControlLockRequestedEvent : GameEvent {
        public readonly ulong PlayerClientId;
        public readonly bool Locked;
        public readonly bool LockLook;
        public readonly bool ResetVelocity;

        public PostMatchControlLockRequestedEvent(ulong playerClientId, bool locked, bool lockLook,
            bool resetVelocity) {
            PlayerClientId = playerClientId;
            Locked = locked;
            LockLook = lockLook;
            ResetVelocity = resetVelocity;
        }
    }

    public class PostMatchSniperOverlayDisableEvent : GameEvent {
        public readonly ulong PlayerClientId;
        public readonly bool PlayZoomSound;

        public PostMatchSniperOverlayDisableEvent(ulong playerClientId, bool playZoomSound) {
            PlayerClientId = playerClientId;
            PlayZoomSound = playZoomSound;
        }
    }
}


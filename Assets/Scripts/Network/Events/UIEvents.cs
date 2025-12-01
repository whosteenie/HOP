namespace Network.Events {
    /// <summary>
    /// Event published when player health should be updated in the HUD.
    /// </summary>
    public class UpdateHealthEvent : GameEvent {
        public readonly float Current;
        public readonly float Max;

        public UpdateHealthEvent(float current, float max) {
            Current = current;
            Max = max;
        }
    }

    /// <summary>
    /// Event published when weapon ammo should be updated in the HUD.
    /// </summary>
    public class UpdateAmmoEvent : GameEvent {
        public readonly int Current;
        public readonly int Max;

        public UpdateAmmoEvent(int current, int max) {
            Current = current;
            Max = max;
        }
    }

    /// <summary>
    /// Event published when tag status should be updated in the HUD.
    /// </summary>
    public class UpdateTagStatusEvent : GameEvent {
        public readonly bool IsTagged;

        public UpdateTagStatusEvent(bool isTagged) {
            IsTagged = isTagged;
        }
    }

    /// <summary>
    /// Event published when kill multiplier should be updated in the HUD.
    /// </summary>
    public class UpdateMultiplierEvent : GameEvent {
        public readonly float Current;
        public readonly float Max;

        public UpdateMultiplierEvent(float current, float max) {
            Current = current;
            Max = max;
        }
    }

    /// <summary>
    /// Event published when the HUD should be shown.
    /// </summary>
    public class ShowHUDEvent : GameEvent {
    }

    /// <summary>
    /// Event published when the HUD should be hidden.
    /// </summary>
    public class HideHUDEvent : GameEvent {
    }

    /// <summary>
    /// Event published when a kill feed entry should be added.
    /// </summary>
    public class AddKillFeedEntryEvent : GameEvent {
        public readonly string Killer;
        public readonly string Victim;
        public readonly bool IsLocalKiller;
        public readonly ulong KillerId;
        public readonly ulong VictimId;
        public readonly bool WasKill;

        public AddKillFeedEntryEvent(string killer, string victim, bool isLocalKiller, ulong killerId, ulong victimId, bool wasKill = true) {
            Killer = killer;
            Victim = victim;
            IsLocalKiller = isLocalKiller;
            KillerId = killerId;
            VictimId = victimId;
            WasKill = wasKill;
        }
    }

    /// <summary>
    /// Event published when the kill feed should be shown.
    /// </summary>
    public class ShowKillFeedEvent : GameEvent {
    }

    /// <summary>
    /// Event published when the kill feed should be hidden.
    /// </summary>
    public class HideKillFeedEvent : GameEvent {
    }

    /// <summary>
    /// Event published when match time should be updated in the scoreboard.
    /// </summary>
    public class SetMatchTimeEvent : GameEvent {
        public readonly int Seconds;

        public SetMatchTimeEvent(int seconds) {
            Seconds = seconds;
        }
    }

    /// <summary>
    /// Event published when the scoreboard should be shown.
    /// </summary>
    public class ShowScoreboardEvent : GameEvent {
    }

    /// <summary>
    /// Event published when the scoreboard should be hidden.
    /// </summary>
    public class HideScoreboardEvent : GameEvent {
    }
}


namespace Events {
    /// <summary>
    /// Event published when display resolution is applied.
    /// </summary>
    public class ResolutionChangedEvent : GameEvent {
        public readonly int Width;
        public readonly int Height;

        public ResolutionChangedEvent(int width, int height) {
            Width = width;
            Height = height;
        }
    }

    /// <summary>
    /// Event published when persisted game settings are saved.
    /// </summary>
    public class GameSettingsChangedEvent : GameEvent {
    }

    /// <summary>
    /// Event published when social settings are saved.
    /// </summary>
    public class SocialSettingsChangedEvent : GameEvent {
    }

    /// <summary>
    /// Event published when a player's mute state changes.
    /// </summary>
    public class PlayerMuteChangedEvent : GameEvent {
        public readonly string PlayerId;
        public readonly bool IsMuted;

        public PlayerMuteChangedEvent(string playerId, bool isMuted) {
            PlayerId = playerId;
            IsMuted = isMuted;
        }
    }

    /// <summary>
    /// Event published when key bindings are saved and applied.
    /// </summary>
    public class BindingsAppliedEvent : GameEvent {
    }
}

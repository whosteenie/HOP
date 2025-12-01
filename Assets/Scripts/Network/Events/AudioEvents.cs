using Game.Audio;
using UnityEngine;

namespace Network.Events {
    /// <summary>
    /// Event published when a UI sound should be played.
    /// </summary>
    public class PlayUISoundEvent : GameEvent {
        public readonly SfxKey Key;

        public PlayUISoundEvent(SfxKey key) {
            Key = key;
        }
    }

    /// <summary>
    /// Event published when a world sound should be played at a specific position.
    /// </summary>
    public class PlayWorldSoundEvent : GameEvent {
        public readonly SfxKey Key;
        public readonly Transform Parent;
        public readonly Vector3 Position;
        public readonly bool AllowOverlap;

        public PlayWorldSoundEvent(SfxKey key, Transform parent, Vector3 position, bool allowOverlap) {
            Key = key;
            Parent = parent;
            Position = position;
            AllowOverlap = allowOverlap;
        }
    }

    /// <summary>
    /// Event published when a specific sound should be stopped.
    /// </summary>
    public class StopSoundEvent : GameEvent {
        public readonly SfxKey Key;

        public StopSoundEvent(SfxKey key) {
            Key = key;
        }
    }

    /// <summary>
    /// Event published when all sounds should be stopped.
    /// </summary>
    public class StopAllSoundsEvent : GameEvent {
    }
}


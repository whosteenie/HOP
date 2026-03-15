using Unity.Netcode;
using UnityEngine;

namespace Network.Events {
    // Local-only playback events (no networking).
    public sealed class PlayLocalSoundIdEvent : GameEvent {
        public readonly string SoundId;

        public PlayLocalSoundIdEvent(string soundId) {
            SoundId = soundId;
        }
    }

    public sealed class PlayLocalWorldSoundIdEvent : GameEvent {
        public readonly string SoundId;
        public readonly Vector3 Position;
        public readonly bool AllowOverlap;

        public PlayLocalWorldSoundIdEvent(string soundId, Vector3 position, bool allowOverlap) {
            SoundId = soundId;
            Position = position;
            AllowOverlap = allowOverlap;
        }
    }

    public sealed class PlayLocalAttachedSoundIdEvent : GameEvent {
        public readonly string SoundId;
        public readonly Transform Parent;
        public readonly bool AllowOverlap;

        public PlayLocalAttachedSoundIdEvent(string soundId, Transform parent, bool allowOverlap) {
            SoundId = soundId;
            Parent = parent;
            AllowOverlap = allowOverlap;
        }
    }

    public sealed class StopLocalSoundIdEvent : GameEvent {
        public readonly string SoundId;

        public StopLocalSoundIdEvent(string soundId) {
            SoundId = soundId;
        }
    }

    public sealed class StopAllLocalSoundsEvent : GameEvent {
    }

    // Convenience network request events (route through the local player's NetworkAudioRelay).
    public sealed class RequestNetworkWorldSoundIdEvent : GameEvent {
        public readonly string SoundId;
        public readonly Vector3 Position;
        public readonly bool AllowOverlap;
        public readonly uint Seed;

        public RequestNetworkWorldSoundIdEvent(string soundId, Vector3 position, bool allowOverlap, uint seed = 0) {
            SoundId = soundId;
            Position = position;
            AllowOverlap = allowOverlap;
            Seed = seed;
        }
    }

    public sealed class RequestNetworkAttachedSoundIdEvent : GameEvent {
        public readonly string SoundId;
        public readonly NetworkObjectReference AttachTo;
        public readonly bool AllowOverlap;
        public readonly uint Seed;

        public RequestNetworkAttachedSoundIdEvent(string soundId, NetworkObjectReference attachTo, bool allowOverlap,
            uint seed = 0) {
            SoundId = soundId;
            AttachTo = attachTo;
            AllowOverlap = allowOverlap;
            Seed = seed;
        }
    }
}


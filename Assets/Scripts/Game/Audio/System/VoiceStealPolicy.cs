using System;

namespace Game.Audio.System {
    [Serializable]
    public enum VoiceStealPolicy : byte {
        DropNew = 0,
        StealLowestPriorityThenOldest = 1
    }
}


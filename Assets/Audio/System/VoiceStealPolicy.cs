using System;

namespace Game.Audio2 {
    [Serializable]
    public enum VoiceStealPolicy : byte {
        DropNew = 0,
        StealLowestPriorityThenOldest = 1
    }
}


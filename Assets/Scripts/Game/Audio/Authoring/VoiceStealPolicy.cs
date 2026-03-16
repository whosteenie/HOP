using System;

namespace Game.Audio.Authoring {
    [Serializable]
    public enum VoiceStealPolicy : byte {
        DropNew = 0,
        StealLowestPriorityThenOldest = 1
    }
}
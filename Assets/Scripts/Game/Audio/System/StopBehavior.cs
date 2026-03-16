using System;

namespace Game.Audio.System {
    [Serializable]
    public enum StopBehavior : byte {
        NotStoppable = 0,
        StopLast = 1,
        StopAll = 2
    }
}


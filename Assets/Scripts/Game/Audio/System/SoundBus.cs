using System;

namespace Game.Audio.System {
    [Serializable]
    public enum SoundBus : byte {
        Master = 0,
        Sfx = 1,
        Ui = 2,
        Weapons = 3,
        Foley = 4,
        Ambience = 5,
        Music = 6,
        Gameplay = 7
    }
}


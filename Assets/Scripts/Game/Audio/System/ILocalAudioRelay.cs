using Unity.Netcode;
using UnityEngine;

namespace Game.Audio.System {
    internal interface ILocalAudioRelay {
        void RequestPlay(string soundId, Vector3 worldPosition, bool allowOverlap = true, uint seed = 0);
        void RequestPlayAttached(string soundId, NetworkObjectReference attachTo, bool allowOverlap = true, uint seed = 0);
        void RequestStop(string soundId);
    }
}


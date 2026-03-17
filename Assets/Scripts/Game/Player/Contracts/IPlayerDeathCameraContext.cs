using Unity.Cinemachine;
using UnityEngine;

namespace Game.Player.Contracts {
    public interface IPlayerDeathCameraContext {
        CinemachineCamera FpCamera { get; }
        CinemachineCamera DeathCamera { get; }
        SkinnedMeshRenderer PlayerMesh { get; }
    }
}

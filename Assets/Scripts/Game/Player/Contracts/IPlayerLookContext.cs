using Unity.Cinemachine;
using UnityEngine;

namespace Game.Player.Contracts {
    public interface IPlayerLookContext {
        Transform PlayerTransform { get; }
        CinemachineCamera FpCamera { get; }
        Vector2 LookInput { get; }
        Vector3 HorizontalVelocity { get; }
        bool IsRagdoll { get; }

        void UpdateTurnAnimationFromLook(float yawDelta);
    }
}

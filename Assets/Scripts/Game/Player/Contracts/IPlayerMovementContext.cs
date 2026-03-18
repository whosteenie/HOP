using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Contracts {
    public interface IPlayerMovementContext {
        Transform PlayerTransform { get; }
        CharacterController CharacterController { get; }
        CinemachineCamera FpCamera { get; }
        Transform FpCameraTransform { get; }
        Camera WeaponCamera { get; }
        NetworkObject NetworkObject { get; }
        LayerMask WorldLayer { get; }
        LayerMask PlayerLayer { get; }
        LayerMask EnemyLayer { get; }
        NetworkVariable<bool> NetIsCrouching { get; }
        NetworkVariable<bool> NetIsSliding { get; }
        NetworkVariable<bool> NetIsWallRunning { get; }
        NetworkVariable<bool> NetIsRightWallRun { get; }
        NetworkVariable<float> NetWallRunDirection { get; }
        Vector2 MoveInput { get; }
        bool SprintInput { get; }
        bool CrouchInput { get; }
        bool IsJumpHeld { get; }
        bool IsDead { get; }
        bool IsGrounded { get; }
        bool IsHoldingHopball { get; }
        bool IsTagged { get; }
        bool IsPreMatchMovementLocked { get; }
        bool IsGunTagMode { get; }
        Vector3 Position { get; }
        Vector3 FullVelocity { get; }
        Color CurrentBaseColor { get; }
        NetworkVariable<Vector4> PlayerBaseColorNetwork { get; }
        Game.Weapon.Core.Weapon CurrentWeapon { get; }

        void SetLookTilt(float tilt);
        void SetCrouchingAnimation(bool isCrouching);
        void SetSlidingAnimationState(bool isSliding, bool playTrigger = false);
        void TriggerJumpAnimation();
        void TriggerMantleAnimation();
        GameObject GetCurrentFpWeapon();
    }
}

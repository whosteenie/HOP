using Game.Weapon.Manager;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Contracts {
    public interface IPlayerVisualContext {
        bool IsOwner { get; }
        bool IsDead { get; }
        bool IsGrounded { get; }
        bool IsPostMatchFlowStarted { get; }
        bool IsWallRunning { get; }
        bool IsRightWallRunning { get; }
        bool IsTagged { get; }
        ulong NetworkObjectId { get; }
        NetworkObject NetworkObject { get; }
        Transform PlayerTransform { get; }
        Animator PlayerAnimator { get; }
        CinemachineCamera FpCamera { get; }
        Camera WeaponCamera { get; }
        WeaponManager WeaponManager { get; }
        CharacterController CharacterController { get; }
        SkinnedMeshRenderer PlayerMesh { get; }
        GameObject PlayerModelRoot { get; }
        Transform WorldWeaponSocket { get; }
        Color TaggedGlowColor { get; }
        LayerMask WorldLayer { get; }
        NetworkVariable<bool> NetIsJumping { get; }
        NetworkVariable<bool> NetIsFalling { get; }
        NetworkVariable<bool> NetIsSliding { get; }
        NetworkVariable<bool> NetIsWallRunning { get; }
        NetworkVariable<bool> NetIsRightWallRun { get; }
        NetworkVariable<float> NetWallRunDirection { get; }
        NetworkVariable<int> JumpAnimationSequence { get; }
        NetworkVariable<int> LandAnimationSequence { get; }
        NetworkVariable<int> MantleAnimationSequence { get; }

        void PlayWalkSound();
        void PlayRunSound();
        void SetWeaponCameraEnabled(bool enabled);
    }
}

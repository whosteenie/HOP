using Game.Audio.System;
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
        ulong NetworkObjectId { get; }
        NetworkObject NetworkObject { get; }
        Transform PlayerTransform { get; }
        Animator PlayerAnimator { get; }
        NetworkAudioRelay AudioRelay { get; }
        CinemachineCamera FpCamera { get; }
        WeaponManager WeaponManager { get; }
        SkinnedMeshRenderer PlayerMesh { get; }
        GameObject PlayerModelRoot { get; }
        Transform WorldWeaponSocket { get; }
        GameObject[] WorldWeaponPrefabs { get; }
        Color TaggedGlowColor { get; }
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
    }
}

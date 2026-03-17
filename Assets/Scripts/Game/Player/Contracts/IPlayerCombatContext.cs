using Game.Spawning;
using Game.Weapon.Manager;
using Game.Weapon.Presentation;
using Network.Components;
using Unity.Collections;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Contracts {
    public interface IPlayerCombatContext {
        Transform PlayerTransform { get; }
        CharacterController CharacterController { get; }
        ClientNetworkTransform ClientNetworkTransform { get; }
        WeaponManager WeaponManager { get; }
        GameObject PlayerModelRoot { get; }
        Transform WorldWeaponSocket { get; }
        Animator PlayerAnimator { get; }
        CinemachineCamera FpCamera { get; }
        WeaponCameraController WeaponCameraController { get; }
        CinemachineImpulseSource ImpulseSource { get; }
        Vector3 FullVelocity { get; }
        bool IsGrounded { get; }
        NetworkVariable<float> NetHealth { get; }
        NetworkVariable<bool> NetIsDead { get; }
        NetworkVariable<int> Deaths { get; }
        NetworkVariable<float> DamageDealt { get; }
        NetworkVariable<int> Kills { get; }
        NetworkVariable<int> Assists { get; }
        NetworkVariable<FixedString64Bytes> PlayerName { get; }
        bool IsHoldingHopball { get; }
        float BaseFov { get; }
        SpawnPoint.Team CurrentTeam { get; }

        void SetOutOfBoundsGraceWindow(float seconds);
        void ResetLookPitchFromRespawn();
        void ClearLookInput();
        Vector2 ResampleHeldMovementInputFromRespawn(string reason = "Unknown");
        void ResetWeaponState(bool resetAllAmmo = false, bool switchToWeapon0 = false, bool updateHUD = false);
        void ResetVelocity();
        void SetRenderersEnabled(bool enabled);
        void InvalidateRendererCache();
        void ForceRendererBoundsUpdate();
        void ApplyDeathShadowState(bool wasHoldingHopball);
        void ApplyOwnerDefaultShadowState();
        void ApplyVisibleShadowState();
        void ResetSpawnAnimationTime();
        void PlayHitEffects(Vector3 hitPoint, float amount);
        float GetOutOfBoundsKillY();
        bool IsYLevelOutOfBoundsKillEnabled();
    }
}

using Game.Audio.System;
using Game.Weapon.Manager;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapon.Core {
    public interface IWeaponOwnerContext {
        bool IsOwner { get; }
        bool IsDead { get; }
        bool IsGrounded { get; }
        bool IsSliding { get; }
        bool IsWallRunning { get; }
        bool IsSniperOverlayActive { get; }
        bool SprintInput { get; }
        Vector2 MoveInput { get; }
        float CurrentPitch { get; }
        float MaxSpeed { get; }
        Vector3 HorizontalVelocity { get; }
        Vector3 FullVelocity { get; }
        Vector3 Position { get; }
        Vector3 SniperMuzzleCameraOffset { get; }
        Transform PlayerTransform { get; }
        Transform FpCameraTransform { get; }
        Animator PlayerAnimator { get; }
        LayerMask EnemyLayer { get; }
        LayerMask WorldLayer { get; }
        NetworkObject NetworkObject { get; }
        ulong OwnerClientId { get; }
        WeaponDamageRelay DamageRelay { get; }
        WeaponFxRelay FxRelay { get; }
        NetworkAudioRelay AudioRelay { get; }
        WeaponManager WeaponManager { get; }
        Weapon CurrentWeapon { get; }
        NetworkVariable<float> ReplicatedDamageMultiplierState { get; }
    }

    public interface IWeaponCombatParticipant {
        bool IsOwner { get; }
        bool IsDead { get; }
        ulong OwnerClientId { get; }
        NetworkObject NetworkObject { get; }
        WeaponManager WeaponManager { get; }
        WeaponDamageRelay DamageRelay { get; }
        bool ApplyDamageServerAuth(float damage, Vector3 hitPoint, Vector3 hitDirection, ulong attackerClientId,
            string bodyPartTag, bool isHeadshot, string weaponId);
        void ProcessRespawnRequest();
    }
}

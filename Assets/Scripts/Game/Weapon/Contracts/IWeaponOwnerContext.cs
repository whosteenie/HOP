using Unity.Netcode;
using UnityEngine;

namespace Game.Weapon.Contracts {
    public enum WeaponAmmoSyncReason : byte {
        ReloadStarted,
        ReloadSingleRound,
        ReloadCompleted,
        ReloadCanceled,
        RefillCurrentWeapon
    }

    public interface IWeaponDamageRelay {
        void SendHitConfirmToOwner(bool wasKill);
    }

    public interface IWeaponFxRelay {
        void RequestShotFx(Vector3 endPoint, Vector3 hitNormal, bool madeImpact,
            bool hitPlayer, NetworkObjectReference hitPlayerRef, bool playMuzzleFlash = true,
            Vector3 shooterVelocity = default);
    }

    public interface IWeaponFacade {
        bool TryGetRemoteWorldMuzzlePosition(out Vector3 muzzlePosition);
        void PlayNetworkedMuzzleFlash(Vector3 endPoint);
        void SpawnTracerLocal(Vector3 start, Vector3 end, Vector3 hitNormal, bool madeImpact, bool hitPlayer,
            NetworkObjectReference hitPlayerRef = default, Vector3 shooterVelocity = default);
    }

    public interface IWeaponManagerFacade {
        int CurrentWeaponIndex { get; }
        bool IsPullingOut { get; }
        bool IsPostMatchFlowActive { get; }
        Camera WeaponCamera { get; }
        bool RegisterServerShot(int weaponIndex, ulong shotId, float clientShotTime, out string reason);
        bool ValidateServerHitClaim(int weaponIndex, ulong shotId, out string reason);
        bool TryComputeServerDamage(int weaponIndex, Vector3 hitPoint, out float damage, out string reason);
        string GetWeaponIdByIndex(int index);
        bool IsFriendlyFireAgainst(ulong victimClientId);
        void HandlePullOutCompleted();
        void ReportWeaponStateSync(int weaponIndex, WeaponAmmoSyncReason reason, int localAmmoAfterEvent);
        void ReportShotFired(int weaponIndex, ulong shotId, float clientShotTime);
        void RegisterServerShotAndLogOnAuthority(int weaponIndex, ulong shotId, float clientShotTime);
        void ProcessWeaponSwitchRequest(int newIndex);
        void UpdateServerWeaponState(int weaponIndex, byte reason, int localAmmoAfterEvent);
        void ResetAllWeaponAmmoOnAuthority();
    }

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
        IWeaponDamageRelay DamageRelay { get; }
        IWeaponFxRelay FxRelay { get; }
        IWeaponManagerFacade WeaponManager { get; }
        IWeaponFacade CurrentWeapon { get; }
        NetworkVariable<float> ReplicatedDamageMultiplierState { get; }
    }

    public interface IWeaponCombatParticipant {
        bool IsOwner { get; }
        bool IsDead { get; }
        ulong OwnerClientId { get; }
        NetworkObject NetworkObject { get; }
        IWeaponManagerFacade WeaponManager { get; }
        IWeaponDamageRelay DamageRelay { get; }
        bool ApplyDamageServerAuth(float damage, Vector3 hitPoint, Vector3 hitDirection, ulong attackerClientId,
            string bodyPartTag, bool isHeadshot, string weaponId);
        void ProcessRespawnRequest();
    }
}

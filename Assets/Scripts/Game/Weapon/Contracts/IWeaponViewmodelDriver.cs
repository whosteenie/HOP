using System.Collections.Generic;
using UnityEngine;

namespace Game.Weapon.Contracts {
    public interface IWeaponDataRuntime {
        int InstanceId { get; }
        string AssetName { get; }
        string WeaponName { get; }
        int KinemationSpecialHandling { get; }
        int KinemationGrappleWeaponIndex { get; }
        int[] KinemationReloadEventSoundIndices { get; }
    }

    public interface IWeaponRuntimeContext {
        IWeaponDataRuntime GetCurrentWeaponData();
        void HandleKinemationEquipCompleted();
    }

    public interface IWeaponViewmodelDriver {
        void InitializeIfNeeded(int renderLayer);
        void ClearPendingWeaponSoundEvents();
        Transform GetMuzzleTransform();
        void SyncActiveAmmo(int authoritativeAmmo);
        void SyncLocomotion(Vector2 moveInput, bool sprinting, bool tacticalSprinting, bool isGrounded,
            float lookPitchDegrees);
        void NotifyReloadCanceledByShot();
        void AbortReloadAndSyncAmmo(int authoritativeAmmo);
        void ResetReloadTracking();
        int ConsumeReloadSingleEventCount();
        bool ConsumeReloadCompleteEvent();
        bool IsReloadSequenceInProgress();
        bool HasKinemationFireSound();
        string GetKinemationFireSoundId();
        bool HasAnyKinemationEventSound();
        void ConsumeWeaponEventSoundIndices(List<int> destination);
        bool TryGetKinemationSoundId(int clipIndex, out string soundId);
        int GetKinemationSoundClipCount();
        bool IsLikelyReloadEventSoundClip(int clipIndex);
        bool AreKinemationSoundsEnabled();
        bool IsKinemationSoundRoutingEnabled();
        void PlayFireAnimation(int authoritativeAmmoBeforeShot = -1);
        void PlayReloadAnimation();
        void PlayReloadCompleteAnimation();
    }
}
using Diagnostics;
using Game.Weapon.Manager;
using UnityEngine;

namespace Game.Weapon.Core {
    internal sealed class WeaponReload {
        private readonly Weapon _weapon;

        public WeaponReload(Weapon weapon) {
            _weapon = weapon;
        }

        public bool TryAutoReloadFromEmptyClick() {
            if(_weapon.CurrentAmmo != 0) return false;
            if(_weapon.Reloading) return false;
            if(!_weapon.AutoReloadArmed) return false;
            if(!CanReload()) return false;

            _weapon.AutoReloadArmed = false;
            StartReload();
            return true;
        }

        public void StartReload() {
            if(_weapon.CurrentWeaponData && _weapon.Manager != null && !_weapon.Manager.IsPullingOut &&
               _weapon.KinDriver == null) {
                DevLog.LogError(
                    $"[Weapon][KIN-Strict] Reload blocked: missing KinFpWeaponDriver for '{(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")}'.",
                    _weapon);
                return;
            }

            if(!CanReload()) return;

            _weapon.AutoReloadArmed = false;
            _weapon.Reloading = true;

            _weapon.ReloadExpectedCompleteTime = Time.time + Weapon.KinemationReloadFallbackSeconds;
            _weapon.KinemationReloadFallbackDeadline = _weapon.ReloadExpectedCompleteTime;
            _weapon.SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason.ReloadStarted);
            _weapon.PlayReloadEffectsInternal();
        }

        public void InterruptReloadForShot() {
            if(!_weapon.Reloading || _weapon.CurrentWeaponData == null || _weapon.CurrentWeaponData.useMagReload) return;

            ConsumePendingSingleRoundEvents();

            if(_weapon.KinDriver != null) {
                _weapon.KinDriver.NotifyReloadCanceledByShot();
            }

            CancelReload();
        }

        public void CancelReloadForWeaponSwitch() {
            CancelReload();
        }

        public void CancelReload() {
            if(!_weapon.Reloading) return;

            if(!_weapon.UseKinemationInternalSoundsInternal() &&
               !_weapon.ShouldSuppressLegacyReloadSoundInternal() &&
               _weapon.OwnerContext is { IsOwner: true } &&
               _weapon.AudioRelay != null) {
                var soundId = _weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.reloadSoundId : "";
                if(!string.IsNullOrWhiteSpace(soundId)) {
                    _weapon.AudioRelay.RequestStop(soundId);
                }
            }

            if(_weapon.KinDriver != null) {
                _weapon.StopKinemationEventSoundsInternal();
                _weapon.KinDriver.AbortReloadAndSyncAmmo(_weapon.CurrentAmmo);
            }

            _weapon.Reloading = false;
            _weapon.ReloadExpectedCompleteTime = float.PositiveInfinity;
            _weapon.KinemationReloadFallbackDeadline = float.PositiveInfinity;
            if(_weapon.KinDriver != null) {
                _weapon.KinDriver.ResetReloadTracking();
            }

            _weapon.ExitReloadAnimationInternal();
            _weapon.SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason.ReloadCanceled);
        }

        public void ResetWeapon() {
            if(!_weapon.CurrentWeaponData) return;

            _weapon.CurrentAmmo = _weapon.GetMagCapacityInternal();
            _weapon.Reloading = false;
            _weapon.LastFireTime = Time.time;
            _weapon.AutoReloadArmed = false;
            _weapon.ReloadExpectedCompleteTime = float.PositiveInfinity;
            _weapon.KinemationReloadFallbackDeadline = float.PositiveInfinity;
            if(_weapon.KinDriver != null) {
                _weapon.KinDriver.ResetReloadTracking();
            }

            _weapon.SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason.RefillCurrentWeapon);
        }

        public void PrepareForPostMatchPodium() {
            if(_weapon.CurrentWeaponData == null) return;

            if(_weapon.Reloading) {
                CancelReload();
            }

            _weapon.CurrentAmmo = _weapon.GetMagCapacityInternal();
            if(_weapon.KinDriver != null) {
                _weapon.KinDriver.SyncActiveAmmo(_weapon.CurrentAmmo);
            }

            _weapon.PublishAmmoToHudInternal();
            _weapon.SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason.RefillCurrentWeapon);
        }

        public void RunReloadWatchdog() {
            if(Time.time < _weapon.NextReloadRecoveryAllowedTime) return;
            if(!_weapon.Reloading) return;
            if(Time.time <= _weapon.ReloadExpectedCompleteTime) return;

            if(_weapon.CurrentWeaponData != null && !_weapon.CurrentWeaponData.useMagReload) {
                CompleteKinemationPartialReload();
            } else {
                CompleteReload();
            }

            _weapon.NextReloadRecoveryAllowedTime = Time.time + Weapon.ReloadRecoveryCooldownSeconds;
        }

        public void UpdateKinemationReloadState() {
            if(!_weapon.Reloading || _weapon.KinDriver == null) return;

            var reloadSingleEvents = _weapon.KinDriver.ConsumeReloadSingleEventCount();
            for(var i = 0; i < reloadSingleEvents; i++) {
                HandleSingleRoundReload();
            }

            if(_weapon.KinDriver.ConsumeReloadCompleteEvent()) {
                CompleteReload();
                return;
            }

            if(!_weapon.KinDriver.IsReloadSequenceInProgress()) {
                if(_weapon.CurrentWeaponData != null && !_weapon.CurrentWeaponData.useMagReload) {
                    CompleteKinemationPartialReload();
                } else {
                    CompleteReload();
                }

                return;
            }

            if(Time.time <= _weapon.KinemationReloadFallbackDeadline) return;
            if(_weapon.CurrentWeaponData != null && !_weapon.CurrentWeaponData.useMagReload) {
                CompleteKinemationPartialReload();
            } else {
                CompleteReload();
            }

            _weapon.NextReloadRecoveryAllowedTime = Time.time + Weapon.ReloadRecoveryCooldownSeconds;
        }

        private bool CanReload() {
            if(!_weapon.CurrentWeaponData || _weapon.Manager == null || _weapon.Manager.IsPullingOut) return false;
            if(_weapon.KinDriver == null) return false;
            return _weapon.CurrentAmmo < _weapon.GetMagCapacityInternal() && !_weapon.Reloading;
        }

        private void CompleteReload() {
            if(!_weapon.CurrentWeaponData) return;
            _weapon.CurrentAmmo = _weapon.GetMagCapacityInternal();
            _weapon.Reloading = false;
            _weapon.AutoReloadArmed = false;
            _weapon.ReloadExpectedCompleteTime = float.PositiveInfinity;
            _weapon.KinemationReloadFallbackDeadline = float.PositiveInfinity;
            if(_weapon.KinDriver != null) {
                _weapon.KinDriver.ResetReloadTracking();
            }

            _weapon.ExitReloadAnimationInternal();
            _weapon.PublishAmmoToHudInternal();
            _weapon.SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason.ReloadCompleted);
        }

        private void HandleSingleRoundReload() {
            if(!_weapon.Reloading || _weapon.CurrentWeaponData == null) return;
            if(_weapon.CurrentWeaponData.useMagReload) return;
            var magCapacity = _weapon.GetMagCapacityInternal();
            if(_weapon.CurrentAmmo >= magCapacity) return;

            _weapon.CurrentAmmo = Mathf.Min(_weapon.CurrentAmmo + 1, magCapacity);
            _weapon.PublishAmmoToHudInternal(magCapacity);
            _weapon.SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason.ReloadSingleRound);
        }

        private void CompleteKinemationPartialReload() {
            _weapon.Reloading = false;
            _weapon.AutoReloadArmed = false;
            _weapon.ReloadExpectedCompleteTime = float.PositiveInfinity;
            _weapon.KinemationReloadFallbackDeadline = float.PositiveInfinity;
            if(_weapon.KinDriver != null) {
                _weapon.KinDriver.ResetReloadTracking();
            }

            _weapon.ExitReloadAnimationInternal();
            _weapon.SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason.ReloadCanceled);

            if(_weapon.CurrentWeaponData != null) {
                _weapon.PublishAmmoToHudInternal();
            }
        }

        private void ConsumePendingSingleRoundEvents() {
            if(_weapon.KinDriver == null) return;

            var reloadSingleEvents = _weapon.KinDriver.ConsumeReloadSingleEventCount();
            for(var i = 0; i < reloadSingleEvents; i++) {
                HandleSingleRoundReload();
            }
        }
    }
}

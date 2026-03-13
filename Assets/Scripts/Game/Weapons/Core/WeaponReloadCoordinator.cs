using Game.Weapons.Manager;
using UnityEngine;

namespace Game.Weapons.Core {
    internal sealed class WeaponReloadCoordinator {
        private readonly Weapon _weapon;

        public WeaponReloadCoordinator(Weapon weapon) {
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
            if(!CanReload()) return;

            _weapon.AutoReloadArmed = false;
            _weapon.Reloading = true;

            if(_weapon.KinemationDriver == null) {
                Debug.LogError(
                    $"[Weapon][KIN-Strict] Reload blocked: missing KinemationFpWeaponDriver for '{(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")}'.",
                    _weapon);
                _weapon.Reloading = false;
                return;
            }

            _weapon.ReloadExpectedCompleteTime = Time.time + Weapon.KinemationReloadFallbackSeconds;
            _weapon.KinemationReloadFallbackDeadline = _weapon.ReloadExpectedCompleteTime;
            _weapon.SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason.ReloadStarted);
            _weapon.PlayReloadEffectsInternal();
        }

        public void CancelReloadForWeaponSwitch() {
            CancelReload();
        }

        public void CancelReload() {
            if(!_weapon.Reloading) return;

            if(!_weapon.UseKinemationInternalSoundsInternal() &&
               !_weapon.ShouldSuppressLegacyReloadSoundInternal() &&
               _weapon.PlayerController != null &&
               _weapon.PlayerController.IsOwner &&
               _weapon.AudioRelay != null) {
                var soundId = _weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.reloadSoundId : "";
                if(!string.IsNullOrWhiteSpace(soundId)) {
                    _weapon.AudioRelay.RequestStop(soundId);
                }
            }

            if(_weapon.KinemationDriver != null) {
                _weapon.StopKinemationEventSoundsForCurrentWeaponInternal();
                _weapon.KinemationDriver.AbortReloadAndSyncAmmo(_weapon.CurrentAmmo);
            }

            _weapon.Reloading = false;
            _weapon.ReloadExpectedCompleteTime = float.PositiveInfinity;
            _weapon.KinemationReloadFallbackDeadline = float.PositiveInfinity;
            if(_weapon.KinemationDriver != null) {
                _weapon.KinemationDriver.ResetReloadTracking();
            }

            _weapon.ExitReloadAnimationInternal();
            _weapon.SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason.ReloadCanceled);
        }

        public void ResetWeapon() {
            if(!_weapon.CurrentWeaponData) return;

            _weapon.CurrentAmmo = _weapon.GetCurrentMagCapacityInternal();
            _weapon.Reloading = false;
            _weapon.LastFireTime = Time.time;
            _weapon.AutoReloadArmed = false;
            _weapon.ReloadExpectedCompleteTime = float.PositiveInfinity;
            _weapon.KinemationReloadFallbackDeadline = float.PositiveInfinity;
            if(_weapon.KinemationDriver != null) {
                _weapon.KinemationDriver.ResetReloadTracking();
            }

            _weapon.SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason.RefillCurrentWeapon);
        }

        public void PrepareForPostMatchPodium() {
            if(_weapon.CurrentWeaponData == null) return;

            if(_weapon.Reloading) {
                CancelReload();
            } else {
                if(!_weapon.UseKinemationInternalSoundsInternal() &&
                   !_weapon.ShouldSuppressLegacyReloadSoundInternal() &&
                   _weapon.PlayerController != null &&
                   _weapon.PlayerController.IsOwner &&
                   _weapon.AudioRelay != null) {
                    var soundId = _weapon.CurrentWeaponData.reloadSoundId;
                    if(!string.IsNullOrWhiteSpace(soundId)) {
                        _weapon.AudioRelay.RequestStop(soundId);
                    }
                }

                if(_weapon.KinemationDriver != null) {
                    _weapon.StopKinemationEventSoundsForCurrentWeaponInternal();
                    _weapon.KinemationDriver.AbortReloadAndSyncAmmo(_weapon.CurrentAmmo);
                    _weapon.KinemationDriver.ResetReloadTracking();
                }

                _weapon.Reloading = false;
                _weapon.ReloadExpectedCompleteTime = float.PositiveInfinity;
                _weapon.KinemationReloadFallbackDeadline = float.PositiveInfinity;
                _weapon.ExitReloadAnimationInternal();
            }

            _weapon.CurrentAmmo = _weapon.GetCurrentMagCapacityInternal();
            if(_weapon.KinemationDriver != null) {
                _weapon.KinemationDriver.SyncActiveAmmo(_weapon.CurrentAmmo);
            }

            _weapon.PublishOwnerAmmoToHudInternal();
            _weapon.SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason.RefillCurrentWeapon);
        }

        public void RunReloadWatchdog() {
            if(Time.time < _weapon.NextReloadRecoveryAllowedTime) return;
            if(!_weapon.Reloading) return;
            if(Time.time <= _weapon.ReloadExpectedCompleteTime) return;

            if(_weapon.CurrentWeaponData != null && !_weapon.CurrentWeaponData.useMagReload) {
                CompleteKinemationPartialReloadWithoutFilling();
            } else {
                CompleteReload();
            }

            _weapon.NextReloadRecoveryAllowedTime = Time.time + Weapon.ReloadRecoveryCooldownSeconds;
        }

        public void UpdateKinemationReloadState() {
            if(!_weapon.Reloading || _weapon.KinemationDriver == null) return;

            var reloadSingleEvents = _weapon.KinemationDriver.ConsumeReloadSingleEventCount();
            for(var i = 0; i < reloadSingleEvents; i++) {
                HandleKinemationReloadSingleRound();
            }

            if(_weapon.KinemationDriver.ConsumeReloadCompleteEvent()) {
                CompleteReload();
                return;
            }

            if(!_weapon.KinemationDriver.IsReloadSequenceInProgress()) {
                if(_weapon.CurrentWeaponData != null && !_weapon.CurrentWeaponData.useMagReload) {
                    CompleteKinemationPartialReloadWithoutFilling();
                } else {
                    CompleteReload();
                }

                return;
            }

            if(Time.time <= _weapon.KinemationReloadFallbackDeadline) return;
            if(_weapon.CurrentWeaponData != null && !_weapon.CurrentWeaponData.useMagReload) {
                CompleteKinemationPartialReloadWithoutFilling();
            } else {
                CompleteReload();
            }

            _weapon.NextReloadRecoveryAllowedTime = Time.time + Weapon.ReloadRecoveryCooldownSeconds;
        }

        private bool CanReload() {
            if(!_weapon.CurrentWeaponData || _weapon.Manager == null || _weapon.Manager.IsPullingOut) return false;
            if(_weapon.KinemationDriver == null) return false;
            return _weapon.CurrentAmmo < _weapon.GetCurrentMagCapacityInternal() && !_weapon.Reloading;
        }

        private void CompleteReload() {
            if(!_weapon.CurrentWeaponData) return;
            _weapon.CurrentAmmo = _weapon.GetCurrentMagCapacityInternal();
            _weapon.Reloading = false;
            _weapon.AutoReloadArmed = false;
            _weapon.ReloadExpectedCompleteTime = float.PositiveInfinity;
            _weapon.KinemationReloadFallbackDeadline = float.PositiveInfinity;
            if(_weapon.KinemationDriver != null) {
                _weapon.KinemationDriver.ResetReloadTracking();
            }

            _weapon.ExitReloadAnimationInternal();
            _weapon.PublishOwnerAmmoToHudInternal();
            _weapon.SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason.ReloadCompleted);
        }

        private void HandleKinemationReloadSingleRound() {
            if(!_weapon.Reloading || _weapon.CurrentWeaponData == null) return;
            if(_weapon.CurrentWeaponData.useMagReload) return;
            var magCapacity = _weapon.GetCurrentMagCapacityInternal();
            if(_weapon.CurrentAmmo >= magCapacity) return;

            _weapon.CurrentAmmo = Mathf.Min(_weapon.CurrentAmmo + 1, magCapacity);
            _weapon.PublishOwnerAmmoToHudInternal(magCapacity);
            _weapon.SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason.ReloadSingleRound);
        }

        private void CompleteKinemationPartialReloadWithoutFilling() {
            _weapon.Reloading = false;
            _weapon.AutoReloadArmed = false;
            _weapon.ReloadExpectedCompleteTime = float.PositiveInfinity;
            _weapon.KinemationReloadFallbackDeadline = float.PositiveInfinity;
            if(_weapon.KinemationDriver != null) {
                _weapon.KinemationDriver.ResetReloadTracking();
            }

            _weapon.ExitReloadAnimationInternal();
            _weapon.SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason.ReloadCanceled);

            if(_weapon.CurrentWeaponData != null) {
                _weapon.PublishOwnerAmmoToHudInternal();
            }
        }
    }
}

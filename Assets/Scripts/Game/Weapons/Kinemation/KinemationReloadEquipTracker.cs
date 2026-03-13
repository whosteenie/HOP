using System;
using UnityEngine;

namespace Game.Weapons.Kinemation {
    /// <summary>Reload and equip state machine for the KIN viewmodel. Holds tracking flags, grace times, and Drake-related reload state.</summary>
    internal sealed class KinemationReloadEquipTracker {
        private const float ReloadEnterGraceSeconds = 0.2f;
        private const float ReloadSignalGraceSeconds = 0.25f;
        private const float EquipEnterGraceSeconds = 0.2f;
        private const float EquipSignalGraceSeconds = 0.05f;

        private bool _isTrackingReload;
        private bool _reloadHasBeenActive;
        private bool _reloadHasReceivedAnyEvent;
        private bool _reloadCompleteEventReceived;
        private bool _isTrackingEquip;
        private bool _equipHasBeenActive;
        private bool _equipCompleteEventReceived;
        private int _pendingReloadSingleEvents;
        private float _reloadTrackStartTime;
        private float _lastReloadSignalTime;
        private int _lastReloadSingleEventFrame = -1;
        private float _lastReloadSingleEventTime = -1f;
        private string _lastReloadSingleEventSource = "";
        private int _reloadSingleEventsReceivedDuringCurrentReload;
        private int _reloadSingleEventsConsumedDuringCurrentReload;
        private float _equipTrackStartTime;
        private float _lastEquipSignalTime;

        // Drake/Kar reload-flow state (who suppresses shell on next reload, etc.)
        private bool _drakeCurrentReloadStartedEmpty;
        private bool _drakeCurrentEmptyReloadSawAmmoEject;
        private bool _drakeTopShellEjectedSinceReloadComplete;
        private bool _drakeShotCanceledReloadAfterAmmoEject;
        private bool _drakeShotCanceledEmptyReloadAfterAmmoEject;
        private bool _suppressDrakeTopShellEjectOnNextReload;
        private bool _suppressDrakeBottomShellOnNextReload;

        public void StartReloadTracking() {
            _isTrackingReload = true;
            _reloadTrackStartTime = Time.time;
            _lastReloadSignalTime = Time.time;
        }

        public void StartEquipTracking() {
            _isTrackingEquip = true;
            _equipTrackStartTime = Time.time;
            _lastEquipSignalTime = Time.time;
        }

        public void ResetReloadTracking() {
            _isTrackingReload = false;
            _reloadHasBeenActive = false;
            _reloadHasReceivedAnyEvent = false;
            _reloadCompleteEventReceived = false;
            _drakeCurrentReloadStartedEmpty = false;
            _drakeCurrentEmptyReloadSawAmmoEject = false;
            _pendingReloadSingleEvents = 0;
            _reloadTrackStartTime = 0f;
            _lastReloadSignalTime = 0f;
            _lastReloadSingleEventFrame = -1;
            _lastReloadSingleEventTime = -1f;
            _lastReloadSingleEventSource = "";
            _reloadSingleEventsReceivedDuringCurrentReload = 0;
            _reloadSingleEventsConsumedDuringCurrentReload = 0;
        }

        public void ResetEquipTracking() {
            _isTrackingEquip = false;
            _equipHasBeenActive = false;
            _equipCompleteEventReceived = false;
            _equipTrackStartTime = 0f;
            _lastEquipSignalTime = 0f;
        }

        public bool IsReloadSequenceInProgress(bool reloadClipActiveNow) {
            if(!_isTrackingReload) return false;
            if(_reloadCompleteEventReceived) return false;
            if(reloadClipActiveNow) {
                _reloadHasBeenActive = true;
                _lastReloadSignalTime = Time.time;
                return true;
            }
            if(_reloadHasReceivedAnyEvent && Time.time - _lastReloadSignalTime <= ReloadSignalGraceSeconds) return true;
            if(!_reloadHasBeenActive) return Time.time - _reloadTrackStartTime < ReloadEnterGraceSeconds;
            _isTrackingReload = false;
            return false;
        }

        public bool IsEquipSequenceInProgress(bool equipActiveNow, float equipProgress, float equipUnlockNormalizedTime) {
            if(!_isTrackingEquip) return false;
            if(_equipCompleteEventReceived) {
                _isTrackingEquip = false;
                return false;
            }
            if(equipActiveNow) {
                _equipHasBeenActive = true;
                _lastEquipSignalTime = Time.time;
                if(equipProgress >= equipUnlockNormalizedTime) {
                    _isTrackingEquip = false;
                    return false;
                }
                return true;
            }
            if(_equipHasBeenActive && Time.time - _lastEquipSignalTime <= EquipSignalGraceSeconds) return true;
            if(!_equipHasBeenActive) return Time.time - _equipTrackStartTime < EquipEnterGraceSeconds;
            _isTrackingEquip = false;
            return false;
        }

        public int ConsumeReloadSingleEventCount() {
            if(_pendingReloadSingleEvents <= 0) return 0;
            var count = _pendingReloadSingleEvents;
            _pendingReloadSingleEvents = 0;
            _reloadSingleEventsConsumedDuringCurrentReload += count;
            return count;
        }

        public bool ConsumeReloadCompleteEvent() {
            if(!_reloadCompleteEventReceived) return false;
            _reloadCompleteEventReceived = false;
            _isTrackingReload = false;
            return true;
        }

        public void NotifyReloadSingleEvent(string sourceTag, int currentFrame, bool isTrackingReload) {
            var source = string.IsNullOrWhiteSpace(sourceTag) ? "(unknown)" : sourceTag;
            if(!isTrackingReload) return;
            if(currentFrame == _lastReloadSingleEventFrame) return;
            _lastReloadSingleEventFrame = currentFrame;
            _lastReloadSingleEventTime = Time.time;
            _lastReloadSingleEventSource = source;
            _reloadHasReceivedAnyEvent = true;
            _reloadHasBeenActive = true;
            _lastReloadSignalTime = Time.time;
            _pendingReloadSingleEvents++;
            _reloadSingleEventsReceivedDuringCurrentReload++;
        }

        public void NotifyReloadCompleteEvent(string sourceTag) {
            _reloadHasReceivedAnyEvent = true;
            _reloadHasBeenActive = true;
            _lastReloadSignalTime = Time.time;
            _reloadCompleteEventReceived = true;
        }

        public void NotifyEquipCompleteEvent(Action onEquipComplete) {
            if(!_isTrackingEquip) return;
            _equipHasBeenActive = true;
            _equipCompleteEventReceived = true;
            _lastEquipSignalTime = Time.time;
            _isTrackingEquip = false;
            onEquipComplete?.Invoke();
        }

        // Drake state
        public void SetDrakeReloadStartedEmpty(bool value) => _drakeCurrentReloadStartedEmpty = value;
        public bool GetDrakeCurrentReloadStartedEmpty() => _drakeCurrentReloadStartedEmpty;
        public bool GetDrakeCurrentEmptyReloadSawAmmoEject() => _drakeCurrentEmptyReloadSawAmmoEject;

        public bool GetSuppressDrakeTopShellOnNextReload() => _suppressDrakeTopShellEjectOnNextReload;
        public bool GetSuppressDrakeBottomShellOnNextReload() => _suppressDrakeBottomShellOnNextReload;
        public bool GetDrakeTopShellEjectedSinceReloadComplete() => _drakeTopShellEjectedSinceReloadComplete;
        public bool GetDrakeShotCanceledReloadAfterAmmoEject() => _drakeShotCanceledReloadAfterAmmoEject;

        public void ClearSuppressDrakeFlagsAfterReloadStart() {
            _suppressDrakeTopShellEjectOnNextReload = false;
            _suppressDrakeBottomShellOnNextReload = false;
            _drakeShotCanceledReloadAfterAmmoEject = false;
            _drakeShotCanceledEmptyReloadAfterAmmoEject = false;
        }

        public bool ShouldHideDrakeTopShellForThisReload(bool isDrake, bool drakeTopShellEjectedSinceReloadComplete, bool drakeShotCanceledReloadAfterAmmoEject) {
            return isDrake && drakeTopShellEjectedSinceReloadComplete && drakeShotCanceledReloadAfterAmmoEject;
        }

        public void NotifyAmmoEjectForDrake() {
            _drakeTopShellEjectedSinceReloadComplete = true;
            _drakeShotCanceledReloadAfterAmmoEject = false;
            if(_drakeCurrentReloadStartedEmpty) _drakeCurrentEmptyReloadSawAmmoEject = true;
        }

        public void NotifyShellShowClearDrakeState() {
            _drakeTopShellEjectedSinceReloadComplete = false;
            _drakeShotCanceledReloadAfterAmmoEject = false;
            _drakeShotCanceledEmptyReloadAfterAmmoEject = false;
            _suppressDrakeTopShellEjectOnNextReload = false;
            _suppressDrakeBottomShellOnNextReload = false;
        }

        public void NotifyReloadCompleteClearDrakeState() {
            _drakeTopShellEjectedSinceReloadComplete = false;
            _drakeShotCanceledReloadAfterAmmoEject = false;
            _drakeShotCanceledEmptyReloadAfterAmmoEject = false;
            _suppressDrakeTopShellEjectOnNextReload = false;
            _suppressDrakeBottomShellOnNextReload = false;
        }

        public void MarkDrakeReloadCanceledByShot() {
            _drakeShotCanceledReloadAfterAmmoEject = true;
            if(_drakeCurrentReloadStartedEmpty && _drakeCurrentEmptyReloadSawAmmoEject) {
                _drakeShotCanceledEmptyReloadAfterAmmoEject = true;
                _suppressDrakeBottomShellOnNextReload = true;
            }
        }

        public bool IsTrackingReload => _isTrackingReload;
        public bool IsTrackingEquip => _isTrackingEquip;
    }
}

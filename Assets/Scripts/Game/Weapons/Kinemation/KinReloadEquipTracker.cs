using System;
using UnityEngine;

namespace Game.Weapons.Kinemation {
    /// <summary>Reload and equip state machine for the KIN viewmodel. Holds tracking flags, grace times, and Drake-related reload state.</summary>
    internal sealed class KinReloadEquipTracker {
        private const float ReloadEnterGraceSeconds = 0.2f;
        private const float ReloadSignalGraceSeconds = 0.25f;
        private const float EquipEnterGraceSeconds = 0.2f;
        private const float EquipSignalGraceSeconds = 0.05f;

        private bool _reloadHasBeenActive;
        private bool _reloadHasReceivedAnyEvent;
        private bool _reloadCompleteEventReceived;
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
            IsTrackingReload = true;
            _reloadTrackStartTime = Time.time;
            _lastReloadSignalTime = Time.time;
        }

        public void StartEquipTracking() {
            IsTrackingEquip = true;
            _equipTrackStartTime = Time.time;
            _lastEquipSignalTime = Time.time;
        }

        public void ResetReloadTracking() {
            IsTrackingReload = false;
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
            IsTrackingEquip = false;
            _equipHasBeenActive = false;
            _equipCompleteEventReceived = false;
            _equipTrackStartTime = 0f;
            _lastEquipSignalTime = 0f;
        }

        public bool IsReloadSequenceInProgress(bool reloadClipActiveNow) {
            if(!IsTrackingReload) return false;
            if(_reloadCompleteEventReceived) return false;
            if(reloadClipActiveNow) {
                _reloadHasBeenActive = true;
                _lastReloadSignalTime = Time.time;
                return true;
            }
            if(_reloadHasReceivedAnyEvent && Time.time - _lastReloadSignalTime <= ReloadSignalGraceSeconds) return true;
            if(!_reloadHasBeenActive) return Time.time - _reloadTrackStartTime < ReloadEnterGraceSeconds;
            IsTrackingReload = false;
            return false;
        }

        public bool IsEquipSequenceInProgress(bool equipActiveNow, float equipProgress, float equipUnlockNormalizedTime) {
            if(!IsTrackingEquip) return false;
            if(_equipCompleteEventReceived) {
                IsTrackingEquip = false;
                return false;
            }
            if(equipActiveNow) {
                _equipHasBeenActive = true;
                _lastEquipSignalTime = Time.time;
                if(!(equipProgress >= equipUnlockNormalizedTime)) return true;
                IsTrackingEquip = false;
                return false;
            }
            switch(_equipHasBeenActive) {
                case true when Time.time - _lastEquipSignalTime <= EquipSignalGraceSeconds:
                    return true;
                case false:
                    return Time.time - _equipTrackStartTime < EquipEnterGraceSeconds;
                default:
                    IsTrackingEquip = false;
                    return false;
            }
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
            IsTrackingReload = false;
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
            if(!IsTrackingEquip) return;
            _equipHasBeenActive = true;
            _equipCompleteEventReceived = true;
            _lastEquipSignalTime = Time.time;
            IsTrackingEquip = false;
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

        public static bool ShouldHideDrakeTopShellForThisReload(bool isDrake, bool drakeTopShellEjectedSinceReloadComplete, bool drakeShotCanceledReloadAfterAmmoEject) {
            return isDrake && drakeTopShellEjectedSinceReloadComplete && drakeShotCanceledReloadAfterAmmoEject;
        }

        public void NotifyAmmoEjectForDrake() {
            if(IsTrackingReload) {
                _reloadHasReceivedAnyEvent = true;
                _lastReloadSignalTime = Time.time;
                _reloadHasBeenActive = true;
            }
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
            if(!_drakeCurrentReloadStartedEmpty || !_drakeCurrentEmptyReloadSawAmmoEject) return;
            _drakeShotCanceledEmptyReloadAfterAmmoEject = true;
            _suppressDrakeBottomShellOnNextReload = true;
        }

        public bool IsTrackingReload { get; private set; }

        private bool IsTrackingEquip { get; set; }
    }
}

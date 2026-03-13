using System.Collections.Generic;
using Game.Weapons.Core;
using Game.Weapons.Manager;
using UnityEngine;

namespace Game.Weapons {
    public sealed partial class KinemationFpWeaponDriver {
        public void PlayEquipAnimation(bool immediate) {
            if(!TryCacheActiveWeapon()) return;
            PrepareActiveWeaponForEquip();
            ResetEquipTracking();
            if(immediate) {
                _activeWeapon.OnEquipped_Immediate();
            } else {
                _isTrackingEquip = true;
                _equipTrackStartTime = Time.time;
                _lastEquipSignalTime = Time.time;
                _activeWeapon.OnEquipped();
            }
            ApplyGrappleWeaponIndex();
        }

        public void PlayFireAnimation(int authoritativeAmmoBeforeShot = -1) {
            if(!TryCacheActiveWeapon()) return;

            // KIN can stay inside reload states for a short window even after gameplay allows firing.
            // Force-clear that state so fire anims always hard-interrupt reload anims on this frame.
            var reloadStateBlockingFire = _activeWeapon != null &&
                                          (_isTrackingReload ||
                                           FpsWeaponIsReloadingField?.GetValue(_activeWeapon) is true ||
                                           IsAnyReloadClipActive());
            if(reloadStateBlockingFire) {
                LogDrakeDebug(
                    $"PlayFireAnimation interrupt path. frame={Time.frameCount} time={Time.time:F3} " +
                    $"isReloadTracking={_isTrackingReload} ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete} " +
                    $"suppressNextReload={_suppressDrakeTopShellEjectOnNextReload} appliedNow={_isDrakeTopShellSuppressionApplied}");
                if(IsActiveWeaponLikelyDrake()) {
                    ArmDrakeTopShellEjectSuppressionOnNextReload();
                }

                var ammoForInterrupt = authoritativeAmmoBeforeShot >= 0
                    ? authoritativeAmmoBeforeShot
                    : GetActiveWeaponAmmoForInterrupt();
                AbortReloadAndSyncAmmo(ammoForInterrupt);
            }

            SuppressInternalMuzzleFx(_activeWeapon);
            _activeWeapon.OnFirePressed();
            _activeWeapon.OnFireReleased();
        }

        public void PlayReloadAnimation() {
            if(!TryCacheActiveWeapon()) return;
            ResetReloadTracking();
            _isTrackingReload = true;
            _reloadTrackStartTime = Time.time;
            _lastReloadSignalTime = Time.time;
            var activeAmmoAtReloadStart = GetActiveWeaponAmmoForInterrupt();
            _drakeCurrentReloadStartedEmpty = IsActiveWeaponLikelyDrake() && activeAmmoAtReloadStart <= 0;
            _drakeCurrentEmptyReloadSawAmmoEject = false;
            LogDrakeDebug(
                $"PlayReloadAnimation start. frame={Time.frameCount} time={Time.time:F3} " +
                $"suppressNextReload={_suppressDrakeTopShellEjectOnNextReload} " +
                $"suppressBottomNextReload={_suppressDrakeBottomShellOnNextReload} " +
                $"reloadStartedEmpty={_drakeCurrentReloadStartedEmpty} " +
                $"ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete} " +
                $"shotCanceledAfterEject={_drakeShotCanceledReloadAfterAmmoEject} " +
                $"shotCanceledEmptyAfterEject={_drakeShotCanceledEmptyReloadAfterAmmoEject} " +
                $"topAppliedNow={_isDrakeTopShellSuppressionApplied} bottomAppliedNow={_isDrakeBottomShellSuppressionApplied}");

            var shouldHideTopShellForThisReload = IsActiveWeaponLikelyDrake() &&
                                                  _drakeTopShellEjectedSinceReloadComplete &&
                                                  _drakeShotCanceledReloadAfterAmmoEject;
            if(_suppressDrakeTopShellEjectOnNextReload || shouldHideTopShellForThisReload) {
                SuppressDrakeTopShellForReloadStart();
                LogDrakeDebug(
                    $"PlayReloadAnimation applying suppressed top shell. frame={Time.frameCount} time={Time.time:F3} " +
                    $"appliedNow={_isDrakeTopShellSuppressionApplied}");
            }
            if(_suppressDrakeBottomShellOnNextReload) {
                SuppressDrakeBottomShellForReloadStart();
                LogDrakeDebug(
                    $"PlayReloadAnimation applying suppressed bottom shell. frame={Time.frameCount} time={Time.time:F3} " +
                    $"appliedNow={_isDrakeBottomShellSuppressionApplied}");
            }

            _suppressDrakeTopShellEjectOnNextReload = false;
            _suppressDrakeBottomShellOnNextReload = false;
            _drakeShotCanceledReloadAfterAmmoEject = false;
            _drakeShotCanceledEmptyReloadAfterAmmoEject = false;

            _activeWeapon.OnReload();
        }

        public void ArmDrakeTopShellEjectSuppressionOnNextReload() {
            NotifyDrakeReloadCanceledByShot();
        }

        public void NotifyDrakeReloadCanceledByShot() {
            if(!TryCacheActiveWeapon() || _activeWeapon == null) {
                LogDrakeDebug("ArmNextReload skipped: no active KIN weapon.");
                return;
            }

            if(!IsActiveWeaponLikelyDrake()) {
                LogDrakeDebug("ArmNextReload skipped: active weapon not Drake.");
                return;
            }

            _drakeShotCanceledReloadAfterAmmoEject = true;
            if(_drakeCurrentReloadStartedEmpty && _drakeCurrentEmptyReloadSawAmmoEject) {
                _drakeShotCanceledEmptyReloadAfterAmmoEject = true;
                _suppressDrakeBottomShellOnNextReload = true;
            }
            LogDrakeDebug(
                $"MarkReloadCanceledByShot. frame={Time.frameCount} time={Time.time:F3} " +
                $"ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete} " +
                $"reloadStartedEmpty={_drakeCurrentReloadStartedEmpty} " +
                $"emptySawAmmoEject={_drakeCurrentEmptyReloadSawAmmoEject} " +
                $"suppressBottomNextReload={_suppressDrakeBottomShellOnNextReload}");
        }

        public static void PlayReloadCompleteAnimation() {
            // KINEMATION handles reload completion internally via its own state machine.
        }

        public bool IsReloadSequenceInProgress() {
            if(!_isTrackingReload) {
                return false;
            }

            if(_reloadCompleteEventReceived) {
                return false;
            }

            var reloadActiveNow = IsAnyReloadClipActive();
            if(reloadActiveNow) {
                _reloadHasBeenActive = true;
                _lastReloadSignalTime = Time.time;
                return true;
            }

            if(_reloadHasReceivedAnyEvent && Time.time - _lastReloadSignalTime <= ReloadSignalGraceSeconds) {
                return true;
            }

            // Allow one short transition window before we decide reload never started.
            if(!_reloadHasBeenActive) {
                return Time.time - _reloadTrackStartTime < ReloadEnterGraceSeconds;
            }

            _isTrackingReload = false;
            return false;
        }

        public int ConsumeReloadSingleEventCount() {
            if(_pendingReloadSingleEvents <= 0) return 0;
            var count = _pendingReloadSingleEvents;
            _pendingReloadSingleEvents = 0;
            _reloadSingleEventsConsumedDuringCurrentReload += count;
            LogReloadSingleDebug(
                $"Consume count={count} frame={Time.frameCount} time={Time.time:F3} " +
                $"receivedTotal={_reloadSingleEventsReceivedDuringCurrentReload} " +
                $"consumedTotal={_reloadSingleEventsConsumedDuringCurrentReload} " +
                $"lastSource='{_lastReloadSingleEventSource}'");
            return count;
        }

        public bool ConsumeReloadCompleteEvent() {
            if(!_reloadCompleteEventReceived) return false;
            _reloadCompleteEventReceived = false;
            _isTrackingReload = false;
            return true;
        }

        public bool IsKinemationSoundEventRoutingEnabled() {
            if(!routeWeaponSoundEventsToAudioService) {
                return false;
            }

            return TryCacheActiveWeapon() && _activeWeapon != null && _activeWeapon.weaponSettings != null;
        }

        public int GetKinemationEventSoundClipCount() {
            if(!TryCacheActiveWeapon() || _activeWeapon == null || _activeWeapon.weaponSettings == null) {
                return 0;
            }

            return _activeWeapon.weaponSettings.weaponEventSounds != null
                ? _activeWeapon.weaponSettings.weaponEventSounds.Count
                : 0;
        }

        public bool IsLikelyReloadEventSoundClip(int clipIndex) {
            if(!TryCacheActiveWeapon() || _activeWeapon == null || _activeWeapon.weaponSettings == null) {
                return false;
            }

            var eventSounds = _activeWeapon.weaponSettings.weaponEventSounds;
            if(eventSounds == null || clipIndex < 0 || clipIndex >= eventSounds.Count) {
                return false;
            }

            var data = GetActiveWeaponData();
            if(data != null && data.kinemationReloadEventSoundIndices is { Length: > 0 }) {
                foreach(var configuredIndex in data.kinemationReloadEventSoundIndices) {
                    if(configuredIndex == clipIndex) {
                        return true;
                    }
                }

                return false;
            }

            ReportMissingKinemationReloadSoundIndexConfig(data);
            return false;
        }

        public void SyncActiveAmmo(int authoritativeAmmo) {
            if(!TryCacheActiveWeapon() || _activeWeapon == null) return;
            ApplyAuthoritativeAmmoToActiveWeapon(authoritativeAmmo, cancelPendingInvokes: false, out var clampedAmmo,
                out var maxAmmo);
            SyncAmmoDrivenViewmodelVisuals(clampedAmmo, maxAmmo);
        }

        public void AbortReloadAndSyncAmmo(int authoritativeAmmo) {
            if(!TryCacheActiveWeapon() || _activeWeapon == null) return;

            _activeWeapon.CancelInvoke();
            _activeWeapon.OnFireReleased();
            ApplyAuthoritativeAmmoToActiveWeapon(authoritativeAmmo, cancelPendingInvokes: false, out var clampedAmmo,
                out var maxAmmo);
            SyncAmmoDrivenViewmodelVisuals(clampedAmmo, maxAmmo);
            ForceReloadAnimatorsToIdle();
            StopActiveWeaponAudioPlayback();
            ResetReloadTracking();
            ClearPendingWeaponSoundEvents();
        }

        private void ForceReloadAnimatorsToIdle() {
            if(_activeWeapon == null) return;

            var animators = new List<Animator>(8);
            AddUniqueAnimator(animators, FpsWeaponCharacterAnimatorField?.GetValue(_activeWeapon) as Animator);
            AddUniqueAnimator(animators, FpsWeaponAnimatorField?.GetValue(_activeWeapon) as Animator);
            AddUniqueAnimator(animators, _fpsAnimator);

            var weaponAnimators = GetActiveWeaponAnimators();
            foreach(var weaponAnimator in weaponAnimators) {
                AddUniqueAnimator(animators, weaponAnimator);
            }

            foreach(var t in animators) {
                SnapAnimatorToIdle(t, forceRebindIfReloadStillActive: true);
            }
        }

        private static void AddUniqueAnimator(List<Animator> destination, Animator animator) {
            if(destination == null || animator == null) return;
            if(destination.Contains(animator)) return;
            destination.Add(animator);
        }

        private void SuppressDrakeTopShellForReloadStart() {
            if(_activeWeapon == null || !IsActiveWeaponLikelyDrake()) {
                LogDrakeDebug("SuppressAtReloadStart skipped: active weapon not Drake.");
                return;
            }

            if(!EnsureDrakeTopShellSuppressionTarget()) {
                LogDrakeDebug(
                    $"Drake suppression target not found. frame={Time.frameCount} time={Time.time:F3}");
                return;
            }

            // Keep top shell hidden for this reload start when consumed by the two-flag rule.
            ApplyDrakeTopShellSuppressionNow();

            LogDrakeDebug(
                $"Drake reload start. topShellHidden={_isDrakeTopShellSuppressionApplied} " +
                $"target={_suppressedDrakeTopShellTransform.name} frame={Time.frameCount} time={Time.time:F3} " +
                $"suppressNextReload={_suppressDrakeTopShellEjectOnNextReload} " +
                $"ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete} " +
                $"shotCanceledAfterEject={_drakeShotCanceledReloadAfterAmmoEject}");
        }

        private bool EnsureDrakeTopShellSuppressionTarget() {
            if(_suppressedDrakeTopShellTransform != null) {
                return true;
            }

            if(!TryResolveDrakeTopShellTransform(out var topShellTransform) || topShellTransform == null) {
                LogDrakeDebug(
                    $"EnsureTarget failed. frame={Time.frameCount} time={Time.time:F3} " +
                    $"activeWeapon={(_activeWeapon != null ? _activeWeapon.name : "(null)")}");
                return false;
            }

            _suppressedDrakeTopShellTransform = topShellTransform;
            _suppressedDrakeTopShellOriginalLocalPosition = topShellTransform.localPosition;
            _hasSuppressedDrakeTopShellOriginalLocalPosition = true;
            _suppressedDrakeTopShellOriginalLocalScale = topShellTransform.localScale;
            _hasSuppressedDrakeTopShellOriginalLocalScale = true;
            _isDrakeTopShellSuppressionApplied = false;

            var shellRenderers = topShellTransform.GetComponentsInChildren<Renderer>(true);
            if(shellRenderers is not { Length: > 0 }) return true;
            _suppressedDrakeTopShellRenderers = shellRenderers;
            _suppressedDrakeTopShellRendererEnabledStates = new bool[shellRenderers.Length];
            for(var i = 0; i < shellRenderers.Length; i++) {
                var shellRenderer = shellRenderers[i];
                if(shellRenderer == null) continue;
                _suppressedDrakeTopShellRendererEnabledStates[i] = shellRenderer.enabled;
            }

            return true;
        }

        private void ApplyDrakeTopShellSuppressionNow() {
            if(_suppressedDrakeTopShellTransform == null) {
                LogDrakeDebug("ApplySuppression skipped: target null.");
                return;
            }

            if(_hasSuppressedDrakeTopShellOriginalLocalPosition) {
                _suppressedDrakeTopShellTransform.localPosition =
                    _suppressedDrakeTopShellOriginalLocalPosition + Vector3.down * DrakeTopShellHideOffset;
            }

            if(_hasSuppressedDrakeTopShellOriginalLocalScale) {
                _suppressedDrakeTopShellTransform.localScale = Vector3.zero;
            }

            if(_suppressedDrakeTopShellRenderers != null) {
                foreach(var shellRenderer in _suppressedDrakeTopShellRenderers) {
                    if(shellRenderer == null) continue;
                    shellRenderer.enabled = false;
                }
            }

            _isDrakeTopShellSuppressionApplied = true;
            LogDrakeDebug(
                $"ApplySuppression applied. target={_suppressedDrakeTopShellTransform.name} " +
                $"frame={Time.frameCount} time={Time.time:F3}");
        }

        private void RestoreDrakeTopShellImmediate() {
            if(_suppressedDrakeTopShellRenderers != null && _suppressedDrakeTopShellRendererEnabledStates != null) {
                var limit = Mathf.Min(_suppressedDrakeTopShellRenderers.Length,
                    _suppressedDrakeTopShellRendererEnabledStates.Length);
                for(var i = 0; i < limit; i++) {
                    var shellRenderer = _suppressedDrakeTopShellRenderers[i];
                    if(shellRenderer == null) continue;
                    shellRenderer.enabled = _suppressedDrakeTopShellRendererEnabledStates[i];
                }
            }

            if(_suppressedDrakeTopShellTransform != null && _hasSuppressedDrakeTopShellOriginalLocalPosition) {
                _suppressedDrakeTopShellTransform.localPosition = _suppressedDrakeTopShellOriginalLocalPosition;
            }
            if(_suppressedDrakeTopShellTransform != null && _hasSuppressedDrakeTopShellOriginalLocalScale) {
                _suppressedDrakeTopShellTransform.localScale = _suppressedDrakeTopShellOriginalLocalScale;
            }

            _suppressedDrakeTopShellTransform = null;
            _suppressedDrakeTopShellRenderers = null;
            _suppressedDrakeTopShellRendererEnabledStates = null;
            _suppressedDrakeTopShellOriginalLocalPosition = Vector3.zero;
            _hasSuppressedDrakeTopShellOriginalLocalPosition = false;
            _suppressedDrakeTopShellOriginalLocalScale = Vector3.one;
            _hasSuppressedDrakeTopShellOriginalLocalScale = false;
            _isDrakeTopShellSuppressionApplied = false;
            LogDrakeDebug($"RestoreSuppression complete. frame={Time.frameCount} time={Time.time:F3}");
        }

        private void SuppressDrakeBottomShellForReloadStart() {
            if(_activeWeapon == null || !IsActiveWeaponLikelyDrake()) {
                LogDrakeDebug("Bottom suppress skipped: active weapon not Drake.");
                return;
            }

            if(!EnsureDrakeBottomShellSuppressionTarget()) {
                LogDrakeDebug(
                    $"Bottom suppression target not found. frame={Time.frameCount} time={Time.time:F3}");
                return;
            }

            ApplyDrakeBottomShellSuppressionNow();
            LogDrakeDebug(
                $"Drake reload start. bottomShellHidden={_isDrakeBottomShellSuppressionApplied} " +
                $"target={_suppressedDrakeBottomShellTransform.name} frame={Time.frameCount} time={Time.time:F3}");
        }

        private bool EnsureDrakeBottomShellSuppressionTarget() {
            if(_suppressedDrakeBottomShellTransform != null) {
                return true;
            }

            if(!TryResolveDrakeBottomShellTransform(out var bottomShellTransform) || bottomShellTransform == null) {
                return false;
            }

            _suppressedDrakeBottomShellTransform = bottomShellTransform;
            _suppressedDrakeBottomShellOriginalLocalPosition = bottomShellTransform.localPosition;
            _hasSuppressedDrakeBottomShellOriginalLocalPosition = true;
            _suppressedDrakeBottomShellOriginalLocalScale = bottomShellTransform.localScale;
            _hasSuppressedDrakeBottomShellOriginalLocalScale = true;
            _isDrakeBottomShellSuppressionApplied = false;

            var shellRenderers = bottomShellTransform.GetComponentsInChildren<Renderer>(true);
            if(shellRenderers is not { Length: > 0 }) return true;
            _suppressedDrakeBottomShellRenderers = shellRenderers;
            _suppressedDrakeBottomShellRendererEnabledStates = new bool[shellRenderers.Length];
            for(var i = 0; i < shellRenderers.Length; i++) {
                var shellRenderer = shellRenderers[i];
                if(shellRenderer == null) continue;
                _suppressedDrakeBottomShellRendererEnabledStates[i] = shellRenderer.enabled;
            }

            return true;
        }

        private void ApplyDrakeBottomShellSuppressionNow() {
            if(_suppressedDrakeBottomShellTransform == null) {
                LogDrakeDebug("ApplyBottomSuppression skipped: target null.");
                return;
            }

            if(_hasSuppressedDrakeBottomShellOriginalLocalPosition) {
                _suppressedDrakeBottomShellTransform.localPosition =
                    _suppressedDrakeBottomShellOriginalLocalPosition + Vector3.down * DrakeTopShellHideOffset;
            }

            if(_hasSuppressedDrakeBottomShellOriginalLocalScale) {
                _suppressedDrakeBottomShellTransform.localScale = Vector3.zero;
            }

            if(_suppressedDrakeBottomShellRenderers != null) {
                foreach(var shellRenderer in _suppressedDrakeBottomShellRenderers) {
                    if(shellRenderer == null) continue;
                    shellRenderer.enabled = false;
                }
            }

            _isDrakeBottomShellSuppressionApplied = true;
            LogDrakeDebug(
                $"ApplyBottomSuppression applied. target={_suppressedDrakeBottomShellTransform.name} " +
                $"frame={Time.frameCount} time={Time.time:F3}");
        }

        private void RestoreDrakeBottomShellImmediate() {
            if(_suppressedDrakeBottomShellRenderers != null && _suppressedDrakeBottomShellRendererEnabledStates != null) {
                var limit = Mathf.Min(_suppressedDrakeBottomShellRenderers.Length,
                    _suppressedDrakeBottomShellRendererEnabledStates.Length);
                for(var i = 0; i < limit; i++) {
                    var shellRenderer = _suppressedDrakeBottomShellRenderers[i];
                    if(shellRenderer == null) continue;
                    shellRenderer.enabled = _suppressedDrakeBottomShellRendererEnabledStates[i];
                }
            }

            if(_suppressedDrakeBottomShellTransform != null && _hasSuppressedDrakeBottomShellOriginalLocalPosition) {
                _suppressedDrakeBottomShellTransform.localPosition = _suppressedDrakeBottomShellOriginalLocalPosition;
            }
            if(_suppressedDrakeBottomShellTransform != null && _hasSuppressedDrakeBottomShellOriginalLocalScale) {
                _suppressedDrakeBottomShellTransform.localScale = _suppressedDrakeBottomShellOriginalLocalScale;
            }

            _suppressedDrakeBottomShellTransform = null;
            _suppressedDrakeBottomShellRenderers = null;
            _suppressedDrakeBottomShellRendererEnabledStates = null;
            _suppressedDrakeBottomShellOriginalLocalPosition = Vector3.zero;
            _hasSuppressedDrakeBottomShellOriginalLocalPosition = false;
            _suppressedDrakeBottomShellOriginalLocalScale = Vector3.one;
            _hasSuppressedDrakeBottomShellOriginalLocalScale = false;
            _isDrakeBottomShellSuppressionApplied = false;
            LogDrakeDebug($"RestoreBottomSuppression complete. frame={Time.frameCount} time={Time.time:F3}");
        }

        private bool TryResolveDrakeBottomShellTransform(out Transform bottomShellTransform) {
            bottomShellTransform = null;
            if(_activeWeapon == null) return false;

            var partReferences = GetActiveWeaponPartReferences();
            if(partReferences != null)
                return TryResolveConfiguredWeaponPartReference(
                    partReferences.DrakeBottomShell,
                    DrakeBottomShellReferenceKey,
                    nameof(KinemationWeaponPartReferences.DrakeBottomShell),
                    out bottomShellTransform);
            ReportMissingKinemationPartReference(DrakeBottomShellReferenceKey,
                nameof(KinemationWeaponPartReferences.DrakeBottomShell), true);
            return false;

        }

        private void HideKarLoopBulletForReloadLoop() {
            if(_activeWeapon == null || !IsActiveWeaponLikelyKar()) return;
            if(!EnsureKarLoopBulletTarget()) {
                LogDrakeDebug(
                    $"Kar loop bullet target not found. frame={Time.frameCount} time={Time.time:F3}");
                return;
            }

            ApplyKarLoopBulletHiddenNow();
            LogDrakeDebug(
                $"Kar loop bullet hidden. target={_karLoopBulletTransform.name} " +
                $"frame={Time.frameCount} time={Time.time:F3}");
        }

        private bool EnsureKarLoopBulletTarget() {
            if(_karLoopBulletTransform != null) {
                return true;
            }

            if(!TryResolveKarLoopBulletTransform(out var loopBulletTransform) || loopBulletTransform == null) {
                return false;
            }

            _karLoopBulletTransform = loopBulletTransform;
            _karLoopBulletOriginalLocalPosition = loopBulletTransform.localPosition;
            _hasKarLoopBulletOriginalLocalPosition = true;
            _karLoopBulletOriginalLocalScale = loopBulletTransform.localScale;
            _hasKarLoopBulletOriginalLocalScale = true;
            _isKarLoopBulletHidden = false;

            var bulletRenderers = loopBulletTransform.GetComponentsInChildren<Renderer>(true);
            if(bulletRenderers is not { Length: > 0 }) return true;
            _karLoopBulletRenderers = bulletRenderers;
            _karLoopBulletRendererEnabledStates = new bool[bulletRenderers.Length];
            for(var i = 0; i < bulletRenderers.Length; i++) {
                var bulletRenderer = bulletRenderers[i];
                if(bulletRenderer == null) continue;
                _karLoopBulletRendererEnabledStates[i] = bulletRenderer.enabled;
            }

            return true;
        }

        private void ApplyKarLoopBulletHiddenNow() {
            if(_karLoopBulletTransform == null) return;

            if(_hasKarLoopBulletOriginalLocalPosition) {
                _karLoopBulletTransform.localPosition =
                    _karLoopBulletOriginalLocalPosition + Vector3.down * KarLoopBulletHideOffset;
            }

            if(_hasKarLoopBulletOriginalLocalScale) {
                _karLoopBulletTransform.localScale = Vector3.zero;
            }

            if(_karLoopBulletRenderers != null) {
                foreach(var bulletRenderer in _karLoopBulletRenderers) {
                    if(bulletRenderer == null) continue;
                    bulletRenderer.enabled = false;
                }
            }

            _isKarLoopBulletHidden = true;
        }

        private void RestoreKarLoopBulletImmediate() {
            if(_karLoopBulletRenderers != null && _karLoopBulletRendererEnabledStates != null) {
                var limit = Mathf.Min(_karLoopBulletRenderers.Length, _karLoopBulletRendererEnabledStates.Length);
                for(var i = 0; i < limit; i++) {
                    var bulletRenderer = _karLoopBulletRenderers[i];
                    if(bulletRenderer == null) continue;
                    bulletRenderer.enabled = _karLoopBulletRendererEnabledStates[i];
                }
            }

            if(_karLoopBulletTransform != null && _hasKarLoopBulletOriginalLocalPosition) {
                _karLoopBulletTransform.localPosition = _karLoopBulletOriginalLocalPosition;
            }
            if(_karLoopBulletTransform != null && _hasKarLoopBulletOriginalLocalScale) {
                _karLoopBulletTransform.localScale = _karLoopBulletOriginalLocalScale;
            }

            _karLoopBulletTransform = null;
            _karLoopBulletRenderers = null;
            _karLoopBulletRendererEnabledStates = null;
            _karLoopBulletOriginalLocalPosition = Vector3.zero;
            _hasKarLoopBulletOriginalLocalPosition = false;
            _karLoopBulletOriginalLocalScale = Vector3.one;
            _hasKarLoopBulletOriginalLocalScale = false;
            _isKarLoopBulletHidden = false;
            LogDrakeDebug($"RestoreKarLoopBullet complete. frame={Time.frameCount} time={Time.time:F3}");
        }

        private bool TryResolveKarLoopBulletTransform(out Transform loopBulletTransform) {
            loopBulletTransform = null;
            if(_activeWeapon == null) return false;

            var partReferences = GetActiveWeaponPartReferences();
            if(partReferences != null)
                return TryResolveConfiguredWeaponPartReference(
                    partReferences.KarLoopBullet,
                    KarLoopBulletReferenceKey,
                    nameof(KinemationWeaponPartReferences.KarLoopBullet),
                    out loopBulletTransform);
            ReportMissingKinemationPartReference(KarLoopBulletReferenceKey,
                nameof(KinemationWeaponPartReferences.KarLoopBullet), true);
            return false;

        }

        private bool TryResolveDrakeTopShellTransform(out Transform topShellTransform) {
            topShellTransform = null;
            if(_activeWeapon == null) return false;

            var partReferences = GetActiveWeaponPartReferences();
            if(partReferences != null)
                return TryResolveConfiguredWeaponPartReference(
                    partReferences.DrakeTopShell,
                    DrakeTopShellReferenceKey,
                    nameof(KinemationWeaponPartReferences.DrakeTopShell),
                    out topShellTransform);
            ReportMissingKinemationPartReference(DrakeTopShellReferenceKey,
                nameof(KinemationWeaponPartReferences.DrakeTopShell), true);
            return false;

        }

        private WeaponData GetActiveWeaponData() {
            _weaponManager = _weaponManager ? _weaponManager : GetComponentInParent<WeaponManager>();
            if(_weaponManager == null || _weaponManager.CurrentWeapon == null) return null;
            return _weaponManager.CurrentWeapon.CurrentWeaponData;
        }

        private WeaponData.KinemationSpecialHandling GetActiveWeaponSpecialHandling() {
            var data = GetActiveWeaponData();
            if(data == null) {
                return WeaponData.KinemationSpecialHandling.Null;
            }

            if(data.kinemationSpecialHandling == WeaponData.KinemationSpecialHandling.Null) {
                ReportMissingKinemationAssignment(
                    data,
                    MissingKinemationSpecialHandlingWarnings,
                    nameof(WeaponData.kinemationSpecialHandling),
                    "Drake/Kar special handling is disabled until assigned.");
            }

            return data.kinemationSpecialHandling;
        }

        private bool IsActiveWeaponLikelyDrake() {
            if(_activeWeapon == null) return false;

            var handling = GetActiveWeaponSpecialHandling();
            return handling == WeaponData.KinemationSpecialHandling.DrakeShell;
        }

        private bool IsActiveWeaponLikelyKar() {
            if(_activeWeapon == null) return false;

            var handling = GetActiveWeaponSpecialHandling();
            return handling == WeaponData.KinemationSpecialHandling.KarLoopBullet;
        }

        /// <summary>
        /// Stable weapon bucket mapping used for runtime grapple clavicle offsets.
        /// Driven by WeaponData.KinemationGrappleWeaponIndex only.
        /// </summary>
        private int GetGrappleWeaponIndex() {
            var data = GetActiveWeaponData();
            if(data == null) {
                return -1;
            }

            if(data.kinemationGrappleWeaponIndex != WeaponData.KinemationGrappleWeaponIndex.Null)
                return (int)data.kinemationGrappleWeaponIndex;
            ReportMissingKinemationAssignment(
                data,
                MissingKinemationGrappleIndexWarnings,
                nameof(WeaponData.kinemationGrappleWeaponIndex),
                "Grapple animation index is invalid until assigned.");
            return -1;

        }

        private static void ReportMissingKinemationAssignment(WeaponData data, HashSet<int> warningCache, string fieldName,
            string impactDescription) {
            if(data == null || warningCache == null) return;
            var id = data.GetInstanceID();
            if(!warningCache.Add(id)) return;

            var weaponLabel = string.IsNullOrWhiteSpace(data.weaponName) ? data.name : data.weaponName;
            Debug.LogError(
                $"[KinemationFpWeaponDriver] WeaponData '{weaponLabel}' has {fieldName}=NULL. " +
                $"{impactDescription}",
                data);
        }

        private static void ReportMissingKinemationReloadSoundIndexConfig(WeaponData data) {
            if(data == null) return;
            var id = data.GetInstanceID();
            if(!MissingKinemationReloadSoundIndexWarnings.Add(id)) return;

            var weaponLabel = string.IsNullOrWhiteSpace(data.weaponName) ? data.name : data.weaponName;
            Debug.LogError(
                $"[KinemationFpWeaponDriver] WeaponData '{weaponLabel}' has no kinemationReloadEventSoundIndices configured. " +
                "Reload event SFX stopping is strict and requires explicit index assignment.",
                data);
        }

        private bool TryResolveConfiguredWeaponPartReference(Transform configuredPart, int partReferenceKey,
            string partFieldName, out Transform resolvedPart) {
            resolvedPart = null;
            if(_activeWeapon == null) return false;

            if(configuredPart == null) {
                ReportMissingKinemationPartReference(partReferenceKey, partFieldName, false);
                return false;
            }

            if(!configuredPart.IsChildOf(_activeWeapon.transform)) {
                ReportInvalidKinemationPartReference(partReferenceKey, partFieldName, configuredPart);
                return false;
            }

            resolvedPart = configuredPart;
            return true;
        }

        private int BuildPartReferenceWarningKey(int partReferenceKey) {
            var weaponId = _activeWeapon != null ? _activeWeapon.GetInstanceID() : 0;
            return unchecked(weaponId * 397 ^ partReferenceKey);
        }

        private void ReportMissingKinemationPartReference(int partReferenceKey, string partFieldName,
            bool missingComponent) {
            var warningKey = BuildPartReferenceWarningKey(partReferenceKey);
            if(!MissingKinemationPartReferenceWarnings.Add(warningKey)) return;

            var weaponLabel = GetActiveWeaponLabel();
            var guidance = missingComponent
                ? "Add KinemationWeaponPartReferences to the weapon prefab and assign required parts."
                : "Assign this field on KinemationWeaponPartReferences.";
            Debug.LogError(
                $"[KinemationFpWeaponDriver] Weapon '{weaponLabel}' is missing explicit part reference '{partFieldName}'. " +
                $"{guidance}",
                _activeWeapon);
        }

        private void ReportInvalidKinemationPartReference(int partReferenceKey, string partFieldName,
            Transform configuredPart) {
            var warningKey = BuildPartReferenceWarningKey(partReferenceKey);
            if(!InvalidKinemationPartReferenceWarnings.Add(warningKey)) return;

            var weaponLabel = GetActiveWeaponLabel();
            Debug.LogError(
                $"[KinemationFpWeaponDriver] Weapon '{weaponLabel}' has invalid part reference '{partFieldName}' " +
                $"(assigned '{configuredPart.name}', outside active weapon hierarchy).",
                _activeWeapon);
        }

        private string GetActiveWeaponLabel() {
            var data = GetActiveWeaponData();
            if(data != null) {
                return string.IsNullOrWhiteSpace(data.weaponName) ? data.name : data.weaponName;
            }

            return _activeWeapon != null ? _activeWeapon.name : "(unknown)";
        }

        private void ApplyGrappleWeaponIndex() {
            if(_fpsAnimator == null) return;
            var weaponIndex = GetGrappleWeaponIndex();
            if(weaponIndex < 0) return;
            _fpsAnimator.SetFloat(GrappleWeaponIndexHash, weaponIndex);
        }

        private void PrepareRuntimeGrappleClavicleOffset() {
            if(!enableRuntimeGrappleClavicleOffset) {
                _runtimeGrappleClavicleOffset = Vector3.zero;
                _runtimeGrappleOffsetWeaponIndex = 0;
                _isRuntimeGrappleClavicleOffsetActive = false;
                return;
            }
            if(_activeWeapon == null) {
                TryCacheActiveWeapon();
            }

            _runtimeGrappleOffsetWeaponIndex = GetGrappleWeaponIndex();
            switch(_runtimeGrappleOffsetWeaponIndex) {
                case < 0:
                    _runtimeGrappleClavicleOffset = Vector3.zero;
                    _isRuntimeGrappleClavicleOffsetActive = false;
                    return;
                case 0:
                    sAkViewmodelLocalPosition = transform.localPosition;
                    sHasAkViewmodelReference = true;
                    _runtimeGrappleClavicleOffset = Vector3.zero;
                    _isRuntimeGrappleClavicleOffsetActive = false;
                    return;
            }

            var akReference = sHasAkViewmodelReference ? sAkViewmodelLocalPosition : DefaultAkViewmodelLocalPosition;
            _runtimeGrappleClavicleOffset = akReference - transform.localPosition;
            _isRuntimeGrappleClavicleOffsetActive = false;
        }

        private void ClearRuntimeGrappleClavicleOffset() {
            _isRuntimeGrappleClavicleOffsetActive = false;
            _runtimeGrappleClavicleOffset = Vector3.zero;
            _runtimeGrappleOffsetWeaponIndex = 0;
        }

        private int GetActiveWeaponAmmoForInterrupt() {
            if(_activeWeapon == null) {
                return 0;
            }

            if(FpsWeaponActiveAmmoField?.GetValue(_activeWeapon) is int activeAmmo) {
                return Mathf.Max(0, activeAmmo);
            }

            return _activeWeapon.weaponSettings != null ? Mathf.Max(0, _activeWeapon.weaponSettings.ammo) : 0;
        }

        public string GetKinemationFireSoundId() {
            return !TryCacheActiveWeapon() ? string.Empty : _activeWeaponFireSoundId;
        }

        public bool HasKinemationFireSound() {
            return !string.IsNullOrWhiteSpace(GetKinemationFireSoundId());
        }

        public bool HasAnyKinemationEventSound() {
            if(!TryCacheActiveWeapon() || _activeWeapon == null || _activeWeapon.weaponSettings == null) {
                return false;
            }

            return HasAnyValidAudioClip(_activeWeapon.weaponSettings.weaponEventSounds);
        }

        public int ConsumeWeaponFireSoundEventCount() {
            if(_pendingWeaponFireSoundEvents <= 0) return 0;
            var count = _pendingWeaponFireSoundEvents;
            _pendingWeaponFireSoundEvents = 0;
            return count;
        }

        public void ClearPendingWeaponSoundEvents() {
            _pendingWeaponFireSoundEvents = 0;
            _pendingWeaponEventSoundIndices.Clear();
        }

        public void ConsumeWeaponEventSoundIndices(List<int> destination) {
            if(destination == null) return;
            if(_pendingWeaponEventSoundIndices.Count == 0) return;

            destination.AddRange(_pendingWeaponEventSoundIndices);
            _pendingWeaponEventSoundIndices.Clear();
        }

        public bool TryGetKinemationEventSoundId(int clipIndex, out string soundId) {
            soundId = string.Empty;
            if(clipIndex < 0) return false;
            if(!TryCacheActiveWeapon() || _activeWeapon == null || _activeWeapon.weaponSettings == null) return false;

            var weaponEventSounds = _activeWeapon.weaponSettings.weaponEventSounds;
            if(weaponEventSounds == null || clipIndex >= weaponEventSounds.Count) return false;
            if(weaponEventSounds[clipIndex] == null) return false;

            soundId = KinemationSoundIdUtility.BuildEventSoundId(_activeWeaponSoundKey, clipIndex);
            return !string.IsNullOrWhiteSpace(soundId);
        }

        public bool IsEquipSequenceInProgress() {
            if(!_isTrackingEquip) {
                return false;
            }

            if(_equipCompleteEventReceived) {
                _isTrackingEquip = false;
                return false;
            }

            var equipActiveNow = TryGetEquipStateProgress(out var equipProgress);
            if(equipActiveNow) {
                _equipHasBeenActive = true;
                _lastEquipSignalTime = Time.time;
                if(!(equipProgress >= equipUnlockNormalizedTime)) return true;
                _isTrackingEquip = false;
                return false;
            }

            switch(_equipHasBeenActive) {
                case true when Time.time - _lastEquipSignalTime <= EquipSignalGraceSeconds:
                    return true;
                case false:
                    return Time.time - _equipTrackStartTime < EquipEnterGraceSeconds;
                default:
                    _isTrackingEquip = false;
                    return false;
            }
        }

        private void ResetEquipTracking() {
            _isTrackingEquip = false;
            _equipHasBeenActive = false;
            _equipCompleteEventReceived = false;
            _equipTrackStartTime = 0f;
            _lastEquipSignalTime = 0f;
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
            LogDrakeDebug(
                $"ResetReloadTracking. frame={Time.frameCount} time={Time.time:F3} " +
                $"ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete} " +
                $"topAppliedNow={_isDrakeTopShellSuppressionApplied} bottomAppliedNow={_isDrakeBottomShellSuppressionApplied} " +
                $"suppressTopNextReload={_suppressDrakeTopShellEjectOnNextReload} " +
                $"suppressBottomNextReload={_suppressDrakeBottomShellOnNextReload}");
        }

        public void NotifyReloadSingleEvent(string sourceTag = null) {
            var source = string.IsNullOrWhiteSpace(sourceTag) ? "(unknown)" : sourceTag;
            if(!_isTrackingReload) {
                LogReloadSingleDebug(
                    $"Ignored (not tracking) source='{source}' frame={Time.frameCount} time={Time.time:F3}");
                return;
            }

            if(Time.frameCount == _lastReloadSingleEventFrame) {
                LogReloadSingleDebug(
                    $"Ignored same-frame duplicate source='{source}' frame={Time.frameCount} time={Time.time:F3} " +
                    $"lastSource='{_lastReloadSingleEventSource}'");
                return;
            }

            var deltaSinceLast = _lastReloadSingleEventTime < 0f ? -1f : Time.time - _lastReloadSingleEventTime;
            _lastReloadSingleEventFrame = Time.frameCount;
            _lastReloadSingleEventTime = Time.time;
            _lastReloadSingleEventSource = source;
            _reloadHasReceivedAnyEvent = true;
            _reloadHasBeenActive = true;
            _lastReloadSignalTime = Time.time;
            _pendingReloadSingleEvents++;
            _reloadSingleEventsReceivedDuringCurrentReload++;
            if(IsActiveWeaponLikelyKar()) {
                HideKarLoopBulletForReloadLoop();
            }
            LogReloadSingleDebug(
                $"Queued +1 source='{source}' frame={Time.frameCount} time={Time.time:F3} " +
                $"pending={_pendingReloadSingleEvents} receivedTotal={_reloadSingleEventsReceivedDuringCurrentReload} " +
                $"deltaSinceLast={deltaSinceLast:F3}");
        }

        public void NotifyAmmoEjectEvent() {
            if(IsActiveWeaponLikelyDrake()) {
                _drakeTopShellEjectedSinceReloadComplete = true;
                _drakeShotCanceledReloadAfterAmmoEject = false;
                if(_drakeCurrentReloadStartedEmpty) {
                    _drakeCurrentEmptyReloadSawAmmoEject = true;
                }
                if(_isDrakeBottomShellSuppressionApplied) {
                    LogDrakeDebug(
                        $"NotifyAmmoEjectEvent restoring bottom shell. frame={Time.frameCount} time={Time.time:F3}");
                    RestoreDrakeBottomShellImmediate();
                }
            }

            if(!_isTrackingReload) return;
            _reloadHasReceivedAnyEvent = true;
            _reloadHasBeenActive = true;
            _lastReloadSignalTime = Time.time;
        }

        public void NotifyShellShowEvent() {
            var isDrake = IsActiveWeaponLikelyDrake();
            var isKar = IsActiveWeaponLikelyKar();
            switch(isDrake) {
                case false when !isKar:
                    return;
                case true:
                    LogDrakeDebug(
                        $"NotifyShellShowEvent. frame={Time.frameCount} time={Time.time:F3} " +
                        $"topAppliedBeforeShow={_isDrakeTopShellSuppressionApplied} " +
                        $"bottomAppliedBeforeShow={_isDrakeBottomShellSuppressionApplied}");

                    _drakeTopShellEjectedSinceReloadComplete = false;
                    _drakeShotCanceledReloadAfterAmmoEject = false;
                    _drakeShotCanceledEmptyReloadAfterAmmoEject = false;
                    _suppressDrakeTopShellEjectOnNextReload = false;
                    _suppressDrakeBottomShellOnNextReload = false;
                    RestoreDrakeTopShellImmediate();
                    RestoreDrakeBottomShellImmediate();
                    break;
            }

            if(!isKar) return;
            LogDrakeDebug(
                $"NotifyShellShowEvent restoring kar loop bullet. frame={Time.frameCount} time={Time.time:F3} " +
                $"hiddenBeforeShow={_isKarLoopBulletHidden}");
            RestoreKarLoopBulletImmediate();
        }

        public void NotifyReloadCompleteEvent(string sourceTag = null) {
            var isDrake = IsActiveWeaponLikelyDrake();
            var isKar = IsActiveWeaponLikelyKar();

            if(isDrake) {
                LogDrakeDebug(
                    $"NotifyReloadCompleteEvent restoring shell. frame={Time.frameCount} time={Time.time:F3} " +
                    $"topAppliedBeforeRestore={_isDrakeTopShellSuppressionApplied} " +
                    $"bottomAppliedBeforeRestore={_isDrakeBottomShellSuppressionApplied}");
                _drakeTopShellEjectedSinceReloadComplete = false;
                _drakeShotCanceledReloadAfterAmmoEject = false;
                _drakeShotCanceledEmptyReloadAfterAmmoEject = false;
                _suppressDrakeTopShellEjectOnNextReload = false;
                _suppressDrakeBottomShellOnNextReload = false;
                RestoreDrakeTopShellImmediate();
                RestoreDrakeBottomShellImmediate();
            }

            if(isKar) {
                LogDrakeDebug(
                    $"NotifyReloadCompleteEvent restoring kar loop bullet. frame={Time.frameCount} time={Time.time:F3} " +
                    $"hiddenBeforeRestore={_isKarLoopBulletHidden}");
                RestoreKarLoopBulletImmediate();
            }

            if(!_isTrackingReload) return;
            _reloadHasReceivedAnyEvent = true;
            _reloadHasBeenActive = true;
            _lastReloadSignalTime = Time.time;
            _reloadCompleteEventReceived = true;
            var source = string.IsNullOrWhiteSpace(sourceTag) ? "(unknown)" : sourceTag;
            LogReloadSingleDebug(
                $"ReloadComplete source='{source}' frame={Time.frameCount} time={Time.time:F3} " +
                $"receivedSingles={_reloadSingleEventsReceivedDuringCurrentReload} " +
                $"consumedSingles={_reloadSingleEventsConsumedDuringCurrentReload} " +
                $"pendingSingles={_pendingReloadSingleEvents}");
        }

        public void NotifyWeaponFireSoundEvent() {
            if(!IsKinemationSoundEventRoutingEnabled()) return;

            var fireSounds = _activeWeapon.weaponSettings.fireSounds;
            if(!HasAnyValidAudioClip(fireSounds)) return;
            _pendingWeaponFireSoundEvents++;
        }

        public void NotifyWeaponEventSoundEvent(int clipIndex) {
            if(!IsKinemationSoundEventRoutingEnabled()) return;
            if(clipIndex < 0) return;

            var weaponEventSounds = _activeWeapon.weaponSettings.weaponEventSounds;
            if(weaponEventSounds == null || clipIndex >= weaponEventSounds.Count) return;
            if(weaponEventSounds[clipIndex] == null) return;

            _pendingWeaponEventSoundIndices.Add(clipIndex);
        }

        public void NotifyEquipCompleteEvent() {
            if(!_isTrackingEquip) return;
            _equipHasBeenActive = true;
            _equipCompleteEventReceived = true;
            _lastEquipSignalTime = Time.time;

            _weaponManager = _weaponManager ? _weaponManager : GetComponentInParent<WeaponManager>();
            if(_weaponManager == null) return;
            _weaponManager.HandleKinemationEquipCompleted();
        }
    }
}


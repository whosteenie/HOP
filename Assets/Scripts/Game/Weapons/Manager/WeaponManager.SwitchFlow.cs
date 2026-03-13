using System.Collections;
using Game.Match;
using Game.Menu;
using Game.Player.Hopball;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Weapons {
    public partial class WeaponManager {
        private void UpdateKinemationEquipCompletionGate() {
            if(!IsPullingOut || !_requiresKinemationEquipCompleteForCurrentPullOut) return;
            if(CurrentWeaponIndex < 0 || CurrentWeaponIndex >= _fpWeaponInstances.Count) return;

            var currentFpWeapon = _fpWeaponInstances[CurrentWeaponIndex];
            if(!TryGetKinemationDriver(currentFpWeapon, out var kinemationDriver) || kinemationDriver == null) return;
            if(!kinemationDriver.HasActiveWeapon()) return;
            if(kinemationDriver.IsEquipSequenceInProgress()) return;

            HandlePullOutCompleted();
        }

        private void HideCurrentWorldWeapon() {
            if(CurrentWorldWeaponInstance == null) return;
            CurrentWorldWeaponInstance.SetActive(false);
            CurrentWorldWeaponInstance = null;
        }

        private void HideCurrentWeaponVisuals() {
            if(CurrentWeaponIndex >= 0 && CurrentWeaponIndex < _fpWeaponInstances.Count) {
                var oldFp = _fpWeaponInstances[CurrentWeaponIndex];
                if(oldFp != null) {
                    oldFp.SetActive(false);
                }
            }

            HideCurrentWorldWeapon();
        }

        private void QueuePendingTpWeapon(WeaponData data) {
            _pendingTpWeapon = ResolveWorldWeaponObject(data);
            if(_pendingTpWeapon != null) {
                _pendingTpWeapon.SetActive(false);
            }

            CurrentWorldWeaponInstance = null;
        }

        private void TriggerTpPullOutAnimation(int weaponIndex) {
            if(_playerAnimator == null) return;
            var slot = Mathf.Clamp(GetSlotForIndex(weaponIndex), 0, 1);
            _playerAnimator.SetInteger(WeaponIndexHash, slot);
            _playerAnimator.SetTrigger(PullOutHash);
        }

        private void ScheduleKinemationPullOutCompletionIfNeeded(int weaponIndex, float? delayOverride = null,
            bool forceSchedule = false) {
            if(!forceSchedule && !autoCompleteKinemationPullOut) return;
            if(_requiresKinemationEquipCompleteForCurrentPullOut) return;
            if(weaponIndex < 0 || weaponIndex >= _fpWeaponInstances.Count) return;

            var fpWeaponRoot = _fpWeaponInstances[weaponIndex];
            if(!forceSchedule && !TryGetKinemationDriver(fpWeaponRoot, out _)) return;

            if(_kinemationPullOutCompletionCoroutine != null) {
                StopCoroutine(_kinemationPullOutCompletionCoroutine);
            }

            var delay = delayOverride ?? Mathf.Max(0f, kinemationPullOutCompleteDelay);
            _kinemationPullOutCompletionCoroutine = StartCoroutine(KinemationPullOutCompletionRoutine(delay));
        }

        private IEnumerator KinemationPullOutCompletionRoutine(float delay) {
            if(delay > 0f) {
                yield return new WaitForSeconds(delay);
            } else {
                yield return null;
            }

            _kinemationPullOutCompletionCoroutine = null;
            HandlePullOutCompleted();
        }

        public void SwitchWeapon(int newIndex) {
            if(newIndex < 0 || newIndex >= weaponDataList.Count)
                return;

            if(IsOwner && _pendingPredictedWeaponIndex >= 0 && _pendingPredictedWeaponIndex != newIndex) {
                return;
            }

            // Check if holding hopball - if so, allow switching even to same weapon
            // Also check if restoring after dissolve to allow switch
            var isHoldingHopball = false;
            var isRestoringAfterDissolve = false;
            if(IsOwner) {
                if(playerController == null) return;
                var hopballController = playerController.PlayerHopballController;
                if(hopballController != null) {
                    if(hopballController.IsHoldingHopball) {
                        isHoldingHopball = true;
                        // Drop hopball when switching weapons (let weapon switch visuals handle showing)
                        hopballController.DropHopball(PlayerHopballController.HopballDropReason.WeaponSwitch);
                    }

                    // Check if restoring after dissolve
                    if(PlayerHopballController.IsRestoringAfterDissolve) {
                        isRestoringAfterDissolve = true;
                    }
                }
            }

            // Block switching to same weapon unless holding hopball or restoring after dissolve
            if(newIndex == CurrentWeaponIndex && !isHoldingHopball && !isRestoringAfterDissolve)
                return;

            if(!TryValidateSwitchTargetStrict(newIndex, out _, out _)) {
                return;
            }

            if(HasWeaponAuthority) {
                ProcessWeaponSwitchAuthorityRequest(newIndex);
                return;
            }

            if(!IsOwner) {
                return;
            }

            if(MatchCombatAuthority.Instance != null && NetworkObject != null && NetworkObject.IsSpawned) {
                ApplyApprovedLocalWeaponSwitch(newIndex);
                _pendingPredictedWeaponIndex = newIndex;
                MatchCombatAuthority.Instance.RequestWeaponSwitchAuthorityServerRpc(
                    new NetworkObjectReference(NetworkObject), newIndex);
            } else {
                Debug.LogError(
                    "[WeaponManager] MatchCombatAuthority is missing in the active gameplay scene. Weapon switches cannot be authority-validated.");
            }
        }

        /// <summary>
        /// Called from player animation event to show the TP weapon during pull out animation.
        /// </summary>
        public void ShowTpWeapon() {
            if(_pendingTpWeapon == null) return;
            _pendingTpWeapon.SetActive(true);

            // Update weapon data with the now-active TP weapon
            if(CurrentWeapon != null && CurrentWeaponIndex >= 0) {
                var data = weaponDataList[CurrentWeaponIndex];
                var fpWeapon = _fpWeaponInstances[CurrentWeaponIndex];
                if(fpWeapon == null || !TryGetKinemationDriver(fpWeapon, out var driver) || driver == null) {
                    Debug.LogError(
                        $"[WeaponManager][KIN-Strict] Missing KinemationFpWeaponDriver for '{data.weaponName}' in ShowTpWeapon.");
                    return;
                }
                var magCapacity = ResolveWeaponCapacity(data);
                if(magCapacity <= 0) {
                    Debug.LogError(
                        $"[WeaponManager][KIN-Strict] Invalid KIN ammo capacity for '{data.weaponName}' in ShowTpWeapon.");
                    return;
                }
                var restoredAmmo = ResolveRestoredAmmo(CurrentWeaponIndex, magCapacity, seedWhenMissing: false);

                CurrentWeapon.SwitchToWeapon(
                    data,
                    fpWeapon,
                    _pendingTpWeapon,
                    restoredAmmo,
                    magCapacity
                );
            }

            CurrentWorldWeaponInstance = _pendingTpWeapon;
            _pendingTpWeapon = null;

            EnsureWorldWeaponShadowState();
            EnsureWeaponHierarchyActive();

            _pendingHolsterHideSlot = -1;
            UpdateHolsterVisibility();
            RefreshOwnerHolsterShadowState();
        }

        /// <summary>
        /// Called when the pull-out animation completes (via animation event).
        /// Allows shooting and reloading again.
        /// </summary>
        public void HandlePullOutCompleted() {
            if(_pendingTpWeapon != null &&
               (GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch ||
                CurrentWorldWeaponInstance == null ||
                !CurrentWorldWeaponInstance.activeSelf)) {
                ShowTpWeapon();
            }

            IsPullingOut = false;
            _requiresKinemationEquipCompleteForCurrentPullOut = false;
            if(_kinemationPullOutCompletionCoroutine == null) return;
            StopCoroutine(_kinemationPullOutCompletionCoroutine);
            _kinemationPullOutCompletionCoroutine = null;
            ReconcileStableTpWeaponState();
        }

        public void HandleThirdPersonPullOutCompleted() {
            if(_requiresKinemationEquipCompleteForCurrentPullOut) {
                return;
            }

            HandlePullOutCompleted();
        }

        public void HandleKinemationEquipCompleted() {
            HandlePullOutCompleted();
        }

        /// <summary>
        /// Triggers the pullout animation. Used when hopball dissolves to restore weapon visibility.
        /// </summary>
        public void TriggerPullOutAnimation() {
            var isPostMatch = GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch;

            var requiresKinemationEquipCompletion = false;
            if(IsOwner && CurrentWeaponIndex >= 0 && CurrentWeaponIndex < weaponDataList.Count &&
               CurrentWeaponIndex < _fpWeaponInstances.Count) {
                var data = weaponDataList[CurrentWeaponIndex];
                var fpWeapon = _fpWeaponInstances[CurrentWeaponIndex];

                if(data != null && fpWeapon != null) {
                    if(!fpWeapon.activeSelf) {
                        fpWeapon = ActivateFpWeapon(CurrentWeaponIndex, data, triggerPullOutAnimation: true);
                    } else if(TryGetKinemationDriver(fpWeapon, out var kinemationDriver) && kinemationDriver != null) {
                        TryGetKinemationBindingForData(data, out var kinemationBinding);
                        ApplyResolvedKinemationViewmodelPose(fpWeapon, kinemationBinding);
                        kinemationDriver.InitializeIfNeeded(GetFpWeaponLayer());
                        kinemationDriver.PlayEquipAnimation(immediate: false);
                    }
                }

                requiresKinemationEquipCompletion = fpWeapon != null && TryGetKinemationDriver(fpWeapon, out _);
            }
            
            // If we're not switching weapons (e.g., after hopball dissolve), we need to set up _pendingTpWeapon
            // so the animation event can show it. The weapon might already be inactive from HideWorldWeapon().
            if(_pendingTpWeapon == null && CurrentWeaponIndex >= 0 && CurrentWeaponIndex < weaponDataList.Count) {
                QueuePendingTpWeapon(weaponDataList[CurrentWeaponIndex]);
                // Set holster slot to hide the correct holster during pullout
                _pendingHolsterHideSlot = GetSlotForIndex(CurrentWeaponIndex);
                UpdateHolsterVisibility();
            }
            
            if(_playerAnimator != null) {
                TriggerTpPullOutAnimation(CurrentWeaponIndex);
            }
            
            // Mark as pulling out
            IsPullingOut = true;
            _requiresKinemationEquipCompleteForCurrentPullOut = requiresKinemationEquipCompletion && !isPostMatch;
            if(isPostMatch) {
                ScheduleKinemationPullOutCompletionIfNeeded(
                    CurrentWeaponIndex,
                    Mathf.Max(kinemationPullOutCompleteDelay, postMatchPullOutFailSafeDelay),
                    forceSchedule: true
                );
            } else {
                ScheduleKinemationPullOutCompletionIfNeeded(CurrentWeaponIndex);
            }
        }

        /// <summary>
        /// Cancels any pending pull-out transition and forces a stable TP weapon state.
        /// Used during post-match blackout to avoid visible switch artifacts on podium.
        /// </summary>
        public void CancelPendingPullOutForPostMatch() {
            IsPullingOut = false;
            _requiresKinemationEquipCompleteForCurrentPullOut = false;
            _pendingHolsterHideSlot = -1;
            if(_kinemationPullOutCompletionCoroutine != null) {
                StopCoroutine(_kinemationPullOutCompletionCoroutine);
                _kinemationPullOutCompletionCoroutine = null;
            }

            if(_playerAnimator != null) {
                _playerAnimator.ResetTrigger(PullOutHash);
            }

            if(CurrentWorldWeaponInstance == null) {
                ResolveCurrentWorldWeaponReference();
            }

            _pendingTpWeapon = null;
            if(CurrentWorldWeaponInstance != null && !CurrentWorldWeaponInstance.activeSelf) {
                CurrentWorldWeaponInstance.SetActive(true);
            }

            EnsureWeaponHierarchyActive();

            // Podium flow needs visible TP weapon even for owners.
            if(playerController != null) {
                if(playerController.PlayerRenderer != null) {
                    playerController.PlayerRenderer.SetWorldWeaponRenderersEnabled(true);
                }

                if(playerController.PlayerShadow != null) {
                    playerController.PlayerShadow.SetWorldWeaponRenderersShadowMode(ShadowCastingMode.On);
                }
            }

            UpdateHolsterVisibility();
        }

        /// <summary>
        /// Sets the TP animator to the correct weapon hold state for podium. Called after animator is ready (e.g. post SnapBonesToRoot).
        /// </summary>
        public void SetTpWeaponIndexForPodium() {
            if(_playerAnimator == null) return;
            var slot = Mathf.Clamp(GetSlotForIndex(CurrentWeaponIndex), 0, 1);
            _playerAnimator.SetInteger(WeaponIndexHash, slot);
            var layerIndex = _playerAnimator.GetLayerIndex("Weapon Hold Layer");
            if(layerIndex < 0) return;
            var stateName = slot == 0 ? "AKAim" : "PistolAim";
            _playerAnimator.Play(stateName, layerIndex, 0f);
        }

        private void EnsureWorldWeaponShadowState() {
            if(CurrentWorldWeaponInstance == null) return;

            if(!CurrentWorldWeaponInstance.activeSelf) {
                CurrentWorldWeaponInstance.SetActive(true);
            }

            var isOwner = playerController != null && playerController.IsOwner;
            var isPostMatch = GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch;
            var targetMode = isOwner && !isPostMatch
                ? ShadowCastingMode.ShadowsOnly
                : ShadowCastingMode.On;

            var playerShadow = playerController != null ? playerController.PlayerShadow : null;
            if(playerShadow != null) {
                playerShadow.SetWorldWeaponRenderersShadowMode(targetMode);
                return;
            }

            var renderers = CurrentWorldWeaponInstance.GetComponentsInChildren<MeshRenderer>(true);
            foreach(var mr in renderers) {
                if(mr == null) continue;
                mr.enabled = true;
                mr.shadowCastingMode = targetMode;
            }
        }

        private void EnsureWeaponHierarchyActive() {
            if(CurrentWorldWeaponInstance == null) return;
            EnsureHierarchyActive(CurrentWorldWeaponInstance);
        }

        private void SyncCurrentWeaponPresentationToResolvedWorldWeapon(GameObject worldWeaponInstance) {
            if(CurrentWeapon == null) return;
            if(CurrentWeaponIndex < 0 || CurrentWeaponIndex >= weaponDataList.Count) return;

            var data = weaponDataList[CurrentWeaponIndex];
            if(data == null) return;

            GameObject fpWeapon = null;
            if(CurrentWeaponIndex >= 0 && CurrentWeaponIndex < _fpWeaponInstances.Count) {
                fpWeapon = _fpWeaponInstances[CurrentWeaponIndex];
            }

            var magCapacity = ResolveWeaponCapacity(data);
            if(magCapacity <= 0) return;

            var restoredAmmo = Mathf.Clamp(CurrentWeapon.currentAmmo, 0, magCapacity);
            if(restoredAmmo == 0 && CurrentWeapon.CurrentWeaponData != data) {
                restoredAmmo = ResolveRestoredAmmo(CurrentWeaponIndex, magCapacity, seedWhenMissing: false);
            }

            CurrentWeapon.SwitchToWeapon(data, fpWeapon, worldWeaponInstance, restoredAmmo, magCapacity);
        }

        private void ReconcileStableTpWeaponState() {
            if(_deferTpRevealUntilRespawn || IsPullingOut) return;
            if(playerController == null) return;
            if(playerController.NetIsDead is { Value: true }) return;
            if(playerController.PlayerRagdoll != null && playerController.PlayerRagdoll.IsRagdoll) return;
            if(playerController.IsHoldingHopball) return;
            if(CurrentWeaponIndex < 0 || CurrentWeaponIndex >= weaponDataList.Count) return;

            var expectedWeapon = ResolveWorldWeaponObject(weaponDataList[CurrentWeaponIndex]);
            if(expectedWeapon == null) return;

            var repairedPresentationState = CurrentWorldWeaponInstance != expectedWeapon;
            if(CurrentWorldWeaponInstance != null && CurrentWorldWeaponInstance != expectedWeapon) {
                CurrentWorldWeaponInstance.SetActive(false);
            }

            CurrentWorldWeaponInstance = expectedWeapon;
            if(!CurrentWorldWeaponInstance.activeSelf) {
                CurrentWorldWeaponInstance.SetActive(true);
                repairedPresentationState = true;
            }

            if(_pendingTpWeapon != null || _pendingHolsterHideSlot != -1) {
                repairedPresentationState = true;
            }

            if(CurrentWeapon != null && CurrentWeapon.CurrentWeaponData != weaponDataList[CurrentWeaponIndex]) {
                repairedPresentationState = true;
            }

            _pendingTpWeapon = null;
            _pendingHolsterHideSlot = -1;

            if(repairedPresentationState) {
                SyncCurrentWeaponPresentationToResolvedWorldWeapon(CurrentWorldWeaponInstance);
            }

            EnsureWeaponHierarchyActive();
            EnsureWorldWeaponShadowState();
            UpdateHolsterVisibility();
            RefreshOwnerHolsterShadowState();
        }

        private static void EnsureHierarchyActive(GameObject instanceRoot) {
            if(instanceRoot == null) return;
            var parent = instanceRoot.transform;
            while(parent != null) {
                if(!parent.gameObject.activeSelf) {
                    parent.gameObject.SetActive(true);
                }

                parent = parent.parent;
            }
        }
        public void ProcessWeaponSwitchAuthorityRequest(int newIndex) {
            if(!HasWeaponAuthority) return;
            var approvedWeaponIndex = GetServerAuthoritativeWeaponIndex();
            if(!TryConsumeWeaponSwitchQuota()) {
                RejectPredictedWeaponSwitchOwnerRpc(approvedWeaponIndex);
                return;
            }

            if(!TryValidateSwitchTargetStrict(newIndex, out _, out _)) {
                RejectPredictedWeaponSwitchOwnerRpc(approvedWeaponIndex);
                return;
            }

            ApplyServerAuthoritativeWeaponSwitch(newIndex);
            if(ResolvePlayerState() == null) {
                RejectPredictedWeaponSwitchOwnerRpc(approvedWeaponIndex);
                return;
            }

            if(ReplicatedEquippedWeaponIndex.Value != newIndex) {
                ReplicatedEquippedWeaponIndex.Value = newIndex;
            }
        }

        [Rpc(SendTo.Owner)]
        private void RejectPredictedWeaponSwitchOwnerRpc(int approvedWeaponIndex) {
            if(!IsOwner) {
                return;
            }

            if(approvedWeaponIndex < 0 || approvedWeaponIndex >= weaponDataList.Count) {
                approvedWeaponIndex = _lastApprovedWeaponIndex;
            }

            _pendingPredictedWeaponIndex = -1;

            if(approvedWeaponIndex < 0 || approvedWeaponIndex >= weaponDataList.Count) {
                return;
            }

            if(CurrentWeaponIndex == approvedWeaponIndex) {
                _lastApprovedWeaponIndex = approvedWeaponIndex;
                return;
            }

            ApplyApprovedLocalWeaponSwitch(approvedWeaponIndex, playSwitchAudio: false);
            _lastApprovedWeaponIndex = approvedWeaponIndex;
        }

        private void ApplyApprovedLocalWeaponSwitch(int newIndex, bool playSwitchAudio = true) {
            if(newIndex < 0 || newIndex >= weaponDataList.Count) {
                return;
            }

            var isPostMatch = GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch;
            if(!TryValidateSwitchTargetStrict(newIndex, out var data, out var magCapacity)) {
                return;
            }

            if(playSwitchAudio && IsOwner && Audio2.AudioService.Instance != null) {
                Audio2.AudioService.Instance.Play("ui.weapon.switch", Vector3.zero);
            }

            if(CurrentWeapon != null && CurrentWeapon.IsReloadInProgress) {
                CurrentWeapon.CancelReloadForWeaponSwitch();
            }

            if(CurrentWeapon != null && CurrentWeaponIndex >= 0) {
                _ammoAuthority.CacheCurrentAmmo(CurrentWeaponIndex, CurrentWeapon.currentAmmo);
            }

            var previousWeaponIndex = CurrentWeaponIndex;
            var previousWorldWeapon = CurrentWorldWeaponInstance;

            if(CurrentWeaponIndex >= 0) {
                HideCurrentWeaponVisuals();
            }

            CurrentWeaponIndex = newIndex;
            _pendingHolsterHideSlot = GetSlotForIndex(CurrentWeaponIndex);

            var fp = ActivateFpWeapon(CurrentWeaponIndex, data, triggerPullOutAnimation: true);
            if(fp == null) {
                Debug.LogError($"[WeaponManager][KIN-Strict] Failed to activate FP weapon for '{data.weaponName}'.");
                CurrentWeaponIndex = previousWeaponIndex;
                if(previousWeaponIndex >= 0 && previousWeaponIndex < _fpWeaponInstances.Count) {
                    var previousFp = _fpWeaponInstances[previousWeaponIndex];
                    if(previousFp != null) {
                        previousFp.SetActive(true);
                    }
                }

                if(previousWorldWeapon != null) {
                    previousWorldWeapon.SetActive(true);
                    CurrentWorldWeaponInstance = previousWorldWeapon;
                }

                _pendingHolsterHideSlot = -1;
                UpdateHolsterVisibility();
                return;
            }

            var hasKinemationDriver = fp != null && TryGetKinemationDriver(fp, out _);
            _requiresKinemationEquipCompleteForCurrentPullOut = hasKinemationDriver && !isPostMatch;

            QueuePendingTpWeapon(data);

            var restoredAmmo = ResolveRestoredAmmo(CurrentWeaponIndex, magCapacity, seedWhenMissing: false);

            CurrentWeapon.SwitchToWeapon(data, fp, null, restoredAmmo, magCapacity);
            IsPullingOut = true;
            if(isPostMatch) {
                ScheduleKinemationPullOutCompletionIfNeeded(
                    CurrentWeaponIndex,
                    Mathf.Max(kinemationPullOutCompleteDelay, postMatchPullOutFailSafeDelay),
                    forceSchedule: true
                );
            } else {
                ScheduleKinemationPullOutCompletionIfNeeded(CurrentWeaponIndex);
            }

            if(_playerAnimator != null) {
                TriggerTpPullOutAnimation(newIndex);
            }

            UpdateHolsterVisibility();
            RefreshOwnerHolsterShadowState();
        }

        private void ApplyRemoteWeaponSwitch(int newIndex) {
            if(newIndex < 0 || newIndex >= weaponDataList.Count) return;

            HideCurrentWorldWeapon();

            CurrentWeaponIndex = newIndex;
            var data = weaponDataList[newIndex];
            _pendingHolsterHideSlot = GetSlotForIndex(CurrentWeaponIndex);

            QueuePendingTpWeapon(data);

            if(_playerAnimator == null) return;
            TriggerTpPullOutAnimation(newIndex);

            UpdateHolsterVisibility();
        }
    }
}

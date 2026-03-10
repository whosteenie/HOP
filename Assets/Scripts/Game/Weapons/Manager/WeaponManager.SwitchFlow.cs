using System.Collections;
using Game.Menu;
using Game.Player.Hopball;
using Network.AntiCheat;
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
            var isPostMatch = GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch;

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

            if(!TryValidateSwitchTargetStrict(newIndex, out var data, out var magCapacity)) {
                return;
            }

            if(IsOwner) {
                if(Audio2.AudioService.Instance != null) {
                    Audio2.AudioService.Instance.Play("ui.weapon.switch", Vector3.zero);
                }
            }

            if(CurrentWeapon != null && CurrentWeapon.IsReloadInProgress) {
                CurrentWeapon.CancelReloadForWeaponSwitch();
            }

            if(IsServer) {
                ApplyServerAuthoritativeWeaponSwitch(newIndex);
            }

            // Cache ammo from current weapon before switching away
            if(CurrentWeapon != null && CurrentWeaponIndex >= 0) {
                _ammoAuthority.CacheCurrentAmmo(CurrentWeaponIndex, CurrentWeapon.currentAmmo);
            }

            var previousWeaponIndex = CurrentWeaponIndex;
            var previousWorldWeapon = CurrentWorldWeaponInstance;

            // Immediately hide current weapon (no sheath delay)
            if(CurrentWeaponIndex >= 0) {
                HideCurrentWeaponVisuals();
            }

            // Commit to new weapon index immediately
            CurrentWeaponIndex = newIndex;
            _pendingHolsterHideSlot = GetSlotForIndex(CurrentWeaponIndex);

            // Prepare and show new FP weapon
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

            // Prepare new 3P weapon but DON'T show it yet - wait for animation event
            QueuePendingTpWeapon(data);

            // Restore ammo from authoritative KINEMATION capacity path.
            var restoredAmmo = ResolveRestoredAmmo(CurrentWeaponIndex, magCapacity, seedWhenMissing: false);

            // Update weapon data immediately (no waiting for animations)
            // Pass null for worldWeaponInstance since it's not shown yet - will be set when TP weapon is shown
            CurrentWeapon.SwitchToWeapon(data, fp, null, restoredAmmo, magCapacity);
            // Set pulling out state. During post-match, rely on TP animation events (not FP/KIN),
            // with a longer fail-safe timer in case an event is missing.
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

            if(_playerAnimator == null) return;
            TriggerTpPullOutAnimation(newIndex);

            if(IsOwner) {
                if(IsServer) {
                    if(TryConsumeWeaponSwitchQuota()) {
                        BroadcastWeaponSwitchClientRpc(newIndex);
                    }
                } else {
                    RequestWeaponSwitchBroadcastServerRpc(newIndex);
                }
            }

            UpdateHolsterVisibility();
            RefreshOwnerHolsterShadowState();
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
            if(GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch && _pendingTpWeapon != null) {
                ShowTpWeapon();
            }

            IsPullingOut = false;
            _requiresKinemationEquipCompleteForCurrentPullOut = false;
            if(_kinemationPullOutCompletionCoroutine == null) return;
            StopCoroutine(_kinemationPullOutCompletionCoroutine);
            _kinemationPullOutCompletionCoroutine = null;
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
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void RequestWeaponSwitchBroadcastServerRpc(int newIndex, RpcParams rpcParams = default) {
            if(rpcParams.Receive.SenderClientId != OwnerClientId) {
                AntiCheatLogger.LogAuthorityViolation("WeaponManager.RequestWeaponSwitchBroadcastServerRpc",
                    rpcParams.Receive.SenderClientId);
                return;
            }

            if(!TryConsumeWeaponSwitchQuota()) return;
            if(!TryValidateSwitchTargetStrict(newIndex, out _, out _)) return;

            ApplyServerAuthoritativeWeaponSwitch(newIndex);
            BroadcastWeaponSwitchClientRpc(newIndex);
        }
        [Rpc(SendTo.Everyone, Delivery = RpcDelivery.Reliable)]
        private void BroadcastWeaponSwitchClientRpc(int newIndex) {
            if(IsOwner) return;
            ApplyRemoteWeaponSwitch(newIndex);
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

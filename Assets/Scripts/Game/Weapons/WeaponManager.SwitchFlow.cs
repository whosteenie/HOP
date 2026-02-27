using System.Collections;
using Game.Menu;
using Game.Player;
using Network.Events;
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
            if(CurrentWorldWeaponInstance != null) {
                CurrentWorldWeaponInstance.SetActive(false);
                CurrentWorldWeaponInstance = null;
            }
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

        private void ScheduleKinemationPullOutCompletionIfNeeded(int weaponIndex) {
            if(!autoCompleteKinemationPullOut) return;
            if(_requiresKinemationEquipCompleteForCurrentPullOut) return;
            if(weaponIndex < 0 || weaponIndex >= _fpWeaponInstances.Count) return;

            var fpWeaponRoot = _fpWeaponInstances[weaponIndex];
            if(!TryGetKinemationDriver(fpWeaponRoot, out _)) return;

            if(_kinemationPullOutCompletionCoroutine != null) {
                StopCoroutine(_kinemationPullOutCompletionCoroutine);
            }

            _kinemationPullOutCompletionCoroutine = StartCoroutine(KinemationPullOutCompletionRoutine());
        }

        private IEnumerator KinemationPullOutCompletionRoutine() {
            var delay = Mathf.Max(0f, kinemationPullOutCompleteDelay);
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

            if(IsOwner) {
                if(Audio2.AudioService.Instance != null) {
                    Audio2.AudioService.Instance.Play("ui.weapon.switch", Vector3.zero);
                }
            }

            // Publish weapon switch event
            EventBus.Publish(new WeaponSwitchedEvent(newIndex));

            // Cache ammo from current weapon before switching away
            if(CurrentWeapon != null && CurrentWeaponIndex >= 0) {
                _ammoAuthority.CacheCurrentAmmo(CurrentWeaponIndex, CurrentWeapon.currentAmmo);
            }

            // Immediately hide current weapon (no sheath delay)
            if(CurrentWeaponIndex >= 0) {
                HideCurrentWeaponVisuals();
            }

            // Commit to new weapon index immediately
            CurrentWeaponIndex = newIndex;
            var data = weaponDataList[CurrentWeaponIndex];
            _pendingHolsterHideSlot = GetSlotForIndex(CurrentWeaponIndex);

            // Prepare and show new FP weapon
            var fp = ActivateFpWeapon(CurrentWeaponIndex, data, triggerPullOutAnimation: true);
            _requiresKinemationEquipCompleteForCurrentPullOut =
                fp != null &&
                TryGetKinemationDriver(fp, out _);

            // Prepare new 3P weapon but DON'T show it yet - wait for animation event
            QueuePendingTpWeapon(data);

            // Restore ammo from authoritative KINEMATION capacity path.
            var magCapacity = ResolveWeaponCapacity(data);
            var restoredAmmo = ResolveRestoredAmmo(CurrentWeaponIndex, magCapacity, seedWhenMissing: false);

            // Update weapon data immediately (no waiting for animations)
            // Pass null for worldWeaponInstance since it's not shown yet - will be set when TP weapon is shown
            CurrentWeapon.SwitchToWeapon(data, fp, null, restoredAmmo, magCapacity);
            ReportAmmoSync(CurrentWeaponIndex, restoredAmmo);

            // Set pulling out state
            // The pull-out animation will call HandlePullOutCompleted() when done
            IsPullingOut = true;
            ScheduleKinemationPullOutCompletionIfNeeded(CurrentWeaponIndex);

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
                var magCapacity = ResolveWeaponCapacity(data);
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
            IsPullingOut = false;
            _requiresKinemationEquipCompleteForCurrentPullOut = false;
            if(_kinemationPullOutCompletionCoroutine != null) {
                StopCoroutine(_kinemationPullOutCompletionCoroutine);
                _kinemationPullOutCompletionCoroutine = null;
            }
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
            if(_playerAnimator == null) return;
            
            // If we're not switching weapons (e.g., after hopball dissolve), we need to set up _pendingTpWeapon
            // so the animation event can show it. The weapon might already be inactive from HideWorldWeapon().
            if(_pendingTpWeapon == null && CurrentWeaponIndex >= 0 && CurrentWeaponIndex < weaponDataList.Count) {
                QueuePendingTpWeapon(weaponDataList[CurrentWeaponIndex]);
                // Set holster slot to hide the correct holster during pullout
                _pendingHolsterHideSlot = GetSlotForIndex(CurrentWeaponIndex);
                UpdateHolsterVisibility();
            }
            
            TriggerTpPullOutAnimation(CurrentWeaponIndex);
            
            // Mark as pulling out
            IsPullingOut = true;
            _requiresKinemationEquipCompleteForCurrentPullOut = false;
            ScheduleKinemationPullOutCompletionIfNeeded(CurrentWeaponIndex);
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

        [Rpc(SendTo.Server)]
        private void RequestWeaponSwitchBroadcastServerRpc(int newIndex) {
            if(!TryConsumeWeaponSwitchQuota()) return;
            BroadcastWeaponSwitchClientRpc(newIndex);
        }

        [Rpc(SendTo.Everyone, Delivery = RpcDelivery.Unreliable)]
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

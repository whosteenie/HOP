using System.Collections;
using Events;
using Game.Audio.System;
using Game.Weapon.Core;
using Network.AntiCheat;
using Network.Core;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Weapon.Manager {
    internal sealed class WeaponSwitch {
        #region Fields

        private readonly WeaponManager _root;

        #endregion

        #region Construction

        public WeaponSwitch(WeaponManager root) {
            _root = root;
        }

        #endregion

        #region Properties

        private bool HasWeaponAuthority => NetworkAuthority.HasGlobalAuthority(_root);

        #endregion

        #region Update Gates

        public void UpdateKinemationEquipCompletionGate() {
            if(!_root.IsPullingOutInternal || !_root.RequiresKinemationEquipCompleteForCurrentPullOut) return;
            if(_root.CurrentWeaponIndexInternal < 0 || _root.CurrentWeaponIndexInternal >= _root.FpWeaponInstancesRef.Count) return;

            var currentFpWeapon = _root.FpWeaponInstancesRef[_root.CurrentWeaponIndexInternal];
            if(!WeaponManager.TryGetKinemationDriverInternal(currentFpWeapon, out var kinemationDriver) || kinemationDriver == null) return;
            if(!kinemationDriver.HasActiveWeapon()) return;
            if(kinemationDriver.IsEquipSequenceInProgress()) return;

            HandlePullOutCompleted();
        }

        #endregion

        #region Public Switch Flow

        public void SwitchWeapon(int newIndex) {
            if(newIndex < 0 || newIndex >= _root.WeaponDataListRef.Count) return;

            if(_root.IsOwner && _root.PendingPredictedWeaponIndex >= 0 && _root.PendingPredictedWeaponIndex != newIndex) {
                return;
            }

            var isHoldingHopball = false;
            var isRestoringAfterDissolve = false;
            if(_root.IsOwner) {
                if(_root.PlayerControllerRef == null) return;
                var playerNetworkObject = _root.PlayerControllerRef.NetworkObject;
                if(playerNetworkObject != null) {
                    var switchRequestedEvent =
                        new WeaponSwitchRequestedEvent(playerNetworkObject.NetworkObjectId, newIndex);
                    EventBus.Publish(switchRequestedEvent);
                    isHoldingHopball = switchRequestedEvent.WasHoldingHopball;
                    isRestoringAfterDissolve = switchRequestedEvent.WasRestoringAfterDissolve;
                }
            }

            if(newIndex == _root.CurrentWeaponIndexInternal && isHoldingHopball) {
                _root.RestoreCurrentWeaponPresentationAfterHopballDrop();
                TriggerPullOutAnimation();
                return;
            }

            if(newIndex == _root.CurrentWeaponIndexInternal && !isHoldingHopball && !isRestoringAfterDissolve) {
                return;
            }

            if(!_root.TryValidateSwitchTargetStrict(newIndex, out _, out _)) {
                return;
            }

            if(HasWeaponAuthority) {
                ProcessWeaponSwitchRequest(newIndex);
                return;
            }

            if(!_root.IsOwner) {
                return;
            }

            if(WeaponCombatAuthority.Instance != null && _root.NetworkObject != null && _root.NetworkObject.IsSpawned) {
                ApplyApprovedLocalWeaponSwitch(newIndex);
                _root.PendingPredictedWeaponIndex = newIndex;
                WeaponCombatAuthority.Instance.RequestWeaponSwitchServerRpc(
                    new NetworkObjectReference(_root.NetworkObject), newIndex);
            } else {
                Debug.LogError(
                    "[WeaponManager] MatchCombatAuthority is missing in the active gameplay scene. Weapon switches cannot be authority-validated.");
            }
        }

        public void ProcessWeaponSwitchRequest(int newIndex) {
            if(!HasWeaponAuthority) return;
            var approvedWeaponIndex = GetServerAuthoritativeWeaponIndex();
            if(!TryConsumeWeaponSwitchQuota()) {
                _root.RejectPredictedWeaponSwitchOwnerRpc(approvedWeaponIndex);
                return;
            }

            if(!_root.TryValidateSwitchTargetStrict(newIndex, out _, out _)) {
                _root.RejectPredictedWeaponSwitchOwnerRpc(approvedWeaponIndex);
                return;
            }

            _root.ApplyServerWeaponSwitch(newIndex);
            if(_root.ResolvePlayerState() == null) {
                _root.RejectPredictedWeaponSwitchOwnerRpc(approvedWeaponIndex);
                return;
            }

            if(_root.ReplicatedEquippedWeaponIndex.Value != newIndex) {
                _root.ReplicatedEquippedWeaponIndex.Value = newIndex;
            } else {
                _root.ConfirmPredictedWeaponSwitchOwnerRpc(newIndex);
            }
        }

        public void RejectPredictedWeaponSwitchOwner(int approvedWeaponIndex) {
            if(!_root.IsOwner) return;

            if(approvedWeaponIndex < 0 || approvedWeaponIndex >= _root.WeaponDataListRef.Count) {
                approvedWeaponIndex = _root.LastApprovedWeaponIndex;
            }

            _root.PendingPredictedWeaponIndex = -1;

            if(approvedWeaponIndex < 0 || approvedWeaponIndex >= _root.WeaponDataListRef.Count) {
                return;
            }

            if(_root.CurrentWeaponIndexInternal == approvedWeaponIndex) {
                _root.LastApprovedWeaponIndex = approvedWeaponIndex;
                return;
            }

            ApplyApprovedLocalWeaponSwitch(approvedWeaponIndex, false);
            _root.LastApprovedWeaponIndex = approvedWeaponIndex;
        }

        public void ConfirmPredictedWeaponSwitchOwner(int approvedWeaponIndex) {
            if(!_root.IsOwner) return;
            if(approvedWeaponIndex < 0 || approvedWeaponIndex >= _root.WeaponDataListRef.Count) return;

            _root.PendingPredictedWeaponIndex = -1;
            _root.LastApprovedWeaponIndex = approvedWeaponIndex;

            if(_root.CurrentWeaponIndexInternal != approvedWeaponIndex) {
                ApplyApprovedLocalWeaponSwitch(approvedWeaponIndex, false);
            }
        }

        public void ApplyApprovedLocalWeaponSwitch(int newIndex, bool playSwitchAudio = true) {
            if(newIndex < 0 || newIndex >= _root.WeaponDataListRef.Count) {
                return;
            }

            var isPostMatch = _root.IsPostMatchFlowActive;
            if(!_root.TryValidateSwitchTargetStrict(newIndex, out var data, out var magCapacity)) {
                return;
            }

            if(playSwitchAudio && _root.IsOwner && AudioService.Instance != null) {
                AudioService.Instance.Play("ui.weapon.switch", Vector3.zero);
            }

            if(_root.CurrentWeaponInternal != null && _root.CurrentWeaponInternal.IsReloadInProgress) {
                _root.CurrentWeaponInternal.CancelReloadForWeaponSwitch();
            }

            if(_root.CurrentWeaponInternal != null && _root.CurrentWeaponIndexInternal >= 0) {
                _root.AmmoAuthorityRef.CacheCurrentAmmo(_root.CurrentWeaponIndexInternal, _root.CurrentWeaponInternal.currentAmmo);
            }

            var previousWeaponIndex = _root.CurrentWeaponIndexInternal;
            var previousWorldWeapon = _root.CurrentWorldWeaponInstanceInternal;

            if(_root.CurrentWeaponIndexInternal >= 0) {
                HideCurrentWeaponVisuals();
            }

            _root.CurrentWeaponIndexInternal = newIndex;
            _root.PendingHolsterHideSlot = _root.GetSlotForIndexInternal(_root.CurrentWeaponIndexInternal);

            var fp = _root.ActivateFpWeaponInternal(_root.CurrentWeaponIndexInternal, data, true);
            if(fp == null) {
                Debug.LogError($"[WeaponManager][KIN-Strict] Failed to activate FP weapon for '{data.weaponName}'.");
                _root.CurrentWeaponIndexInternal = previousWeaponIndex;
                if(previousWeaponIndex >= 0 && previousWeaponIndex < _root.FpWeaponInstancesRef.Count) {
                    var previousFp = _root.FpWeaponInstancesRef[previousWeaponIndex];
                    if(previousFp != null) {
                        previousFp.SetActive(true);
                    }
                }

                if(previousWorldWeapon != null) {
                    previousWorldWeapon.SetActive(true);
                    _root.CurrentWorldWeaponInstanceInternal = previousWorldWeapon;
                }

                _root.PendingHolsterHideSlot = -1;
                _root.RefreshHolsterVisibility();
                return;
            }

            var hasKinemationDriver = fp != null && WeaponManager.TryGetKinemationDriverInternal(fp, out _);
            _root.RequiresKinemationEquipCompleteForCurrentPullOut = hasKinemationDriver && !isPostMatch;

            QueuePendingTpWeapon(data);

            var restoredAmmo = _root.ResolveRestoredAmmo(_root.CurrentWeaponIndexInternal, magCapacity, false);

            _root.CurrentWeaponInternal.SwitchToWeapon(data, fp, null, restoredAmmo, magCapacity);
            _root.IsPullingOutInternal = true;
            if(isPostMatch) {
                ScheduleKinemationPullOutIfNeeded(
                    _root.CurrentWeaponIndexInternal,
                    Mathf.Max(_root.KinemationPullOutCompleteDelay, _root.PostMatchPullOutFailSafeDelay),
                    forceSchedule: true
                );
            } else {
                ScheduleKinemationPullOutIfNeeded(_root.CurrentWeaponIndexInternal);
            }

            if(_root.PlayerAnimatorRef != null) {
                TriggerTpPullOutAnimation(newIndex);
            }

            _root.RefreshHolsterVisibility();
            _root.RefreshHolsterShadowState();
        }

        public void ApplyRemoteWeaponSwitch(int newIndex) {
            if(newIndex < 0 || newIndex >= _root.WeaponDataListRef.Count) return;

            HideCurrentWorldWeapon();

            _root.CurrentWeaponIndexInternal = newIndex;
            var data = _root.WeaponDataListRef[newIndex];
            _root.PendingHolsterHideSlot = _root.GetSlotForIndexInternal(_root.CurrentWeaponIndexInternal);

            QueuePendingTpWeapon(data);

            if(_root.PlayerAnimatorRef == null) return;
            TriggerTpPullOutAnimation(newIndex);

            _root.RefreshHolsterVisibility();
        }

        #endregion

        #region Pull-Out Flow

        public void ShowTpWeapon() {
            if(_root.PendingTpWeapon == null) return;
            _root.PendingTpWeapon.SetActive(true);

            if(_root.CurrentWeaponInternal != null && _root.CurrentWeaponIndexInternal >= 0) {
                var data = _root.WeaponDataListRef[_root.CurrentWeaponIndexInternal];
                var fpWeapon = _root.FpWeaponInstancesRef[_root.CurrentWeaponIndexInternal];
                if(fpWeapon == null || !WeaponManager.TryGetKinemationDriverInternal(fpWeapon, out var driver) || driver == null) {
                    Debug.LogError(
                        $"[WeaponManager][KIN-Strict] Missing KinemationFpWeaponDriver for '{data.weaponName}' in ShowTpWeapon.");
                    return;
                }

                var magCapacity = _root.ResolveWeaponCapacity(data);
                if(magCapacity <= 0) {
                    Debug.LogError(
                        $"[WeaponManager][KIN-Strict] Invalid KIN ammo capacity for '{data.weaponName}' in ShowTpWeapon.");
                    return;
                }

                var restoredAmmo = _root.ResolveRestoredAmmo(_root.CurrentWeaponIndexInternal, magCapacity, false);

                _root.CurrentWeaponInternal.SwitchToWeapon(
                    data,
                    fpWeapon,
                    _root.PendingTpWeapon,
                    restoredAmmo,
                    magCapacity
                );
            }

            _root.CurrentWorldWeaponInstanceInternal = _root.PendingTpWeapon;
            _root.PendingTpWeapon = null;

            _root.EnsureWorldWeaponShadowStateInternal();
            _root.EnsureWeaponHierarchyActiveInternal();

            _root.PendingHolsterHideSlot = -1;
            _root.RefreshHolsterVisibility();
            _root.RefreshHolsterShadowState();
        }

        public void HandlePullOutCompleted() {
            if(_root.PendingTpWeapon != null &&
               (_root.IsPostMatchFlowActive ||
                _root.CurrentWorldWeaponInstanceInternal == null ||
                !_root.CurrentWorldWeaponInstanceInternal.activeSelf)) {
                ShowTpWeapon();
            }

            _root.IsPullingOutInternal = false;
            _root.RequiresKinemationEquipCompleteForCurrentPullOut = false;
            if(_root.KinemationPullOutCompletionCoroutine == null) return;
            _root.StopCoroutine(_root.KinemationPullOutCompletionCoroutine);
            _root.KinemationPullOutCompletionCoroutine = null;
            ReconcileStableTpWeaponState();
        }

        public void HandleThirdPersonPullOutCompleted() {
            if(_root.RequiresKinemationEquipCompleteForCurrentPullOut) {
                return;
            }

            HandlePullOutCompleted();
        }

        public void HandleKinemationEquipCompleted() {
            HandlePullOutCompleted();
        }

        public void TriggerPullOutAnimation() {
            var isPostMatch = _root.IsPostMatchFlowActive;

            var requiresKinemationEquipCompletion = false;
            if(_root.IsOwner && _root.CurrentWeaponIndexInternal >= 0 && _root.CurrentWeaponIndexInternal < _root.WeaponDataListRef.Count &&
               _root.CurrentWeaponIndexInternal < _root.FpWeaponInstancesRef.Count) {
                var data = _root.WeaponDataListRef[_root.CurrentWeaponIndexInternal];
                var fpWeapon = _root.FpWeaponInstancesRef[_root.CurrentWeaponIndexInternal];
                if(data != null && fpWeapon != null) {
                    if(!fpWeapon.activeSelf) {
                        fpWeapon = _root.ActivateFpWeaponInternal(_root.CurrentWeaponIndexInternal, data, true);
                    } else if(WeaponManager.TryGetKinemationDriverInternal(fpWeapon, out var kinemationDriver) && kinemationDriver != null) {
                        _root.TryGetKinemationBinding(data, out var kinemationBinding);
                        _root.ApplyKinemationViewmodelPoseInternal(fpWeapon, kinemationBinding);
                        kinemationDriver.InitializeIfNeeded(_root.GetFpWeaponLayerInternal());
                        kinemationDriver.PlayEquipAnimation(immediate: false);
                    }
                }

                requiresKinemationEquipCompletion = fpWeapon != null && WeaponManager.TryGetKinemationDriverInternal(fpWeapon, out _);
            }

            if(_root.PendingTpWeapon == null && _root.CurrentWeaponIndexInternal >= 0 && _root.CurrentWeaponIndexInternal < _root.WeaponDataListRef.Count) {
                QueuePendingTpWeapon(_root.WeaponDataListRef[_root.CurrentWeaponIndexInternal]);
                _root.PendingHolsterHideSlot = _root.GetSlotForIndexInternal(_root.CurrentWeaponIndexInternal);
                _root.RefreshHolsterVisibility();
            }

            if(_root.PlayerAnimatorRef != null) {
                TriggerTpPullOutAnimation(_root.CurrentWeaponIndexInternal);
            }

            _root.IsPullingOutInternal = true;
            _root.RequiresKinemationEquipCompleteForCurrentPullOut = requiresKinemationEquipCompletion && !isPostMatch;
            if(isPostMatch) {
                ScheduleKinemationPullOutIfNeeded(
                    _root.CurrentWeaponIndexInternal,
                    Mathf.Max(_root.KinemationPullOutCompleteDelay, _root.PostMatchPullOutFailSafeDelay),
                    forceSchedule: true
                );
            } else {
                ScheduleKinemationPullOutIfNeeded(_root.CurrentWeaponIndexInternal);
            }
        }

        public void CancelPendingPullOutForPostMatch() {
            _root.IsPullingOutInternal = false;
            _root.RequiresKinemationEquipCompleteForCurrentPullOut = false;
            _root.PendingHolsterHideSlot = -1;
            if(_root.KinemationPullOutCompletionCoroutine != null) {
                _root.StopCoroutine(_root.KinemationPullOutCompletionCoroutine);
                _root.KinemationPullOutCompletionCoroutine = null;
            }

            if(_root.PlayerAnimatorRef != null) {
                _root.PlayerAnimatorRef.ResetTrigger(WeaponManager.PullOutHashInternal);
            }

            if(_root.CurrentWorldWeaponInstanceInternal == null) {
                _root.ResolveCurrentWorldWeaponRefInternal();
            }

            _root.PendingTpWeapon = null;
            if(_root.CurrentWorldWeaponInstanceInternal != null && !_root.CurrentWorldWeaponInstanceInternal.activeSelf) {
                _root.CurrentWorldWeaponInstanceInternal.SetActive(true);
            }

            _root.EnsureWeaponHierarchyActiveInternal();

            if(_root.PlayerControllerRef != null && _root.PlayerControllerRef.NetworkObject != null) {
                EventBus.Publish(new PlayerWorldWeaponPresentationRefreshRequestedEvent(
                    _root.PlayerControllerRef.NetworkObjectId, usePodiumShadowState: false));
            }

            _root.RefreshHolsterVisibility();
        }

        public void PrepareForPostMatchPresentation() {
            CancelPendingPullOutForPostMatch();
            ShowTpWeapon();
            HandlePullOutCompleted();
            SetTpWeaponIndexForPodium();
            _root.RefreshHolsterVisibility();

            if(_root.CurrentWorldWeaponInstanceInternal != null && !_root.CurrentWorldWeaponInstanceInternal.activeSelf) {
                _root.CurrentWorldWeaponInstanceInternal.SetActive(true);
            }

            if(_root.PlayerControllerRef != null && _root.PlayerControllerRef.NetworkObject != null) {
                EventBus.Publish(new PlayerWorldWeaponPresentationRefreshRequestedEvent(
                    _root.PlayerControllerRef.NetworkObjectId, usePodiumShadowState: true));
            }

            _root.EnsureWorldWeaponShadowStateInternal();
        }

        public void RestoreCurrentWeaponPresentationAfterHopballDrop() {
            if(_root.CurrentWeaponIndexInternal < 0 || _root.CurrentWeaponIndexInternal >= _root.FpWeaponInstancesRef.Count) {
                return;
            }

            var currentFp = _root.FpWeaponInstancesRef[_root.CurrentWeaponIndexInternal];
            if(currentFp != null) {
                WeaponManager.EnsureHierarchyActiveInternal(currentFp);
                currentFp.SetActive(true);
            }

            if(_root.CurrentWorldWeaponInstanceInternal != null) {
                _root.CurrentWorldWeaponInstanceInternal.SetActive(true);
            }

            _root.RefreshHolsterVisibility();
            _root.RefreshHolsterShadowState();
            _root.EnsureWorldWeaponShadowStateInternal();
        }

        #endregion

        #region Podium And Presentation Recovery

        public void SetTpWeaponIndexForPodium() {
            if(_root.PlayerAnimatorRef == null) return;
            var slot = Mathf.Clamp(_root.GetSlotForIndexInternal(_root.CurrentWeaponIndexInternal), 0, 1);
            _root.PlayerAnimatorRef.SetInteger(WeaponManager.WeaponIndexHashInternal, slot);
            var layerIndex = _root.PlayerAnimatorRef.GetLayerIndex("Weapon Hold Layer");
            if(layerIndex < 0) return;
            var stateName = slot == 0 ? "AKAim" : "PistolAim";
            _root.PlayerAnimatorRef.Play(stateName, layerIndex, 0f);
        }

        #endregion

        #region Internal Facade

        internal void EnsureWorldWeaponShadowStateInternal() => EnsureWorldWeaponShadowState();

        internal void EnsureWeaponHierarchyActiveInternal() => EnsureWeaponHierarchyActive();

        #endregion

        #region Private Switch Helpers

        private void HideCurrentWorldWeapon() {
            if(_root.CurrentWorldWeaponInstanceInternal == null) return;
            _root.CurrentWorldWeaponInstanceInternal.SetActive(false);
            _root.CurrentWorldWeaponInstanceInternal = null;
        }

        private void HideCurrentWeaponVisuals() {
            if(_root.CurrentWeaponIndexInternal >= 0 && _root.CurrentWeaponIndexInternal < _root.FpWeaponInstancesRef.Count) {
                var oldFp = _root.FpWeaponInstancesRef[_root.CurrentWeaponIndexInternal];
                if(oldFp != null) {
                    oldFp.SetActive(false);
                }
            }

            HideCurrentWorldWeapon();
        }

        private void QueuePendingTpWeapon(WeaponData data) {
            _root.PendingTpWeapon = _root.ResolveWorldWeaponObject(data);
            if(_root.PendingTpWeapon != null) {
                _root.PendingTpWeapon.SetActive(false);
            }

            _root.CurrentWorldWeaponInstanceInternal = null;
        }

        private void TriggerTpPullOutAnimation(int weaponIndex) {
            if(_root.PlayerAnimatorRef == null) return;
            var slot = Mathf.Clamp(_root.GetSlotForIndexInternal(weaponIndex), 0, 1);
            _root.PlayerAnimatorRef.SetInteger(WeaponManager.WeaponIndexHashInternal, slot);
            _root.PlayerAnimatorRef.SetTrigger(WeaponManager.PullOutHashInternal);
        }

        private void ReconcileStableTpWeaponState() {
            if(_root.DeferTpRevealUntilRespawn || _root.IsPullingOutInternal) return;
            if(_root.PlayerControllerRef == null) return;
            if(_root.PlayerControllerRef.NetIsDead is { Value: true }) return;
            if(_root.PlayerControllerRef.PlayerRagdoll != null && _root.PlayerControllerRef.PlayerRagdoll.IsRagdoll) return;
            if(_root.PlayerControllerRef.IsHoldingHopball) return;
            if(_root.CurrentWeaponIndexInternal < 0 || _root.CurrentWeaponIndexInternal >= _root.WeaponDataListRef.Count) return;

            var expectedWeapon = _root.ResolveWorldWeaponObject(_root.WeaponDataListRef[_root.CurrentWeaponIndexInternal]);
            if(expectedWeapon == null) return;

            var repairedPresentationState = _root.CurrentWorldWeaponInstanceInternal != expectedWeapon;
            if(_root.CurrentWorldWeaponInstanceInternal != null && _root.CurrentWorldWeaponInstanceInternal != expectedWeapon) {
                _root.CurrentWorldWeaponInstanceInternal.SetActive(false);
            }

            _root.CurrentWorldWeaponInstanceInternal = expectedWeapon;
            if(!_root.CurrentWorldWeaponInstanceInternal.activeSelf) {
                _root.CurrentWorldWeaponInstanceInternal.SetActive(true);
                repairedPresentationState = true;
            }

            if(_root.PendingTpWeapon != null || _root.PendingHolsterHideSlot != -1) {
                repairedPresentationState = true;
            }

            if(_root.CurrentWeaponInternal != null && _root.CurrentWeaponInternal.CurrentWeaponData != _root.WeaponDataListRef[_root.CurrentWeaponIndexInternal]) {
                repairedPresentationState = true;
            }

            _root.PendingTpWeapon = null;
            _root.PendingHolsterHideSlot = -1;

            if(repairedPresentationState) {
                SyncWeaponPresentationToWorldWeapon(_root.CurrentWorldWeaponInstanceInternal);
            }

            _root.EnsureWeaponHierarchyActiveInternal();
            _root.EnsureWorldWeaponShadowStateInternal();
            _root.RefreshHolsterVisibility();
            _root.RefreshHolsterShadowState();
        }

        #endregion

        #region Private Pull-Out Timing

        private void ScheduleKinemationPullOutIfNeeded(int weaponIndex, float? delayOverride = null,
            bool forceSchedule = false) {
            if(!forceSchedule && !_root.AutoCompleteKinemationPullOut) return;
            if(_root.RequiresKinemationEquipCompleteForCurrentPullOut) return;
            if(weaponIndex < 0 || weaponIndex >= _root.FpWeaponInstancesRef.Count) return;

            var fpWeaponRoot = _root.FpWeaponInstancesRef[weaponIndex];
            if(!forceSchedule && !WeaponManager.TryGetKinemationDriverInternal(fpWeaponRoot, out _)) return;

            if(_root.KinemationPullOutCompletionCoroutine != null) {
                _root.StopCoroutine(_root.KinemationPullOutCompletionCoroutine);
            }

            var delay = delayOverride ?? Mathf.Max(0f, _root.KinemationPullOutCompleteDelay);
            _root.KinemationPullOutCompletionCoroutine =
                _root.StartCoroutine(KinemationPullOutCompletionRoutine(delay));
        }

        private IEnumerator KinemationPullOutCompletionRoutine(float delay) {
            if(delay > 0f) {
                yield return new WaitForSeconds(delay);
            } else {
                yield return null;
            }

            _root.KinemationPullOutCompletionCoroutine = null;
            HandlePullOutCompleted();
        }

        #endregion

        #region Private Validation And Authority

        private bool TryConsumeWeaponSwitchQuota() {
            var config = AntiCheatConfig.Instance;
            if(config == null) return true;
            if(RpcRateLimiter.TryConsume(_root.OwnerClientId, RpcRateLimiter.Keys.WeaponSwitch, config.weaponSwitchLimit,
                    config.rpcWindowSeconds)) {
                return true;
            }

            AntiCheatLogger.LogRateLimit(_root.OwnerClientId, RpcRateLimiter.Keys.WeaponSwitch);
            return false;
        }

        private int GetServerAuthoritativeWeaponIndex() {
            return _root.ServerAuthoritativeWeaponIndex >= 0 ? _root.ServerAuthoritativeWeaponIndex : _root.CurrentWeaponIndexInternal;
        }

        #endregion

        #region Private World Weapon Presentation

        private void EnsureWorldWeaponShadowState() {
            if(_root.CurrentWorldWeaponInstanceInternal == null) return;

            if(!_root.CurrentWorldWeaponInstanceInternal.activeSelf) {
                _root.CurrentWorldWeaponInstanceInternal.SetActive(true);
            }

            var isOwner = _root.PlayerControllerRef != null && _root.PlayerControllerRef.IsOwner;
            var isPostMatch = _root.IsPostMatchFlowActive;
            var targetMode = isOwner && !isPostMatch ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;

            var playerShadow = _root.PlayerControllerRef != null ? _root.PlayerControllerRef.PlayerShadow : null;
            if(playerShadow != null) {
                playerShadow.SetWorldWeaponShadowMode(targetMode);
                return;
            }

            var renderers = _root.CurrentWorldWeaponInstanceInternal.GetComponentsInChildren<MeshRenderer>(true);
            foreach(var meshRenderer in renderers) {
                if(meshRenderer == null) continue;
                meshRenderer.enabled = true;
                meshRenderer.shadowCastingMode = targetMode;
            }
        }

        private void EnsureWeaponHierarchyActive() {
            if(_root.CurrentWorldWeaponInstanceInternal == null) return;
            EnsureHierarchyActive(_root.CurrentWorldWeaponInstanceInternal);
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

        #endregion

        #region Private Presentation Repair

        private void SyncWeaponPresentationToWorldWeapon(GameObject worldWeaponInstance) {
            if(_root.CurrentWeaponInternal == null) return;
            if(_root.CurrentWeaponIndexInternal < 0 || _root.CurrentWeaponIndexInternal >= _root.WeaponDataListRef.Count) return;

            var data = _root.WeaponDataListRef[_root.CurrentWeaponIndexInternal];
            if(data == null) return;

            GameObject fpWeapon = null;
            if(_root.CurrentWeaponIndexInternal >= 0 && _root.CurrentWeaponIndexInternal < _root.FpWeaponInstancesRef.Count) {
                fpWeapon = _root.FpWeaponInstancesRef[_root.CurrentWeaponIndexInternal];
            }

            var magCapacity = _root.ResolveWeaponCapacity(data);
            if(magCapacity <= 0) return;

            var restoredAmmo = Mathf.Clamp(_root.CurrentWeaponInternal.currentAmmo, 0, magCapacity);
            if(restoredAmmo == 0 && _root.CurrentWeaponInternal.CurrentWeaponData != data) {
                restoredAmmo = _root.ResolveRestoredAmmo(_root.CurrentWeaponIndexInternal, magCapacity, false);
            }

            _root.CurrentWeaponInternal.SwitchToWeapon(data, fpWeapon, worldWeaponInstance, restoredAmmo, magCapacity);
        }

        #endregion
    }
}

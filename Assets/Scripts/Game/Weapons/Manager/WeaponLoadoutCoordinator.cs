using System.Collections.Generic;
using Game.Weapons.World;
using UnityEngine;

namespace Game.Weapons.Manager {
    internal sealed class WeaponLoadoutCoordinator {
        private readonly WeaponManager _root;

        public WeaponLoadoutCoordinator(WeaponManager root) {
            _root = root;
        }

        public int GetPrimarySelectionIndex() {
            if(_root.KinemationCatalogRef.IsEmpty) {
                _root.BuildKinemationWeaponLookup();
            }
            if(_root.PlayerControllerRef == null || _root.KinemationCatalogRef.PrimaryWeaponOptions.Count == 0) {
                return 0;
            }

            return Mathf.Clamp(_root.PlayerControllerRef.primaryWeaponIndex.Value, 0,
                _root.KinemationCatalogRef.PrimaryWeaponOptions.Count - 1);
        }

        public int GetSecondarySelectionIndex() {
            if(_root.KinemationCatalogRef.IsEmpty) {
                _root.BuildKinemationWeaponLookup();
            }
            if(_root.PlayerControllerRef == null || _root.KinemationCatalogRef.SecondaryWeaponOptions.Count == 0) {
                return 0;
            }

            return Mathf.Clamp(_root.PlayerControllerRef.secondaryWeaponIndex.Value, 0,
                _root.KinemationCatalogRef.SecondaryWeaponOptions.Count - 1);
        }

        public bool ApplyOwnerLoadoutSelection(int primaryIndex, int secondaryIndex, bool deferTpRevealUntilRespawn = true) {
            if(!_root.IsOwner || _root.PlayerControllerRef == null) return false;
            if(_root.KinemationCatalogRef.IsEmpty) {
                _root.BuildKinemationWeaponLookup();
            }

            if(!IsValidSelectionIndex(_root.KinemationCatalogRef.PrimaryWeaponOptions, primaryIndex, "Primary")) return false;
            if(!IsValidSelectionIndex(_root.KinemationCatalogRef.SecondaryWeaponOptions, secondaryIndex, "Secondary")) return false;

            var primaryChanged = _root.PlayerControllerRef.primaryWeaponIndex.Value != primaryIndex;
            var secondaryChanged = _root.PlayerControllerRef.secondaryWeaponIndex.Value != secondaryIndex;
            if(!primaryChanged && !secondaryChanged) {
                return false;
            }

            _root.SuppressLoadoutRebuildCallbacks = true;
            try {
                if(primaryChanged) {
                    _root.PlayerControllerRef.primaryWeaponIndex.Value = primaryIndex;
                }

                if(secondaryChanged) {
                    _root.PlayerControllerRef.secondaryWeaponIndex.Value = secondaryIndex;
                }
            } finally {
                _root.SuppressLoadoutRebuildCallbacks = false;
            }

            RebuildEquippedWeapons(false, deferTpRevealUntilRespawn);
            return true;
        }

        public int GetCurrentHolsterSlot() => GetSlotForIndex(_root.CurrentWeaponIndexInternal);
        public void RefreshHolsterVisibility() => UpdateHolsterVisibility();

        public void InitializeWeapons() {
            if(_root.CurrentWeaponInternal == null) {
                Debug.LogError("[WeaponManager] Weapon component not assigned!");
                return;
            }

            if(_root.PlayerControllerRef != null) {
                _root.PlayerControllerRef.primaryWeaponIndex.OnValueChanged -= _root.OnWeaponIndexChangedInternal;
                _root.PlayerControllerRef.primaryWeaponIndex.OnValueChanged += _root.OnWeaponIndexChangedInternal;
                _root.PlayerControllerRef.secondaryWeaponIndex.OnValueChanged -= _root.OnWeaponIndexChangedInternal;
                _root.PlayerControllerRef.secondaryWeaponIndex.OnValueChanged += _root.OnWeaponIndexChangedInternal;
            }

            BuildEquippedWeaponList();
            if(!_root.BuildWorldWeaponLookup()) return;
            _root.LogStrictStartupValidationOnce();
            if(!ValidateStrictEquippedWeaponConfiguration()) return;
            SetupHolsteredWeaponModels();
            DisableUnequippedWorldWeapons();

            if(_root.WeaponDataListRef == null || _root.WeaponDataListRef.Count == 0) {
                Debug.LogError("[WeaponManager] weaponDataList is empty!");
                return;
            }

            HideAllWorldWeapons();
            _root.InstantiateFpWeaponInstancesInternal();

            if(_root.FpWeaponInstancesRef.Count != _root.WeaponDataListRef.Count) {
                Debug.LogError(
                    $"[WeaponManager][KIN-Strict] FP instance count mismatch. expected={_root.WeaponDataListRef.Count} actual={_root.FpWeaponInstancesRef.Count}");
            }

            if(_root.FpWeaponInstancesRef.Count > 0) {
                _root.EquipInitialWeaponInternal(ResolveInitialEquippedWeaponIndex());
                _root.WeaponsInitialized = true;
            } else {
                Debug.LogError("[WeaponManager] No weapons instantiated!");
            }

            UpdateHolsterVisibility();

            if(_root.IsOwner) {
                _root.RefreshOwnerAmmoHudFromCurrentWeapon();
            }

            _root.EnsureFpWeaponLightingRigInternal();
        }

        public void ApplyTpWeaponStateOnRespawn() {
            if(_root.PlayerAnimatorRef != null) {
                var slot = Mathf.Clamp(GetSlotForIndex(_root.CurrentWeaponIndexInternal), 0, 1);
                _root.PlayerAnimatorRef.SetInteger(_root.WeaponIndexHashInternal, slot);
                _root.PlayerAnimatorRef.Rebind();
                _root.PlayerAnimatorRef.Update(0f);
            }

            if(_root.DeferredRespawnWorldWeapon != null) {
                if(_root.CurrentWorldWeaponInstanceInternal != null &&
                   _root.CurrentWorldWeaponInstanceInternal != _root.DeferredRespawnWorldWeapon) {
                    _root.CurrentWorldWeaponInstanceInternal.SetActive(false);
                }

                _root.CurrentWorldWeaponInstanceInternal = _root.DeferredRespawnWorldWeapon;
                _root.DeferredRespawnWorldWeapon = null;
            }

            ResolveCurrentWorldWeaponReference();
            if(_root.CurrentWorldWeaponInstanceInternal != null && !_root.CurrentWorldWeaponInstanceInternal.activeSelf) {
                _root.CurrentWorldWeaponInstanceInternal.SetActive(true);
            }

            if(_root.CurrentWorldWeaponInstanceInternal != null) {
                _root.EnsureWeaponHierarchyActiveInternal();
                _root.EnsureWorldWeaponShadowStateInternal();

                if(_root.IsOwner && _root.PlayerRendererRef != null) {
                    _root.PlayerRendererRef.SetWorldWeaponRenderersEnabled(true);
                }
            }

            if(_root.IsOwner) {
                var currentFpWeapon = _root.GetCurrentFpWeapon();
                if(currentFpWeapon != null) {
                    if(_root.CurrentWeaponIndexInternal >= 0 && _root.CurrentWeaponIndexInternal < _root.WeaponDataListRef.Count &&
                       _root.TryGetKinemationDriverInternal(currentFpWeapon, out _)) {
                        var data = _root.WeaponDataListRef[_root.CurrentWeaponIndexInternal];
                        _root.TryGetKinemationBindingForData(data, out var kinemationBinding);
                        _root.ApplyResolvedKinemationViewmodelPoseInternal(currentFpWeapon, kinemationBinding);
                    }

                    _root.EnsureHierarchyActiveInternal(currentFpWeapon);
                    currentFpWeapon.SetActive(true);

                    _root.SetupFpWeaponSkinnedMeshRenderersInternal(currentFpWeapon);
                    if(_root.PlayerRendererRef != null) {
                        _root.PlayerRendererRef.SetFpWeaponRenderersEnabled(true, currentFpWeapon);
                        _root.PlayerRendererRef.SetFpWeaponSkinnedRenderersEnabled(true, currentFpWeapon);
                    }
                }
            }

            _root.DeferTpRevealUntilRespawn = false;
            UpdateHolsterVisibility();
        }

        public void OnWeaponIndexChanged(int oldValue, int newValue) {
            if(_root.SuppressLoadoutRebuildCallbacks) return;

            var shouldDeferTpReveal = _root.PlayerControllerRef != null &&
                                      (_root.PlayerControllerRef.NetIsDead is { Value: true } ||
                                       (_root.PlayerControllerRef.PlayerRagdoll != null &&
                                        _root.PlayerControllerRef.PlayerRagdoll.IsRagdoll));

            RebuildEquippedWeapons(!shouldDeferTpReveal, shouldDeferTpReveal);
        }

        internal int GetSlotForIndexInternal(int index) => GetSlotForIndex(index);
        internal void ResolveCurrentWorldWeaponReferenceInternal() => ResolveCurrentWorldWeaponReference();

        private static bool IsValidSelectionIndex(IReadOnlyList<WeaponData> options, int requestedIndex, string slotLabel) {
            if(options == null || options.Count == 0) {
                Debug.LogError($"[WeaponManager] Cannot apply {slotLabel} selection. No options configured.");
                return false;
            }

            if(requestedIndex >= 0 && requestedIndex < options.Count) return true;

            Debug.LogError(
                $"[WeaponManager] Rejecting {slotLabel} selection index {requestedIndex}. Valid range is [0..{options.Count - 1}].");
            return false;
        }

        private void SetupHolsteredWeaponModels() {
            _root.PrimaryHolsterInternal = _root.ResolveHolsterWeaponObject(GetWeaponDataForSlot(0));
            _root.SecondaryHolsterInternal = _root.ResolveHolsterWeaponObject(GetWeaponDataForSlot(1));

            DisableUnequippedHolsterModels();

            if(_root.PrimaryHolsterInternal == null) {
                Debug.LogError("[WeaponManager] Missing Primary holster world weapon binding.");
            }

            if(_root.SecondaryHolsterInternal == null) {
                Debug.LogError("[WeaponManager] Missing Secondary holster world weapon binding.");
            }

            DisableHolster(_root.PrimaryHolsterInternal);
            DisableHolster(_root.SecondaryHolsterInternal);
        }

        private void DisableUnequippedHolsterModels() {
            if(_root.WorldWeaponSocketRef == null || _root.WorldWeaponSocketRef.root == null) return;

            var equippedHolsters = new HashSet<GameObject>();
            if(_root.PrimaryHolsterInternal != null) equippedHolsters.Add(_root.PrimaryHolsterInternal);
            if(_root.SecondaryHolsterInternal != null) equippedHolsters.Add(_root.SecondaryHolsterInternal);

            var bindings = _root.WorldWeaponSocketRef.root.GetComponentsInChildren<WorldWeaponBinding>(true);
            foreach(var binding in bindings) {
                if(binding == null || binding.WeaponData == null) continue;
                if(binding.transform.IsChildOf(_root.WorldWeaponSocketRef)) continue;

                var holsterObject = binding.gameObject;
                if(holsterObject == null) continue;

                if(equippedHolsters.Contains(holsterObject)) continue;
                if(holsterObject.activeSelf) {
                    holsterObject.SetActive(false);
                }
            }
        }

        private WeaponData GetWeaponDataForSlot(int slot) {
            if(_root.WeaponDataListRef == null || _root.WeaponDataListRef.Count == 0) return null;
            foreach(var data in _root.WeaponDataListRef) {
                if(data == null) continue;
                var weaponSlot = ResolveWeaponSlot(data);
                if(weaponSlot == slot) {
                    return data;
                }
            }

            return null;
        }

        private static int ResolveWeaponSlot(WeaponData data) {
            if(data == null) return -1;
            var slot = data.WeaponSlotIndex;
            return slot is 0 or 1 ? slot : -1;
        }

        private static void DisableHolster(GameObject holster) {
            if(holster == null) return;
            if(holster.activeSelf) {
                holster.SetActive(false);
            }
        }

        private void UpdateHolsterVisibility() {
            var currentSlot = GetSlotForIndex(_root.CurrentWeaponIndexInternal);

            if(_root.PrimaryHolsterInternal != null) {
                var showPrimary = currentSlot != 0 || _root.PendingHolsterHideSlot == 0;
                if(_root.PrimaryHolsterInternal.activeSelf != showPrimary) {
                    _root.PrimaryHolsterInternal.SetActive(showPrimary);
                }
            }

            if(_root.SecondaryHolsterInternal == null) return;
            var showSecondary = currentSlot != 1 || _root.PendingHolsterHideSlot == 1;
            if(_root.SecondaryHolsterInternal.activeSelf != showSecondary) {
                _root.SecondaryHolsterInternal.SetActive(showSecondary);
            }
        }

        private int GetSlotForIndex(int index) {
            var data = _root.GetWeaponDataByIndex(index);
            if(data == null) return -1;
            return ResolveWeaponSlot(data);
        }

        private void RebuildEquippedWeapons(bool preserveCurrentSlot, bool deferTpRevealUntilRespawn) {
            if(_root.CurrentWeaponInternal == null || _root.FpCameraRef == null) {
                _root.ValidateComponentsForPublicUse();
            }

            if(_root.CurrentWeaponInternal == null || _root.FpCameraRef == null) {
                return;
            }

            var targetSlot = preserveCurrentSlot
                ? Mathf.Clamp(GetSlotForIndex(_root.CurrentWeaponIndexInternal), 0, 1)
                : 0;
            var previousWorldWeapon = _root.CurrentWorldWeaponInstanceInternal;
            var keepPreviousWorldWeaponVisible =
                deferTpRevealUntilRespawn && previousWorldWeapon != null && previousWorldWeapon.activeSelf;

            BuildEquippedWeaponList();
            if(!_root.BuildWorldWeaponLookup()) return;
            if(!ValidateStrictEquippedWeaponConfiguration()) return;
            SetupHolsteredWeaponModels();
            DisableUnequippedWorldWeapons();

            if(_root.WeaponDataListRef == null || _root.WeaponDataListRef.Count == 0) {
                Debug.LogError("[WeaponManager] weaponDataList is empty after rebuild!");
                return;
            }

            _root.DestroyFpWeaponInstancesInternal();
            HideAllWorldWeapons(keepPreviousWorldWeaponVisible ? previousWorldWeapon : null);
            _root.InstantiateFpWeaponInstancesInternal();

            if(_root.FpWeaponInstancesRef.Count == 0) {
                Debug.LogError("[WeaponManager] No FP weapons available after rebuild!");
                return;
            }

            var targetIndex = ResolveIndexForSlot(targetSlot);
            _root.EquipInitialWeaponInternal(targetIndex);

            _root.DeferTpRevealUntilRespawn = deferTpRevealUntilRespawn;
            if(_root.DeferTpRevealUntilRespawn) {
                var nextWorldWeapon = _root.CurrentWorldWeaponInstanceInternal;
                if(nextWorldWeapon != null) {
                    nextWorldWeapon.SetActive(false);
                    _root.DeferredRespawnWorldWeapon = nextWorldWeapon != previousWorldWeapon || !keepPreviousWorldWeaponVisible
                        ? nextWorldWeapon
                        : null;
                } else {
                    _root.DeferredRespawnWorldWeapon = null;
                }

                if(keepPreviousWorldWeaponVisible && previousWorldWeapon != null) {
                    previousWorldWeapon.SetActive(true);
                    _root.CurrentWorldWeaponInstanceInternal = previousWorldWeapon;
                } else {
                    _root.CurrentWorldWeaponInstanceInternal = null;
                }
            } else {
                _root.DeferredRespawnWorldWeapon = null;
                ResolveCurrentWorldWeaponReference();
                if(_root.CurrentWorldWeaponInstanceInternal != null && !_root.CurrentWorldWeaponInstanceInternal.activeSelf) {
                    _root.CurrentWorldWeaponInstanceInternal.SetActive(true);
                }

                if(_root.CurrentWorldWeaponInstanceInternal != null) {
                    _root.EnsureWorldWeaponShadowStateInternal();
                }
            }

            UpdateHolsterVisibility();
        }

        private void HideAllWorldWeapons(GameObject keepVisible = null) {
            if(_root.WorldWeaponSocketRef == null) return;

            foreach(Transform child in _root.WorldWeaponSocketRef) {
                if(child == null) continue;
                if(keepVisible != null && child.gameObject == keepVisible) continue;
                child.gameObject.SetActive(false);
            }

            _root.CurrentWorldWeaponInstanceInternal = keepVisible;
            _root.PendingTpWeapon = null;
        }

        private int ResolveIndexForSlot(int slot) {
            if(_root.WeaponDataListRef == null || _root.WeaponDataListRef.Count == 0) return 0;

            for(var i = 0; i < _root.WeaponDataListRef.Count; i++) {
                var data = _root.WeaponDataListRef[i];
                if(data == null) continue;
                if(ResolveWeaponSlot(data) == slot) {
                    return i;
                }
            }

            return Mathf.Clamp(slot, 0, _root.WeaponDataListRef.Count - 1);
        }

        private void ResolveCurrentWorldWeaponReference() {
            if(_root.WorldWeaponSocketRef == null) return;
            if(_root.CurrentWeaponIndexInternal < 0 || _root.CurrentWeaponIndexInternal >= _root.WeaponDataListRef.Count) return;

            var data = _root.WeaponDataListRef[_root.CurrentWeaponIndexInternal];
            if(data == null) return;

            var worldObj = _root.ResolveWorldWeaponObject(data);
            if(worldObj != null) {
                _root.CurrentWorldWeaponInstanceInternal = worldObj;
            }
        }

        private void DisableUnequippedWorldWeapons() {
            if(_root.WorldWeaponSocketRef == null) return;

            var equippedWorldWeapons = new HashSet<GameObject>();
            if(_root.WeaponDataListRef != null) {
                foreach(var weaponData in _root.WeaponDataListRef) {
                    if(weaponData == null) continue;
                    var worldWeapon = _root.ResolveWorldWeaponObject(weaponData);
                    if(worldWeapon != null) {
                        equippedWorldWeapons.Add(worldWeapon);
                    }
                }
            }

            foreach(Transform child in _root.WorldWeaponSocketRef) {
                if(child == null) continue;

                var isEquipped = equippedWorldWeapons.Contains(child.gameObject);
                var isCurrentWeapon = _root.CurrentWorldWeaponInstanceInternal != null &&
                                      _root.CurrentWorldWeaponInstanceInternal == child.gameObject;

                if(!isEquipped && !isCurrentWeapon) {
                    child.gameObject.SetActive(false);
                }
            }
        }

        private bool ValidateStrictEquippedWeaponConfiguration() {
            if(_root.KinemationFpsPlayerPrefabRef == null) {
                Debug.LogError("[WeaponManager] Missing KINEMATION FPS player prefab.");
                return false;
            }

            if(_root.WeaponCameraRef == null) {
                Debug.LogError("[WeaponManager] Missing WeaponCamera. Strict mode requires WeaponCamera for FP viewmodels.");
                return false;
            }

            if(_root.WorldWeaponSocketRef == null) {
                Debug.LogError("[WeaponManager] Missing WorldWeaponSocket. Strict mode requires explicit WorldWeaponBinding objects.");
                return false;
            }

            if(_root.WeaponDataListRef == null || _root.WeaponDataListRef.Count == 0) {
                return false;
            }

            var isValid = true;
            foreach(var data in _root.WeaponDataListRef) {
                if(data == null) {
                    Debug.LogError("[WeaponManager] Equipped weapon data is null.");
                    isValid = false;
                    continue;
                }

                if(ResolveWeaponSlot(data) < 0) {
                    Debug.LogError($"[WeaponManager] Weapon '{data.weaponName}' has invalid slot assignment.");
                    isValid = false;
                }

                if(!_root.TryGetKinemationBindingForData(data, out var binding) || binding == null ||
                   binding.kinemationWeaponPrefab == null) {
                    Debug.LogError($"[WeaponManager] Weapon '{data.weaponName}' is missing a KINEMATION binding/prefab.");
                    isValid = false;
                    continue;
                }

                if(WeaponManager.ResolveKinemationWeaponCapacity(binding.kinemationWeaponPrefab) <= 0) {
                    Debug.LogError(
                        $"[WeaponManager] Weapon '{data.weaponName}' has invalid KINEMATION ammo capacity. " +
                        "Set FPSWeaponSettings.ammo > 0.");
                    isValid = false;
                }

                if(_root.ResolveWorldWeaponObject(data) == null) {
                    Debug.LogError(
                        $"[WeaponManager] Weapon '{data.weaponName}' missing WorldWeaponBinding under WorldWeaponSocket.");
                    isValid = false;
                } else {
                    var worldWeapon = _root.ResolveWorldWeaponObject(data);
                    var worldBinding = worldWeapon != null ? worldWeapon.GetComponent<WorldWeaponBinding>() : null;
                    if(worldBinding == null) {
                        Debug.LogError(
                            $"[WeaponManager] Weapon '{data.weaponName}' world weapon '{worldWeapon.name}' is missing WorldWeaponBinding component.");
                        isValid = false;
                    } else if(!worldBinding.TryGetRuntimeReferences(out _, out _)) {
                        Debug.LogError(
                            $"[WeaponManager] Weapon '{data.weaponName}' world weapon '{worldWeapon.name}' is missing assigned muzzle reference.");
                        isValid = false;
                    }
                }

                if(_root.ResolveHolsterWeaponObject(data) != null) continue;
                Debug.LogError(
                    $"[WeaponManager] Weapon '{data.weaponName}' missing holster WorldWeaponBinding outside WorldWeaponSocket.");
                isValid = false;
            }

            return isValid;
        }

        private void BuildEquippedWeaponList() {
            _root.BuildKinemationWeaponLookup();
            _root.WeaponDataListRef.Clear();

            if(_root.PlayerControllerRef == null) {
                Debug.LogError("[WeaponManager] Missing PlayerController while building equipped weapon list.");
                return;
            }

            var primaryIndex = _root.PlayerControllerRef.primaryWeaponIndex.Value;
            var secondaryIndex = _root.PlayerControllerRef.secondaryWeaponIndex.Value;

            var primary = GetWeaponFromOptions(_root.KinemationCatalogRef.PrimaryWeaponOptions, primaryIndex, "Primary");
            if(primary != null) {
                _root.WeaponDataListRef.Add(primary);
            }

            var secondary = GetWeaponFromOptions(_root.KinemationCatalogRef.SecondaryWeaponOptions, secondaryIndex, "Secondary");
            if(secondary != null) {
                _root.WeaponDataListRef.Add(secondary);
            }
        }

        private static WeaponData GetWeaponFromOptions(IReadOnlyList<WeaponData> options, int storedIndex, string slotLabel) {
            if(options == null || options.Count == 0) {
                Debug.LogError($"[WeaponManager] No {slotLabel} weapon options assigned.");
                return null;
            }

            if(storedIndex < 0 || storedIndex >= options.Count) {
                Debug.LogError($"[WeaponManager] {slotLabel} weapon index {storedIndex} out of range [0..{options.Count - 1}].");
                return null;
            }

            var weaponData = options[storedIndex];
            if(weaponData == null) {
                Debug.LogError($"[WeaponManager] {slotLabel} weapon at index {storedIndex} is null.");
            }

            return weaponData;
        }

        private int ResolveInitialEquippedWeaponIndex() {
            var replicatedIndex = _root.ReplicatedEquippedWeaponIndex.Value;
            if(replicatedIndex >= 0 && replicatedIndex < _root.WeaponDataListRef.Count) {
                return replicatedIndex;
            }

            return 0;
        }
    }
}

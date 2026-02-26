using System.Collections.Generic;
using UnityEngine;

namespace Game.Weapons {
    public partial class WeaponManager {
        public int GetPrimarySelectionIndex() {
            if(_kinemationCatalog.IsEmpty) {
                BuildKinemationWeaponLookup();
            }
            if(playerController == null || _kinemationCatalog.PrimaryWeaponOptions.Count == 0) {
                return 0;
            }

            return Mathf.Clamp(playerController.primaryWeaponIndex.Value, 0, _kinemationCatalog.PrimaryWeaponOptions.Count - 1);
        }

        public int GetSecondarySelectionIndex() {
            if(_kinemationCatalog.IsEmpty) {
                BuildKinemationWeaponLookup();
            }
            if(playerController == null || _kinemationCatalog.SecondaryWeaponOptions.Count == 0) {
                return 0;
            }

            return Mathf.Clamp(playerController.secondaryWeaponIndex.Value, 0, _kinemationCatalog.SecondaryWeaponOptions.Count - 1);
        }

        public bool ApplyOwnerLoadoutSelection(int primaryIndex, int secondaryIndex,
            bool deferTpRevealUntilRespawn = true) {
            if(!IsOwner || playerController == null) return false;
            if(_kinemationCatalog.IsEmpty) {
                BuildKinemationWeaponLookup();
            }

            if(!IsValidSelectionIndex(_kinemationCatalog.PrimaryWeaponOptions, primaryIndex, "Primary")) return false;
            if(!IsValidSelectionIndex(_kinemationCatalog.SecondaryWeaponOptions, secondaryIndex, "Secondary")) return false;

            var primaryChanged = playerController.primaryWeaponIndex.Value != primaryIndex;
            var secondaryChanged = playerController.secondaryWeaponIndex.Value != secondaryIndex;
            if(!primaryChanged && !secondaryChanged) {
                return false;
            }

            _suppressLoadoutRebuildCallbacks = true;
            try {
                if(primaryChanged) {
                    playerController.primaryWeaponIndex.Value = primaryIndex;
                }

                if(secondaryChanged) {
                    playerController.secondaryWeaponIndex.Value = secondaryIndex;
                }
            } finally {
                _suppressLoadoutRebuildCallbacks = false;
            }

            RebuildEquippedWeapons(
                preserveCurrentSlot: false,
                deferTpRevealUntilRespawn: deferTpRevealUntilRespawn
            );
            return true;
        }

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

        #region Holstered Weapons

        private void SetupHolsteredWeaponModels() {
            PrimaryHolster = ResolveWorldWeaponObject(GetWeaponDataForSlot(0));
            SecondaryHolster = ResolveWorldWeaponObject(GetWeaponDataForSlot(1));

            if(PrimaryHolster == null) {
                Debug.LogError("[WeaponManager] Missing Primary holster world weapon binding.");
            }

            if(SecondaryHolster == null) {
                Debug.LogError("[WeaponManager] Missing Secondary holster world weapon binding.");
            }

            DisableHolster(PrimaryHolster);
            DisableHolster(SecondaryHolster);
        }

        private WeaponData GetWeaponDataForSlot(int slot) {
            if(weaponDataList == null || weaponDataList.Count == 0) return null;
            for(var i = 0; i < weaponDataList.Count; i++) {
                var data = weaponDataList[i];
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
            var currentSlot = GetSlotForIndex(CurrentWeaponIndex);

            if(PrimaryHolster != null) {
                var showPrimary = currentSlot != 0 || _pendingHolsterHideSlot == 0;
                if(PrimaryHolster.activeSelf != showPrimary) {
                    PrimaryHolster.SetActive(showPrimary);
                }
            }

            if(SecondaryHolster == null) return;
            var showSecondary = currentSlot != 1 || _pendingHolsterHideSlot == 1;
            if(SecondaryHolster.activeSelf != showSecondary) {
                SecondaryHolster.SetActive(showSecondary);
            }
        }

        #endregion

        private int GetSlotForIndex(int index) {
            var data = GetWeaponDataByIndex(index);
            if(data == null) return -1;
            return ResolveWeaponSlot(data);
        }

        public int GetCurrentHolsterSlot() => GetSlotForIndex(CurrentWeaponIndex);
        public void RefreshHolsterVisibility() => UpdateHolsterVisibility();

        /// <summary>
        /// Rebuilds equipped FP/TP weapons from current loadout indices.
        /// </summary>
        private void RebuildEquippedWeapons(bool preserveCurrentSlot, bool deferTpRevealUntilRespawn) {
            if(CurrentWeapon == null || _fpCamera == null) {
                ValidateComponents();
            }

            if(CurrentWeapon == null || _fpCamera == null) {
                return;
            }

            var targetSlot = preserveCurrentSlot
                ? Mathf.Clamp(GetSlotForIndex(CurrentWeaponIndex), 0, 1)
                : 0;
            var previousWorldWeapon = CurrentWorldWeaponInstance;
            var keepPreviousWorldWeaponVisible =
                deferTpRevealUntilRespawn && previousWorldWeapon != null && previousWorldWeapon.activeSelf;

            BuildEquippedWeaponList();
            if(!BuildWorldWeaponLookup()) return;
            if(!ValidateStrictEquippedWeaponConfiguration()) return;
            SetupHolsteredWeaponModels();
            DisableUnequippedWorldWeapons();

            if(weaponDataList == null || weaponDataList.Count == 0) {
                Debug.LogError("[WeaponManager] weaponDataList is empty after rebuild!");
                return;
            }

            DestroyFpWeaponInstances();
            HideAllWorldWeapons(keepPreviousWorldWeaponVisible ? previousWorldWeapon : null);
            InstantiateFpWeaponInstances();

            if(_fpWeaponInstances.Count == 0) {
                Debug.LogError("[WeaponManager] No FP weapons available after rebuild!");
                return;
            }

            var targetIndex = ResolveIndexForSlot(targetSlot);
            EquipInitialWeapon(targetIndex);

            _deferTpRevealUntilRespawn = deferTpRevealUntilRespawn;
            if(_deferTpRevealUntilRespawn) {
                var nextWorldWeapon = CurrentWorldWeaponInstance;
                if(nextWorldWeapon != null) {
                    nextWorldWeapon.SetActive(false);
                    _deferredRespawnWorldWeapon = nextWorldWeapon != previousWorldWeapon || !keepPreviousWorldWeaponVisible
                        ? nextWorldWeapon
                        : null;
                } else {
                    _deferredRespawnWorldWeapon = null;
                }

                if(keepPreviousWorldWeaponVisible && previousWorldWeapon != null) {
                    previousWorldWeapon.SetActive(true);
                    CurrentWorldWeaponInstance = previousWorldWeapon;
                } else {
                    CurrentWorldWeaponInstance = null;
                }
            } else {
                _deferredRespawnWorldWeapon = null;
                ResolveCurrentWorldWeaponReference();
                if(CurrentWorldWeaponInstance != null && !CurrentWorldWeaponInstance.activeSelf) {
                    CurrentWorldWeaponInstance.SetActive(true);
                }

                if(CurrentWorldWeaponInstance != null) {
                    EnsureWorldWeaponShadowState();
                }
            }

            UpdateHolsterVisibility();
        }

        private void HideAllWorldWeapons(GameObject keepVisible = null) {
            if(_worldWeaponSocket == null) return;

            foreach(Transform child in _worldWeaponSocket) {
                if(child == null) continue;
                if(keepVisible != null && child.gameObject == keepVisible) continue;
                child.gameObject.SetActive(false);
            }

            CurrentWorldWeaponInstance = keepVisible;
            _pendingTpWeapon = null;
        }

        private int ResolveIndexForSlot(int slot) {
            if(weaponDataList == null || weaponDataList.Count == 0) return 0;

            for(var i = 0; i < weaponDataList.Count; i++) {
                var data = weaponDataList[i];
                if(data == null) continue;
                if(ResolveWeaponSlot(data) == slot) {
                    return i;
                }
            }

            return Mathf.Clamp(slot, 0, weaponDataList.Count - 1);
        }

        private void ResolveCurrentWorldWeaponReference() {
            if(_worldWeaponSocket == null) return;
            if(CurrentWeaponIndex < 0 || CurrentWeaponIndex >= weaponDataList.Count) return;

            var data = weaponDataList[CurrentWeaponIndex];
            if(data == null) return;

            var worldObj = ResolveWorldWeaponObject(data);
            if(worldObj != null) {
                CurrentWorldWeaponInstance = worldObj;
            }
        }

        /// <summary>
        /// Called when weapon index NetworkVariables change.
        /// </summary>
        private void OnWeaponIndexChanged(int oldValue, int newValue) {
            if(_suppressLoadoutRebuildCallbacks) return;

            var shouldDeferTpReveal = playerController != null &&
                                      (playerController.NetIsDead is { Value: true } ||
                                       (playerController.PlayerRagdoll != null &&
                                        playerController.PlayerRagdoll.IsRagdoll));

            RebuildEquippedWeapons(
                preserveCurrentSlot: !shouldDeferTpReveal,
                deferTpRevealUntilRespawn: shouldDeferTpReveal
            );
        }

        /// <summary>
        /// Disables all world weapons that aren't in the player's equipped weapon list.
        /// Ensures only selected weapons are visible on the player model.
        /// </summary>
        private void DisableUnequippedWorldWeapons() {
            if(_worldWeaponSocket == null) return;

            // Collect all equipped world weapon objects from equipped WeaponData entries.
            var equippedWorldWeapons = new HashSet<GameObject>();
            if(weaponDataList != null) {
                foreach(var weaponData in weaponDataList) {
                    if(weaponData == null) continue;
                    var worldWeapon = ResolveWorldWeaponObject(weaponData);
                    if(worldWeapon != null) {
                        equippedWorldWeapons.Add(worldWeapon);
                    }
                }
            }

            // Disable all world weapons that aren't in the equipped list
            foreach(Transform child in _worldWeaponSocket) {
                if(child == null) continue;

                // Check if this weapon is in the equipped list
                var isEquipped = equippedWorldWeapons.Contains(child.gameObject);
                
                // Also check if it's the current world weapon (should be active)
                var isCurrentWeapon = CurrentWorldWeaponInstance != null && 
                                      CurrentWorldWeaponInstance == child.gameObject;
                
                // Disable if not equipped and not current weapon
                if(!isEquipped && !isCurrentWeapon) {
                    child.gameObject.SetActive(false);
                }
            }
        }

        private bool ValidateStrictEquippedWeaponConfiguration() {
            if(kinemationFpsPlayerPrefab == null) {
                Debug.LogError("[WeaponManager] Missing KINEMATION FPS player prefab.");
                return false;
            }

            if(_weaponCamera == null) {
                Debug.LogError("[WeaponManager] Missing WeaponCamera. Strict mode requires WeaponCamera for FP viewmodels.");
                return false;
            }

            if(_worldWeaponSocket == null) {
                Debug.LogError("[WeaponManager] Missing WorldWeaponSocket. Strict mode requires explicit WorldWeaponBinding objects.");
                return false;
            }

            if(weaponDataList == null || weaponDataList.Count == 0) {
                return false;
            }

            var isValid = true;
            foreach(var data in weaponDataList) {
                if(data == null) {
                    Debug.LogError("[WeaponManager] Equipped weapon data is null.");
                    isValid = false;
                    continue;
                }

                if(ResolveWeaponSlot(data) < 0) {
                    Debug.LogError($"[WeaponManager] Weapon '{data.weaponName}' has invalid slot assignment.");
                    isValid = false;
                }

                if(!TryGetKinemationBindingForData(data, out var binding) || binding == null ||
                   binding.kinemationWeaponPrefab == null) {
                    Debug.LogError($"[WeaponManager] Weapon '{data.weaponName}' is missing a KINEMATION binding/prefab.");
                    isValid = false;
                    continue;
                }

                if(ResolveKinemationWeaponCapacity(binding.kinemationWeaponPrefab) <= 0) {
                    Debug.LogError(
                        $"[WeaponManager] Weapon '{data.weaponName}' has invalid KINEMATION ammo capacity. " +
                        "Set FPSWeaponSettings.ammo > 0.");
                    isValid = false;
                }

                if(ResolveWorldWeaponObject(data) == null) {
                    Debug.LogError(
                        $"[WeaponManager] Weapon '{data.weaponName}' missing WorldWeaponBinding under WorldWeaponSocket.");
                    isValid = false;
                }
            }

            return isValid;
        }

        private void BuildEquippedWeaponList() {
            BuildKinemationWeaponLookup();
            weaponDataList = new List<WeaponData>();

            if(playerController == null) {
                Debug.LogError("[WeaponManager] Missing PlayerController while building equipped weapon list.");
                return;
            }

            var primaryIndex = playerController.primaryWeaponIndex.Value;
            var secondaryIndex = playerController.secondaryWeaponIndex.Value;

            var primary = GetWeaponFromOptions(_kinemationCatalog.PrimaryWeaponOptions, primaryIndex, "Primary");
            if(primary != null) {
                weaponDataList.Add(primary);
            }

            var secondary = GetWeaponFromOptions(_kinemationCatalog.SecondaryWeaponOptions, secondaryIndex, "Secondary");
            if(secondary != null) {
                weaponDataList.Add(secondary);
            }
        }

        private static WeaponData GetWeaponFromOptions(IReadOnlyList<WeaponData> options, int storedIndex, string slotLabel) {
            if(options == null || options.Count == 0) {
                Debug.LogError($"[WeaponManager] No {slotLabel} weapon options assigned.");
                return null;
            }

            if(storedIndex < 0 || storedIndex >= options.Count) {
                Debug.LogError(
                    $"[WeaponManager] {slotLabel} weapon index {storedIndex} out of range [0..{options.Count - 1}].");
                return null;
            }

            var weaponData = options[storedIndex];
            if(weaponData == null) {
                Debug.LogError($"[WeaponManager] {slotLabel} weapon at index {storedIndex} is null.");
            }

            return weaponData;
        }
    }
}

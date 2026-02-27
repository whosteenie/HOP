using Game.Player;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Weapons {
    public partial class WeaponManager {
        private void ResolveKinemationViewmodelPose(KinemationWeaponBinding binding, out Vector3 localPosition,
            out Vector3 localEulerAngles) {
            if(binding != null && binding.useCustomViewmodelPose) {
                localPosition = binding.viewmodelLocalPosition;
                localEulerAngles = binding.viewmodelLocalEulerAngles;
                return;
            }

            localPosition = kinemationViewmodelLocalPosition;
            localEulerAngles = kinemationViewmodelLocalEulerAngles;
        }

        private void ApplyResolvedKinemationViewmodelPose(GameObject fpWeaponRoot, KinemationWeaponBinding binding) {
            if(fpWeaponRoot == null) return;
            ResolveKinemationViewmodelPose(binding, out var localPosition, out var localEulerAngles);
            fpWeaponRoot.transform.localPosition = localPosition;
            fpWeaponRoot.transform.localEulerAngles = localEulerAngles;
        }

        private static bool TryGetKinemationDriver(GameObject fpWeaponRoot, out KinemationFpWeaponDriver driver) {
            driver = fpWeaponRoot != null ? fpWeaponRoot.GetComponent<KinemationFpWeaponDriver>() : null;
            return driver != null;
        }

        private int GetFpWeaponLayer() {
            return IsOwner ? LayerMask.NameToLayer("Weapon") : LayerMask.NameToLayer("Masked");
        }

        private GameObject ActivateFpWeapon(int weaponIndex, WeaponData data, bool triggerPullOutAnimation) {
            if(weaponIndex < 0 || weaponIndex >= _fpWeaponInstances.Count || data == null) return null;

            var fp = _fpWeaponInstances[weaponIndex];
            if(fp == null) return null;

            if(!TryGetKinemationDriver(fp, out var kinemationDriver) || kinemationDriver == null) {
                return null;
            }

            TryGetKinemationBindingForData(data, out var kinemationBinding);
            ApplyResolvedKinemationViewmodelPose(fp, kinemationBinding);
            fp.SetActive(true);
            kinemationDriver.InitializeIfNeeded(GetFpWeaponLayer());
            kinemationDriver.PlayEquipAnimation(immediate: !triggerPullOutAnimation);
            return fp;
        }

        public GameObject GetCurrentFpWeapon() {
            if(CurrentWeaponIndex < 0 || CurrentWeaponIndex >= _fpWeaponInstances.Count) return null;
            return _fpWeaponInstances[CurrentWeaponIndex];
        }

        public void UpdateAllFpArmTagGlow(bool isTagged) {
            if(!IsOwner || playerController == null) return;
            var visualController = playerController.VisualController;
            if(visualController == null) return;

            for(var i = 0; i < _fpWeaponInstances.Count; i++) {
                var fpWeapon = _fpWeaponInstances[i];
                if(fpWeapon == null) continue;
                visualController.UpdateFpArmTagGlow(isTagged, fpWeapon);
            }
        }

        public void SetCurrentFpWeaponVisible(bool visible) {
            var fpWeapon = GetCurrentFpWeapon();
            if(fpWeapon == null) return;

            _playerRenderer.SetFpWeaponRenderersEnabled(visible, fpWeapon);
        }

        public void OffsetCurrentFpWeapon(Vector3 localPosition, Vector3 localEulerAngles) {
            var fpWeapon = GetCurrentFpWeapon();
            if(fpWeapon == null) return;
            fpWeapon.transform.localPosition = localPosition;
            fpWeapon.transform.localEulerAngles = localEulerAngles;
        }

        /// <summary>
        /// Recursively sets the layer of a GameObject and all its children
        /// </summary>
        private static void SetGameObjectAndChildrenLayer(GameObject obj, int layer) {
            if(obj == null) return;

            obj.layer = layer;
            foreach(Transform child in obj.transform) {
                SetGameObjectAndChildrenLayer(child.gameObject, layer);
            }
        }

        /// <summary>
        /// Enables and configures SkinnedMeshRenderers for FP weapon models (e.g., arm models).
        /// Sets shadow casting to Off and ensures they are enabled.
        /// Also applies player material customization from PlayerPrefs (owner only).
        /// </summary>
        private void SetupFpWeaponSkinnedMeshRenderers(GameObject fpWeaponInstance) {
            if(fpWeaponInstance == null) return;

            // Use PlayerRenderer for enabled state
            _playerRenderer.SetFpWeaponSkinnedRenderersEnabled(true, fpWeaponInstance);

            var skinnedRenderers = fpWeaponInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach(var skinnedRenderer in skinnedRenderers) {
                if(skinnedRenderer == null) continue;
                // Shadow mode is handled by PlayerShadow, but we set it here for initial setup
                skinnedRenderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            // Apply player material customization (owner only, local rendering)
            // Use same approach as hopball arms - apply to all renderers
            if(!IsOwner) return;
            ApplyPlayerMaterialToFpWeapon(fpWeaponInstance);

            // Add tag glow update
            var tagController = playerController.GetComponent<PlayerTagController>();
            if(tagController == null || !tagController.isTagged.Value) return;
            var visualController = playerController.GetComponent<PlayerVisualController>();
            if (visualController != null) {
                visualController.UpdateFpArmTagGlow(true, fpWeaponInstance);
            }
        }

        /// <summary>
        /// Applies player material customization from PlayerVisualController to FP weapon arms only.
        /// Only called for owners since FP weapon rendering is fully local.
        /// </summary>
        private void ApplyPlayerMaterialToFpWeapon(GameObject fpWeaponInstance) {
            if(fpWeaponInstance == null || playerController == null) return;

            // Use PlayerVisualController to ensure we use the cached, generated material
            // instead of creating a new one from the mesh which misses customization packets.
            var visualController = playerController.GetComponent<PlayerVisualController>();
            if(visualController != null) {
                visualController.ApplyMaterialToFpArms(fpWeaponInstance);
            }
        }

        private void DestroyFpWeaponInstances() {
            if(_kinemationPullOutCompletionCoroutine != null) {
                StopCoroutine(_kinemationPullOutCompletionCoroutine);
                _kinemationPullOutCompletionCoroutine = null;
            }

            foreach(var fpWeapon in _fpWeaponInstances) {
                if(fpWeapon == null) continue;
                var holderRoot = ResolveFpHolderRoot(fpWeapon);
                Destroy(holderRoot != null ? holderRoot : fpWeapon);
            }

            _fpWeaponInstances.Clear();
            _ammoAuthority.ClearAll();
        }

        private GameObject ResolveFpHolderRoot(GameObject fpWeapon) {
            if(fpWeapon == null) return null;

            var node = fpWeapon.transform;
            while(node.parent != null && !IsFpHolderParent(node.parent)) {
                node = node.parent;
            }

            return IsFpHolderParent(node.parent) ? node.gameObject : null;
        }

        private bool IsFpHolderParent(Transform parent) {
            if(parent == null) return false;
            if(_fpCamera != null && parent == _fpCamera.transform) return true;
            return _weaponCamera != null && parent == _weaponCamera.transform;
        }

        private void InstantiateFpWeaponInstances() {
            for(var i = 0; i < weaponDataList.Count; i++) {
                var data = weaponDataList[i];
                if(data == null) {
                    Debug.LogError($"[WeaponManager] Invalid weapon data at index {i}");
                    continue;
                }

                if(!TryGetKinemationBindingForData(data, out var kinemationBinding)) {
                    Debug.LogError($"[WeaponManager] Weapon '{data.weaponName}' is missing a KINEMATION binding.");
                    continue;
                }

                if(_weaponCamera == null) {
                    Debug.LogError("[WeaponManager] Missing WeaponCamera. Cannot spawn KINEMATION viewmodel.");
                    continue;
                }

                var kinemationSwayHolder = new GameObject("SwayHolder");
                var kinemationSway = kinemationSwayHolder.AddComponent<WeaponSway>();
                kinemationSwayHolder.transform.SetParent(_weaponCamera.transform, false);
                kinemationSwayHolder.transform.localPosition = Vector3.zero;
                kinemationSwayHolder.transform.localEulerAngles = Vector3.zero;
                if(_fpCamera != null) {
                    kinemationSway.SetCameraTransform(_fpCamera.transform);
                }

                var kinemationBobHolder = new GameObject("BobHolder");
                kinemationBobHolder.transform.SetParent(kinemationSwayHolder.transform, false);
                kinemationBobHolder.transform.localPosition = Vector3.zero;
                kinemationBobHolder.transform.localEulerAngles = Vector3.zero;
                var legacyBob = kinemationBobHolder.AddComponent<WeaponBob>();
                legacyBob.ConfigureFeatures(
                    false,
                    false,
                    true,
                    true
                );

                var kinemationHolder = new GameObject("KinemationHolder");
                kinemationHolder.transform.SetParent(kinemationBobHolder.transform, false);
                ResolveKinemationViewmodelPose(kinemationBinding, out var localPosition, out var localEulerAngles);
                kinemationHolder.transform.localPosition = localPosition;
                kinemationHolder.transform.localEulerAngles = localEulerAngles;

                const bool disableWeaponSounds = false;
                const bool disablePlayerSounds = true;

                var kinemationDriver = kinemationHolder.AddComponent<KinemationFpWeaponDriver>();
                kinemationDriver.Configure(
                    kinemationFpsPlayerPrefab,
                    kinemationBinding.kinemationWeaponPrefab,
                    disableWeaponSounds,
                    disablePlayerSounds,
                    true,
                    false,
                    false,
                    true,
                    true,
                    kinemationSprintWalkGaitValue,
                    kinemationEquipUnlockNormalizedTime,
                    kinemationBinding.logDrakeAmmoEjectDebug
                );

                var fpLayer = GetFpWeaponLayer();
                SetGameObjectAndChildrenLayer(kinemationHolder, fpLayer);
                kinemationDriver.InitializeIfNeeded(fpLayer);
                SetupFpWeaponSkinnedMeshRenderers(kinemationHolder);

                kinemationHolder.SetActive(false);
                _fpWeaponInstances.Add(kinemationHolder);
                var capacity = ResolveWeaponCapacity(data);
                if(capacity <= 0) {
                    Debug.LogError(
                        $"[WeaponManager][KIN-Strict] Invalid KIN ammo capacity while instantiating '{data.weaponName}'.");
                    return;
                }
                _ammoAuthority.SeedMagazine(i, capacity);
            }
        }
    }
}

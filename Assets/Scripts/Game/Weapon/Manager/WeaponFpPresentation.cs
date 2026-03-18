using Diagnostics;
using Game.Weapon.Core;
using Game.Weapon.Kinemation;
using Game.Weapon.Presentation;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Weapon.Manager {
    internal sealed class WeaponFpPresentation {
        private readonly WeaponManager _root;

        public WeaponFpPresentation(WeaponManager root) {
            _root = root;
        }

        public GameObject GetCurrentFpWeapon() {
            if(_root.CurrentWeaponIndexInternal < 0 || _root.CurrentWeaponIndexInternal >= _root.FpWeaponInstancesRef.Count) return null;
            return _root.FpWeaponInstancesRef[_root.CurrentWeaponIndexInternal];
        }

        public GameObject GetFpWeaponRootForDisconnect() {
            var fpWeapon = GetCurrentFpWeapon();
            if(fpWeapon == null || !fpWeapon.activeSelf) return null;
            var holderRoot = ResolveFpHolderRoot(fpWeapon);
            return holderRoot != null ? holderRoot : fpWeapon;
        }

        public void ApplyKinemationViewmodelPose(GameObject fpWeaponRoot, KinemationWeaponBinding binding) {
            if(fpWeaponRoot == null) return;
            ResolveKinemationViewmodelPose(binding, out var localPosition, out var localEulerAngles);
            fpWeaponRoot.transform.localPosition = localPosition;
            fpWeaponRoot.transform.localEulerAngles = localEulerAngles;
        }

        public static bool TryGetKinemationDriver(GameObject fpWeaponRoot, out KinFpWeaponDriver driver) {
            driver = fpWeaponRoot != null ? fpWeaponRoot.GetComponent<KinFpWeaponDriver>() : null;
            return driver != null;
        }

        public int GetFpWeaponLayer() {
            return _root.IsOwner ? LayerMask.NameToLayer("Weapon") : LayerMask.NameToLayer("Masked");
        }

        public GameObject ActivateFpWeapon(int weaponIndex, WeaponData data, bool triggerPullOutAnimation) {
            if(weaponIndex < 0 || weaponIndex >= _root.FpWeaponInstancesRef.Count || data == null) return null;

            var fp = _root.FpWeaponInstancesRef[weaponIndex];
            if(fp == null) return null;

            if(!TryGetKinemationDriver(fp, out var kinemationDriver) || kinemationDriver == null) {
                return null;
            }

            _root.TryGetKinemationBinding(data, out var kinemationBinding);
            ApplyKinemationViewmodelPose(fp, kinemationBinding);
            fp.SetActive(true);
            kinemationDriver.InitializeIfNeeded(GetFpWeaponLayer());
            kinemationDriver.PlayEquipAnimation(immediate: !triggerPullOutAnimation);
            return fp;
        }

        public void DestroyFpWeaponInstances() {
            if(_root.KinemationPullOutCompletionCoroutine != null) {
                _root.StopCoroutine(_root.KinemationPullOutCompletionCoroutine);
                _root.KinemationPullOutCompletionCoroutine = null;
            }

            foreach(var fpWeapon in _root.FpWeaponInstancesRef) {
                if(fpWeapon == null) continue;
                var holderRoot = ResolveFpHolderRoot(fpWeapon);
                Object.Destroy(holderRoot != null ? holderRoot : fpWeapon);
            }

            _root.FpWeaponInstancesRef.Clear();
            _root.AmmoAuthorityRef.ClearAll();
        }

        public void InstantiateFpWeaponInstances() {
            for(var i = 0; i < _root.WeaponDataListRef.Count; i++) {
                var data = _root.WeaponDataListRef[i];
                if(data == null) {
                    DevLog.LogError($"[WeaponManager] Invalid weapon data at index {i}");
                    continue;
                }

                if(!_root.TryGetKinemationBinding(data, out var kinemationBinding)) {
                    DevLog.LogError($"[WeaponManager] Weapon '{data.weaponName}' is missing a KINEMATION binding.");
                    continue;
                }

                if(_root.WeaponCameraRef == null) {
                    DevLog.LogError("[WeaponManager] Missing WeaponCamera. Cannot spawn KINEMATION viewmodel.");
                    continue;
                }

                var kinemationSwayHolder = new GameObject("SwayHolder");
                var kinemationSway = kinemationSwayHolder.AddComponent<WeaponSway>();
                kinemationSwayHolder.transform.SetParent(_root.WeaponCameraRef.transform, false);
                kinemationSwayHolder.transform.localPosition = Vector3.zero;
                kinemationSwayHolder.transform.localEulerAngles = Vector3.zero;
                if(_root.FpCameraRef != null) {
                    kinemationSway.SetCameraTransform(_root.FpCameraRef.transform);
                }

                var kinemationBobHolder = new GameObject("BobHolder");
                kinemationBobHolder.transform.SetParent(kinemationSwayHolder.transform, false);
                kinemationBobHolder.transform.localPosition = Vector3.zero;
                kinemationBobHolder.transform.localEulerAngles = Vector3.zero;
                var legacyBob = kinemationBobHolder.AddComponent<WeaponBob>();
                legacyBob.ConfigureFeatures(false, false, true, true);

                var kinemationHolder = new GameObject("KinemationHolder");
                kinemationHolder.transform.SetParent(kinemationBobHolder.transform, false);
                ResolveKinemationViewmodelPose(kinemationBinding, out var localPosition, out var localEulerAngles);
                kinemationHolder.transform.localPosition = localPosition;
                kinemationHolder.transform.localEulerAngles = localEulerAngles;

                const bool disableWeaponSounds = false;
                const bool disablePlayerSounds = true;

                var kinemationDriver = kinemationHolder.AddComponent<KinFpWeaponDriver>();
                kinemationDriver.Configure(
                    _root.KinemationFpsPlayerPrefabRef,
                    kinemationBinding.kinemationWeaponPrefab,
                    disableWeaponSounds,
                    disablePlayerSounds,
                    true,
                    false,
                    false,
                    true,
                    true,
                    _root.KinemationSprintWalkGaitValue,
                    _root.KinemationEquipUnlockNormalizedTime
                );

                var fpLayer = GetFpWeaponLayer();
                KinemationViewmodelUtility.SetLayerRecursive(kinemationHolder, fpLayer);
                kinemationDriver.InitializeIfNeeded(fpLayer);
                SetupFpRenderers(kinemationHolder);

                kinemationHolder.SetActive(false);
                _root.FpWeaponInstancesRef.Add(kinemationHolder);
                var capacity = _root.ResolveWeaponCapacity(data);
                if(capacity <= 0) {
                    DevLog.LogError(
                        $"[WeaponManager][KIN-Strict] Invalid KIN ammo capacity while instantiating '{data.weaponName}'.");
                    return;
                }
                _root.AmmoAuthorityRef.SeedMagazine(i, capacity);
            }
        }

        public void SetupFpRenderers(GameObject fpWeaponInstance) {
            if(fpWeaponInstance == null) return;

            var skinnedRenderers = fpWeaponInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach(var skinnedRenderer in skinnedRenderers) {
                if(skinnedRenderer == null) continue;
                skinnedRenderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            _root.RequestFpVisualRefreshInternal(fpWeaponInstance);
        }

        private void ResolveKinemationViewmodelPose(KinemationWeaponBinding binding, out Vector3 localPosition,
            out Vector3 localEulerAngles) {
            if(binding is { useCustomViewmodelPose: true }) {
                localPosition = binding.viewmodelLocalPosition;
                localEulerAngles = binding.viewmodelLocalEulerAngles;
                return;
            }

            localPosition = _root.KinemationViewmodelLocalPosition;
            localEulerAngles = _root.KinemationViewmodelLocalEulerAngles;
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
            if(_root.FpCameraRef != null && parent == _root.FpCameraRef.transform) return true;
            return _root.WeaponCameraRef != null && parent == _root.WeaponCameraRef.transform;
        }
    }
}

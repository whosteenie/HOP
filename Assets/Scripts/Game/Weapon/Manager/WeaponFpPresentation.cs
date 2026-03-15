using Game.Player.Combat;
using Game.Player.Visual;
using Game.Weapon.Core;
using Game.Weapon.Kinemation;
using Game.Weapon.Presentation;
using KINEMATION.FPSAnimationPack.Scripts.Sounds;
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

        public GameObject GetFpWeaponHolderRootForDisconnect() {
            var fpWeapon = GetCurrentFpWeapon();
            if(fpWeapon == null || !fpWeapon.activeSelf) return null;
            var holderRoot = ResolveFpHolderRoot(fpWeapon);
            return holderRoot != null ? holderRoot : fpWeapon;
        }

        public void UpdateAllFpArmTagGlow(bool isTagged) {
            if(!_root.IsOwner || _root.PlayerControllerRef == null) return;
            var visualController = _root.PlayerControllerRef.VisualController;
            if(visualController == null) return;

            foreach(var fpWeapon in _root.FpWeaponInstancesRef) {
                if(fpWeapon == null) continue;
                visualController.UpdateFpArmTagGlow(isTagged, fpWeapon);
            }
        }

        public void SetCurrentFpWeaponVisible(bool visible) {
            var fpWeapon = GetCurrentFpWeapon();
            if(fpWeapon == null) return;

            _root.PlayerRendererRef.SetFpWeaponRenderersEnabled(visible, fpWeapon);
        }

        public void HideFpVisualsForDisconnectTransition() {
            if(!_root.IsOwner) return;
            foreach(var fpWeapon in _root.FpWeaponInstancesRef) {
                if(fpWeapon != null && fpWeapon.activeSelf) {
                    fpWeapon.SetActive(false);
                }
            }
        }

        public void OffsetCurrentFpWeapon(Vector3 localPosition, Vector3 localEulerAngles) {
            var fpWeapon = GetCurrentFpWeapon();
            if(fpWeapon == null) return;
            fpWeapon.transform.localPosition = localPosition;
            fpWeapon.transform.localEulerAngles = localEulerAngles;
        }

        public void ApplyKinemationViewmodelPose(GameObject fpWeaponRoot, WeaponManager.KinemationWeaponBinding binding) {
            if(fpWeaponRoot == null) return;
            ResolveKinemationViewmodelPose(binding, out var localPosition, out var localEulerAngles);
            fpWeaponRoot.transform.localPosition = localPosition;
            fpWeaponRoot.transform.localEulerAngles = localEulerAngles;
        }

        public static bool TryGetKinemationDriver(GameObject fpWeaponRoot, out KinFpWeaponDriver driver) {
            driver = fpWeaponRoot != null ? fpWeaponRoot.GetComponent<KinFpWeaponDriver>() : null;
            return driver != null;
        }

        /// <summary>Sets layer on root and all descendants. Used when making FP viewmodel instance ready.</summary>
        public static void SetLayerRecursive(GameObject root, int layer) {
            if(root == null) return;
            root.layer = layer;
            foreach(Transform child in root.transform) {
                SetLayerRecursive(child.gameObject, layer);
            }
        }

        /// <summary>Disables shadow casting and receiving on all renderers under root. Used when making FP viewmodel ready.</summary>
        public static void DisableViewmodelShadows(GameObject root) {
            if(root == null) return;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach(var r in renderers) {
                if(r == null) continue;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
        }

        /// <summary>Attaches reload/event relays to animators and weapon sounds on the viewmodel, binds driver, and optionally destroys original sound components.</summary>
        public static void AttachReloadEventRelays(GameObject viewmodelRoot, KinFpWeaponDriver driver,
            bool weaponSoundPlaybackDisabled, bool disablePlayerSounds) {
            if(viewmodelRoot == null || driver == null) return;

            var animators = viewmodelRoot.GetComponentsInChildren<Animator>(true);
            foreach(var animator in animators) {
                if(animator == null) continue;
                var relay = animator.GetComponent<KinReloadEventRelay>();
                if(relay == null) relay = animator.gameObject.AddComponent<KinReloadEventRelay>();
                relay.Bind(driver);
            }

            var weaponSounds = viewmodelRoot.GetComponentsInChildren<FPSWeaponSound>(true);
            foreach(var weaponSound in weaponSounds) {
                if(weaponSound == null) continue;
                var relay = weaponSound.GetComponent<KinReloadEventRelay>();
                if(relay == null) relay = weaponSound.gameObject.AddComponent<KinReloadEventRelay>();
                relay.Bind(driver);
                if(weaponSoundPlaybackDisabled) Object.Destroy(weaponSound);
            }

            if(!disablePlayerSounds) return;
            var playerSounds = viewmodelRoot.GetComponentsInChildren<FPSPlayerSound>(true);
            foreach(var playerSound in playerSounds) {
                if(playerSound == null) continue;
                if(playerSound.GetComponent<KinPlayerSoundEventRelay>() == null)
                    playerSound.gameObject.AddComponent<KinPlayerSoundEventRelay>();
                Object.Destroy(playerSound);
            }
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
                    Debug.LogError($"[WeaponManager] Invalid weapon data at index {i}");
                    continue;
                }

                if(!_root.TryGetKinemationBinding(data, out var kinemationBinding)) {
                    Debug.LogError($"[WeaponManager] Weapon '{data.weaponName}' is missing a KINEMATION binding.");
                    continue;
                }

                if(_root.WeaponCameraRef == null) {
                    Debug.LogError("[WeaponManager] Missing WeaponCamera. Cannot spawn KINEMATION viewmodel.");
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
                SetGameObjectAndChildrenLayer(kinemationHolder, fpLayer);
                kinemationDriver.InitializeIfNeeded(fpLayer);
                SetupFpWeaponSkinnedMeshRenderers(kinemationHolder);

                kinemationHolder.SetActive(false);
                _root.FpWeaponInstancesRef.Add(kinemationHolder);
                var capacity = _root.ResolveWeaponCapacity(data);
                if(capacity <= 0) {
                    Debug.LogError(
                        $"[WeaponManager][KIN-Strict] Invalid KIN ammo capacity while instantiating '{data.weaponName}'.");
                    return;
                }
                _root.AmmoAuthorityRef.SeedMagazine(i, capacity);
            }
        }

        public void SetupFpWeaponSkinnedMeshRenderers(GameObject fpWeaponInstance) {
            if(fpWeaponInstance == null) return;

            _root.PlayerRendererRef.SetFpWeaponSkinnedRenderersEnabled(true, fpWeaponInstance);

            var skinnedRenderers = fpWeaponInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach(var skinnedRenderer in skinnedRenderers) {
                if(skinnedRenderer == null) continue;
                skinnedRenderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            if(!_root.IsOwner) return;
            ApplyPlayerMaterialToFpWeapon(fpWeaponInstance);

            var tagController = _root.PlayerControllerRef.GetComponent<PlayerTagController>();
            if(tagController == null || !tagController.IsTagged.Value) return;
            var visualController = _root.PlayerControllerRef.GetComponent<PlayerVisualController>();
            if(visualController != null) {
                visualController.UpdateFpArmTagGlow(true, fpWeaponInstance);
            }
        }

        public static void EnsureHierarchyActive(GameObject instanceRoot) {
            if(instanceRoot == null) return;
            var parent = instanceRoot.transform;
            while(parent != null) {
                if(!parent.gameObject.activeSelf) {
                    parent.gameObject.SetActive(true);
                }

                parent = parent.parent;
            }
        }

        private void ResolveKinemationViewmodelPose(WeaponManager.KinemationWeaponBinding binding, out Vector3 localPosition,
            out Vector3 localEulerAngles) {
            if(binding is { useCustomViewmodelPose: true }) {
                localPosition = binding.viewmodelLocalPosition;
                localEulerAngles = binding.viewmodelLocalEulerAngles;
                return;
            }

            localPosition = _root.KinemationViewmodelLocalPosition;
            localEulerAngles = _root.KinemationViewmodelLocalEulerAngles;
        }

        private static void SetGameObjectAndChildrenLayer(GameObject obj, int layer) {
            if(obj == null) return;

            obj.layer = layer;
            foreach(Transform child in obj.transform) {
                SetGameObjectAndChildrenLayer(child.gameObject, layer);
            }
        }

        private void ApplyPlayerMaterialToFpWeapon(GameObject fpWeaponInstance) {
            if(fpWeaponInstance == null || _root.PlayerControllerRef == null) return;

            var visualController = _root.PlayerControllerRef.GetComponent<PlayerVisualController>();
            if(visualController != null) {
                visualController.ApplyMaterialToFpArms(fpWeaponInstance);
            }
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

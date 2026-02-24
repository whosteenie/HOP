using System.Collections.Generic;
using KINEMATION.FPSAnimationPack.Scripts.Camera;
using KINEMATION.FPSAnimationPack.Scripts.Player;
using KINEMATION.FPSAnimationPack.Scripts.Sounds;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Weapons {
    [DisallowMultipleComponent]
    public sealed class KinemationFpWeaponDriver : MonoBehaviour {
        [Header("KINEMATION")]
        [SerializeField] private GameObject fpsPlayerPrefab;
        [SerializeField] private GameObject weaponPrefab;
        [SerializeField] private bool disableKinemationSounds = true;
        [SerializeField] private bool tagArmsForLegacyHooks;
        [SerializeField] private string armRootName = "SK_Arms_Mono";

        private GameObject _playerInstance;
        private FPSPlayerSettings _runtimePlayerSettings;
        private FPSPlayer _fpsPlayer;
        private FPSWeapon _activeWeapon;
        private Transform _muzzleTransform;

        public void Configure(GameObject playerPrefab, GameObject fpWeaponPrefab, bool disableSounds, bool tagArms) {
            fpsPlayerPrefab = playerPrefab;
            weaponPrefab = fpWeaponPrefab;
            disableKinemationSounds = disableSounds;
            tagArmsForLegacyHooks = tagArms;
        }

        public bool InitializeIfNeeded(int renderLayer) {
            if(_playerInstance != null) {
                SetLayerRecursive(_playerInstance, renderLayer);
                return _activeWeapon != null || TryCacheActiveWeapon();
            }

            if(fpsPlayerPrefab == null || weaponPrefab == null) {
                Debug.LogWarning("[KinemationFpWeaponDriver] Missing prefabs. Cannot initialize KINEMATION viewmodel.");
                return false;
            }

            _playerInstance = Instantiate(fpsPlayerPrefab, transform, false);
            _playerInstance.name = "KinemationViewmodel";
            _playerInstance.SetActive(false);

            _fpsPlayer = _playerInstance.GetComponentInChildren<FPSPlayer>(true);
            if(_fpsPlayer == null) {
                Debug.LogWarning(
                    "[KinemationFpWeaponDriver] FPSPlayer component missing on KINEMATION player prefab hierarchy.");
                Destroy(_playerInstance);
                _playerInstance = null;
                return false;
            }

            _fpsPlayer.SetCharacterControllerMovementEnabled(false);

            BuildRuntimeSettings();
            DisableUnneededComponents();
            SetLayerRecursive(_playerInstance, renderLayer);
            DisableViewmodelShadows(_playerInstance);

            _playerInstance.SetActive(true);
            TryCacheActiveWeapon();

            if(tagArmsForLegacyHooks) {
                TryTagArmRoot();
            }

            // FPSPlayer creates its weapon instances in Start(), so cache may complete on a later frame.
            return _playerInstance != null;
        }

        public void PlayEquipAnimation(bool immediate) {
            if(!TryCacheActiveWeapon()) return;
            if(immediate) {
                _activeWeapon.OnEquipped_Immediate();
            } else {
                _activeWeapon.OnEquipped();
            }
        }

        public void PlayFireAnimation() {
            if(!TryCacheActiveWeapon()) return;
            _activeWeapon.OnFirePressed();
            _activeWeapon.OnFireReleased();
        }

        public void PlayReloadAnimation() {
            if(!TryCacheActiveWeapon()) return;
            _activeWeapon.OnReload();
        }

        public void PlayReloadCompleteAnimation() {
            // KINEMATION handles reload completion internally via its own state machine.
        }

        public Transform GetMuzzleTransform() {
            TryCacheActiveWeapon();
            return _muzzleTransform;
        }

        public bool HasActiveWeapon() {
            return TryCacheActiveWeapon();
        }

        private void BuildRuntimeSettings() {
            var sourceSettings = _fpsPlayer.playerSettings;
            if(sourceSettings != null) {
                _runtimePlayerSettings = Instantiate(sourceSettings);
            } else {
                _runtimePlayerSettings = ScriptableObject.CreateInstance<FPSPlayerSettings>();
            }

            _runtimePlayerSettings.weaponPrefabs = new List<GameObject> { weaponPrefab };
            _fpsPlayer.playerSettings = _runtimePlayerSettings;
        }

        private void DisableUnneededComponents() {
            var inputComponents = _playerInstance.GetComponentsInChildren<PlayerInput>(true);
            foreach(var inputComponent in inputComponents) {
                if(inputComponent != null) {
                    inputComponent.enabled = false;
                }
            }

            var controllers = _playerInstance.GetComponentsInChildren<CharacterController>(true);
            foreach(var controller in controllers) {
                if(controller != null) {
                    controller.enabled = false;
                }
            }

            var cameraAnim = _playerInstance.GetComponentInChildren<FPSCameraAnimator>(true);
            if(cameraAnim != null) {
                cameraAnim.enabled = false;
            }

            var camera = _playerInstance.GetComponentInChildren<Camera>(true);
            if(camera != null) {
                camera.enabled = false;
            }

            var listener = _playerInstance.GetComponentInChildren<AudioListener>(true);
            if(listener != null) {
                listener.enabled = false;
            }

            if(!disableKinemationSounds) return;

            var playerSounds = _playerInstance.GetComponentsInChildren<FPSPlayerSound>(true);
            foreach(var playerSound in playerSounds) {
                if(playerSound != null) {
                    playerSound.enabled = false;
                }
            }

            var weaponSounds = _playerInstance.GetComponentsInChildren<FPSWeaponSound>(true);
            foreach(var weaponSound in weaponSounds) {
                if(weaponSound != null) {
                    weaponSound.enabled = false;
                }
            }

            var audioSources = _playerInstance.GetComponentsInChildren<AudioSource>(true);
            foreach(var source in audioSources) {
                if(source != null) {
                    source.enabled = false;
                }
            }
        }

        private void TryTagArmRoot() {
            if(string.IsNullOrEmpty(armRootName)) return;
            if(!TryFindChildByName(_playerInstance.transform, armRootName, out var armRoot)) return;

            try {
                armRoot.gameObject.tag = "Arm";
            } catch(UnityException) {
                // If the tag does not exist in TagManager, skip tagging.
            }
        }

        private static bool TryFindChildByName(Transform root, string targetName, out Transform found) {
            if(root.name == targetName) {
                found = root;
                return true;
            }

            for(var i = 0; i < root.childCount; i++) {
                var child = root.GetChild(i);
                if(TryFindChildByName(child, targetName, out found)) {
                    return true;
                }
            }

            found = null;
            return false;
        }

        private static Transform ResolveMuzzleTransform(FPSWeapon activeWeapon) {
            if(activeWeapon == null) return null;

            var transforms = activeWeapon.GetComponentsInChildren<Transform>(true);
            foreach(var t in transforms) {
                if(t != null && t.name == "Muzzle") {
                    return t;
                }
            }

            if(activeWeapon.aimPoint != null) {
                var fallback = new GameObject("Muzzle").transform;
                fallback.SetParent(activeWeapon.aimPoint, false);
                fallback.localPosition = Vector3.zero;
                fallback.localRotation = Quaternion.identity;
                return fallback;
            }

            return activeWeapon.transform;
        }

        private bool TryCacheActiveWeapon() {
            if(_activeWeapon != null && _muzzleTransform != null) {
                return true;
            }

            if(_fpsPlayer == null || _playerInstance == null) {
                return false;
            }

            if(_activeWeapon == null) {
                _activeWeapon = FindActiveWeaponComponent();
                if(_activeWeapon == null) {
                    return false;
                }
            }

            _muzzleTransform ??= ResolveMuzzleTransform(_activeWeapon);
            return _activeWeapon != null;
        }

        private FPSWeapon FindActiveWeaponComponent() {
            var weapons = _playerInstance.GetComponentsInChildren<FPSWeapon>(true);
            if(weapons == null || weapons.Length == 0) {
                return null;
            }

            foreach(var weapon in weapons) {
                if(weapon == null) continue;
                if(weapon.gameObject.activeInHierarchy) {
                    return weapon;
                }
            }

            return weapons[0];
        }

        private static void SetLayerRecursive(GameObject root, int layer) {
            if(root == null) return;
            root.layer = layer;

            foreach(Transform child in root.transform) {
                SetLayerRecursive(child.gameObject, layer);
            }
        }

        private static void DisableViewmodelShadows(GameObject root) {
            if(root == null) return;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach(var renderer in renderers) {
                if(renderer == null) continue;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        private void OnDestroy() {
            if(_runtimePlayerSettings != null) {
                Destroy(_runtimePlayerSettings);
                _runtimePlayerSettings = null;
            }
        }
    }
}

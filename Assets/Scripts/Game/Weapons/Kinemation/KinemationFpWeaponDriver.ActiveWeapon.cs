using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using KINEMATION.FPSAnimationPack.Scripts.Sounds;
using UnityEngine;
using UnityEngine.VFX;

namespace Game.Weapons {
    public sealed partial class KinemationFpWeaponDriver {
        private Animator[] GetActiveWeaponAnimators() {
            return _activeWeaponComponentCache.GetAnimators(_activeWeapon);
        }

        private FPSWeaponSound[] GetActiveWeaponSounds() {
            return _activeWeaponComponentCache.GetSounds(_activeWeapon);
        }

        private FPSWeaponSound[] GetWeaponSounds(FPSWeapon weapon) {
            return _activeWeaponComponentCache.GetSounds(_activeWeapon, weapon);
        }

        private ParticleSystem[] GetWeaponParticleSystems(FPSWeapon weapon) {
            return _activeWeaponComponentCache.GetParticleSystems(_activeWeapon, weapon);
        }

        private VisualEffect[] GetWeaponVisualEffects(FPSWeapon weapon) {
            return _activeWeaponComponentCache.GetVisualEffects(_activeWeapon, weapon);
        }

        private Light[] GetWeaponLights(FPSWeapon weapon) {
            return _activeWeaponComponentCache.GetLights(_activeWeapon, weapon);
        }

        private Pdw90Animation[] GetActiveWeaponPdwAnimations() {
            return _activeWeaponComponentCache.GetPdwAnimations(_activeWeapon);
        }

        private AudioSource[] GetActiveWeaponAudioSources() {
            return _activeWeaponComponentCache.GetAudioSources(_activeWeapon);
        }

        private KinemationWeaponPartReferences GetActiveWeaponPartReferences() {
            return _activeWeaponComponentCache.GetPartReferences(_activeWeapon);
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

        // TODO(KIN-SPLIT): Extract active-weapon switch + relay sync flow into a dedicated coordinator.
        private bool TryCacheActiveWeapon() {
            if(_activeWeapon != null && !_activeWeapon.gameObject.activeInHierarchy) {
                var resolvedWeapon = FindActiveWeaponComponent();
                if(resolvedWeapon != null && resolvedWeapon != _activeWeapon) {
                    _activeWeapon = resolvedWeapon;
                    _muzzleTransform = null;
                    _activeWeaponComponentCache.Invalidate();
                    if(_playerInstance != null && _renderLayer >= 0) {
                        SetLayerRecursive(_playerInstance, _renderLayer);
                    }

                    DisableViewmodelShadows(_playerInstance);
                    AttachReloadEventRelays();
                }
            }

            if(_activeWeapon != null) {
                _activeWeaponComponentCache.Ensure(_activeWeapon);
            }

            if(_activeWeapon != null && _muzzleTransform != null && _activeWeapon.gameObject.activeInHierarchy) {
                ApplyActiveWeaponSoundToggles(_activeWeapon);
                RefreshActiveWeaponSoundMetadata(_activeWeapon);
                SuppressInternalMuzzleFx(_activeWeapon);
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
                _activeWeaponComponentCache.Invalidate();

                if(_renderLayer >= 0) {
                    SetLayerRecursive(_playerInstance, _renderLayer);
                }
                DisableViewmodelShadows(_playerInstance);
                AttachReloadEventRelays();
            }

            _activeWeaponComponentCache.Ensure(_activeWeapon);
            if(_muzzleTransform == null) {
                var partReferences = GetActiveWeaponPartReferences();
                if(partReferences != null) {
                    TryResolveConfiguredWeaponPartReference(
                        partReferences.FpMuzzleTransform,
                        FpMuzzleReferenceKey,
                        nameof(KinemationWeaponPartReferences.FpMuzzleTransform),
                        out _muzzleTransform);
                } else {
                    ReportMissingKinemationPartReference(FpMuzzleReferenceKey,
                        nameof(KinemationWeaponPartReferences.FpMuzzleTransform), true);
                }
            }

            ApplyActiveWeaponSoundToggles(_activeWeapon);
            RefreshActiveWeaponSoundMetadata(_activeWeapon);
            SuppressInternalMuzzleFx(_activeWeapon);
            return _activeWeapon != null;
        }

        private void Update() {
            if(_playerInstance == null) return;
            if(_activeWeapon == null) {
                TryCacheActiveWeapon();
            }
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Player.Core {
    internal sealed class PlayerWeaponPresentation {
        private readonly PlayerController _player;
        private readonly Dictionary<GameObject, MeshRenderer[]> _cachedWeaponRenderers = new();
        private readonly Dictionary<GameObject, Collider[]> _cachedWeaponColliders = new();

        public PlayerWeaponPresentation(PlayerController player) {
            _player = player;
        }

        public void ResetWeaponState(bool resetAllAmmo, bool switchToWeapon0, bool updateHud) {
            var weaponManager = _player.WeaponManager;
            if(!_player.IsOwner || weaponManager == null) return;

            if(resetAllAmmo) {
                weaponManager.ResetAllWeaponAmmo();
            }

            var currentWeapon = weaponManager.CurrentWeapon;
            if(currentWeapon != null) {
                currentWeapon.ResetWeapon();

                var weaponInstance = currentWeapon.GetWeaponPrefab();
                if(weaponInstance != null) {
                    EnsureWeaponHierarchyActive(weaponInstance);
                    EnsureWeaponShadowVisibility(weaponInstance);

                    if(_player.PlayerRenderer == null) {
                        Debug.LogError("[PlayerController] PlayerRenderer not found!");
                        return;
                    }

                    _player.PlayerRenderer.SetWorldWeaponRenderersEnabled(true);
                }

                if(updateHud) {
                    PlayerUiEventBridge.PublishWeaponHudRefresh(currentWeapon.currentAmmo, currentWeapon.GetMagSize(),
                        _player.NetHealth.Value, 1f, Weapon.Core.Weapon.MaxDamageMultiplier);
                }
            }

            if(switchToWeapon0) {
                _player.PlayerInput.SwitchWeapon(0);
            }
        }

        private void EnsureWeaponHierarchyActive(GameObject weaponInstance) {
            if(weaponInstance == null) return;

            var parent = weaponInstance.transform;
            while(parent != null) {
                if(!parent.gameObject.activeSelf) {
                    parent.gameObject.SetActive(true);
                }

                parent = parent.parent;
            }

            weaponInstance.SetActive(true);

            if(!_cachedWeaponColliders.TryGetValue(weaponInstance, out var colliders)) {
                colliders = weaponInstance.GetComponentsInChildren<Collider>(true);
                _cachedWeaponColliders[weaponInstance] = colliders;
            }

            foreach(var col in colliders) {
                if(col != null && !col.enabled) {
                    col.enabled = true;
                }
            }
        }

        private void EnsureWeaponShadowVisibility(GameObject weaponInstance) {
            if(weaponInstance == null) return;

            if(!_cachedWeaponRenderers.TryGetValue(weaponInstance, out var meshRenderers)) {
                meshRenderers = weaponInstance.GetComponentsInChildren<MeshRenderer>(true);
                _cachedWeaponRenderers[weaponInstance] = meshRenderers;
            }

            if(_player.PlayerRenderer == null) {
                Debug.LogError("[PlayerController] PlayerRenderer not found! Cannot enable world weapon renderers.");
                return;
            }

            _player.PlayerRenderer.SetWorldWeaponRenderersEnabled(true);

            foreach(var meshRenderer in meshRenderers) {
                if(meshRenderer == null) continue;
                meshRenderer.shadowCastingMode = ShadowCastingMode.On;
            }
        }
    }
}

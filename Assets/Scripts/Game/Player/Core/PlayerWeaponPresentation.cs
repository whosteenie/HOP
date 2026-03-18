using System.Collections.Generic;
using Diagnostics;
using Events;
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
                        DevLog.LogError("[PlayerController] PlayerRenderer not found!");
                        return;
                    }

                    _player.PlayerRenderer.SetWorldWeaponRenderersEnabled(true);
                }

                if(updateHud) {
                    EventBus.Publish(new UpdateAmmoEvent(currentWeapon.currentAmmo, currentWeapon.GetMagSize()));
                    EventBus.Publish(new UpdateHealthEvent(_player.NetHealth.Value, 100f));
                    EventBus.Publish(new UpdateMultiplierEvent(1f, Game.Weapon.Core.Weapon.MaxDamageMultiplier));
                }
            }

            if(switchToWeapon0) {
                _player.PlayerInputController.SwitchWeapon(0);
            }
        }

        public void UpdateAllFpArmTagGlow(bool isTagged) {
            var weaponManager = _player.WeaponManager;
            var visualController = _player.VisualController;
            if(!_player.IsOwner || weaponManager == null || visualController == null) return;

            foreach(var fpWeapon in weaponManager.FpWeaponInstancesRef) {
                if(fpWeapon == null) continue;
                visualController.UpdateFpArmTagGlow(isTagged, fpWeapon);
            }
        }

        public void SetCurrentFpWeaponVisible(bool visible) {
            var fpWeapon = GetCurrentFpWeapon();
            if(fpWeapon == null || _player.PlayerRenderer == null) return;
            _player.PlayerRenderer.SetFpWeaponRenderersEnabled(visible, fpWeapon);
        }

        public void HideFpVisualsForDisconnectTransition() {
            var weaponManager = _player.WeaponManager;
            if(!_player.IsOwner || weaponManager == null) return;

            foreach(var fpWeapon in weaponManager.FpWeaponInstancesRef) {
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

        public GameObject GetCurrentFpWeapon() {
            var weaponManager = _player.WeaponManager;
            return weaponManager != null ? weaponManager.GetCurrentFpWeapon() : null;
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
                DevLog.LogError("[PlayerController] PlayerRenderer not found! Cannot enable world weapon renderers.");
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

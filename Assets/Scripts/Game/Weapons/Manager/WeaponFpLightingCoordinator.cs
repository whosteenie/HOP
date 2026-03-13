using UnityEngine;

namespace Game.Weapons.Manager {
    internal sealed class WeaponFpLightingCoordinator {
        private readonly WeaponManager _root;

        public WeaponFpLightingCoordinator(WeaponManager root) {
            _root = root;
        }

        public void EnsureFpWeaponLightingRig() {
            if(!_root.EnableFpWeaponLightRig || !_root.IsOwner || _root.WeaponCameraRef == null) {
                SetFpWeaponLightingRigActive(false);
                return;
            }

            var targetLayer = LayerMask.NameToLayer("Weapon");
            if(targetLayer < 0) {
                if(!_root.LoggedMissingWeaponLayer) {
                    Debug.LogWarning("[WeaponManager] FP light rig requires a 'Weapon' layer.");
                    _root.LoggedMissingWeaponLayer = true;
                }

                SetFpWeaponLightingRigActive(false);
                return;
            }

            EnsureFpWeaponLightingRigRoot(targetLayer);
            if(_root.FpLightRigRoot == null) return;

            _root.FpKeyLight = EnsureFpWeaponLight(_root.FpLightRigRoot, WeaponManager.FpKeyLightNameConst, targetLayer);
            _root.FpFillLight = EnsureFpWeaponLight(_root.FpLightRigRoot, WeaponManager.FpFillLightNameConst, targetLayer);

            ConfigureFpWeaponLight(
                _root.FpKeyLight,
                _root.FpKeyLightLocalPosition,
                _root.FpKeyLightLocalEulerAngles,
                _root.FpKeyLightColor,
                _root.FpKeyLightIntensity,
                _root.FpKeyLightRange,
                _root.FpKeyLightSpotAngle,
                targetLayer
            );

            ConfigureFpWeaponLight(
                _root.FpFillLight,
                _root.FpFillLightLocalPosition,
                _root.FpFillLightLocalEulerAngles,
                _root.FpFillLightColor,
                _root.FpFillLightIntensity,
                _root.FpFillLightRange,
                _root.FpFillLightSpotAngle,
                targetLayer
            );

            SetFpWeaponLightingRigActive(true);
        }

        private void EnsureFpWeaponLightingRigRoot(int targetLayer) {
            if(_root.WeaponCameraRef == null) {
                _root.FpLightRigRoot = null;
                return;
            }

            var cameraTransform = _root.WeaponCameraRef.transform;
            if(_root.FpLightRigRoot != null && _root.FpLightRigRoot.parent == cameraTransform) {
                _root.FpLightRigRoot.localPosition = Vector3.zero;
                _root.FpLightRigRoot.localRotation = Quaternion.identity;
                _root.FpLightRigRoot.gameObject.layer = targetLayer;
                return;
            }

            var existing = FindDirectChildByName(cameraTransform, WeaponManager.FpLightRigRootNameConst);
            if(existing != null) {
                _root.FpLightRigRoot = existing;
            } else {
                var rootGo = new GameObject(WeaponManager.FpLightRigRootNameConst);
                _root.FpLightRigRoot = rootGo.transform;
                _root.FpLightRigRoot.SetParent(cameraTransform, false);
            }

            _root.FpLightRigRoot.localPosition = Vector3.zero;
            _root.FpLightRigRoot.localRotation = Quaternion.identity;
            _root.FpLightRigRoot.gameObject.layer = targetLayer;
        }

        private static Light EnsureFpWeaponLight(Transform parent, string lightName, int targetLayer) {
            if(parent == null) return null;

            var child = FindDirectChildByName(parent, lightName);
            Light lightComponent;
            if(child != null) {
                lightComponent = child.GetComponent<Light>();
                if(lightComponent == null) {
                    lightComponent = child.gameObject.AddComponent<Light>();
                }
            } else {
                var go = new GameObject(lightName);
                go.transform.SetParent(parent, false);
                go.layer = targetLayer;
                lightComponent = go.AddComponent<Light>();
            }

            return lightComponent;
        }

        private static Transform FindDirectChildByName(Transform parent, string childName) {
            if(parent == null || string.IsNullOrEmpty(childName)) return null;
            var count = parent.childCount;
            for(var i = 0; i < count; i++) {
                var child = parent.GetChild(i);
                if(child != null && child.name == childName) {
                    return child;
                }
            }

            return null;
        }

        private static void ConfigureFpWeaponLight(
            Light lightComponent,
            Vector3 localPosition,
            Vector3 localEulerAngles,
            Color color,
            float intensity,
            float range,
            float spotAngle,
            int targetLayer) {
            if(lightComponent == null) return;

            var transform = lightComponent.transform;
            transform.localPosition = localPosition;
            transform.localEulerAngles = localEulerAngles;

            lightComponent.type = LightType.Spot;
            lightComponent.shadows = LightShadows.None;
            lightComponent.color = color;
            lightComponent.intensity = Mathf.Max(0f, intensity);
            lightComponent.range = Mathf.Max(0.1f, range);
            lightComponent.spotAngle = Mathf.Clamp(spotAngle, 1f, 179f);
            lightComponent.cullingMask = 1 << targetLayer;
            lightComponent.renderMode = LightRenderMode.Auto;
            lightComponent.enabled = true;

            lightComponent.gameObject.layer = targetLayer;
        }

        private void SetFpWeaponLightingRigActive(bool active) {
            if(_root.FpLightRigRoot != null && _root.FpLightRigRoot.gameObject.activeSelf != active) {
                _root.FpLightRigRoot.gameObject.SetActive(active);
            }
        }
    }
}

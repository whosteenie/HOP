using UnityEngine;

namespace Game.Weapons {
    public partial class WeaponManager {
        [Header("FP Weapon Lighting")]
        [SerializeField] private bool enableFpWeaponLightRig = true;
        [SerializeField] private Vector3 fpKeyLightLocalPosition = new(0.08f, 0.06f, -0.04f);
        [SerializeField] private Vector3 fpKeyLightLocalEulerAngles = new(12f, -15f, 0f);
        [SerializeField, Min(0f)] private float fpKeyLightIntensity = 1f;
        [SerializeField, Min(0.1f)] private float fpKeyLightRange = 3.5f;
        [SerializeField, Range(1f, 179f)] private float fpKeyLightSpotAngle = 75f;
        [SerializeField] private Color fpKeyLightColor = new(1f, 0.97f, 0.92f, 1f);

        [SerializeField] private Vector3 fpFillLightLocalPosition = new(-0.08f, -0.04f, -0.02f);
        [SerializeField] private Vector3 fpFillLightLocalEulerAngles = new(16f, 18f, 0f);
        [SerializeField, Min(0f)] private float fpFillLightIntensity = 0.35f;
        [SerializeField, Min(0.1f)] private float fpFillLightRange = 3f;
        [SerializeField, Range(1f, 179f)] private float fpFillLightSpotAngle = 90f;
        [SerializeField] private Color fpFillLightColor = new(0.92f, 0.96f, 1f, 1f);

        private const string FpLightRigRootName = "FP_LightRig";
        private const string FpKeyLightName = "FP_Key";
        private const string FpFillLightName = "FP_Fill";

        private Transform _fpLightRigRoot;
        private Light _fpKeyLight;
        private Light _fpFillLight;
        private bool _loggedMissingWeaponLayer;

        private void EnsureFpWeaponLightingRig() {
            if(!enableFpWeaponLightRig || !IsOwner || _weaponCamera == null) {
                SetFpWeaponLightingRigActive(false);
                return;
            }

            var targetLayer = LayerMask.NameToLayer("Weapon");
            if(targetLayer < 0) {
                if(!_loggedMissingWeaponLayer) {
                    Debug.LogWarning("[WeaponManager] FP light rig requires a 'Weapon' layer.");
                    _loggedMissingWeaponLayer = true;
                }

                SetFpWeaponLightingRigActive(false);
                return;
            }

            EnsureFpWeaponLightingRigRoot(targetLayer);
            if(_fpLightRigRoot == null) return;

            _fpKeyLight = EnsureFpWeaponLight(_fpLightRigRoot, FpKeyLightName, targetLayer);
            _fpFillLight = EnsureFpWeaponLight(_fpLightRigRoot, FpFillLightName, targetLayer);

            ConfigureFpWeaponLight(
                _fpKeyLight,
                fpKeyLightLocalPosition,
                fpKeyLightLocalEulerAngles,
                fpKeyLightColor,
                fpKeyLightIntensity,
                fpKeyLightRange,
                fpKeyLightSpotAngle,
                targetLayer
            );

            ConfigureFpWeaponLight(
                _fpFillLight,
                fpFillLightLocalPosition,
                fpFillLightLocalEulerAngles,
                fpFillLightColor,
                fpFillLightIntensity,
                fpFillLightRange,
                fpFillLightSpotAngle,
                targetLayer
            );

            SetFpWeaponLightingRigActive(true);
        }

        private void EnsureFpWeaponLightingRigRoot(int targetLayer) {
            if(_weaponCamera == null) {
                _fpLightRigRoot = null;
                return;
            }

            var cameraTransform = _weaponCamera.transform;
            if(_fpLightRigRoot != null && _fpLightRigRoot.parent == cameraTransform) {
                _fpLightRigRoot.localPosition = Vector3.zero;
                _fpLightRigRoot.localRotation = Quaternion.identity;
                _fpLightRigRoot.gameObject.layer = targetLayer;
                return;
            }

            var existing = cameraTransform.Find(FpLightRigRootName);
            if(existing != null) {
                _fpLightRigRoot = existing;
            } else {
                var rootGo = new GameObject(FpLightRigRootName);
                _fpLightRigRoot = rootGo.transform;
                _fpLightRigRoot.SetParent(cameraTransform, false);
            }

            _fpLightRigRoot.localPosition = Vector3.zero;
            _fpLightRigRoot.localRotation = Quaternion.identity;
            _fpLightRigRoot.gameObject.layer = targetLayer;
        }

        private static Light EnsureFpWeaponLight(Transform parent, string lightName, int targetLayer) {
            if(parent == null) return null;

            var child = parent.Find(lightName);
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
            lightComponent.lightmapBakeType = LightmapBakeType.Realtime;
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
            if(_fpLightRigRoot != null && _fpLightRigRoot.gameObject.activeSelf != active) {
                _fpLightRigRoot.gameObject.SetActive(active);
            }
        }
    }
}

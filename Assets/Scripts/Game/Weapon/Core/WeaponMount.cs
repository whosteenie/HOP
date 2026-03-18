using Diagnostics;
using Game.Match;
using Game.Weapon.Kinemation;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Game.Weapon.Core {
    internal sealed class WeaponMount {
        private readonly Weapon _weapon;
        private Camera _cachedMainSceneCamera;

        public WeaponMount(Weapon weapon) {
            _weapon = weapon;
        }

        public void SyncKinemationLocomotion() {
            var kinemationDriver = _weapon.KinDriver;
            var ownerContext = _weapon.OwnerContext;
            if(kinemationDriver == null || ownerContext is not { IsOwner: true }) {
                return;
            }

            var isSliding = ownerContext.IsSliding;
            var isWallRunning = ownerContext.IsWallRunning;
            var isPreMatch = MatchTimerManager.Instance != null && MatchTimerManager.Instance.IsPreMatch;
            var isPostMatch = _weapon.Manager != null && _weapon.Manager.IsPostMatchFlowActive;

            var moveInput = ownerContext.MoveInput;
            var sprintInput = ownerContext.SprintInput;
            var treatedGrounded = ownerContext.IsGrounded || isWallRunning;

            if(isWallRunning) {
                var horizontalSpeed = ownerContext.HorizontalVelocity.magnitude;
                var maxSpeed = Mathf.Max(ownerContext.MaxSpeed, 0.01f);
                var normalizedWallRunSpeed = Mathf.Clamp01(horizontalSpeed / maxSpeed);
                moveInput = new Vector2(0f, Mathf.Max(0.35f, normalizedWallRunSpeed));
                sprintInput = horizontalSpeed >= 8f;
            }

            if(isSliding) {
                moveInput = Vector2.zero;
                sprintInput = false;
            }

            if(isPreMatch || isPostMatch) {
                moveInput = Vector2.zero;
                sprintInput = false;
                treatedGrounded = true;
            }

            var lookPitch = ownerContext.CurrentPitch;

            kinemationDriver.SyncLocomotion(
                moveInput,
                sprintInput,
                tacticalSprinting: false,
                isGrounded: treatedGrounded,
                lookPitchDegrees: lookPitch
            );
        }

        public void TryPrewarmKinemationMuzzleIfNeeded() {
            if(_weapon.HasPrewarmedKinemationMuzzleForCurrentWeapon) return;
            if(_weapon.KinDriver == null) return;
            if(_weapon.OwnerContext is not { IsOwner: true }) return;
            _weapon.PrewarmKinemationMuzzleFxInternal();
        }

        public void SwitchToWeapon(WeaponData newWeaponData, GameObject fpWeaponInstance,
            GameObject worldWeaponInstance, int restoredAmmo, int magCapacity) {
            if(_weapon.IsReloadInProgress) {
                _weapon.CancelReloadInternal();
            }

            _weapon.ClearKinemationMuzzleFxInternal();

            _weapon.CurrentWeaponData = newWeaponData;
            _weapon.CurrentFpWeaponInstance = fpWeaponInstance;
            _weapon.CurrentWorldWeaponInstance = worldWeaponInstance;
            _weapon.CurrentWorldWeaponBinding = worldWeaponInstance != null
                ? worldWeaponInstance.GetComponent<WorldWeaponBinding>()
                : null;
            _weapon.CurrentMagCapacity = magCapacity;
            _weapon.KinDriver = fpWeaponInstance != null
                ? fpWeaponInstance.GetComponent<KinFpWeaponDriver>()
                : null;

            if(_weapon.KinDriver != null) {
                var fpLayer = _weapon.OwnerContext is { IsOwner: true }
                    ? LayerMask.NameToLayer("Weapon")
                    : LayerMask.NameToLayer("Masked");
                _weapon.KinDriver.InitializeIfNeeded(fpLayer);
                _weapon.KinDriver.ClearPendingWeaponSoundEvents();
            }

            _weapon.FpMuzzleTransform = _weapon.KinDriver != null
                ? _weapon.KinDriver.GetMuzzleTransform()
                : null;
            _weapon.WorldMuzzleTransform = null;
            _weapon.WorldMuzzleLight = null;
            if(_weapon.CurrentWorldWeaponBinding != null &&
               _weapon.CurrentWorldWeaponBinding.TryGetRuntimeReferences(
                   out var boundWorldMuzzle,
                   out var boundWorldMuzzleLight)) {
                _weapon.WorldMuzzleTransform = boundWorldMuzzle;
                _weapon.WorldMuzzleLight = boundWorldMuzzleLight;
            }

            _weapon.HasPrewarmedKinemationMuzzleForCurrentWeapon = false;

            if(_weapon.KinDriver != null) {
                _weapon.PrewarmKinemationMuzzleFxInternal();
            }

            _weapon.CurrentAmmo = Mathf.Clamp(restoredAmmo, 0, _weapon.GetMagCapacityInternal());
            if(_weapon.KinDriver != null) {
                _weapon.KinDriver.SyncActiveAmmo(_weapon.CurrentAmmo);
            }

            _weapon.Reloading = false;
            _weapon.AutoReloadArmed = false;
            _weapon.ReloadExpectedCompleteTime = float.PositiveInfinity;

            _weapon.FpMuzzleLight = null;
            if(fpWeaponInstance != null && _weapon.KinDriver != null) {
                var fpLight = fpWeaponInstance.GetComponentInChildren<Light>(true);
                _weapon.FpMuzzleLight = fpLight != null ? fpLight.gameObject : null;
                if(_weapon.FpMuzzleLight != null) {
                    _weapon.FpMuzzleLight.SetActive(false);
                }
            }

            if(_weapon.WorldMuzzleLight != null) {
                _weapon.WorldMuzzleLight.SetActive(false);
            }

            if(newWeaponData != null && newWeaponData.bulletTrail != null) {
                _weapon.InitializeTrailPoolInternal();
            }

            if(newWeaponData != null) {
                _weapon.PublishAmmoToHudInternal();
            }
        }

        public bool TryGetRemoteWorldMuzzlePosition(out Vector3 muzzlePosition) {
            muzzlePosition = default;
            if(!TryGetStrictWorldMuzzleTransform(out var muzzleTransform, "TryGetRemoteWorldMuzzlePosition")) {
                return false;
            }

            muzzlePosition = muzzleTransform.position;
            return true;
        }

        private bool TryGetMuzzlePosition(out Vector3 muzzlePosition) {
            muzzlePosition = default;
            if(_weapon.CurrentWeaponData == null) return false;

            var ownerContext = _weapon.OwnerContext;
            switch(ownerContext) {
                case { IsOwner: true, IsSniperOverlayActive: true }: {
                    var fpCameraTransform = ownerContext.FpCameraTransform;
                    if(fpCameraTransform == null) return false;

                    muzzlePosition = fpCameraTransform.TransformPoint(ownerContext.SniperMuzzleCameraOffset);
                    return true;
                }
                case { IsOwner: true }: {
                    if(!TryGetRequiredOwnerMuzzleTransform(out var ownerMuzzleTransform, "TryGetMuzzlePosition")) {
                        return false;
                    }

                    muzzlePosition = ownerMuzzleTransform.position;
                    return true;
                }
            }

            if(!TryGetStrictWorldMuzzleTransform(out var remoteWorldMuzzleTransform, "TryGetMuzzlePosition")) {
                return false;
            }

            muzzlePosition = remoteWorldMuzzleTransform.position;
            return true;
        }

        private bool TryGetMuzzlePositionFromCamera(out Vector3 muzzlePosition) {
            muzzlePosition = default;
            var ownerContext = _weapon.OwnerContext;
            if(ownerContext is not { IsOwner: true } || _weapon.CurrentWeaponData == null) {
                return TryGetMuzzlePosition(out muzzlePosition);
            }

            if(!ownerContext.IsSniperOverlayActive) {
                if(!TryGetRequiredOwnerMuzzleTransform(out var muzzleTransform, "TryGetMuzzlePositionFromCamera")) {
                    return false;
                }

                muzzlePosition = muzzleTransform.position;
                return true;
            }

            var fpCameraTransform = ownerContext.FpCameraTransform;
            if(fpCameraTransform == null) {
                return false;
            }

            muzzlePosition = fpCameraTransform.TransformPoint(ownerContext.SniperMuzzleCameraOffset);
            return true;
        }

        public bool TryGetOwnerTracerStartPosition(out Vector3 tracerStartPosition) {
            tracerStartPosition = default;
            if(!TryGetMuzzlePositionFromCamera(out var muzzlePosition)) {
                return false;
            }

            tracerStartPosition = muzzlePosition;
            TryRemapOwnerWeaponCameraPointToMainCamera(muzzlePosition, out tracerStartPosition);
            return true;
        }

        public bool TryGetRequiredOwnerMuzzleTransform(out Transform muzzleTransform, string context, bool logErrors = true) {
            muzzleTransform = null;

            var ownerContext = _weapon.OwnerContext;
            if(ownerContext is not { IsOwner: true }) {
                if(logErrors) {
                    DevLog.LogError(
                        $"[Weapon][MuzzleStrict][{context}] Owner-only muzzle query called on non-owner.",
                        _weapon);
                }
                return false;
            }

            var isPostMatch = _weapon.Manager != null && _weapon.Manager.IsPostMatchFlowActive;
            return isPostMatch
                ? TryGetStrictWorldMuzzleTransform(out muzzleTransform, context, allowOwnerInstance: true,
                    logErrors: logErrors)
                : TryGetStrictFpMuzzleTransform(out muzzleTransform, context, logErrors: logErrors);
        }

        public bool TryGetStrictWorldMuzzleTransform(out Transform muzzleTransform, string context,
            bool allowOwnerInstance = false, bool logErrors = true) {
            muzzleTransform = null;

            var ownerContext = _weapon.OwnerContext;
            if(ownerContext is { IsOwner: true } && !allowOwnerInstance) {
                if(logErrors) {
                    DevLog.LogError(
                        $"[Weapon][RemoteMuzzleStrict][{context}] Called on owner instance. " +
                        $"weapon={(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")}",
                        _weapon);
                }
                return false;
            }

            if(_weapon.CurrentWorldWeaponInstance == null) {
                if(logErrors) {
                    DevLog.LogError(
                        $"[Weapon][RemoteMuzzleStrict][{context}] Missing current world weapon instance. " +
                        $"weapon={(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")}",
                        _weapon);
                }
                return false;
            }

            if(_weapon.CurrentWorldWeaponBinding == null ||
               _weapon.CurrentWorldWeaponBinding.gameObject != _weapon.CurrentWorldWeaponInstance) {
                _weapon.CurrentWorldWeaponBinding =
                    _weapon.CurrentWorldWeaponInstance.GetComponent<WorldWeaponBinding>();
            }

            if(_weapon.CurrentWorldWeaponBinding == null) {
                if(logErrors) {
                    DevLog.LogError(
                        $"[Weapon][RemoteMuzzleStrict][{context}] Missing WorldWeaponBinding on world weapon. " +
                        $"weapon={(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")} " +
                        $"worldWeapon={_weapon.CurrentWorldWeaponInstance.name}",
                        _weapon);
                }
                return false;
            }

            if(!_weapon.CurrentWorldWeaponInstance.activeInHierarchy) {
                if(logErrors) {
                    DevLog.LogError(
                        $"[Weapon][RemoteMuzzleStrict][{context}] World weapon inactive. " +
                        $"weapon={(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")} " +
                        $"worldWeapon={_weapon.CurrentWorldWeaponInstance.name}",
                        _weapon);
                }
                return false;
            }

            if(!_weapon.CurrentWorldWeaponBinding.TryGetRuntimeReferences(
                   out var worldMuzzleTransform,
                   out var boundMuzzleLight)) {
                if(logErrors) {
                    DevLog.LogError(
                        $"[Weapon][RemoteMuzzleStrict][{context}] Assigned muzzle reference is null. " +
                        $"weapon={(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")} " +
                        $"worldWeapon={_weapon.CurrentWorldWeaponInstance.name}",
                        _weapon);
                }
                return false;
            }

            _weapon.WorldMuzzleTransform = worldMuzzleTransform;
            if(boundMuzzleLight != null) {
                _weapon.WorldMuzzleLight = boundMuzzleLight;
            }

            if(!_weapon.WorldMuzzleTransform.gameObject.activeInHierarchy) {
                if(logErrors) {
                    DevLog.LogError(
                        $"[Weapon][RemoteMuzzleStrict][{context}] Muzzle transform inactive. " +
                        $"weapon={(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")} " +
                        $"worldWeapon={_weapon.CurrentWorldWeaponInstance.name} " +
                        $"muzzlePath={GetTransformPath(_weapon.WorldMuzzleTransform)}",
                        _weapon);
                }
                return false;
            }

            muzzleTransform = _weapon.WorldMuzzleTransform;
            return true;
        }

        private bool TryGetStrictFpMuzzleTransform(out Transform muzzleTransform, string context, bool logErrors = true) {
            muzzleTransform = null;

            var ownerContext = _weapon.OwnerContext;
            if(ownerContext is not { IsOwner: true }) {
                if(logErrors) {
                    DevLog.LogError($"[Weapon][MuzzleStrict][{context}] FP muzzle requested by non-owner.", _weapon);
                }
                return false;
            }

            if(_weapon.KinDriver == null) {
                if(logErrors) {
                    DevLog.LogError(
                        $"[Weapon][MuzzleStrict][{context}] Missing KinemationFpWeaponDriver for owner weapon " +
                        $"'{(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")}'.",
                        _weapon);
                }
                return false;
            }

            _weapon.FpMuzzleTransform = _weapon.KinDriver.GetMuzzleTransform();
            if(_weapon.FpMuzzleTransform == null) {
                if(logErrors) {
                    DevLog.LogError(
                        $"[Weapon][MuzzleStrict][{context}] FP muzzle transform missing for weapon " +
                        $"'{(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")}'.",
                        _weapon);
                }
                return false;
            }

            if(!_weapon.FpMuzzleTransform.gameObject.activeInHierarchy) {
                if(logErrors) {
                    DevLog.LogError(
                        $"[Weapon][MuzzleStrict][{context}] FP muzzle transform inactive. " +
                        $"weapon={(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")} " +
                        $"muzzlePath={GetTransformPath(_weapon.FpMuzzleTransform)}",
                        _weapon);
                }
                return false;
            }

            muzzleTransform = _weapon.FpMuzzleTransform;
            return true;
        }

        private void TryRemapOwnerWeaponCameraPointToMainCamera(Vector3 sourcePoint, out Vector3 remappedPoint) {
            remappedPoint = sourcePoint;
            var ownerContext = _weapon.OwnerContext;
            if(ownerContext == null) return;
            if(!ownerContext.IsOwner) return;
            if(_weapon.Manager != null && _weapon.Manager.IsPostMatchFlowActive) return;
            if(ownerContext.IsSniperOverlayActive) return;

            var weaponCamera = _weapon.Manager != null ? _weapon.Manager.WeaponCameraRef : null;
            if(weaponCamera == null) return;
            if(!TryResolveMainSceneCamera(weaponCamera, out var mainSceneCamera)) return;
            if(weaponCamera == mainSceneCamera) return;

            var viewportInWeaponCamera = weaponCamera.WorldToViewportPoint(sourcePoint);
            if(viewportInWeaponCamera.z <= 0f) return;

            var transformCamera = mainSceneCamera.transform;
            var preferredDepth = Vector3.Dot(sourcePoint - transformCamera.position, transformCamera.forward);
            var remapDepth = Mathf.Max(mainSceneCamera.nearClipPlane + 0.02f, preferredDepth);
            remappedPoint = mainSceneCamera.ViewportToWorldPoint(new Vector3(
                viewportInWeaponCamera.x,
                viewportInWeaponCamera.y,
                remapDepth));
        }

        private bool TryResolveMainSceneCamera(Camera weaponCamera, out Camera mainSceneCamera) {
            mainSceneCamera = null;

            if(IsUsableMainSceneCamera(_cachedMainSceneCamera, weaponCamera)) {
                mainSceneCamera = _cachedMainSceneCamera;
                return true;
            }

            _cachedMainSceneCamera = FindCandidateMainSceneCamera(weaponCamera);
            mainSceneCamera = _cachedMainSceneCamera;

            if(mainSceneCamera != weaponCamera) return mainSceneCamera != null;
            _cachedMainSceneCamera = null;
            mainSceneCamera = null;

            return mainSceneCamera != null;
        }

        private static bool IsUsableMainSceneCamera(Camera camera, Camera weaponCamera) {
            if(camera == null || camera == weaponCamera || !camera.isActiveAndEnabled) {
                return false;
            }

            if(camera.CompareTag("MainCamera")) {
                return true;
            }

            var cameraData = camera.GetUniversalAdditionalCameraData();
            return cameraData != null && cameraData.renderType == CameraRenderType.Base;
        }

        private static Camera FindCandidateMainSceneCamera(Camera weaponCamera) {
            var cameraCount = Camera.allCamerasCount;
            if(cameraCount <= 0) {
                return null;
            }

            var cameras = new Camera[cameraCount];
            Camera.GetAllCameras(cameras);

            Camera fallback = null;
            foreach(var sceneCamera in cameras) {
                if(sceneCamera == null || sceneCamera == weaponCamera || !sceneCamera.isActiveAndEnabled) continue;

                if(sceneCamera.CompareTag("MainCamera")) {
                    return sceneCamera;
                }

                var cameraData = sceneCamera.GetUniversalAdditionalCameraData();
                if(cameraData != null && cameraData.renderType == CameraRenderType.Base && fallback == null) {
                    fallback = sceneCamera;
                }
            }

            return fallback;
        }

        private static string GetTransformPath(Transform transform) {
            if(transform == null) return "(none)";

            var path = transform.name;
            var current = transform.parent;
            while(current != null) {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }
    }
}

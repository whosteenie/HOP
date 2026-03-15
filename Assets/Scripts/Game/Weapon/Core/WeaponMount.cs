using Game.Menu;
using Game.Weapon.Kinemation;
using Game.Weapon.World;
using UnityEngine;

namespace Game.Weapon.Core {
    internal sealed class WeaponMount {
        private readonly Weapon _weapon;

        public WeaponMount(Weapon weapon) {
            _weapon = weapon;
        }

        public void SyncKinemationLocomotion() {
            var kinemationDriver = _weapon.KinDriver;
            var playerController = _weapon.PlayerController;
            if(kinemationDriver == null || playerController == null || !playerController.IsOwner) {
                return;
            }

            var movementController = playerController.MovementController;
            var wallRunController = playerController.WallRunController;
            var isSliding = movementController != null && movementController.IsSliding;
            var isWallRunning = wallRunController != null && wallRunController.IsWallRunning;
            var isPreMatch = GameMenuManager.Instance != null && GameMenuManager.IsPreMatch;
            var isPostMatch = GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch;

            var moveInput = playerController.moveInput;
            var sprintInput = playerController.sprintInput;
            var treatedGrounded = playerController.IsGrounded || isWallRunning;

            if(isWallRunning) {
                var horizontalSpeed = playerController.GetHorizontalVelocity().magnitude;
                var maxSpeed = Mathf.Max(playerController.GetMaxSpeed(), 0.01f);
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

            var lookPitch = 0f;
            var lookController = playerController.LookController;
            if(lookController != null) {
                lookPitch = lookController.CurrentPitch;
            }

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
            if(_weapon.PlayerController == null || !_weapon.PlayerController.IsOwner) return;
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
                var fpLayer = _weapon.PlayerController != null && _weapon.PlayerController.IsOwner
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

            var playerController = _weapon.PlayerController;
            if(playerController != null &&
               playerController.IsOwner &&
               playerController.PlayerInput != null &&
               playerController.PlayerInput.IsSniperOverlayActive) {
                var fpCameraTransform = playerController.FpCameraTransform;
                if(fpCameraTransform == null) return false;

                muzzlePosition =
                    fpCameraTransform.TransformPoint(playerController.PlayerInput.SniperMuzzleCameraOffset);
                return true;
            }

            if(playerController != null && playerController.IsOwner) {
                if(!TryGetRequiredOwnerMuzzleTransform(out var ownerMuzzleTransform, "TryGetMuzzlePosition")) {
                    return false;
                }

                muzzlePosition = ownerMuzzleTransform.position;
                return true;
            }

            if(!TryGetStrictWorldMuzzleTransform(out var remoteWorldMuzzleTransform, "TryGetMuzzlePosition")) {
                return false;
            }

            muzzlePosition = remoteWorldMuzzleTransform.position;
            return true;
        }

        private bool TryGetMuzzlePositionFromCamera(out Vector3 muzzlePosition) {
            muzzlePosition = default;
            var playerController = _weapon.PlayerController;
            if(playerController == null || !playerController.IsOwner || _weapon.CurrentWeaponData == null) {
                return TryGetMuzzlePosition(out muzzlePosition);
            }

            if(playerController.PlayerInput == null || !playerController.PlayerInput.IsSniperOverlayActive) {
                if(!TryGetRequiredOwnerMuzzleTransform(out var muzzleTransform, "TryGetMuzzlePositionFromCamera")) {
                    return false;
                }

                muzzlePosition = muzzleTransform.position;
                return true;
            }

            var fpCameraTransform = playerController.FpCameraTransform;
            if(fpCameraTransform == null) {
                return false;
            }

            muzzlePosition = fpCameraTransform.TransformPoint(playerController.PlayerInput.SniperMuzzleCameraOffset);
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

            var playerController = _weapon.PlayerController;
            if(playerController == null || !playerController.IsOwner) {
                if(logErrors) {
                    Debug.LogError(
                        $"[Weapon][MuzzleStrict][{context}] Owner-only muzzle query called on non-owner.",
                        _weapon);
                }
                return false;
            }

            var isPostMatch = GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch;
            return isPostMatch
                ? TryGetStrictWorldMuzzleTransform(out muzzleTransform, context, allowOwnerInstance: true,
                    logErrors: logErrors)
                : TryGetStrictFpMuzzleTransform(out muzzleTransform, context, logErrors: logErrors);
        }

        public bool TryGetStrictWorldMuzzleTransform(out Transform muzzleTransform, string context,
            bool allowOwnerInstance = false, bool logErrors = true) {
            muzzleTransform = null;

            var playerController = _weapon.PlayerController;
            if(playerController != null && playerController.IsOwner && !allowOwnerInstance) {
                if(logErrors) {
                    Debug.LogError(
                        $"[Weapon][RemoteMuzzleStrict][{context}] Called on owner instance. " +
                        $"weapon={(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")}",
                        _weapon);
                }
                return false;
            }

            if(_weapon.CurrentWorldWeaponInstance == null) {
                if(logErrors) {
                    Debug.LogError(
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
                    Debug.LogError(
                        $"[Weapon][RemoteMuzzleStrict][{context}] Missing WorldWeaponBinding on world weapon. " +
                        $"weapon={(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")} " +
                        $"worldWeapon={_weapon.CurrentWorldWeaponInstance.name}",
                        _weapon);
                }
                return false;
            }

            if(!_weapon.CurrentWorldWeaponInstance.activeInHierarchy) {
                if(logErrors) {
                    Debug.LogError(
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
                    Debug.LogError(
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
                    Debug.LogError(
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

            var playerController = _weapon.PlayerController;
            if(playerController == null || !playerController.IsOwner) {
                if(logErrors) {
                    Debug.LogError($"[Weapon][MuzzleStrict][{context}] FP muzzle requested by non-owner.", _weapon);
                }
                return false;
            }

            if(_weapon.KinDriver == null) {
                if(logErrors) {
                    Debug.LogError(
                        $"[Weapon][MuzzleStrict][{context}] Missing KinemationFpWeaponDriver for owner weapon " +
                        $"'{(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")}'.",
                        _weapon);
                }
                return false;
            }

            _weapon.FpMuzzleTransform = _weapon.KinDriver.GetMuzzleTransform();
            if(_weapon.FpMuzzleTransform == null) {
                if(logErrors) {
                    Debug.LogError(
                        $"[Weapon][MuzzleStrict][{context}] FP muzzle transform missing for weapon " +
                        $"'{(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")}'.",
                        _weapon);
                }
                return false;
            }

            if(!_weapon.FpMuzzleTransform.gameObject.activeInHierarchy) {
                if(logErrors) {
                    Debug.LogError(
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
            var playerController = _weapon.PlayerController;
            if(playerController == null) return;
            if(!playerController.IsOwner) return;
            if(GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch) return;
            if(playerController.PlayerInput != null && playerController.PlayerInput.IsSniperOverlayActive) return;

            var weaponCamera = playerController.WeaponCamera;
            var mainSceneCamera = Camera.main;
            if(weaponCamera == null || mainSceneCamera == null) return;
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

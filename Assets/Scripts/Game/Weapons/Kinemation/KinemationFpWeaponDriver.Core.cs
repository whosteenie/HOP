using System.Collections.Generic;
using Game.Weapons.Kinemation;
using Game.Weapons.Manager;
using KINEMATION.FPSAnimationPack.Scripts.Camera;
using KINEMATION.FPSAnimationPack.Scripts.Player;
using KINEMATION.FPSAnimationPack.Scripts.Sounds;
using Network.Events;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Weapons {
    public sealed partial class KinemationFpWeaponDriver {
        private void OnEnable() {
            EventBus.Subscribe<GrappleStartedEvent>(OnGrappleStarted);
            EventBus.Subscribe<GrappleAnimFirstFrameEvent>(OnGrappleAnimFirstFrame);
            EventBus.Subscribe<GrappleAnimHideEvent>(OnGrappleAnimHide);
            EventBus.Subscribe<GrappleEndedEvent>(OnGrappleEnded);
        }

        private void OnDisable() {
            EventBus.Unsubscribe<GrappleStartedEvent>(OnGrappleStarted);
            EventBus.Unsubscribe<GrappleAnimFirstFrameEvent>(OnGrappleAnimFirstFrame);
            EventBus.Unsubscribe<GrappleAnimHideEvent>(OnGrappleAnimHide);
            EventBus.Unsubscribe<GrappleEndedEvent>(OnGrappleEnded);
            ClearRuntimeGrappleClavicleOffset();
        }

        private void OnGrappleAnimHide(GrappleAnimHideEvent _) {
            ClearRuntimeGrappleClavicleOffset();
        }

        private void OnGrappleEnded(GrappleEndedEvent _) {
            ClearRuntimeGrappleClavicleOffset();
        }

        private void OnGrappleAnimFirstFrame(GrappleAnimFirstFrameEvent _) {
            if(!enableRuntimeGrappleClavicleOffset) return;
            if(_playerInstance == null || !_playerInstance.activeInHierarchy) return;
            CacheWristDebugBonesIfNeeded();
            if(_clavicleLeft == null && !TryFindChildByName(_playerInstance.transform, "clavicle_l", out _clavicleLeft)) {
                return;
            }
            var anchor = GetGrappleCalibrationAnchor();
            if(anchor == null) return;

            var idx = GetGrappleWeaponIndex();

            if(idx == 0) {
                if(!TryGetWeaponCameraTransform(out var cameraTransform)) return;
                sAkAnchorFrame1CameraLocal = cameraTransform.InverseTransformPoint(anchor.position);
                sHasAkAnchorFrame1CameraReference = true;
                return;
            }

            Vector3 resolvedLocalOffset;
            if(sHasAkAnchorFrame1CameraReference && TryGetWeaponCameraTransform(out var frameCameraTransform)) {
                var currentCameraLocal = frameCameraTransform.InverseTransformPoint(anchor.position);
                var cameraLocalOffset = sAkAnchorFrame1CameraLocal - currentCameraLocal;
                var worldOffset = frameCameraTransform.TransformDirection(cameraLocalOffset);
                resolvedLocalOffset = _clavicleLeft.parent != null
                    ? _clavicleLeft.parent.InverseTransformDirection(worldOffset)
                    : worldOffset;
            } else {
                // Alternate path: convert root-delta estimate into clavicle-parent local once at frame1.
                var worldOffset = transform.parent != null
                    ? transform.parent.TransformDirection(_runtimeGrappleClavicleOffset)
                    : _runtimeGrappleClavicleOffset;
                resolvedLocalOffset = _clavicleLeft.parent != null
                    ? _clavicleLeft.parent.InverseTransformDirection(worldOffset)
                    : worldOffset;
            }

            _runtimeGrappleClavicleOffset = resolvedLocalOffset;
            _isRuntimeGrappleClavicleOffsetActive = _runtimeGrappleClavicleOffset.sqrMagnitude > 0.00000001f;
        }

        private void OnGrappleStarted(GrappleStartedEvent grappleStartedEvent) {
            if(grappleStartedEvent is { UseFirstPersonAnimation: false }) {
                ClearRuntimeGrappleClavicleOffset();
                return;
            }

            ApplyGrappleWeaponIndex();
            if(enableRuntimeGrappleClavicleOffset) {
                PrepareRuntimeGrappleClavicleOffset();
                _isRuntimeGrappleClavicleOffsetActive = false;
            } else {
                _isRuntimeGrappleClavicleOffsetActive = false;
                _runtimeGrappleClavicleOffset = Vector3.zero;
                _runtimeGrappleOffsetWeaponIndex = 0;
            }
            if(_fpsAnimator != null) {
                _fpsAnimator.SetTrigger(GrappleHash);
            }
        }

        public void Configure(GameObject playerPrefab, GameObject fpWeaponPrefab, bool disableWeaponSounds,
            bool disablePlayerSounds, bool routeWeaponSoundEvents, bool syncLookPitch,
            bool syncInAirState, bool freezeAirLocomotion, bool forceWalkWhileSprinting,
            float sprintGaitValue,
            float equipUnlockNormalizedProgress) {
            fpsPlayerPrefab = playerPrefab;
            weaponPrefab = fpWeaponPrefab;
            disableKinemationWeaponSounds = disableWeaponSounds;
            disableKinemationPlayerSounds = disablePlayerSounds;
            routeWeaponSoundEventsToAudioService = routeWeaponSoundEvents;
            syncLookPitchWithPlayer = syncLookPitch;
            syncAirborneState = syncInAirState;
            freezeLocomotionInAir = freezeAirLocomotion;
            forceWalkAnimationWhileSprinting = forceWalkWhileSprinting;
            sprintWalkGaitValue = Mathf.Clamp(sprintGaitValue, 0f, 1.99f);
            equipUnlockNormalizedTime = Mathf.Clamp01(equipUnlockNormalizedProgress);
            _hasCachedWristDebugBones = false;
        }

        private void LogDrakeDebug(string message) {
            _ = message;
        }

        private void LogReloadSingleDebug(string message) {
            _ = message;
        }

        public bool InitializeIfNeeded(int renderLayer) {
            _renderLayer = renderLayer;
            _weaponManager = _weaponManager ? _weaponManager : GetComponentInParent<WeaponManager>();

            if(_playerInstance != null) {
                SetLayerRecursive(_playerInstance, _renderLayer);
                return _activeWeapon != null || TryCacheActiveWeapon();
            }

            if(fpsPlayerPrefab == null || weaponPrefab == null) {
                Debug.LogError("[KinemationFpWeaponDriver] Missing prefabs. Cannot initialize KINEMATION viewmodel.",
                    this);
                return false;
            }

            _playerInstance = Instantiate(fpsPlayerPrefab, transform, false);
            _playerInstance.name = "KinemationViewmodel";
            _playerInstance.SetActive(false);

            _fpsPlayer = _playerInstance.GetComponentInChildren<FPSPlayer>(true);
            if(_fpsPlayer == null) {
                Debug.LogError(
                    "[KinemationFpWeaponDriver] FPSPlayer component missing on KINEMATION player prefab hierarchy.",
                    this);
                Destroy(_playerInstance);
                _playerInstance = null;
                return false;
            }

            _fpsAnimator = _fpsPlayer.GetComponent<Animator>();
            DisableFpsPlayerMovementControl();

            BuildRuntimeSettings();
            EnsureDedicatedWeaponAudioSource();
            DisableUnneededComponents();
            SetLayerRecursive(_playerInstance, _renderLayer);
            DisableViewmodelShadows(_playerInstance);

            _playerInstance.SetActive(true);
            AttachReloadEventRelays();
            TryCacheActiveWeapon();

            // FPSPlayer creates its weapon instances in Start(), so cache may complete on a later frame.
            return _playerInstance != null;
        }


        public Transform GetMuzzleTransform() {
            TryCacheActiveWeapon();
            return _muzzleTransform;
        }

        /// <summary>
        /// Returns the FP left hand transform for grapple origin.
        /// Prefers a "GrappleOrigin" empty in the prefab, else ik_hand_l, else hand_l.
        /// </summary>
        public Transform GetGrappleOriginFpTransform() {
            CacheWristDebugBonesIfNeeded();
            if(_grappleOrigin != null) {
                return _grappleOrigin;
            }
            if(_ikHandLeft != null) {
                return _ikHandLeft;
            }
            return _wristDebugHandLeft != null ? _wristDebugHandLeft : null;
        }

        private bool TryGetWeaponCameraTransform(out Transform cameraTransform) {
            cameraTransform = null;
            var cameraComponent = GetComponentInParent<Camera>();
            if(cameraComponent == null) return false;
            cameraTransform = cameraComponent.transform;
            return cameraTransform != null;
        }

        private Transform GetGrappleCalibrationAnchor() {
            if(_ikHandLeft != null) return _ikHandLeft;
            if(_wristDebugHandLeft != null) return _wristDebugHandLeft;
            return _grappleOrigin != null ? _grappleOrigin : _clavicleLeft;
        }

        public bool AreKinemationSoundsEnabled() {
            if(disableKinemationWeaponSounds || routeWeaponSoundEventsToAudioService) {
                return false;
            }

            if(!TryCacheActiveWeapon() || _activeWeapon == null) {
                return false;
            }

            var weaponSounds = GetActiveWeaponSounds();
            foreach(var weaponSound in weaponSounds) {
                if(weaponSound == null || !weaponSound.enabled) continue;

                var weaponAudioSource = GetOrAssignWeaponSoundAudioSource(weaponSound);
                if(weaponAudioSource != null && weaponAudioSource.enabled) {
                    return true;
                }
            }

            return false;
        }

        public bool HasActiveWeapon() {
            return TryCacheActiveWeapon();
        }

        public void SyncLocomotion(Vector2 moveInput, bool sprinting, bool tacticalSprinting, bool isGrounded,
            float lookPitchDegrees) {
            if(_fpsPlayer == null) return;

            if(freezeLocomotionInAir && !isGrounded) {
                moveInput = Vector2.zero;
                sprinting = false;
                tacticalSprinting = false;
            }

            if(forceWalkAnimationWhileSprinting && (sprinting || tacticalSprinting)) {
                var gaitTarget = Mathf.Clamp(sprintWalkGaitValue, 0f, 1.99f);
                if(moveInput.sqrMagnitude > 0.0001f && gaitTarget > 0f) {
                    var moveDir = moveInput.normalized;
                    var moveMag = Mathf.Max(moveInput.magnitude, gaitTarget);
                    moveInput = moveDir * Mathf.Min(moveMag, 1.99f);
                }

                sprinting = false;
                tacticalSprinting = false;
            }

            FpsPlayerMoveInputField?.SetValue(_fpsPlayer, moveInput);

            FpsPlayerLookInputField?.SetValue(_fpsPlayer,
                syncLookPitchWithPlayer ? new Vector2(0f, -lookPitchDegrees) : Vector2.zero);

            FpsPlayerSprintingField?.SetValue(_fpsPlayer, sprinting);
            FpsPlayerTacSprintingField?.SetValue(_fpsPlayer, tacticalSprinting);

            if(_fpsAnimator != null) {
                _fpsAnimator.SetBool(IsInAir, syncAirborneState && !isGrounded);
            }
        }

        private void BuildRuntimeSettings() {
            var sourceSettings = _fpsPlayer.playerSettings;
            _runtimePlayerSettings = sourceSettings != null ? Instantiate(sourceSettings) : ScriptableObject.CreateInstance<FPSPlayerSettings>();

            _runtimePlayerSettings.weaponPrefabs = new List<GameObject> { weaponPrefab };
            _fpsPlayer.playerSettings = _runtimePlayerSettings;
        }

        private void DisableUnneededComponents() {
            // TODO(KIN-SPLIT): Move viewmodel bootstrap/teardown (audio, relay wiring, disabling components)
            // into a dedicated lifecycle/service class.
            var weaponSoundPlaybackDisabled = disableKinemationWeaponSounds || routeWeaponSoundEventsToAudioService;

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

            var playerCamera = _playerInstance.GetComponentInChildren<Camera>(true);
            if(playerCamera != null) {
                playerCamera.enabled = false;
            }

            var listener = _playerInstance.GetComponentInChildren<AudioListener>(true);
            if(listener != null) {
                listener.enabled = false;
            }

            if(disableKinemationPlayerSounds) {
                var playerSounds = _playerInstance.GetComponentsInChildren<FPSPlayerSound>(true);
                foreach(var playerSound in playerSounds) {
                    if(playerSound == null) continue;
                    if(playerSound.GetComponent<KinemationPlayerSoundEventRelay>() == null) {
                        playerSound.gameObject.AddComponent<KinemationPlayerSoundEventRelay>();
                    }

                    Destroy(playerSound);
                }
            }

            if(weaponSoundPlaybackDisabled) {
                var weaponSounds = _playerInstance.GetComponentsInChildren<FPSWeaponSound>(true);
                foreach(var weaponSound in weaponSounds) {
                    if(weaponSound == null) continue;
                    var relay = weaponSound.GetComponent<KinemationReloadEventRelay>();
                    if(relay == null) {
                        relay = weaponSound.gameObject.AddComponent<KinemationReloadEventRelay>();
                    }

                    relay.Bind(this);
                    Destroy(weaponSound);
                }
            }

            if(!disableKinemationPlayerSounds || !weaponSoundPlaybackDisabled) return;

            var audioSources = _playerInstance.GetComponentsInChildren<AudioSource>(true);
            foreach(var source in audioSources) {
                if(source != null) {
                    source.enabled = false;
                }
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
    }
}


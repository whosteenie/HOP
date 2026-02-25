using System.Collections.Generic;
using System.Reflection;
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
        [SerializeField] private bool disableKinemationWeaponSounds;
        [SerializeField] private bool disableKinemationPlayerSounds = true;
        [SerializeField] private bool tagArmsForLegacyHooks;
        [SerializeField] private string armRootName = "SK_Arms_Mono";
        [SerializeField] private Vector3 fallbackMuzzleLocalPosition = Vector3.zero;
        [SerializeField] private Vector3 fallbackMuzzleLocalEulerAngles = Vector3.zero;
        [SerializeField] private bool autoComputeMuzzleFromWeaponBounds = true;
        [SerializeField, Min(0f)] private float autoMuzzleForwardPadding = 0.01f;
        [SerializeField] private bool syncLookPitchWithPlayer;
        [SerializeField] private bool syncAirborneState;
        [SerializeField] private bool freezeLocomotionInAir = true;
        [SerializeField] private bool forceWalkAnimationWhileSprinting = true;
        [SerializeField, Range(0f, 1.99f)] private float sprintWalkGaitValue = 1.2f;

        private GameObject _playerInstance;
        private FPSPlayerSettings _runtimePlayerSettings;
        private FPSPlayer _fpsPlayer;
        private Animator _fpsAnimator;
        private FPSWeapon _activeWeapon;
        private Transform _muzzleTransform;
        private AudioSource _weaponAudioSource;
        private int _renderLayer = -1;
        private bool _hasWarnedFallbackMuzzle;
        private bool _isTrackingReload;
        private bool _reloadHasBeenActive;
        private bool _reloadHasReceivedAnyEvent;
        private bool _reloadCompleteEventReceived;
        private int _pendingReloadSingleEvents;
        private float _reloadTrackStartTime;
        private float _lastReloadSignalTime;
        private const float ReloadEnterGraceSeconds = 0.2f;
        private const float ReloadSignalGraceSeconds = 0.25f;

        private static readonly FieldInfo FpsPlayerMoveInputField =
            typeof(FPSPlayer).GetField("_moveInput", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsPlayerLookInputField =
            typeof(FPSPlayer).GetField("_lookInput", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsPlayerSprintingField =
            typeof(FPSPlayer).GetField("_bSprinting", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsPlayerTacSprintingField =
            typeof(FPSPlayer).GetField("_bTacSprinting", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo FpsPlayerSetMovementEnabledMethod =
            typeof(FPSPlayer).GetMethod("SetCharacterControllerMovementEnabled",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsPlayerAllowControllerMovementField =
            typeof(FPSPlayer).GetField("allowCharacterControllerMovement", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsWeaponSoundAudioSourceField =
            typeof(FPSWeaponSound).GetField("_audioSource", BindingFlags.Instance | BindingFlags.NonPublic);

        public void Configure(GameObject playerPrefab, GameObject fpWeaponPrefab, bool disableWeaponSounds,
            bool disablePlayerSounds, bool tagArms, Vector3 muzzleLocalPosition, Vector3 muzzleLocalEulerAngles,
            bool syncLookPitch, bool syncInAirState, bool freezeAirLocomotion, bool autoMuzzleFromBounds,
            bool forceWalkWhileSprinting, float sprintGaitValue) {
            fpsPlayerPrefab = playerPrefab;
            weaponPrefab = fpWeaponPrefab;
            disableKinemationWeaponSounds = disableWeaponSounds;
            disableKinemationPlayerSounds = disablePlayerSounds;
            tagArmsForLegacyHooks = tagArms;
            fallbackMuzzleLocalPosition = muzzleLocalPosition;
            fallbackMuzzleLocalEulerAngles = muzzleLocalEulerAngles;
            syncLookPitchWithPlayer = syncLookPitch;
            syncAirborneState = syncInAirState;
            freezeLocomotionInAir = freezeAirLocomotion;
            autoComputeMuzzleFromWeaponBounds = autoMuzzleFromBounds;
            forceWalkAnimationWhileSprinting = forceWalkWhileSprinting;
            sprintWalkGaitValue = Mathf.Clamp(sprintGaitValue, 0f, 1.99f);
        }

        public bool InitializeIfNeeded(int renderLayer) {
            _renderLayer = renderLayer;

            if(_playerInstance != null) {
                SetLayerRecursive(_playerInstance, _renderLayer);
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
            ResetReloadTracking();
            _isTrackingReload = true;
            _reloadTrackStartTime = Time.time;
            _lastReloadSignalTime = Time.time;
            _activeWeapon.OnReload();
        }

        public void PlayReloadCompleteAnimation() {
            // KINEMATION handles reload completion internally via its own state machine.
        }

        public bool IsReloadSequenceInProgress() {
            if(!_isTrackingReload) {
                return false;
            }

            if(_reloadCompleteEventReceived) {
                return false;
            }

            var reloadActiveNow = IsAnyReloadClipActive();
            if(reloadActiveNow) {
                _reloadHasBeenActive = true;
                _lastReloadSignalTime = Time.time;
                return true;
            }

            if(_reloadHasReceivedAnyEvent && Time.time - _lastReloadSignalTime <= ReloadSignalGraceSeconds) {
                return true;
            }

            // Allow one short transition window before we decide reload never started.
            if(!_reloadHasBeenActive) {
                return Time.time - _reloadTrackStartTime < ReloadEnterGraceSeconds;
            }

            _isTrackingReload = false;
            return false;
        }

        public int ConsumeReloadSingleEventCount() {
            if(_pendingReloadSingleEvents <= 0) return 0;
            var count = _pendingReloadSingleEvents;
            _pendingReloadSingleEvents = 0;
            return count;
        }

        public bool ConsumeReloadCompleteEvent() {
            if(!_reloadCompleteEventReceived) return false;
            _reloadCompleteEventReceived = false;
            _isTrackingReload = false;
            return true;
        }

        public void ResetReloadTracking() {
            _isTrackingReload = false;
            _reloadHasBeenActive = false;
            _reloadHasReceivedAnyEvent = false;
            _reloadCompleteEventReceived = false;
            _pendingReloadSingleEvents = 0;
            _reloadTrackStartTime = 0f;
            _lastReloadSignalTime = 0f;
        }

        public void NotifyReloadSingleEvent() {
            if(!_isTrackingReload) return;
            _reloadHasReceivedAnyEvent = true;
            _reloadHasBeenActive = true;
            _lastReloadSignalTime = Time.time;
            _pendingReloadSingleEvents++;
        }

        public void NotifyReloadCompleteEvent() {
            if(!_isTrackingReload) return;
            _reloadHasReceivedAnyEvent = true;
            _reloadHasBeenActive = true;
            _lastReloadSignalTime = Time.time;
            _reloadCompleteEventReceived = true;
        }

        public Transform GetMuzzleTransform() {
            TryCacheActiveWeapon();
            return _muzzleTransform;
        }

        public bool AreKinemationSoundsEnabled() {
            if(disableKinemationWeaponSounds) {
                return false;
            }

            if(!TryCacheActiveWeapon() || _activeWeapon == null) {
                return false;
            }

            var weaponSounds = _activeWeapon.GetComponentsInChildren<FPSWeaponSound>(true);
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

            if(syncLookPitchWithPlayer) {
                FpsPlayerLookInputField?.SetValue(_fpsPlayer, new Vector2(0f, -lookPitchDegrees));
            } else {
                FpsPlayerLookInputField?.SetValue(_fpsPlayer, Vector2.zero);
            }

            FpsPlayerSprintingField?.SetValue(_fpsPlayer, sprinting);
            FpsPlayerTacSprintingField?.SetValue(_fpsPlayer, tacticalSprinting);

            if(_fpsAnimator != null) {
                _fpsAnimator.SetBool("IsInAir", syncAirborneState && !isGrounded);
            }
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

            if(disableKinemationPlayerSounds) {
                var playerSounds = _playerInstance.GetComponentsInChildren<FPSPlayerSound>(true);
                foreach(var playerSound in playerSounds) {
                    if(playerSound != null) {
                        playerSound.enabled = false;
                    }
                }
            }

            if(disableKinemationWeaponSounds) {
                var weaponSounds = _playerInstance.GetComponentsInChildren<FPSWeaponSound>(true);
                foreach(var weaponSound in weaponSounds) {
                    if(weaponSound != null) {
                        weaponSound.enabled = false;
                    }
                }
            }

            if(!disableKinemationPlayerSounds || !disableKinemationWeaponSounds) return;

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

        private Transform ResolveMuzzleTransform(FPSWeapon activeWeapon) {
            if(activeWeapon == null) return null;

            var transforms = activeWeapon.GetComponentsInChildren<Transform>(true);
            foreach(var t in transforms) {
                if(t != null && t.name == "Muzzle") {
                    return t;
                }
            }

            if(activeWeapon.aimPoint != null) {
                var existingMuzzle = activeWeapon.aimPoint.Find("Muzzle");
                if(existingMuzzle != null) {
                    return existingMuzzle;
                }

                var fallback = new GameObject("Muzzle").transform;
                fallback.SetParent(activeWeapon.aimPoint, false);
                var localPosition = fallbackMuzzleLocalPosition;
                if(autoComputeMuzzleFromWeaponBounds) {
                    localPosition += ComputeAutoMuzzleLocalPosition(activeWeapon);
                }

                fallback.localPosition = localPosition;
                fallback.localEulerAngles = fallbackMuzzleLocalEulerAngles;

                if(!_hasWarnedFallbackMuzzle) {
                    _hasWarnedFallbackMuzzle = true;
                    Debug.LogWarning(
                        $"[KinemationFpWeaponDriver] Weapon '{activeWeapon.name}' has no explicit 'Muzzle' transform. " +
                        "Using AimPoint fallback; add a Muzzle child on the KIN weapon prefab for precise muzzle FX.");
                }
                return fallback;
            }

            return activeWeapon.transform;
        }

        private bool TryCacheActiveWeapon() {
            if(_activeWeapon != null && _muzzleTransform != null) {
                ApplyActiveWeaponSoundToggles(_activeWeapon);
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

                if(_renderLayer >= 0) {
                    SetLayerRecursive(_playerInstance, _renderLayer);
                }
                DisableViewmodelShadows(_playerInstance);
                AttachReloadEventRelays();
            }

            _muzzleTransform ??= ResolveMuzzleTransform(_activeWeapon);
            ApplyActiveWeaponSoundToggles(_activeWeapon);
            return _activeWeapon != null;
        }

        private void ApplyActiveWeaponSoundToggles(FPSWeapon activeWeapon) {
            if(activeWeapon == null) return;

            var weaponSounds = activeWeapon.GetComponentsInChildren<FPSWeaponSound>(true);
            if(weaponSounds == null || weaponSounds.Length == 0) return;

            var shouldEnableSounds = !disableKinemationWeaponSounds;
            var sharedAudioSource = shouldEnableSounds ? EnsureDedicatedWeaponAudioSource() : null;
            foreach(var weaponSound in weaponSounds) {
                if(weaponSound == null) continue;
                var resolvedAudioSource = shouldEnableSounds
                    ? GetOrAssignWeaponSoundAudioSource(weaponSound, sharedAudioSource)
                    : null;

                weaponSound.enabled = shouldEnableSounds && resolvedAudioSource != null;

                var audioSources = weaponSound.GetComponents<AudioSource>();
                foreach(var source in audioSources) {
                    if(source != null) {
                        source.enabled = shouldEnableSounds;
                    }
                }
            }
        }

        private AudioSource EnsureDedicatedWeaponAudioSource() {
            if(_playerInstance == null) {
                return null;
            }

            if(_weaponAudioSource == null) {
                _weaponAudioSource = _playerInstance.GetComponent<AudioSource>();
                if(_weaponAudioSource == null) {
                    _weaponAudioSource = _playerInstance.AddComponent<AudioSource>();
                }
            }

            _weaponAudioSource.playOnAwake = false;
            _weaponAudioSource.loop = false;
            _weaponAudioSource.spatialBlend = 0f;
            _weaponAudioSource.enabled = !disableKinemationWeaponSounds;
            return _weaponAudioSource;
        }

        private AudioSource GetOrAssignWeaponSoundAudioSource(FPSWeaponSound weaponSound,
            AudioSource preferredSource = null) {
            if(weaponSound == null) return null;

            var assignedSource = FpsWeaponSoundAudioSourceField?.GetValue(weaponSound) as AudioSource;
            if(assignedSource != null) {
                return assignedSource;
            }

            var resolvedSource = preferredSource ?? EnsureDedicatedWeaponAudioSource();
            resolvedSource ??= weaponSound.transform.root.GetComponentInChildren<AudioSource>(true);
            if(resolvedSource == null) {
                return null;
            }

            FpsWeaponSoundAudioSourceField?.SetValue(weaponSound, resolvedSource);
            return resolvedSource;
        }

        private void AttachReloadEventRelays() {
            if(_playerInstance == null) return;
            var animators = _playerInstance.GetComponentsInChildren<Animator>(true);
            foreach(var animator in animators) {
                if(animator == null) continue;
                var relay = animator.GetComponent<KinemationReloadEventRelay>();
                if(relay == null) {
                    relay = animator.gameObject.AddComponent<KinemationReloadEventRelay>();
                }

                relay.Bind(this);
            }
        }

        private bool IsAnyReloadClipActive() {
            if(_fpsAnimator != null && AnimatorHasReloadClip(_fpsAnimator)) {
                return true;
            }

            if(_activeWeapon == null) {
                return false;
            }

            var weaponAnimators = _activeWeapon.GetComponentsInChildren<Animator>(true);
            foreach(var weaponAnimator in weaponAnimators) {
                if(weaponAnimator == null || weaponAnimator == _fpsAnimator) continue;
                if(AnimatorHasReloadClip(weaponAnimator)) {
                    return true;
                }
            }

            return false;
        }

        private static bool AnimatorHasReloadClip(Animator animator) {
            if(animator == null || !animator.isActiveAndEnabled) return false;

            for(var layer = 0; layer < animator.layerCount; layer++) {
                var clips = animator.GetCurrentAnimatorClipInfo(layer);
                if(clips == null || clips.Length == 0) continue;

                foreach(var clipInfo in clips) {
                    var clip = clipInfo.clip;
                    if(clip == null || string.IsNullOrEmpty(clip.name)) continue;
                    if(clip.name.IndexOf("reload", System.StringComparison.OrdinalIgnoreCase) >= 0) {
                        return true;
                    }
                }
            }

            return false;
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

        private Vector3 ComputeAutoMuzzleLocalPosition(FPSWeapon activeWeapon) {
            if(activeWeapon == null || activeWeapon.aimPoint == null) return Vector3.zero;

            var renderers = activeWeapon.GetComponentsInChildren<Renderer>(true);
            if(renderers == null || renderers.Length == 0) return Vector3.zero;

            var origin = activeWeapon.aimPoint.position;
            var forward = activeWeapon.aimPoint.forward;
            var maxForwardDistance = 0f;

            foreach(var renderer in renderers) {
                if(renderer == null) continue;
                var bounds = renderer.bounds;
                var extents = bounds.extents;
                var center = bounds.center;

                for(var xi = -1; xi <= 1; xi += 2) {
                    for(var yi = -1; yi <= 1; yi += 2) {
                        for(var zi = -1; zi <= 1; zi += 2) {
                            var cornerOffset = new Vector3(extents.x * xi, extents.y * yi, extents.z * zi);
                            var worldCorner = center + cornerOffset;
                            var forwardDistance = Vector3.Dot(worldCorner - origin, forward);
                            if(forwardDistance > maxForwardDistance) {
                                maxForwardDistance = forwardDistance;
                            }
                        }
                    }
                }
            }

            if(maxForwardDistance <= 0f) return Vector3.zero;
            return Vector3.forward * (maxForwardDistance + autoMuzzleForwardPadding);
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

        private void DisableFpsPlayerMovementControl() {
            if(_fpsPlayer == null) return;

            if(FpsPlayerSetMovementEnabledMethod != null) {
                FpsPlayerSetMovementEnabledMethod.Invoke(_fpsPlayer, new object[] { false });
                return;
            }

            FpsPlayerAllowControllerMovementField?.SetValue(_fpsPlayer, false);
        }

        private void Update() {
            if(_playerInstance == null || _activeWeapon != null) return;
            TryCacheActiveWeapon();
        }

        private void OnDestroy() {
            if(_runtimePlayerSettings != null) {
                Destroy(_runtimePlayerSettings);
                _runtimePlayerSettings = null;
            }
        }
    }
}

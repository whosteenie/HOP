using System;
using System.Collections.Generic;
using System.Reflection;
using KINEMATION.FPSAnimationPack.Scripts.Camera;
using Network.Events;
using KINEMATION.FPSAnimationPack.Scripts.Player;
using KINEMATION.FPSAnimationPack.Scripts.Sounds;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.VFX;

namespace Game.Weapons {
    [DisallowMultipleComponent]
    public sealed class KinemationFpWeaponDriver : MonoBehaviour {
        [Header("KINEMATION")]
        [SerializeField] private GameObject fpsPlayerPrefab;
        [SerializeField] private GameObject weaponPrefab;
        [SerializeField] private bool disableKinemationWeaponSounds;
        [SerializeField] private bool disableKinemationPlayerSounds = true;
        [SerializeField] private bool routeWeaponSoundEventsToAudioService = true;
        [SerializeField] private bool disableKinemationInternalMuzzleFx = true;
        [SerializeField] private bool syncLookPitchWithPlayer;
        [SerializeField] private bool syncAirborneState;
        [SerializeField] private bool freezeLocomotionInAir = true;
        [SerializeField] private bool forceWalkAnimationWhileSprinting = true;
        [SerializeField, Range(0f, 1.99f)] private float sprintWalkGaitValue = 1.2f;
        [SerializeField, Range(0f, 1f)] private float equipUnlockNormalizedTime = 0.82f;

        [Header("Grapple")]
        [SerializeField] private bool enableRuntimeGrappleClavicleOffset;

        private GameObject _playerInstance;
        private FPSPlayerSettings _runtimePlayerSettings;
        private FPSPlayer _fpsPlayer;
        private Animator _fpsAnimator;
        private FPSWeapon _activeWeapon;
        private Transform _muzzleTransform;
        private AudioSource _weaponAudioSource;
        private int _renderLayer = -1;
        private WeaponManager _weaponManager;
        private bool _isTrackingReload;
        private bool _reloadHasBeenActive;
        private bool _reloadHasReceivedAnyEvent;
        private bool _reloadCompleteEventReceived;
        private bool _drakeCurrentReloadStartedEmpty;
        private bool _drakeCurrentEmptyReloadSawAmmoEject;
        private bool _drakeTopShellEjectedSinceReloadComplete;
        private bool _drakeShotCanceledReloadAfterAmmoEject;
        private bool _drakeShotCanceledEmptyReloadAfterAmmoEject;
        private bool _isTrackingEquip;
        private bool _equipHasBeenActive;
        private bool _equipCompleteEventReceived;
        private int _pendingReloadSingleEvents;
        private int _pendingWeaponFireSoundEvents;
        private readonly List<int> _pendingWeaponEventSoundIndices = new();
        private string _activeWeaponSoundKey = "unknown";
        private string _activeWeaponFireSoundId = "";
        private float _reloadTrackStartTime;
        private float _lastReloadSignalTime;
        private int _lastReloadSingleEventFrame = -1;
        private float _lastReloadSingleEventTime = -1f;
        private string _lastReloadSingleEventSource = "";
        private int _reloadSingleEventsReceivedDuringCurrentReload;
        private int _reloadSingleEventsConsumedDuringCurrentReload;
        private float _equipTrackStartTime;
        private float _lastEquipSignalTime;
        private bool _hasCachedWristDebugBones;
        private Transform _wristDebugUpperarmLeft;
        private Transform _wristDebugLowerarmLeft;
        private Transform _wristDebugTwistLeft;
        private Transform _wristDebugHandLeft;
        private Transform _clavicleLeft;
        private Transform _ikHandLeft;
        private Transform _grappleOrigin; // Optional empty child placed at desired palm position
        private bool _isRuntimeGrappleClavicleOffsetActive;
        private Vector3 _runtimeGrappleClavicleOffset;
        private int _runtimeGrappleOffsetWeaponIndex;
        private readonly HashSet<int> _suppressedMuzzleFxWeaponIds = new();
        private int _cachedActiveWeaponInstanceId;
        private Transform[] _cachedActiveWeaponTransforms;
        private Animator[] _cachedActiveWeaponAnimators;
        private FPSWeaponSound[] _cachedActiveWeaponSounds;
        private ParticleSystem[] _cachedActiveWeaponParticleSystems;
        private VisualEffect[] _cachedActiveWeaponVfxComponents;
        private Light[] _cachedActiveWeaponLights;
        private Pdw90Animation[] _cachedActiveWeaponPdwAnimations;
        private AudioSource[] _cachedActiveWeaponAudioSources;
        private bool _suppressDrakeTopShellEjectOnNextReload;
        private bool _suppressDrakeBottomShellOnNextReload;
        private Transform _suppressedDrakeTopShellTransform;
        private Vector3 _suppressedDrakeTopShellOriginalLocalPosition;
        private bool _hasSuppressedDrakeTopShellOriginalLocalPosition;
        private Vector3 _suppressedDrakeTopShellOriginalLocalScale;
        private bool _hasSuppressedDrakeTopShellOriginalLocalScale;
        private Renderer[] _suppressedDrakeTopShellRenderers;
        private bool[] _suppressedDrakeTopShellRendererEnabledStates;
        private bool _isDrakeTopShellSuppressionApplied;
        private Transform _suppressedDrakeBottomShellTransform;
        private Vector3 _suppressedDrakeBottomShellOriginalLocalPosition;
        private bool _hasSuppressedDrakeBottomShellOriginalLocalPosition;
        private Vector3 _suppressedDrakeBottomShellOriginalLocalScale;
        private bool _hasSuppressedDrakeBottomShellOriginalLocalScale;
        private Renderer[] _suppressedDrakeBottomShellRenderers;
        private bool[] _suppressedDrakeBottomShellRendererEnabledStates;
        private bool _isDrakeBottomShellSuppressionApplied;
        private Transform _karLoopBulletTransform;
        private Vector3 _karLoopBulletOriginalLocalPosition;
        private bool _hasKarLoopBulletOriginalLocalPosition;
        private Vector3 _karLoopBulletOriginalLocalScale;
        private bool _hasKarLoopBulletOriginalLocalScale;
        private Renderer[] _karLoopBulletRenderers;
        private bool[] _karLoopBulletRendererEnabledStates;
        private bool _isKarLoopBulletHidden;
        private const float ReloadEnterGraceSeconds = 0.2f;
        private const float ReloadSignalGraceSeconds = 0.25f;
        private const float EquipEnterGraceSeconds = 0.2f;
        private const float EquipSignalGraceSeconds = 0.05f;
        private const float DrakeTopShellHideOffset = 0.75f;
        private const float KarLoopBulletHideOffset = 0.55f;
        private const string DrakeTopShellName = "12Gauge1";
        private const string DrakeBottomShellName = "12Gauge0";
        private static readonly int EquipHash = Animator.StringToHash("Equip");
        private static readonly int EquipOverrideHash = Animator.StringToHash("Equip_Override");

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
        private static readonly FieldInfo FpsWeaponActiveAmmoField =
            typeof(FPSWeapon).GetField("_activeAmmo", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsWeaponIsReloadingField =
            typeof(FPSWeapon).GetField("_isReloading", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsWeaponIsFiringField =
            typeof(FPSWeapon).GetField("_isFiring", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsWeaponCharacterAnimatorField =
            typeof(FPSWeapon).GetField("characterAnimator", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsWeaponAnimatorField =
            typeof(FPSWeapon).GetField("weaponAnimator", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo Pdw90SmoothAmmoWeightField =
            typeof(Pdw90Animation).GetField("_smoothAmmoWeight", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly int IdleHash = Animator.StringToHash("Idle");
        private static readonly int GrappleHash = Animator.StringToHash("Grapple");
        private static readonly int GrappleWeaponIndexHash = Animator.StringToHash("GrappleWeaponIndex");
        private static readonly Vector3 FixedUpperarmLeftPositionOffset = new(0f, 0.027f, 0f);

        private const float RuntimeGrappleClavicleOffsetScale = 1f;
        private const float GrappleOffsetBlendInNormalized = 0.06f;
        private const float GrappleOffsetBlendOutStartNormalized = 0.82f;
        private const float GrappleOffsetBlendOutEndNormalized = 0.98f;

        /// <summary>Animator layer index where the Grapple blend tree runs (left-hand/grapple layer).</summary>
        private const int GrappleLayerIndex = 8;
        private static readonly Vector3 FixedTwistLeftEulerOffset = new(0f, -7.5f, 0f);
        private static readonly int IsInAir = Animator.StringToHash("IsInAir");
        private static readonly Vector3 DefaultAkViewmodelLocalPosition = new(0.1699999f, -1.750005f, 0f);
        private static bool s_hasAkViewmodelReference;
        private static Vector3 s_akViewmodelLocalPosition = DefaultAkViewmodelLocalPosition;
        private static bool s_hasAkAnchorFrame1CameraReference;
        private static Vector3 s_akAnchorFrame1CameraLocal;

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

            var idx = GetGrappleWeaponIndex(_activeWeaponSoundKey, _activeWeapon);

            if(idx == 0) {
                if(TryGetWeaponCameraTransform(out var cameraTransform)) {
                    s_akAnchorFrame1CameraLocal = cameraTransform.InverseTransformPoint(anchor.position);
                    s_hasAkAnchorFrame1CameraReference = true;
                }
                return;
            }

            Vector3 resolvedLocalOffset;
            if(s_hasAkAnchorFrame1CameraReference && TryGetWeaponCameraTransform(out var frameCameraTransform)) {
                var currentCameraLocal = frameCameraTransform.InverseTransformPoint(anchor.position);
                var cameraLocalOffset = s_akAnchorFrame1CameraLocal - currentCameraLocal;
                var worldOffset = frameCameraTransform.TransformDirection(cameraLocalOffset);
                resolvedLocalOffset = _clavicleLeft.parent != null
                    ? _clavicleLeft.parent.InverseTransformDirection(worldOffset)
                    : worldOffset;
            } else {
                // Fallback: convert root-delta estimate into clavicle-parent local once at frame1.
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
            if(grappleStartedEvent != null && !grappleStartedEvent.UseFirstPersonAnimation) {
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

        public void PlayEquipAnimation(bool immediate) {
            if(!TryCacheActiveWeapon()) return;
            PrepareActiveWeaponForEquip();
            if(immediate) {
                ResetEquipTracking();
                _activeWeapon.OnEquipped_Immediate();
            } else {
                ResetEquipTracking();
                _isTrackingEquip = true;
                _equipTrackStartTime = Time.time;
                _lastEquipSignalTime = Time.time;
                _activeWeapon.OnEquipped();
            }
            ApplyGrappleWeaponIndex();
        }

        public void PlayFireAnimation(int authoritativeAmmoBeforeShot = -1) {
            if(!TryCacheActiveWeapon()) return;

            // KIN can stay inside reload states for a short window even after gameplay allows firing.
            // Force-clear that state so fire anims always hard-interrupt reload anims on this frame.
            if(IsReloadStateBlockingFire()) {
                LogDrakeDebug(
                    $"PlayFireAnimation interrupt path. frame={Time.frameCount} time={Time.time:F3} " +
                    $"isReloadTracking={_isTrackingReload} ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete} " +
                    $"suppressNextReload={_suppressDrakeTopShellEjectOnNextReload} appliedNow={_isDrakeTopShellSuppressionApplied}");
                if(IsActiveWeaponLikelyDrake()) {
                    ArmDrakeTopShellEjectSuppressionOnNextReload();
                }

                var ammoForInterrupt = authoritativeAmmoBeforeShot >= 0
                    ? authoritativeAmmoBeforeShot
                    : GetActiveWeaponAmmoForInterrupt();
                AbortReloadAndSyncAmmo(ammoForInterrupt);
            }

            SuppressInternalMuzzleFx(_activeWeapon);
            _activeWeapon.OnFirePressed();
            _activeWeapon.OnFireReleased();
        }

        public void PlayReloadAnimation() {
            if(!TryCacheActiveWeapon()) return;
            ResetReloadTracking();
            _isTrackingReload = true;
            _reloadTrackStartTime = Time.time;
            _lastReloadSignalTime = Time.time;
            var activeAmmoAtReloadStart = GetActiveWeaponAmmoForInterrupt();
            _drakeCurrentReloadStartedEmpty = IsActiveWeaponLikelyDrake() && activeAmmoAtReloadStart <= 0;
            _drakeCurrentEmptyReloadSawAmmoEject = false;
            LogDrakeDebug(
                $"PlayReloadAnimation start. frame={Time.frameCount} time={Time.time:F3} " +
                $"suppressNextReload={_suppressDrakeTopShellEjectOnNextReload} " +
                $"suppressBottomNextReload={_suppressDrakeBottomShellOnNextReload} " +
                $"reloadStartedEmpty={_drakeCurrentReloadStartedEmpty} " +
                $"ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete} " +
                $"shotCanceledAfterEject={_drakeShotCanceledReloadAfterAmmoEject} " +
                $"shotCanceledEmptyAfterEject={_drakeShotCanceledEmptyReloadAfterAmmoEject} " +
                $"topAppliedNow={_isDrakeTopShellSuppressionApplied} bottomAppliedNow={_isDrakeBottomShellSuppressionApplied}");

            var shouldHideTopShellForThisReload = IsActiveWeaponLikelyDrake() &&
                                                  _drakeTopShellEjectedSinceReloadComplete &&
                                                  _drakeShotCanceledReloadAfterAmmoEject;
            if(_suppressDrakeTopShellEjectOnNextReload || shouldHideTopShellForThisReload) {
                SuppressDrakeTopShellForReloadStart();
                LogDrakeDebug(
                    $"PlayReloadAnimation applying suppressed top shell. frame={Time.frameCount} time={Time.time:F3} " +
                    $"appliedNow={_isDrakeTopShellSuppressionApplied}");
            }
            if(_suppressDrakeBottomShellOnNextReload) {
                SuppressDrakeBottomShellForReloadStart();
                LogDrakeDebug(
                    $"PlayReloadAnimation applying suppressed bottom shell. frame={Time.frameCount} time={Time.time:F3} " +
                    $"appliedNow={_isDrakeBottomShellSuppressionApplied}");
            }

            _suppressDrakeTopShellEjectOnNextReload = false;
            _suppressDrakeBottomShellOnNextReload = false;
            _drakeShotCanceledReloadAfterAmmoEject = false;
            _drakeShotCanceledEmptyReloadAfterAmmoEject = false;

            _activeWeapon.OnReload();
        }

        public void ArmDrakeTopShellEjectSuppressionOnNextReload() {
            NotifyDrakeReloadCanceledByShot();
        }

        public void NotifyDrakeReloadCanceledByShot() {
            if(!TryCacheActiveWeapon() || _activeWeapon == null) {
                LogDrakeDebug("ArmNextReload skipped: no active KIN weapon.");
                return;
            }

            if(!IsActiveWeaponLikelyDrake()) {
                LogDrakeDebug("ArmNextReload skipped: active weapon not Drake.");
                return;
            }

            _drakeShotCanceledReloadAfterAmmoEject = true;
            if(_drakeCurrentReloadStartedEmpty && _drakeCurrentEmptyReloadSawAmmoEject) {
                _drakeShotCanceledEmptyReloadAfterAmmoEject = true;
                _suppressDrakeBottomShellOnNextReload = true;
            }
            LogDrakeDebug(
                $"MarkReloadCanceledByShot. frame={Time.frameCount} time={Time.time:F3} " +
                $"ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete} " +
                $"reloadStartedEmpty={_drakeCurrentReloadStartedEmpty} " +
                $"emptySawAmmoEject={_drakeCurrentEmptyReloadSawAmmoEject} " +
                $"suppressBottomNextReload={_suppressDrakeBottomShellOnNextReload}");
        }

        public static void PlayReloadCompleteAnimation() {
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
            _reloadSingleEventsConsumedDuringCurrentReload += count;
            LogReloadSingleDebug(
                $"Consume count={count} frame={Time.frameCount} time={Time.time:F3} " +
                $"receivedTotal={_reloadSingleEventsReceivedDuringCurrentReload} " +
                $"consumedTotal={_reloadSingleEventsConsumedDuringCurrentReload} " +
                $"lastSource='{_lastReloadSingleEventSource}'");
            return count;
        }

        public bool ConsumeReloadCompleteEvent() {
            if(!_reloadCompleteEventReceived) return false;
            _reloadCompleteEventReceived = false;
            _isTrackingReload = false;
            return true;
        }

        public bool IsKinemationSoundEventRoutingEnabled() {
            if(!routeWeaponSoundEventsToAudioService) {
                return false;
            }

            return TryCacheActiveWeapon() && _activeWeapon != null && _activeWeapon.weaponSettings != null;
        }

        public int GetKinemationEventSoundClipCount() {
            if(!TryCacheActiveWeapon() || _activeWeapon == null || _activeWeapon.weaponSettings == null) {
                return 0;
            }

            return _activeWeapon.weaponSettings.weaponEventSounds != null
                ? _activeWeapon.weaponSettings.weaponEventSounds.Count
                : 0;
        }

        public bool IsLikelyReloadEventSoundClip(int clipIndex) {
            if(!TryCacheActiveWeapon() || _activeWeapon == null || _activeWeapon.weaponSettings == null) {
                return false;
            }

            var eventSounds = _activeWeapon.weaponSettings.weaponEventSounds;
            if(eventSounds == null || clipIndex < 0 || clipIndex >= eventSounds.Count) {
                return false;
            }

            var clip = eventSounds[clipIndex];
            if(clip == null || string.IsNullOrWhiteSpace(clip.name)) {
                return false;
            }

            var clipName = clip.name.ToLowerInvariant();
            return clipName.Contains("reload") || clipName.Contains("insert") || clipName.Contains("shell") ||
                   clipName.Contains("mag") || clipName.Contains("bolt");
        }

        public void SyncActiveAmmo(int authoritativeAmmo) {
            if(!TryCacheActiveWeapon() || _activeWeapon == null) return;
            ApplyAuthoritativeAmmoToActiveWeapon(authoritativeAmmo, cancelPendingInvokes: false, out var clampedAmmo,
                out var maxAmmo);
            SyncAmmoDrivenViewmodelVisuals(clampedAmmo, maxAmmo);
        }

        public void AbortReloadAndSyncAmmo(int authoritativeAmmo) {
            if(!TryCacheActiveWeapon() || _activeWeapon == null) return;

            _activeWeapon.CancelInvoke();
            _activeWeapon.OnFireReleased();
            ApplyAuthoritativeAmmoToActiveWeapon(authoritativeAmmo, cancelPendingInvokes: false, out var clampedAmmo,
                out var maxAmmo);
            SyncAmmoDrivenViewmodelVisuals(clampedAmmo, maxAmmo);
            ForceReloadAnimatorsToIdle();
            StopActiveWeaponAudioPlayback();
            ResetReloadTracking();
            ClearPendingWeaponSoundEvents();
        }

        private void ForceReloadAnimatorsToIdle() {
            if(_activeWeapon == null) return;

            var animators = new List<Animator>(8);
            AddUniqueAnimator(animators, FpsWeaponCharacterAnimatorField?.GetValue(_activeWeapon) as Animator);
            AddUniqueAnimator(animators, FpsWeaponAnimatorField?.GetValue(_activeWeapon) as Animator);
            AddUniqueAnimator(animators, _fpsAnimator);

            var weaponAnimators = GetActiveWeaponAnimators();
            foreach(var weaponAnimator in weaponAnimators) {
                AddUniqueAnimator(animators, weaponAnimator);
            }

            foreach(var t in animators) {
                SnapAnimatorToIdle(t, forceRebindIfReloadStillActive: true);
            }
        }

        private static void AddUniqueAnimator(List<Animator> destination, Animator animator) {
            if(destination == null || animator == null) return;
            if(destination.Contains(animator)) return;
            destination.Add(animator);
        }

        private void SuppressDrakeTopShellForReloadStart() {
            if(_activeWeapon == null || !IsActiveWeaponLikelyDrake()) {
                LogDrakeDebug("SuppressAtReloadStart skipped: active weapon not Drake.");
                return;
            }

            if(!EnsureDrakeTopShellSuppressionTarget()) {
                LogDrakeDebug(
                    $"Drake suppression target not found. frame={Time.frameCount} time={Time.time:F3}");
                return;
            }

            // Keep top shell hidden for this reload start when consumed by the two-flag rule.
            ApplyDrakeTopShellSuppressionNow();

            LogDrakeDebug(
                $"Drake reload start. topShellHidden={_isDrakeTopShellSuppressionApplied} " +
                $"target={_suppressedDrakeTopShellTransform.name} frame={Time.frameCount} time={Time.time:F3} " +
                $"suppressNextReload={_suppressDrakeTopShellEjectOnNextReload} " +
                $"ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete} " +
                $"shotCanceledAfterEject={_drakeShotCanceledReloadAfterAmmoEject}");
        }

        private bool EnsureDrakeTopShellSuppressionTarget() {
            if(_suppressedDrakeTopShellTransform != null) {
                return true;
            }

            if(!TryResolveDrakeTopShellTransform(out var topShellTransform) || topShellTransform == null) {
                LogDrakeDebug(
                    $"EnsureTarget failed. frame={Time.frameCount} time={Time.time:F3} " +
                    $"activeWeapon={(_activeWeapon != null ? _activeWeapon.name : "(null)")}");
                return false;
            }

            _suppressedDrakeTopShellTransform = topShellTransform;
            _suppressedDrakeTopShellOriginalLocalPosition = topShellTransform.localPosition;
            _hasSuppressedDrakeTopShellOriginalLocalPosition = true;
            _suppressedDrakeTopShellOriginalLocalScale = topShellTransform.localScale;
            _hasSuppressedDrakeTopShellOriginalLocalScale = true;
            _isDrakeTopShellSuppressionApplied = false;

            var shellRenderers = topShellTransform.GetComponentsInChildren<Renderer>(true);
            if(shellRenderers is not { Length: > 0 }) return true;
            _suppressedDrakeTopShellRenderers = shellRenderers;
            _suppressedDrakeTopShellRendererEnabledStates = new bool[shellRenderers.Length];
            for(var i = 0; i < shellRenderers.Length; i++) {
                var shellRenderer = shellRenderers[i];
                if(shellRenderer == null) continue;
                _suppressedDrakeTopShellRendererEnabledStates[i] = shellRenderer.enabled;
            }

            return true;
        }

        private void ApplyDrakeTopShellSuppressionNow() {
            if(_suppressedDrakeTopShellTransform == null) {
                LogDrakeDebug("ApplySuppression skipped: target null.");
                return;
            }

            if(_hasSuppressedDrakeTopShellOriginalLocalPosition) {
                _suppressedDrakeTopShellTransform.localPosition =
                    _suppressedDrakeTopShellOriginalLocalPosition + Vector3.down * DrakeTopShellHideOffset;
            }

            if(_hasSuppressedDrakeTopShellOriginalLocalScale) {
                _suppressedDrakeTopShellTransform.localScale = Vector3.zero;
            }

            if(_suppressedDrakeTopShellRenderers != null) {
                foreach(var shellRenderer in _suppressedDrakeTopShellRenderers) {
                    if(shellRenderer == null) continue;
                    shellRenderer.enabled = false;
                }
            }

            _isDrakeTopShellSuppressionApplied = true;
            LogDrakeDebug(
                $"ApplySuppression applied. target={_suppressedDrakeTopShellTransform.name} " +
                $"frame={Time.frameCount} time={Time.time:F3}");
        }

        private void RestoreDrakeTopShellImmediate() {
            if(_suppressedDrakeTopShellRenderers != null && _suppressedDrakeTopShellRendererEnabledStates != null) {
                var limit = Mathf.Min(_suppressedDrakeTopShellRenderers.Length,
                    _suppressedDrakeTopShellRendererEnabledStates.Length);
                for(var i = 0; i < limit; i++) {
                    var shellRenderer = _suppressedDrakeTopShellRenderers[i];
                    if(shellRenderer == null) continue;
                    shellRenderer.enabled = _suppressedDrakeTopShellRendererEnabledStates[i];
                }
            }

            if(_suppressedDrakeTopShellTransform != null && _hasSuppressedDrakeTopShellOriginalLocalPosition) {
                _suppressedDrakeTopShellTransform.localPosition = _suppressedDrakeTopShellOriginalLocalPosition;
            }
            if(_suppressedDrakeTopShellTransform != null && _hasSuppressedDrakeTopShellOriginalLocalScale) {
                _suppressedDrakeTopShellTransform.localScale = _suppressedDrakeTopShellOriginalLocalScale;
            }

            _suppressedDrakeTopShellTransform = null;
            _suppressedDrakeTopShellRenderers = null;
            _suppressedDrakeTopShellRendererEnabledStates = null;
            _suppressedDrakeTopShellOriginalLocalPosition = Vector3.zero;
            _hasSuppressedDrakeTopShellOriginalLocalPosition = false;
            _suppressedDrakeTopShellOriginalLocalScale = Vector3.one;
            _hasSuppressedDrakeTopShellOriginalLocalScale = false;
            _isDrakeTopShellSuppressionApplied = false;
            LogDrakeDebug($"RestoreSuppression complete. frame={Time.frameCount} time={Time.time:F3}");
        }

        private void SuppressDrakeBottomShellForReloadStart() {
            if(_activeWeapon == null || !IsActiveWeaponLikelyDrake()) {
                LogDrakeDebug("Bottom suppress skipped: active weapon not Drake.");
                return;
            }

            if(!EnsureDrakeBottomShellSuppressionTarget()) {
                LogDrakeDebug(
                    $"Bottom suppression target not found. frame={Time.frameCount} time={Time.time:F3}");
                return;
            }

            ApplyDrakeBottomShellSuppressionNow();
            LogDrakeDebug(
                $"Drake reload start. bottomShellHidden={_isDrakeBottomShellSuppressionApplied} " +
                $"target={_suppressedDrakeBottomShellTransform.name} frame={Time.frameCount} time={Time.time:F3}");
        }

        private bool EnsureDrakeBottomShellSuppressionTarget() {
            if(_suppressedDrakeBottomShellTransform != null) {
                return true;
            }

            if(!TryResolveDrakeBottomShellTransform(out var bottomShellTransform) || bottomShellTransform == null) {
                return false;
            }

            _suppressedDrakeBottomShellTransform = bottomShellTransform;
            _suppressedDrakeBottomShellOriginalLocalPosition = bottomShellTransform.localPosition;
            _hasSuppressedDrakeBottomShellOriginalLocalPosition = true;
            _suppressedDrakeBottomShellOriginalLocalScale = bottomShellTransform.localScale;
            _hasSuppressedDrakeBottomShellOriginalLocalScale = true;
            _isDrakeBottomShellSuppressionApplied = false;

            var shellRenderers = bottomShellTransform.GetComponentsInChildren<Renderer>(true);
            if(shellRenderers is not { Length: > 0 }) return true;
            _suppressedDrakeBottomShellRenderers = shellRenderers;
            _suppressedDrakeBottomShellRendererEnabledStates = new bool[shellRenderers.Length];
            for(var i = 0; i < shellRenderers.Length; i++) {
                var shellRenderer = shellRenderers[i];
                if(shellRenderer == null) continue;
                _suppressedDrakeBottomShellRendererEnabledStates[i] = shellRenderer.enabled;
            }

            return true;
        }

        private void ApplyDrakeBottomShellSuppressionNow() {
            if(_suppressedDrakeBottomShellTransform == null) {
                LogDrakeDebug("ApplyBottomSuppression skipped: target null.");
                return;
            }

            if(_hasSuppressedDrakeBottomShellOriginalLocalPosition) {
                _suppressedDrakeBottomShellTransform.localPosition =
                    _suppressedDrakeBottomShellOriginalLocalPosition + Vector3.down * DrakeTopShellHideOffset;
            }

            if(_hasSuppressedDrakeBottomShellOriginalLocalScale) {
                _suppressedDrakeBottomShellTransform.localScale = Vector3.zero;
            }

            if(_suppressedDrakeBottomShellRenderers != null) {
                foreach(var shellRenderer in _suppressedDrakeBottomShellRenderers) {
                    if(shellRenderer == null) continue;
                    shellRenderer.enabled = false;
                }
            }

            _isDrakeBottomShellSuppressionApplied = true;
            LogDrakeDebug(
                $"ApplyBottomSuppression applied. target={_suppressedDrakeBottomShellTransform.name} " +
                $"frame={Time.frameCount} time={Time.time:F3}");
        }

        private void RestoreDrakeBottomShellImmediate() {
            if(_suppressedDrakeBottomShellRenderers != null && _suppressedDrakeBottomShellRendererEnabledStates != null) {
                var limit = Mathf.Min(_suppressedDrakeBottomShellRenderers.Length,
                    _suppressedDrakeBottomShellRendererEnabledStates.Length);
                for(var i = 0; i < limit; i++) {
                    var shellRenderer = _suppressedDrakeBottomShellRenderers[i];
                    if(shellRenderer == null) continue;
                    shellRenderer.enabled = _suppressedDrakeBottomShellRendererEnabledStates[i];
                }
            }

            if(_suppressedDrakeBottomShellTransform != null && _hasSuppressedDrakeBottomShellOriginalLocalPosition) {
                _suppressedDrakeBottomShellTransform.localPosition = _suppressedDrakeBottomShellOriginalLocalPosition;
            }
            if(_suppressedDrakeBottomShellTransform != null && _hasSuppressedDrakeBottomShellOriginalLocalScale) {
                _suppressedDrakeBottomShellTransform.localScale = _suppressedDrakeBottomShellOriginalLocalScale;
            }

            _suppressedDrakeBottomShellTransform = null;
            _suppressedDrakeBottomShellRenderers = null;
            _suppressedDrakeBottomShellRendererEnabledStates = null;
            _suppressedDrakeBottomShellOriginalLocalPosition = Vector3.zero;
            _hasSuppressedDrakeBottomShellOriginalLocalPosition = false;
            _suppressedDrakeBottomShellOriginalLocalScale = Vector3.one;
            _hasSuppressedDrakeBottomShellOriginalLocalScale = false;
            _isDrakeBottomShellSuppressionApplied = false;
            LogDrakeDebug($"RestoreBottomSuppression complete. frame={Time.frameCount} time={Time.time:F3}");
        }

        private bool TryResolveDrakeBottomShellTransform(out Transform bottomShellTransform) {
            bottomShellTransform = null;
            if(_activeWeapon == null) return false;

            var transforms = GetActiveWeaponTransforms();
            if(TryFindNamedTransform(transforms, DrakeBottomShellName, out bottomShellTransform)) {
                return true;
            }

            Transform best = null;
            var bestScore = int.MinValue;
            var bestLocalY = float.PositiveInfinity;

            foreach(var candidate in transforms) {
                if(candidate == null || string.IsNullOrWhiteSpace(candidate.name)) continue;
                if(string.Equals(candidate.name, DrakeTopShellName, System.StringComparison.OrdinalIgnoreCase)) continue;

                var lowerName = candidate.name.ToLowerInvariant();
                var isGaugeBone = lowerName.Contains("12gauge");
                var looksLikeShell = lowerName.Contains("shell") || lowerName.Contains("cartridge") ||
                                     lowerName.Contains("gauge");
                if(!isGaugeBone && !looksLikeShell) continue;

                var score = 0;
                if(isGaugeBone) score += 4;
                if(lowerName.Contains("shell")) score += 2;
                if(lowerName.Contains("bottom") || lowerName.Contains("lower")) score += 2;
                if(lowerName.EndsWith("1")) score += 1;

                var candidateLocalY = candidate.localPosition.y;
                if(score < bestScore) continue;
                if(score == bestScore && candidateLocalY >= bestLocalY) continue;

                best = candidate;
                bestScore = score;
                bestLocalY = candidateLocalY;
            }

            if(best == null) return false;
            bottomShellTransform = best;
            LogDrakeDebug(
                $"TryResolveDrakeBottomShellTransform fallback picked '{bottomShellTransform.name}'. " +
                $"frame={Time.frameCount} time={Time.time:F3}");
            return true;
        }

        private void HideKarLoopBulletForReloadLoop() {
            if(_activeWeapon == null || !IsActiveWeaponLikelyKar()) return;
            if(!EnsureKarLoopBulletTarget()) {
                LogDrakeDebug(
                    $"Kar loop bullet target not found. frame={Time.frameCount} time={Time.time:F3}");
                return;
            }

            ApplyKarLoopBulletHiddenNow();
            LogDrakeDebug(
                $"Kar loop bullet hidden. target={_karLoopBulletTransform.name} " +
                $"frame={Time.frameCount} time={Time.time:F3}");
        }

        private bool EnsureKarLoopBulletTarget() {
            if(_karLoopBulletTransform != null) {
                return true;
            }

            if(!TryResolveKarLoopBulletTransform(out var loopBulletTransform) || loopBulletTransform == null) {
                return false;
            }

            _karLoopBulletTransform = loopBulletTransform;
            _karLoopBulletOriginalLocalPosition = loopBulletTransform.localPosition;
            _hasKarLoopBulletOriginalLocalPosition = true;
            _karLoopBulletOriginalLocalScale = loopBulletTransform.localScale;
            _hasKarLoopBulletOriginalLocalScale = true;
            _isKarLoopBulletHidden = false;

            var bulletRenderers = loopBulletTransform.GetComponentsInChildren<Renderer>(true);
            if(bulletRenderers is not { Length: > 0 }) return true;
            _karLoopBulletRenderers = bulletRenderers;
            _karLoopBulletRendererEnabledStates = new bool[bulletRenderers.Length];
            for(var i = 0; i < bulletRenderers.Length; i++) {
                var bulletRenderer = bulletRenderers[i];
                if(bulletRenderer == null) continue;
                _karLoopBulletRendererEnabledStates[i] = bulletRenderer.enabled;
            }

            return true;
        }

        private void ApplyKarLoopBulletHiddenNow() {
            if(_karLoopBulletTransform == null) return;

            if(_hasKarLoopBulletOriginalLocalPosition) {
                _karLoopBulletTransform.localPosition =
                    _karLoopBulletOriginalLocalPosition + Vector3.down * KarLoopBulletHideOffset;
            }

            if(_hasKarLoopBulletOriginalLocalScale) {
                _karLoopBulletTransform.localScale = Vector3.zero;
            }

            if(_karLoopBulletRenderers != null) {
                foreach(var bulletRenderer in _karLoopBulletRenderers) {
                    if(bulletRenderer == null) continue;
                    bulletRenderer.enabled = false;
                }
            }

            _isKarLoopBulletHidden = true;
        }

        private void RestoreKarLoopBulletImmediate() {
            if(_karLoopBulletRenderers != null && _karLoopBulletRendererEnabledStates != null) {
                var limit = Mathf.Min(_karLoopBulletRenderers.Length, _karLoopBulletRendererEnabledStates.Length);
                for(var i = 0; i < limit; i++) {
                    var bulletRenderer = _karLoopBulletRenderers[i];
                    if(bulletRenderer == null) continue;
                    bulletRenderer.enabled = _karLoopBulletRendererEnabledStates[i];
                }
            }

            if(_karLoopBulletTransform != null && _hasKarLoopBulletOriginalLocalPosition) {
                _karLoopBulletTransform.localPosition = _karLoopBulletOriginalLocalPosition;
            }
            if(_karLoopBulletTransform != null && _hasKarLoopBulletOriginalLocalScale) {
                _karLoopBulletTransform.localScale = _karLoopBulletOriginalLocalScale;
            }

            _karLoopBulletTransform = null;
            _karLoopBulletRenderers = null;
            _karLoopBulletRendererEnabledStates = null;
            _karLoopBulletOriginalLocalPosition = Vector3.zero;
            _hasKarLoopBulletOriginalLocalPosition = false;
            _karLoopBulletOriginalLocalScale = Vector3.one;
            _hasKarLoopBulletOriginalLocalScale = false;
            _isKarLoopBulletHidden = false;
            LogDrakeDebug($"RestoreKarLoopBullet complete. frame={Time.frameCount} time={Time.time:F3}");
        }

        private bool TryResolveKarLoopBulletTransform(out Transform loopBulletTransform) {
            loopBulletTransform = null;
            if(_activeWeapon == null) return false;

            var transforms = GetActiveWeaponTransforms();
            if(TryFindNamedTransform(transforms, "Bullet", out loopBulletTransform)) {
                return true;
            }
            if(TryFindNamedTransform(transforms, "Bullet1", out loopBulletTransform)) {
                return true;
            }
            if(TryFindNamedTransform(transforms, "Round", out loopBulletTransform)) {
                return true;
            }

            Transform best = null;
            var bestScore = int.MinValue;
            foreach(var candidate in transforms) {
                if(candidate == null || string.IsNullOrWhiteSpace(candidate.name)) continue;

                var lowerName = candidate.name.ToLowerInvariant();
                if(lowerName.Contains("muzzle") || lowerName.Contains("aim") || lowerName.Contains("mag")) continue;

                var score = 0;
                if(lowerName.Contains("bullet")) score += 5;
                if(lowerName.Contains("cartridge")) score += 4;
                if(lowerName.Contains("round")) score += 3;
                if(lowerName.Contains("792") || lowerName.Contains("7_92") || lowerName.Contains("7.92")) score += 2;
                if(lowerName.Contains("shell")) score -= 2;

                if(score <= bestScore) continue;
                best = candidate;
                bestScore = score;
            }

            if(best == null || bestScore <= 0) return false;
            loopBulletTransform = best;
            LogDrakeDebug(
                $"TryResolveKarLoopBulletTransform fallback picked '{loopBulletTransform.name}'. " +
                $"frame={Time.frameCount} time={Time.time:F3}");
            return true;
        }

        private bool TryResolveDrakeTopShellTransform(out Transform topShellTransform) {
            topShellTransform = null;
            if(_activeWeapon == null) return false;

            var transforms = GetActiveWeaponTransforms();
            if(TryFindNamedTransform(transforms, DrakeTopShellName, out topShellTransform)) {
                return true;
            }

            if(TryFindNamedTransform(transforms, "12Gauge0", out topShellTransform)) {
                return true;
            }

            Transform best = null;
            var bestScore = int.MinValue;
            var bestLocalY = float.NegativeInfinity;

            foreach(var candidate in transforms) {
                if(candidate == null || string.IsNullOrWhiteSpace(candidate.name)) continue;

                var lowerName = candidate.name.ToLowerInvariant();
                var isGaugeBone = lowerName.Contains("12gauge");
                var looksLikeShell = lowerName.Contains("shell") || lowerName.Contains("cartridge") ||
                                     lowerName.Contains("gauge");
                if(!isGaugeBone && !looksLikeShell) continue;

                var score = 0;
                if(isGaugeBone) score += 4;
                if(lowerName.Contains("shell")) score += 2;
                if(lowerName.Contains("top") || lowerName.Contains("upper")) score += 2;
                if(lowerName.EndsWith("0")) score += 1;

                var candidateLocalY = candidate.localPosition.y;
                if(score < bestScore) continue;
                if(score == bestScore && candidateLocalY <= bestLocalY) continue;

                best = candidate;
                bestScore = score;
                bestLocalY = candidateLocalY;
            }

            if(best == null) return false;
            topShellTransform = best;
            LogDrakeDebug(
                $"TryResolveDrakeTopShellTransform fallback picked '{topShellTransform.name}'. " +
                $"frame={Time.frameCount} time={Time.time:F3}");
            return true;
        }

        private static bool TryFindNamedTransform(Transform[] candidates, string targetName, out Transform resolved) {
            resolved = null;
            if(candidates == null || candidates.Length == 0 || string.IsNullOrWhiteSpace(targetName)) {
                return false;
            }

            foreach(var candidate in candidates) {
                if(candidate == null || string.IsNullOrWhiteSpace(candidate.name)) continue;
                if(!string.Equals(candidate.name, targetName, StringComparison.OrdinalIgnoreCase)) continue;
                resolved = candidate;
                return true;
            }

            return false;
        }

        private bool IsActiveWeaponLikelyDrake() {
            if(_activeWeapon == null) return false;

            if(!string.IsNullOrWhiteSpace(_activeWeaponSoundKey) &&
               _activeWeaponSoundKey.IndexOf("drake", StringComparison.OrdinalIgnoreCase) >= 0) {
                return true;
            }

            if(!string.IsNullOrWhiteSpace(_activeWeapon.name) &&
               _activeWeapon.name.IndexOf("drake", StringComparison.OrdinalIgnoreCase) >= 0) {
                return true;
            }

            return _activeWeapon.weaponSettings != null &&
                   !string.IsNullOrWhiteSpace(_activeWeapon.weaponSettings.name) &&
                   _activeWeapon.weaponSettings.name.IndexOf("drake", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsActiveWeaponLikelyKar() {
            if(_activeWeapon == null) return false;

            if(!string.IsNullOrWhiteSpace(_activeWeaponSoundKey)) {
                var soundKey = _activeWeaponSoundKey.ToLowerInvariant();
                if(soundKey.Contains("kar") || soundKey.Contains("kar98")) {
                    return true;
                }
            }

            if(!string.IsNullOrWhiteSpace(_activeWeapon.name)) {
                var weaponName = _activeWeapon.name.ToLowerInvariant();
                if(weaponName.Contains("kar") || weaponName.Contains("kar98")) {
                    return true;
                }
            }

            if(_activeWeapon.weaponSettings == null ||
               string.IsNullOrWhiteSpace(_activeWeapon.weaponSettings.name)) return false;
            var settingsName = _activeWeapon.weaponSettings.name.ToLowerInvariant();
            return settingsName.Contains("kar") || settingsName.Contains("kar98");
        }

        /// <summary>
        /// Stable weapon bucket mapping used for runtime grapple clavicle offsets.
        /// 0=AK, 1=M1911, 2=PDW, 3=Kar, 4=Drake, 5=DGL, -1=unknown.
        /// </summary>
        private static int GetGrappleWeaponIndex(string weaponSoundKey, FPSWeapon activeWeapon) {
            var key = (weaponSoundKey ?? "").ToLowerInvariant();
            var name = activeWeapon != null ? activeWeapon.name.ToLowerInvariant() ?? "" : "";
            var settingsName = activeWeapon != null ? activeWeapon.weaponSettings != null ? activeWeapon.weaponSettings.name.ToLowerInvariant() : "" : "";
            foreach(var term in new[] { key, name, settingsName }) {
                if(string.IsNullOrEmpty(term)) continue;
                if(term.Contains("dgl") || term.Contains("deagle") || term.Contains("desert.eagle")) return 5;
                if(term.Contains("drake") || term.Contains("shotgun")) return 4;
                if(term.Contains("ak") || term.Contains("akx")) return 0;
                if(term.Contains("m1911") || term.Contains("1911") || term.Contains("pistol")) return 1;
                if(term.Contains("pdw") || term.Contains("p90")) return 2;
                if(term.Contains("kar") || term.Contains("kar98")) return 3;
            }
            return -1;
        }

        private void ApplyGrappleWeaponIndex() {
            if(_fpsAnimator == null) return;
            var weaponIndex = GetGrappleWeaponIndex(_activeWeaponSoundKey, _activeWeapon);
            if(weaponIndex < 0) weaponIndex = 0;
            _fpsAnimator.SetFloat(GrappleWeaponIndexHash, weaponIndex);
        }

        private void PrepareRuntimeGrappleClavicleOffset() {
            if(!enableRuntimeGrappleClavicleOffset) {
                _runtimeGrappleClavicleOffset = Vector3.zero;
                _runtimeGrappleOffsetWeaponIndex = 0;
                _isRuntimeGrappleClavicleOffsetActive = false;
                return;
            }
            if(_activeWeapon == null) {
                TryCacheActiveWeapon();
            }

            _runtimeGrappleOffsetWeaponIndex = GetGrappleWeaponIndex(_activeWeaponSoundKey, _activeWeapon);
            if(_runtimeGrappleOffsetWeaponIndex == 0) {
                s_akViewmodelLocalPosition = transform.localPosition;
                s_hasAkViewmodelReference = true;
                _runtimeGrappleClavicleOffset = Vector3.zero;
                _isRuntimeGrappleClavicleOffsetActive = false;
                return;
            }

            var akReference = s_hasAkViewmodelReference ? s_akViewmodelLocalPosition : DefaultAkViewmodelLocalPosition;
            _runtimeGrappleClavicleOffset = akReference - transform.localPosition;
            _isRuntimeGrappleClavicleOffsetActive = false;
        }

        private void ClearRuntimeGrappleClavicleOffset() {
            _isRuntimeGrappleClavicleOffsetActive = false;
            _runtimeGrappleClavicleOffset = Vector3.zero;
            _runtimeGrappleOffsetWeaponIndex = 0;
        }

        private bool IsReloadStateBlockingFire() {
            if(_activeWeapon == null) {
                return false;
            }

            if(_isTrackingReload) {
                return true;
            }

            if(FpsWeaponIsReloadingField?.GetValue(_activeWeapon) is bool isReloading && isReloading) {
                return true;
            }

            return IsAnyReloadClipActive();
        }

        private int GetActiveWeaponAmmoForInterrupt() {
            if(_activeWeapon == null) {
                return 0;
            }

            if(FpsWeaponActiveAmmoField?.GetValue(_activeWeapon) is int activeAmmo) {
                return Mathf.Max(0, activeAmmo);
            }

            return _activeWeapon.weaponSettings != null ? Mathf.Max(0, _activeWeapon.weaponSettings.ammo) : 0;
        }

        public string GetKinemationFireSoundId() {
            return !TryCacheActiveWeapon() ? string.Empty : _activeWeaponFireSoundId;
        }

        public bool HasKinemationFireSound() {
            return !string.IsNullOrWhiteSpace(GetKinemationFireSoundId());
        }

        public bool HasAnyKinemationEventSound() {
            if(!TryCacheActiveWeapon() || _activeWeapon == null || _activeWeapon.weaponSettings == null) {
                return false;
            }

            return HasAnyValidAudioClip(_activeWeapon.weaponSettings.weaponEventSounds);
        }

        public int ConsumeWeaponFireSoundEventCount() {
            if(_pendingWeaponFireSoundEvents <= 0) return 0;
            var count = _pendingWeaponFireSoundEvents;
            _pendingWeaponFireSoundEvents = 0;
            return count;
        }

        public void ClearPendingWeaponSoundEvents() {
            _pendingWeaponFireSoundEvents = 0;
            _pendingWeaponEventSoundIndices.Clear();
        }

        public void ConsumeWeaponEventSoundIndices(List<int> destination) {
            if(destination == null) return;
            if(_pendingWeaponEventSoundIndices.Count == 0) return;

            destination.AddRange(_pendingWeaponEventSoundIndices);
            _pendingWeaponEventSoundIndices.Clear();
        }

        public bool TryGetKinemationEventSoundId(int clipIndex, out string soundId) {
            soundId = string.Empty;
            if(clipIndex < 0) return false;
            if(!TryCacheActiveWeapon() || _activeWeapon == null || _activeWeapon.weaponSettings == null) return false;

            var weaponEventSounds = _activeWeapon.weaponSettings.weaponEventSounds;
            if(weaponEventSounds == null || clipIndex >= weaponEventSounds.Count) return false;
            if(weaponEventSounds[clipIndex] == null) return false;

            soundId = KinemationSoundIdUtility.BuildEventSoundId(_activeWeaponSoundKey, clipIndex);
            return !string.IsNullOrWhiteSpace(soundId);
        }

        public bool IsEquipSequenceInProgress() {
            if(!_isTrackingEquip) {
                return false;
            }

            if(_equipCompleteEventReceived) {
                _isTrackingEquip = false;
                return false;
            }

            var equipActiveNow = TryGetEquipStateProgress(out var equipProgress);
            if(equipActiveNow) {
                _equipHasBeenActive = true;
                _lastEquipSignalTime = Time.time;
                if(!(equipProgress >= equipUnlockNormalizedTime)) return true;
                _isTrackingEquip = false;
                return false;
            }

            switch(_equipHasBeenActive) {
                case true when Time.time - _lastEquipSignalTime <= EquipSignalGraceSeconds:
                    return true;
                case false:
                    return Time.time - _equipTrackStartTime < EquipEnterGraceSeconds;
                default:
                    _isTrackingEquip = false;
                    return false;
            }
        }

        private void ResetEquipTracking() {
            _isTrackingEquip = false;
            _equipHasBeenActive = false;
            _equipCompleteEventReceived = false;
            _equipTrackStartTime = 0f;
            _lastEquipSignalTime = 0f;
        }

        public void ResetReloadTracking() {
            _isTrackingReload = false;
            _reloadHasBeenActive = false;
            _reloadHasReceivedAnyEvent = false;
            _reloadCompleteEventReceived = false;
            _drakeCurrentReloadStartedEmpty = false;
            _drakeCurrentEmptyReloadSawAmmoEject = false;
            _pendingReloadSingleEvents = 0;
            _reloadTrackStartTime = 0f;
            _lastReloadSignalTime = 0f;
            _lastReloadSingleEventFrame = -1;
            _lastReloadSingleEventTime = -1f;
            _lastReloadSingleEventSource = "";
            _reloadSingleEventsReceivedDuringCurrentReload = 0;
            _reloadSingleEventsConsumedDuringCurrentReload = 0;
            LogDrakeDebug(
                $"ResetReloadTracking. frame={Time.frameCount} time={Time.time:F3} " +
                $"ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete} " +
                $"topAppliedNow={_isDrakeTopShellSuppressionApplied} bottomAppliedNow={_isDrakeBottomShellSuppressionApplied} " +
                $"suppressTopNextReload={_suppressDrakeTopShellEjectOnNextReload} " +
                $"suppressBottomNextReload={_suppressDrakeBottomShellOnNextReload}");
        }

        public void NotifyReloadSingleEvent(string sourceTag = null) {
            var source = string.IsNullOrWhiteSpace(sourceTag) ? "(unknown)" : sourceTag;
            if(!_isTrackingReload) {
                LogReloadSingleDebug(
                    $"Ignored (not tracking) source='{source}' frame={Time.frameCount} time={Time.time:F3}");
                return;
            }

            if(Time.frameCount == _lastReloadSingleEventFrame) {
                LogReloadSingleDebug(
                    $"Ignored same-frame duplicate source='{source}' frame={Time.frameCount} time={Time.time:F3} " +
                    $"lastSource='{_lastReloadSingleEventSource}'");
                return;
            }

            var deltaSinceLast = _lastReloadSingleEventTime < 0f ? -1f : Time.time - _lastReloadSingleEventTime;
            _lastReloadSingleEventFrame = Time.frameCount;
            _lastReloadSingleEventTime = Time.time;
            _lastReloadSingleEventSource = source;
            _reloadHasReceivedAnyEvent = true;
            _reloadHasBeenActive = true;
            _lastReloadSignalTime = Time.time;
            _pendingReloadSingleEvents++;
            _reloadSingleEventsReceivedDuringCurrentReload++;
            if(IsActiveWeaponLikelyKar()) {
                HideKarLoopBulletForReloadLoop();
            }
            LogReloadSingleDebug(
                $"Queued +1 source='{source}' frame={Time.frameCount} time={Time.time:F3} " +
                $"pending={_pendingReloadSingleEvents} receivedTotal={_reloadSingleEventsReceivedDuringCurrentReload} " +
                $"deltaSinceLast={deltaSinceLast:F3}");
        }

        public void NotifyAmmoEjectEvent() {
            if(IsActiveWeaponLikelyDrake()) {
                _drakeTopShellEjectedSinceReloadComplete = true;
                _drakeShotCanceledReloadAfterAmmoEject = false;
                if(_drakeCurrentReloadStartedEmpty) {
                    _drakeCurrentEmptyReloadSawAmmoEject = true;
                }
                if(_isDrakeBottomShellSuppressionApplied) {
                    LogDrakeDebug(
                        $"NotifyAmmoEjectEvent restoring bottom shell. frame={Time.frameCount} time={Time.time:F3}");
                    RestoreDrakeBottomShellImmediate();
                }
            }

            if(!_isTrackingReload) return;
            _reloadHasReceivedAnyEvent = true;
            _reloadHasBeenActive = true;
            _lastReloadSignalTime = Time.time;
        }

        public void NotifyShellShowEvent() {
            var isDrake = IsActiveWeaponLikelyDrake();
            var isKar = IsActiveWeaponLikelyKar();
            switch(isDrake) {
                case false when !isKar:
                    return;
                case true:
                    LogDrakeDebug(
                        $"NotifyShellShowEvent. frame={Time.frameCount} time={Time.time:F3} " +
                        $"topAppliedBeforeShow={_isDrakeTopShellSuppressionApplied} " +
                        $"bottomAppliedBeforeShow={_isDrakeBottomShellSuppressionApplied}");

                    _drakeTopShellEjectedSinceReloadComplete = false;
                    _drakeShotCanceledReloadAfterAmmoEject = false;
                    _drakeShotCanceledEmptyReloadAfterAmmoEject = false;
                    _suppressDrakeTopShellEjectOnNextReload = false;
                    _suppressDrakeBottomShellOnNextReload = false;
                    RestoreDrakeTopShellImmediate();
                    RestoreDrakeBottomShellImmediate();
                    break;
            }

            if(!isKar) return;
            LogDrakeDebug(
                $"NotifyShellShowEvent restoring kar loop bullet. frame={Time.frameCount} time={Time.time:F3} " +
                $"hiddenBeforeShow={_isKarLoopBulletHidden}");
            RestoreKarLoopBulletImmediate();
        }

        public void NotifyReloadCompleteEvent(string sourceTag = null) {
            var isDrake = IsActiveWeaponLikelyDrake();
            var isKar = IsActiveWeaponLikelyKar();

            if(isDrake) {
                LogDrakeDebug(
                    $"NotifyReloadCompleteEvent restoring shell. frame={Time.frameCount} time={Time.time:F3} " +
                    $"topAppliedBeforeRestore={_isDrakeTopShellSuppressionApplied} " +
                    $"bottomAppliedBeforeRestore={_isDrakeBottomShellSuppressionApplied}");
                _drakeTopShellEjectedSinceReloadComplete = false;
                _drakeShotCanceledReloadAfterAmmoEject = false;
                _drakeShotCanceledEmptyReloadAfterAmmoEject = false;
                _suppressDrakeTopShellEjectOnNextReload = false;
                _suppressDrakeBottomShellOnNextReload = false;
                RestoreDrakeTopShellImmediate();
                RestoreDrakeBottomShellImmediate();
            }

            if(isKar) {
                LogDrakeDebug(
                    $"NotifyReloadCompleteEvent restoring kar loop bullet. frame={Time.frameCount} time={Time.time:F3} " +
                    $"hiddenBeforeRestore={_isKarLoopBulletHidden}");
                RestoreKarLoopBulletImmediate();
            }

            if(!_isTrackingReload) return;
            _reloadHasReceivedAnyEvent = true;
            _reloadHasBeenActive = true;
            _lastReloadSignalTime = Time.time;
            _reloadCompleteEventReceived = true;
            var source = string.IsNullOrWhiteSpace(sourceTag) ? "(unknown)" : sourceTag;
            LogReloadSingleDebug(
                $"ReloadComplete source='{source}' frame={Time.frameCount} time={Time.time:F3} " +
                $"receivedSingles={_reloadSingleEventsReceivedDuringCurrentReload} " +
                $"consumedSingles={_reloadSingleEventsConsumedDuringCurrentReload} " +
                $"pendingSingles={_pendingReloadSingleEvents}");
        }

        public void NotifyWeaponFireSoundEvent() {
            if(!IsKinemationSoundEventRoutingEnabled()) return;

            var fireSounds = _activeWeapon.weaponSettings.fireSounds;
            if(!HasAnyValidAudioClip(fireSounds)) return;
            _pendingWeaponFireSoundEvents++;
        }

        public void NotifyWeaponEventSoundEvent(int clipIndex) {
            if(!IsKinemationSoundEventRoutingEnabled()) return;
            if(clipIndex < 0) return;

            var weaponEventSounds = _activeWeapon.weaponSettings.weaponEventSounds;
            if(weaponEventSounds == null || clipIndex >= weaponEventSounds.Count) return;
            if(weaponEventSounds[clipIndex] == null) return;

            _pendingWeaponEventSoundIndices.Add(clipIndex);
        }

        public void NotifyEquipCompleteEvent() {
            if(!_isTrackingEquip) return;
            _equipHasBeenActive = true;
            _equipCompleteEventReceived = true;
            _lastEquipSignalTime = Time.time;

            _weaponManager = _weaponManager ? _weaponManager : GetComponentInParent<WeaponManager>();
            if(_weaponManager == null) return;
            _weaponManager.HandleKinemationEquipCompleted();
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
            if(_wristDebugHandLeft != null) {
                return _wristDebugHandLeft;
            }
            return null;
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
            if(_grappleOrigin != null) return _grappleOrigin;
            return _clavicleLeft;
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

        private void InvalidateActiveWeaponComponentCaches() {
            _cachedActiveWeaponInstanceId = 0;
            _cachedActiveWeaponTransforms = null;
            _cachedActiveWeaponAnimators = null;
            _cachedActiveWeaponSounds = null;
            _cachedActiveWeaponParticleSystems = null;
            _cachedActiveWeaponVfxComponents = null;
            _cachedActiveWeaponLights = null;
            _cachedActiveWeaponPdwAnimations = null;
            _cachedActiveWeaponAudioSources = null;
        }

        private void EnsureActiveWeaponComponentCaches(FPSWeapon weapon) {
            if(weapon == null) {
                InvalidateActiveWeaponComponentCaches();
                return;
            }

            var instanceId = weapon.gameObject.GetInstanceID();
            if(_cachedActiveWeaponInstanceId == instanceId) return;
            _cachedActiveWeaponInstanceId = instanceId;
            _cachedActiveWeaponTransforms = null;
            _cachedActiveWeaponAnimators = null;
            _cachedActiveWeaponSounds = null;
            _cachedActiveWeaponParticleSystems = null;
            _cachedActiveWeaponVfxComponents = null;
            _cachedActiveWeaponLights = null;
            _cachedActiveWeaponPdwAnimations = null;
            _cachedActiveWeaponAudioSources = null;
        }

        private T[] GetActiveWeaponComponents<T>(ref T[] cache) where T : Component {
            if(_activeWeapon == null) return Array.Empty<T>();
            EnsureActiveWeaponComponentCaches(_activeWeapon);
            if(cache == null) {
                cache = _activeWeapon.GetComponentsInChildren<T>(true);
            }

            return cache;
        }

        private T[] GetWeaponComponents<T>(FPSWeapon weapon, ref T[] activeWeaponCache) where T : Component {
            if(weapon == null) return Array.Empty<T>();
            return weapon == _activeWeapon
                ? GetActiveWeaponComponents(ref activeWeaponCache)
                : weapon.GetComponentsInChildren<T>(true);
        }

        private Transform[] GetActiveWeaponTransforms() {
            return GetActiveWeaponComponents(ref _cachedActiveWeaponTransforms);
        }

        private Animator[] GetActiveWeaponAnimators() {
            return GetActiveWeaponComponents(ref _cachedActiveWeaponAnimators);
        }

        private FPSWeaponSound[] GetActiveWeaponSounds() {
            return GetActiveWeaponComponents(ref _cachedActiveWeaponSounds);
        }

        private FPSWeaponSound[] GetWeaponSounds(FPSWeapon weapon) {
            return GetWeaponComponents(weapon, ref _cachedActiveWeaponSounds);
        }

        private ParticleSystem[] GetWeaponParticleSystems(FPSWeapon weapon) {
            return GetWeaponComponents(weapon, ref _cachedActiveWeaponParticleSystems);
        }

        private VisualEffect[] GetWeaponVisualEffects(FPSWeapon weapon) {
            return GetWeaponComponents(weapon, ref _cachedActiveWeaponVfxComponents);
        }

        private Light[] GetWeaponLights(FPSWeapon weapon) {
            return GetWeaponComponents(weapon, ref _cachedActiveWeaponLights);
        }

        private Pdw90Animation[] GetActiveWeaponPdwAnimations() {
            return GetActiveWeaponComponents(ref _cachedActiveWeaponPdwAnimations);
        }

        private AudioSource[] GetActiveWeaponAudioSources() {
            return GetActiveWeaponComponents(ref _cachedActiveWeaponAudioSources);
        }

        private Transform ResolveMuzzleTransform(FPSWeapon activeWeapon) {
            if(activeWeapon == null) return null;

            var transforms = GetWeaponComponents(activeWeapon, ref _cachedActiveWeaponTransforms);
            return ResolveBestExplicitMuzzleCandidate(activeWeapon, transforms, out _);
        }

        private static Transform ResolveBestExplicitMuzzleCandidate(FPSWeapon activeWeapon, Transform[] transforms,
            out int candidateCount) {
            candidateCount = 0;
            if(activeWeapon == null || transforms == null || transforms.Length == 0) return null;

            var candidates = new List<Transform>();
            foreach(var t in transforms) {
                if(t != null && t.name == "Muzzle") {
                    candidates.Add(t);
                }
            }

            candidateCount = candidates.Count;
            switch(candidateCount) {
                case 0:
                    return null;
                case 1:
                    return candidates[0];
            }

            var aimPoint = activeWeapon.aimPoint;
            if(aimPoint == null) {
                return candidates[0];
            }

            Transform best = null;
            var bestScore = float.NegativeInfinity;
            foreach(var candidate in candidates) {
                if(candidate == null) continue;

                var offset = candidate.position - aimPoint.position;
                var forward = aimPoint.forward;
                var forwardScore = Vector3.Dot(forward, offset);
                var lateralDistance = Vector3.Cross(forward, offset).magnitude;
                var score = forwardScore - lateralDistance * 0.05f;

                if(candidate.IsChildOf(aimPoint)) {
                    score -= 0.25f;
                }

                if(score <= bestScore) continue;
                bestScore = score;
                best = candidate;
            }

            return best != null ? best : candidates[0];
        }

        private bool TryCacheActiveWeapon() {
            if(_activeWeapon != null && !_activeWeapon.gameObject.activeInHierarchy) {
                var resolvedWeapon = FindActiveWeaponComponent();
                if(resolvedWeapon != null && resolvedWeapon != _activeWeapon) {
                    _activeWeapon = resolvedWeapon;
                    _muzzleTransform = null;
                    InvalidateActiveWeaponComponentCaches();
                    if(_playerInstance != null && _renderLayer >= 0) {
                        SetLayerRecursive(_playerInstance, _renderLayer);
                    }

                    DisableViewmodelShadows(_playerInstance);
                    AttachReloadEventRelays();
                }
            }

            if(_activeWeapon != null) {
                EnsureActiveWeaponComponentCaches(_activeWeapon);
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
                InvalidateActiveWeaponComponentCaches();

                if(_renderLayer >= 0) {
                    SetLayerRecursive(_playerInstance, _renderLayer);
                }
                DisableViewmodelShadows(_playerInstance);
                AttachReloadEventRelays();
            }

            EnsureActiveWeaponComponentCaches(_activeWeapon);
            _muzzleTransform = _muzzleTransform ? _muzzleTransform : ResolveMuzzleTransform(_activeWeapon);
            ApplyActiveWeaponSoundToggles(_activeWeapon);
            RefreshActiveWeaponSoundMetadata(_activeWeapon);
            SuppressInternalMuzzleFx(_activeWeapon);
            return _activeWeapon != null;
        }

        private void ApplyFixedWristOffsets() {
            if(_playerInstance == null) return;

            CacheWristDebugBonesIfNeeded();
            if(_wristDebugUpperarmLeft == null && _wristDebugTwistLeft == null) return;

            var preserveHandGrip = _wristDebugHandLeft != null;
            Vector3 cachedHandPosition = default;
            Quaternion cachedHandRotation = default;
            if(preserveHandGrip) {
                cachedHandPosition = _wristDebugHandLeft.position;
                cachedHandRotation = _wristDebugHandLeft.rotation;
            }

            ApplyLocalPositionOffset(_wristDebugUpperarmLeft, FixedUpperarmLeftPositionOffset);
            ApplyLocalRotationOffset(_wristDebugTwistLeft, FixedTwistLeftEulerOffset);

            if(preserveHandGrip) {
                _wristDebugHandLeft.SetPositionAndRotation(cachedHandPosition, cachedHandRotation);
            }
        }

        private void CacheWristDebugBonesIfNeeded() {
            if(_hasCachedWristDebugBones || _playerInstance == null) return;

            var root = _playerInstance.transform;
            TryFindChildByName(root, "clavicle_l", out _clavicleLeft);
            TryFindChildByName(root, "upperarm_l", out _wristDebugUpperarmLeft);
            TryFindChildByName(root, "lowerarm_l", out _wristDebugLowerarmLeft);
            TryFindChildByName(root, "lowerarm_twist_01_l", out _wristDebugTwistLeft);
            TryFindChildByName(root, "hand_l", out _wristDebugHandLeft);
            TryFindChildByName(root, "ik_hand_l", out _ikHandLeft);
            TryFindChildByName(root, "GrappleOrigin", out _grappleOrigin);
            _hasCachedWristDebugBones = true;
        }

        private static void ApplyLocalPositionOffset(Transform bone, Vector3 positionOffset) {
            if(bone == null || positionOffset.sqrMagnitude <= 0.00000001f) return;
            bone.localPosition += positionOffset;
        }

        private static void ApplyLocalRotationOffset(Transform bone, Vector3 eulerOffset) {
            if(bone == null || eulerOffset.sqrMagnitude <= 0.000001f) return;
            var delta = Quaternion.Euler(eulerOffset);
            bone.localRotation = bone.localRotation * delta;
        }

        private void ApplyActiveWeaponSoundToggles(FPSWeapon activeWeapon) {
            if(activeWeapon == null) return;

            var weaponSounds = GetWeaponSounds(activeWeapon);
            if(weaponSounds == null || weaponSounds.Length == 0) return;

            var shouldEnableSounds = !disableKinemationWeaponSounds && !routeWeaponSoundEventsToAudioService;
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

        private void RefreshActiveWeaponSoundMetadata(FPSWeapon activeWeapon) {
            if(activeWeapon == null) {
                _activeWeaponSoundKey = "unknown";
                _activeWeaponFireSoundId = string.Empty;
                ApplyGrappleWeaponIndex();
                return;
            }

            var settings = activeWeapon.weaponSettings;
            _activeWeaponSoundKey = KinemationSoundIdUtility.BuildWeaponSoundKey(settings, activeWeapon.name);
            _activeWeaponFireSoundId = settings != null && HasAnyValidAudioClip(settings.fireSounds)
                ? KinemationSoundIdUtility.BuildFireSoundId(_activeWeaponSoundKey)
                : string.Empty;
            ApplyGrappleWeaponIndex();
        }

        private void SuppressInternalMuzzleFx(FPSWeapon activeWeapon) {
            if(!disableKinemationInternalMuzzleFx || activeWeapon == null) return;

            var disabledParticles = 0;
            var disabledVfx = 0;
            var disabledLights = 0;

            var particleSystems = GetWeaponParticleSystems(activeWeapon);
            foreach(var ps in particleSystems) {
                if(ps == null || !IsLikelyMuzzleFxNode(ps.transform)) continue;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var emission = ps.emission;
                emission.enabled = false;
                disabledParticles++;
            }

            var vfxComponents = GetWeaponVisualEffects(activeWeapon);
            foreach(var vfx in vfxComponents) {
                if(vfx == null || !IsLikelyMuzzleFxNode(vfx.transform)) continue;
                vfx.Stop();
                vfx.enabled = false;
                disabledVfx++;
            }

            var lights = GetWeaponLights(activeWeapon);
            foreach(var light in lights) {
                if(light == null || !IsLikelyMuzzleFxNode(light.transform)) continue;
                light.enabled = false;
                disabledLights++;
            }

            if(disabledParticles <= 0 && disabledVfx <= 0 && disabledLights <= 0) return;
            var weaponId = activeWeapon.gameObject.GetInstanceID();
            _suppressedMuzzleFxWeaponIds.Add(weaponId);
        }

        private static bool IsLikelyMuzzleFxNode(Transform transform) {
            var cursor = transform;
            while(cursor != null) {
                var name = cursor.name.ToLowerInvariant();
                if(name.Contains("muzzle") || name.Contains("flash") || name.Contains("shotfx") ||
                   name.Contains("firefx") || name.Contains("fire_fx") || name.Contains("vfx")) {
                    return true;
                }

                cursor = cursor.parent;
            }

            return false;
        }

        private static bool HasAnyValidAudioClip(List<AudioClip> clips) {
            if(clips == null || clips.Count == 0) return false;
            foreach(var c in clips) {
                if(c != null) {
                    return true;
                }
            }

            return false;
        }

        private void ApplyAuthoritativeAmmoToActiveWeapon(int authoritativeAmmo, bool cancelPendingInvokes,
            out int clampedAmmo, out int maxAmmo) {
            clampedAmmo = 0;
            maxAmmo = 1;
            if(_activeWeapon == null) return;
            if(cancelPendingInvokes) {
                _activeWeapon.CancelInvoke();
            }

            maxAmmo = Mathf.Max(1, _activeWeapon.weaponSettings != null ? _activeWeapon.weaponSettings.ammo : authoritativeAmmo);

            clampedAmmo = Mathf.Clamp(authoritativeAmmo, 0, maxAmmo);
            FpsWeaponActiveAmmoField?.SetValue(_activeWeapon, clampedAmmo);
            FpsWeaponIsReloadingField?.SetValue(_activeWeapon, false);
            FpsWeaponIsFiringField?.SetValue(_activeWeapon, false);
        }

        private void PrepareActiveWeaponForEquip() {
            if(_activeWeapon == null) return;

            _activeWeapon.CancelInvoke();
            FpsWeaponIsReloadingField?.SetValue(_activeWeapon, false);
            FpsWeaponIsFiringField?.SetValue(_activeWeapon, false);

            var weaponAnimator = FpsWeaponAnimatorField?.GetValue(_activeWeapon) as Animator;
            SnapAnimatorToIdle(weaponAnimator);
        }

        private void SyncAmmoDrivenViewmodelVisuals(int clampedAmmo, int maxAmmo) {
            if(_activeWeapon == null) return;
            maxAmmo = Mathf.Max(1, maxAmmo);

            // PDW90 viewmodel smooths ammo weight over time by default, which causes a visible one-frame
            // lag after switch/reload-cancel. Push the smoothed value directly to authoritative ammo.
            var targetWeight = 1f - (float)clampedAmmo / maxAmmo;
            var pdwAnimations = GetActiveWeaponPdwAnimations();
            foreach(var pdwAnimation in pdwAnimations) {
                if(pdwAnimation == null) continue;
                Pdw90SmoothAmmoWeightField?.SetValue(pdwAnimation, targetWeight);
            }
        }

        private static void SnapAnimatorToIdle(Animator animator, bool forceRebindIfReloadStillActive = false) {
            if(animator == null || animator.runtimeAnimatorController == null) return;

            var playedIdleOnAnyLayer = false;
            for(var layer = 0; layer < animator.layerCount; layer++) {
                if(!animator.HasState(layer, IdleHash)) continue;
                animator.Play(IdleHash, layer, 0f);
                playedIdleOnAnyLayer = true;
            }

            if(!playedIdleOnAnyLayer) {
                animator.Rebind();
                animator.Update(0f);
                return;
            }

            animator.Update(0f);

            if(!forceRebindIfReloadStillActive || !AnimatorHasReloadClip(animator)) {
                return;
            }

            animator.Rebind();
            animator.Update(0f);

            for(var layer = 0; layer < animator.layerCount; layer++) {
                if(!animator.HasState(layer, IdleHash)) continue;
                animator.Play(IdleHash, layer, 0f);
            }

            animator.Update(0f);
        }

        private void StopActiveWeaponAudioPlayback() {
            if(_weaponAudioSource != null) {
                _weaponAudioSource.Stop();
            }

            if(_activeWeapon == null) return;
            var audioSources = GetActiveWeaponAudioSources();
            foreach(var source in audioSources) {
                if(source == null) continue;
                source.Stop();
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
            _weaponAudioSource.enabled = !disableKinemationWeaponSounds && !routeWeaponSoundEventsToAudioService;
            return _weaponAudioSource;
        }

        private AudioSource GetOrAssignWeaponSoundAudioSource(FPSWeaponSound weaponSound,
            AudioSource preferredSource = null) {
            if(weaponSound == null) return null;

            var assignedSource = FpsWeaponSoundAudioSourceField?.GetValue(weaponSound) as AudioSource;
            if(assignedSource != null) {
                return assignedSource;
            }

            var resolvedSource = preferredSource ? preferredSource : EnsureDedicatedWeaponAudioSource();
            resolvedSource = resolvedSource ? resolvedSource : weaponSound.transform.root.GetComponentInChildren<AudioSource>(true);
            if(resolvedSource == null) {
                return null;
            }

            FpsWeaponSoundAudioSourceField?.SetValue(weaponSound, resolvedSource);
            return resolvedSource;
        }

        private void AttachReloadEventRelays() {
            if(_playerInstance == null) return;
            var weaponSoundPlaybackDisabled = disableKinemationWeaponSounds || routeWeaponSoundEventsToAudioService;

            var animators = _playerInstance.GetComponentsInChildren<Animator>(true);
            foreach(var animator in animators) {
                if(animator == null) continue;
                var relay = animator.GetComponent<KinemationReloadEventRelay>();
                if(relay == null) {
                    relay = animator.gameObject.AddComponent<KinemationReloadEventRelay>();
                }

                relay.Bind(this);
            }

            var weaponSounds = _playerInstance.GetComponentsInChildren<FPSWeaponSound>(true);
            foreach(var weaponSound in weaponSounds) {
                if(weaponSound == null) continue;
                var relay = weaponSound.GetComponent<KinemationReloadEventRelay>();
                if(relay == null) {
                    relay = weaponSound.gameObject.AddComponent<KinemationReloadEventRelay>();
                }

                relay.Bind(this);

                if(weaponSoundPlaybackDisabled) {
                    Destroy(weaponSound);
                }
            }

            if(!disableKinemationPlayerSounds) return;
            var playerSounds = _playerInstance.GetComponentsInChildren<FPSPlayerSound>(true);
            foreach(var playerSound in playerSounds) {
                if(playerSound == null) continue;
                if(playerSound.GetComponent<KinemationPlayerSoundEventRelay>() == null) {
                    playerSound.gameObject.AddComponent<KinemationPlayerSoundEventRelay>();
                }

                Destroy(playerSound);
            }
        }

        private bool IsAnyReloadClipActive() {
            if(_fpsAnimator != null && AnimatorHasReloadClip(_fpsAnimator)) {
                return true;
            }

            if(_activeWeapon == null) {
                return false;
            }

            var weaponAnimators = GetActiveWeaponAnimators();
            foreach(var weaponAnimator in weaponAnimators) {
                if(weaponAnimator == null || weaponAnimator == _fpsAnimator) continue;
                if(AnimatorHasReloadClip(weaponAnimator)) {
                    return true;
                }
            }

            return false;
        }

        private bool TryGetEquipStateProgress(out float normalizedProgress) {
            normalizedProgress = 0f;

            if(TryGetAnimatorEquipProgress(_fpsAnimator, out var characterProgress)) {
                normalizedProgress = characterProgress;
                return true;
            }

            if(_activeWeapon == null) {
                return false;
            }

            var weaponAnimators = GetActiveWeaponAnimators();
            foreach(var weaponAnimator in weaponAnimators) {
                if(weaponAnimator == null || weaponAnimator == _fpsAnimator) continue;
                if(!TryGetAnimatorEquipProgress(weaponAnimator, out var weaponProgress)) continue;
                normalizedProgress = Mathf.Max(normalizedProgress, weaponProgress);
                return true;
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

        private static bool TryGetAnimatorEquipProgress(Animator animator, out float normalizedProgress) {
            normalizedProgress = 0f;
            if(animator == null || !animator.isActiveAndEnabled) return false;

            for(var layer = 0; layer < animator.layerCount; layer++) {
                var currentState = animator.GetCurrentAnimatorStateInfo(layer);
                if(currentState.shortNameHash == EquipHash || currentState.shortNameHash == EquipOverrideHash) {
                    normalizedProgress = Mathf.Max(normalizedProgress, Mathf.Clamp01(currentState.normalizedTime));
                    return true;
                }

                if(!animator.IsInTransition(layer)) continue;
                var nextState = animator.GetNextAnimatorStateInfo(layer);
                if(nextState.shortNameHash != EquipHash && nextState.shortNameHash != EquipOverrideHash) continue;
                normalizedProgress = Mathf.Max(normalizedProgress, Mathf.Clamp01(nextState.normalizedTime));
                return true;
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
            if(_playerInstance == null) return;
            if(_activeWeapon == null) {
                TryCacheActiveWeapon();
            }
        }

        private void LateUpdate() {
            ApplyRuntimeGrappleClavicleOffset();
            ApplyFixedWristOffsets();
            ApplySuppressedDrakeTopShellPose();
            ApplySuppressedDrakeBottomShellPose();
            ApplyHiddenKarLoopBulletPose();
        }

        private void ApplyRuntimeGrappleClavicleOffset() {
            if(!enableRuntimeGrappleClavicleOffset) return;
            if(!_isRuntimeGrappleClavicleOffsetActive || _runtimeGrappleClavicleOffset.sqrMagnitude <= 0.00000001f) return;
            if(_playerInstance == null || !_playerInstance.activeInHierarchy) return;

            CacheWristDebugBonesIfNeeded();
            if(_clavicleLeft == null && !TryFindChildByName(_playerInstance.transform, "clavicle_l", out _clavicleLeft)) {
                return;
            }

            var runtimeWeight = ComputeRuntimeGrappleOffsetWeight();
            if(runtimeWeight <= 0.0001f) return;

            var appliedOffset = _runtimeGrappleClavicleOffset * (RuntimeGrappleClavicleOffsetScale * runtimeWeight);
            _clavicleLeft.localPosition += appliedOffset;
        }

        private float ComputeRuntimeGrappleOffsetWeight() {
            if(_fpsAnimator == null || GrappleLayerIndex >= _fpsAnimator.layerCount) return 1f;

            var clipInfos = _fpsAnimator.GetCurrentAnimatorClipInfo(GrappleLayerIndex);
            if(clipInfos == null || clipInfos.Length == 0) return 0f;

            var clipWeight = 0f;
            foreach(var c in clipInfos) {
                var clip = c.clip;
                if(clip == null) continue;
                if(clip.name.IndexOf("Grapple", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                clipWeight = Mathf.Max(clipWeight, c.weight);
            }
            if(clipWeight <= 0.0001f) return 0f;

            var state = _fpsAnimator.GetCurrentAnimatorStateInfo(GrappleLayerIndex);
            var normalized = Mathf.Repeat(state.normalizedTime, 1f);
            var inWeight = Mathf.Clamp01(normalized / GrappleOffsetBlendInNormalized);
            var outWeight = normalized <= GrappleOffsetBlendOutStartNormalized
                ? 1f
                : 1f - Mathf.Clamp01((normalized - GrappleOffsetBlendOutStartNormalized) /
                    Mathf.Max(0.0001f, GrappleOffsetBlendOutEndNormalized - GrappleOffsetBlendOutStartNormalized));
            return clipWeight * inWeight * outWeight;
        }

        private void ApplySuppressedDrakeTopShellPose() {
            if(!_isDrakeTopShellSuppressionApplied) return;
            if(_suppressedDrakeTopShellTransform == null) return;

            if(_hasSuppressedDrakeTopShellOriginalLocalPosition) {
                _suppressedDrakeTopShellTransform.localPosition =
                    _suppressedDrakeTopShellOriginalLocalPosition + Vector3.down * DrakeTopShellHideOffset;
            }

            if(_hasSuppressedDrakeTopShellOriginalLocalScale) {
                _suppressedDrakeTopShellTransform.localScale = Vector3.zero;
            }

            if(_suppressedDrakeTopShellRenderers == null) return;
            foreach(var shellRenderer in _suppressedDrakeTopShellRenderers) {
                if(shellRenderer == null) continue;
                if(shellRenderer.enabled) {
                    shellRenderer.enabled = false;
                }
            }
        }

        private void ApplySuppressedDrakeBottomShellPose() {
            if(!_isDrakeBottomShellSuppressionApplied) return;
            if(_suppressedDrakeBottomShellTransform == null) return;

            if(_hasSuppressedDrakeBottomShellOriginalLocalPosition) {
                _suppressedDrakeBottomShellTransform.localPosition =
                    _suppressedDrakeBottomShellOriginalLocalPosition + Vector3.down * DrakeTopShellHideOffset;
            }

            if(_hasSuppressedDrakeBottomShellOriginalLocalScale) {
                _suppressedDrakeBottomShellTransform.localScale = Vector3.zero;
            }

            if(_suppressedDrakeBottomShellRenderers == null) return;
            foreach(var shellRenderer in _suppressedDrakeBottomShellRenderers) {
                if(shellRenderer == null) continue;
                if(shellRenderer.enabled) {
                    shellRenderer.enabled = false;
                }
            }
        }

        private void ApplyHiddenKarLoopBulletPose() {
            if(!_isKarLoopBulletHidden) return;
            if(_karLoopBulletTransform == null) return;

            if(_hasKarLoopBulletOriginalLocalPosition) {
                _karLoopBulletTransform.localPosition =
                    _karLoopBulletOriginalLocalPosition + Vector3.down * KarLoopBulletHideOffset;
            }

            if(_hasKarLoopBulletOriginalLocalScale) {
                _karLoopBulletTransform.localScale = Vector3.zero;
            }

            if(_karLoopBulletRenderers == null) return;
            foreach(var bulletRenderer in _karLoopBulletRenderers) {
                if(bulletRenderer == null) continue;
                if(bulletRenderer.enabled) {
                    bulletRenderer.enabled = false;
                }
            }
        }

        private void OnDestroy() {
            RestoreDrakeTopShellImmediate();
            RestoreDrakeBottomShellImmediate();
            RestoreKarLoopBulletImmediate();
            if(_runtimePlayerSettings == null) return;
            Destroy(_runtimePlayerSettings);
            _runtimePlayerSettings = null;
        }
    }
}

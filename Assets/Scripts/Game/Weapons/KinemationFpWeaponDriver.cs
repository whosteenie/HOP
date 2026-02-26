using System.Collections.Generic;
using System.Reflection;
using KINEMATION.FPSAnimationPack.Scripts.Camera;
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
        [SerializeField] private bool tagArmsForLegacyHooks;
        [SerializeField] private string armRootName = "SK_Arms_Mono";
        [SerializeField] private bool syncLookPitchWithPlayer;
        [SerializeField] private bool syncAirborneState;
        [SerializeField] private bool freezeLocomotionInAir = true;
        [SerializeField] private bool forceWalkAnimationWhileSprinting = true;
        [SerializeField, Range(0f, 1.99f)] private float sprintWalkGaitValue = 1.2f;
        [SerializeField, Range(0f, 1f)] private float equipUnlockNormalizedTime = 0.82f;
        [SerializeField] private bool applyWristCorrectionLayer;
        [SerializeField] private string wristCorrectionLayerName = "WristCorrection";
        [SerializeField, Range(0f, 1f)] private float wristCorrectionLayerWeight = 0.25f;
        [SerializeField] private bool logMissingWristCorrectionLayer = true;
        [SerializeField] private bool enableRuntimeWristDebugOverride;
        [SerializeField, Range(0f, 1f)] private float runtimeWristDebugOverrideWeight = 1f;
        [SerializeField] private Vector3 runtimeWristDebugUpperarmLeftEuler = Vector3.zero;
        [SerializeField] private Vector3 runtimeWristDebugUpperarmRightEuler = Vector3.zero;
        [SerializeField] private Vector3 runtimeWristDebugUpperarmLeftPosition = Vector3.zero;
        [SerializeField] private Vector3 runtimeWristDebugUpperarmRightPosition = Vector3.zero;
        [SerializeField] private Vector3 runtimeWristDebugLowerarmLeftEuler = new Vector3(-35f, 0f, 0f);
        [SerializeField] private Vector3 runtimeWristDebugLowerarmRightEuler = new Vector3(-35f, 0f, 0f);
        [SerializeField] private Vector3 runtimeWristDebugLowerarmLeftPosition = Vector3.zero;
        [SerializeField] private Vector3 runtimeWristDebugLowerarmRightPosition = Vector3.zero;
        [SerializeField] private Vector3 runtimeWristDebugTwistLeftEuler = new Vector3(-20f, 0f, 0f);
        [SerializeField] private Vector3 runtimeWristDebugTwistRightEuler = new Vector3(-20f, 0f, 0f);
        [SerializeField] private Vector3 runtimeWristDebugTwistLeftPosition = Vector3.zero;
        [SerializeField] private Vector3 runtimeWristDebugTwistRightPosition = Vector3.zero;
        [SerializeField] private Vector3 runtimeWristDebugHandLeftEuler = new Vector3(-10f, 0f, 0f);
        [SerializeField] private Vector3 runtimeWristDebugHandRightEuler = new Vector3(-10f, 0f, 0f);
        [SerializeField] private Vector3 runtimeWristDebugHandLeftPosition = Vector3.zero;
        [SerializeField] private Vector3 runtimeWristDebugHandRightPosition = Vector3.zero;
        [SerializeField] private bool preserveRuntimeWristDebugHandGrip = true;
        [SerializeField] private bool applyRuntimeWristDebugHandOffsetWhenPreservingGrip;
        [SerializeField] private bool logMissingWristDebugBones = true;

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
        private float _equipTrackStartTime;
        private float _lastEquipSignalTime;
        private RuntimeAnimatorController _cachedWristCorrectionController;
        private int _cachedWristCorrectionLayerIndex = -2;
        private bool _hasWarnedMissingWristCorrectionLayer;
        private bool _hasCachedWristDebugBones;
        private bool _hasWarnedMissingWristDebugBones;
        private Transform _wristDebugUpperarmLeft;
        private Transform _wristDebugUpperarmRight;
        private Transform _wristDebugLowerarmLeft;
        private Transform _wristDebugLowerarmRight;
        private Transform _wristDebugTwistLeft;
        private Transform _wristDebugTwistRight;
        private Transform _wristDebugHandLeft;
        private Transform _wristDebugHandRight;
        private readonly HashSet<int> _suppressedMuzzleFxWeaponIds = new();
        private const float ReloadEnterGraceSeconds = 0.2f;
        private const float ReloadSignalGraceSeconds = 0.25f;
        private const float EquipEnterGraceSeconds = 0.2f;
        private const float EquipSignalGraceSeconds = 0.05f;
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

        public void Configure(GameObject playerPrefab, GameObject fpWeaponPrefab, bool disableWeaponSounds,
            bool disablePlayerSounds, bool routeWeaponSoundEvents, bool tagArms, bool syncLookPitch,
            bool syncInAirState, bool freezeAirLocomotion, bool forceWalkWhileSprinting,
            float sprintGaitValue,
            float equipUnlockNormalizedProgress, bool enableWristCorrectionLayer = false,
            string wristLayerName = "WristCorrection", float wristLayerWeight = 0.25f,
            bool logMissingWristLayer = true, bool enableRuntimeWristDebug = false,
            float runtimeWristDebugWeight = 1f, Vector3 runtimeUpperarmLeftEuler = default(Vector3),
            Vector3 runtimeUpperarmRightEuler = default(Vector3), Vector3 runtimeUpperarmLeftPosition = default(Vector3),
            Vector3 runtimeUpperarmRightPosition = default(Vector3), Vector3 runtimeLowerarmLeftEuler = default(Vector3),
            Vector3 runtimeLowerarmRightEuler = default(Vector3), Vector3 runtimeLowerarmLeftPosition = default(Vector3),
            Vector3 runtimeLowerarmRightPosition = default(Vector3), Vector3 runtimeTwistLeftEuler = default(Vector3),
            Vector3 runtimeTwistRightEuler = default(Vector3), Vector3 runtimeTwistLeftPosition = default(Vector3),
            Vector3 runtimeTwistRightPosition = default(Vector3), Vector3 runtimeHandLeftEuler = default(Vector3),
            Vector3 runtimeHandRightEuler = default(Vector3), Vector3 runtimeHandLeftPosition = default(Vector3),
            Vector3 runtimeHandRightPosition = default(Vector3), bool preserveHandGrip = true,
            bool allowHandOffsetWhenPreservingGrip = false, bool logMissingRuntimeWristBones = true) {
            fpsPlayerPrefab = playerPrefab;
            weaponPrefab = fpWeaponPrefab;
            disableKinemationWeaponSounds = disableWeaponSounds;
            disableKinemationPlayerSounds = disablePlayerSounds;
            routeWeaponSoundEventsToAudioService = routeWeaponSoundEvents;
            tagArmsForLegacyHooks = tagArms;
            syncLookPitchWithPlayer = syncLookPitch;
            syncAirborneState = syncInAirState;
            freezeLocomotionInAir = freezeAirLocomotion;
            forceWalkAnimationWhileSprinting = forceWalkWhileSprinting;
            sprintWalkGaitValue = Mathf.Clamp(sprintGaitValue, 0f, 1.99f);
            equipUnlockNormalizedTime = Mathf.Clamp01(equipUnlockNormalizedProgress);
            applyWristCorrectionLayer = enableWristCorrectionLayer;
            wristCorrectionLayerName = wristLayerName;
            wristCorrectionLayerWeight = Mathf.Clamp01(wristLayerWeight);
            logMissingWristCorrectionLayer = logMissingWristLayer;
            enableRuntimeWristDebugOverride = enableRuntimeWristDebug;
            runtimeWristDebugOverrideWeight = Mathf.Clamp01(runtimeWristDebugWeight);
            runtimeWristDebugUpperarmLeftEuler = runtimeUpperarmLeftEuler;
            runtimeWristDebugUpperarmRightEuler = runtimeUpperarmRightEuler;
            runtimeWristDebugUpperarmLeftPosition = runtimeUpperarmLeftPosition;
            runtimeWristDebugUpperarmRightPosition = runtimeUpperarmRightPosition;
            runtimeWristDebugLowerarmLeftEuler = runtimeLowerarmLeftEuler;
            runtimeWristDebugLowerarmRightEuler = runtimeLowerarmRightEuler;
            runtimeWristDebugLowerarmLeftPosition = runtimeLowerarmLeftPosition;
            runtimeWristDebugLowerarmRightPosition = runtimeLowerarmRightPosition;
            runtimeWristDebugTwistLeftEuler = runtimeTwistLeftEuler;
            runtimeWristDebugTwistRightEuler = runtimeTwistRightEuler;
            runtimeWristDebugTwistLeftPosition = runtimeTwistLeftPosition;
            runtimeWristDebugTwistRightPosition = runtimeTwistRightPosition;
            runtimeWristDebugHandLeftEuler = runtimeHandLeftEuler;
            runtimeWristDebugHandRightEuler = runtimeHandRightEuler;
            runtimeWristDebugHandLeftPosition = runtimeHandLeftPosition;
            runtimeWristDebugHandRightPosition = runtimeHandRightPosition;
            preserveRuntimeWristDebugHandGrip = preserveHandGrip;
            applyRuntimeWristDebugHandOffsetWhenPreservingGrip = allowHandOffsetWhenPreservingGrip;
            logMissingWristDebugBones = logMissingRuntimeWristBones;
            _hasCachedWristDebugBones = false;
            _hasWarnedMissingWristDebugBones = false;
        }

        public void ApplyRuntimeWristDebugSettings(bool enabled, float weight, Vector3 upperarmLeftEuler,
            Vector3 upperarmRightEuler, Vector3 upperarmLeftPosition, Vector3 upperarmRightPosition,
            Vector3 lowerarmLeftEuler, Vector3 lowerarmRightEuler, Vector3 lowerarmLeftPosition,
            Vector3 lowerarmRightPosition, Vector3 twistLeftEuler, Vector3 twistRightEuler, Vector3 twistLeftPosition,
            Vector3 twistRightPosition, Vector3 handLeftEuler, Vector3 handRightEuler, Vector3 handLeftPosition,
            Vector3 handRightPosition, bool preserveHandGrip, bool allowHandOffsetWhenPreservingGrip,
            bool logMissingBones) {
            enableRuntimeWristDebugOverride = enabled;
            runtimeWristDebugOverrideWeight = Mathf.Clamp01(weight);
            runtimeWristDebugUpperarmLeftEuler = upperarmLeftEuler;
            runtimeWristDebugUpperarmRightEuler = upperarmRightEuler;
            runtimeWristDebugUpperarmLeftPosition = upperarmLeftPosition;
            runtimeWristDebugUpperarmRightPosition = upperarmRightPosition;
            runtimeWristDebugLowerarmLeftEuler = lowerarmLeftEuler;
            runtimeWristDebugLowerarmRightEuler = lowerarmRightEuler;
            runtimeWristDebugLowerarmLeftPosition = lowerarmLeftPosition;
            runtimeWristDebugLowerarmRightPosition = lowerarmRightPosition;
            runtimeWristDebugTwistLeftEuler = twistLeftEuler;
            runtimeWristDebugTwistRightEuler = twistRightEuler;
            runtimeWristDebugTwistLeftPosition = twistLeftPosition;
            runtimeWristDebugTwistRightPosition = twistRightPosition;
            runtimeWristDebugHandLeftEuler = handLeftEuler;
            runtimeWristDebugHandRightEuler = handRightEuler;
            runtimeWristDebugHandLeftPosition = handLeftPosition;
            runtimeWristDebugHandRightPosition = handRightPosition;
            preserveRuntimeWristDebugHandGrip = preserveHandGrip;
            applyRuntimeWristDebugHandOffsetWhenPreservingGrip = allowHandOffsetWhenPreservingGrip;
            logMissingWristDebugBones = logMissingBones;

            if(enableRuntimeWristDebugOverride) return;
            _hasWarnedMissingWristDebugBones = false;
        }

        public bool InitializeIfNeeded(int renderLayer) {
            _renderLayer = renderLayer;
            _weaponManager ??= GetComponentInParent<WeaponManager>();

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
            ApplyWristCorrectionLayerWeight();

            if(tagArmsForLegacyHooks) {
                TryTagArmRoot();
            }

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
        }

        public void PlayFireAnimation() {
            if(!TryCacheActiveWeapon()) return;
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

        public bool IsKinemationSoundEventRoutingEnabled() {
            if(!routeWeaponSoundEventsToAudioService) {
                return false;
            }

            if(!TryCacheActiveWeapon() || _activeWeapon == null || _activeWeapon.weaponSettings == null) {
                return false;
            }

            return true;
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
            SnapAnimatorToIdle(FpsWeaponCharacterAnimatorField?.GetValue(_activeWeapon) as Animator);
            SnapAnimatorToIdle(FpsWeaponAnimatorField?.GetValue(_activeWeapon) as Animator);
            StopActiveWeaponAudioPlayback();
            ResetReloadTracking();
            ClearPendingWeaponSoundEvents();
        }

        public string GetKinemationFireSoundId() {
            if(!TryCacheActiveWeapon()) return string.Empty;
            return _activeWeaponFireSoundId;
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
                if(equipProgress >= equipUnlockNormalizedTime) {
                    _isTrackingEquip = false;
                    return false;
                }
                return true;
            }

            if(_equipHasBeenActive && Time.time - _lastEquipSignalTime <= EquipSignalGraceSeconds) {
                return true;
            }

            if(!_equipHasBeenActive) {
                return Time.time - _equipTrackStartTime < EquipEnterGraceSeconds;
            }

            _isTrackingEquip = false;
            return false;
        }

        public void ResetEquipTracking() {
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

            _weaponManager ??= GetComponentInParent<WeaponManager>();
            if(_weaponManager == null) return;
            _weaponManager.HandleKinemationEquipCompleted();
        }

        public Transform GetMuzzleTransform() {
            TryCacheActiveWeapon();
            return _muzzleTransform;
        }

        public bool AreKinemationSoundsEnabled() {
            if(disableKinemationWeaponSounds || routeWeaponSoundEventsToAudioService) {
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
            if(candidateCount == 0) return null;
            if(candidateCount == 1) return candidates[0];

            var aimPoint = activeWeapon.aimPoint;
            if(aimPoint == null) {
                return candidates[0];
            }

            Transform best = null;
            var bestScore = float.NegativeInfinity;
            for(var i = 0; i < candidates.Count; i++) {
                var candidate = candidates[i];
                if(candidate == null) continue;

                var offset = candidate.position - aimPoint.position;
                var forwardScore = Vector3.Dot(aimPoint.forward, offset);
                var lateralDistance = Vector3.Cross(aimPoint.forward, offset).magnitude;
                var score = forwardScore - (lateralDistance * 0.05f);

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
            if(_activeWeapon != null && _muzzleTransform != null) {
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

                if(_renderLayer >= 0) {
                    SetLayerRecursive(_playerInstance, _renderLayer);
                }
                DisableViewmodelShadows(_playerInstance);
                AttachReloadEventRelays();
            }

            _muzzleTransform ??= ResolveMuzzleTransform(_activeWeapon);
            ApplyActiveWeaponSoundToggles(_activeWeapon);
            RefreshActiveWeaponSoundMetadata(_activeWeapon);
            SuppressInternalMuzzleFx(_activeWeapon);
            ApplyWristCorrectionLayerWeight();
            return _activeWeapon != null;
        }

        private void ApplyWristCorrectionLayerWeight() {
            if(!applyWristCorrectionLayer || _fpsAnimator == null) return;
            if(string.IsNullOrWhiteSpace(wristCorrectionLayerName)) return;

            var controller = _fpsAnimator.runtimeAnimatorController;
            if(controller == null) return;

            if(_cachedWristCorrectionController != controller) {
                _cachedWristCorrectionController = controller;
                _cachedWristCorrectionLayerIndex = -2;
                _hasWarnedMissingWristCorrectionLayer = false;
            }

            if(_cachedWristCorrectionLayerIndex == -2) {
                _cachedWristCorrectionLayerIndex = _fpsAnimator.GetLayerIndex(wristCorrectionLayerName);
                if(_cachedWristCorrectionLayerIndex < 0) {
                    if(logMissingWristCorrectionLayer && !_hasWarnedMissingWristCorrectionLayer) {
                        _hasWarnedMissingWristCorrectionLayer = true;
                    }
                    return;
                }
            }

            if(_cachedWristCorrectionLayerIndex < 0 || _cachedWristCorrectionLayerIndex >= _fpsAnimator.layerCount) return;
            _fpsAnimator.SetLayerWeight(_cachedWristCorrectionLayerIndex, Mathf.Clamp01(wristCorrectionLayerWeight));
        }

        private void ApplyRuntimeWristDebugOverride() {
            if(!enableRuntimeWristDebugOverride || _playerInstance == null) return;

            var weight = Mathf.Clamp01(runtimeWristDebugOverrideWeight);
            if(weight <= 0f) return;

            CacheWristDebugBonesIfNeeded();

            ApplyRuntimeWristSideOverride(
                _wristDebugUpperarmLeft,
                _wristDebugLowerarmLeft,
                _wristDebugTwistLeft,
                _wristDebugHandLeft,
                runtimeWristDebugUpperarmLeftEuler,
                runtimeWristDebugUpperarmLeftPosition,
                runtimeWristDebugLowerarmLeftEuler,
                runtimeWristDebugLowerarmLeftPosition,
                runtimeWristDebugTwistLeftEuler,
                runtimeWristDebugTwistLeftPosition,
                runtimeWristDebugHandLeftEuler,
                runtimeWristDebugHandLeftPosition,
                weight
            );
            ApplyRuntimeWristSideOverride(
                _wristDebugUpperarmRight,
                _wristDebugLowerarmRight,
                _wristDebugTwistRight,
                _wristDebugHandRight,
                runtimeWristDebugUpperarmRightEuler,
                runtimeWristDebugUpperarmRightPosition,
                runtimeWristDebugLowerarmRightEuler,
                runtimeWristDebugLowerarmRightPosition,
                runtimeWristDebugTwistRightEuler,
                runtimeWristDebugTwistRightPosition,
                runtimeWristDebugHandRightEuler,
                runtimeWristDebugHandRightPosition,
                weight
            );
        }

        private void CacheWristDebugBonesIfNeeded() {
            if(_hasCachedWristDebugBones || _playerInstance == null) return;

            var root = _playerInstance.transform;
            TryFindChildByName(root, "upperarm_l", out _wristDebugUpperarmLeft);
            TryFindChildByName(root, "upperarm_r", out _wristDebugUpperarmRight);
            TryFindChildByName(root, "lowerarm_l", out _wristDebugLowerarmLeft);
            TryFindChildByName(root, "lowerarm_r", out _wristDebugLowerarmRight);
            TryFindChildByName(root, "lowerarm_twist_01_l", out _wristDebugTwistLeft);
            TryFindChildByName(root, "lowerarm_twist_01_r", out _wristDebugTwistRight);
            TryFindChildByName(root, "hand_l", out _wristDebugHandLeft);
            TryFindChildByName(root, "hand_r", out _wristDebugHandRight);

            _hasCachedWristDebugBones = true;
            if(!logMissingWristDebugBones || _hasWarnedMissingWristDebugBones) return;
            if(_wristDebugUpperarmLeft != null && _wristDebugUpperarmRight != null &&
               _wristDebugLowerarmLeft != null && _wristDebugLowerarmRight != null &&
               _wristDebugTwistLeft != null && _wristDebugTwistRight != null &&
               _wristDebugHandLeft != null && _wristDebugHandRight != null) {
                return;
            }

            _hasWarnedMissingWristDebugBones = true;
        }

        private void ApplyRuntimeWristSideOverride(Transform upperarm, Transform lowerarm, Transform twist, Transform hand,
            Vector3 upperarmEuler, Vector3 upperarmPosition, Vector3 lowerarmEuler, Vector3 lowerarmPosition,
            Vector3 twistEuler, Vector3 twistPosition, Vector3 handEuler, Vector3 handPosition, float weight) {
            var preserveHandGrip = preserveRuntimeWristDebugHandGrip && hand != null;
            Vector3 cachedHandPosition = default;
            Quaternion cachedHandRotation = default;

            if(preserveHandGrip) {
                cachedHandPosition = hand.position;
                cachedHandRotation = hand.rotation;
            }

            ApplyLocalPositionOffset(upperarm, upperarmPosition, weight);
            ApplyLocalRotationOffset(upperarm, upperarmEuler, weight);
            ApplyLocalPositionOffset(lowerarm, lowerarmPosition, weight);
            ApplyLocalRotationOffset(lowerarm, lowerarmEuler, weight);
            ApplyLocalPositionOffset(twist, twistPosition, weight);
            ApplyLocalRotationOffset(twist, twistEuler, weight);

            if(preserveHandGrip) {
                hand.SetPositionAndRotation(cachedHandPosition, cachedHandRotation);
                if(!applyRuntimeWristDebugHandOffsetWhenPreservingGrip) {
                    return;
                }
            }

            ApplyLocalPositionOffset(hand, handPosition, weight);
            ApplyLocalRotationOffset(hand, handEuler, weight);
        }

        private static void ApplyLocalPositionOffset(Transform bone, Vector3 positionOffset, float weight) {
            if(bone == null || positionOffset.sqrMagnitude <= 0.00000001f || weight <= 0f) return;
            bone.localPosition += positionOffset * weight;
        }

        private static void ApplyLocalRotationOffset(Transform bone, Vector3 eulerOffset, float weight) {
            if(bone == null || eulerOffset.sqrMagnitude <= 0.000001f || weight <= 0f) return;
            var delta = Quaternion.Euler(eulerOffset * weight);
            bone.localRotation = bone.localRotation * delta;
        }

        private void ApplyActiveWeaponSoundToggles(FPSWeapon activeWeapon) {
            if(activeWeapon == null) return;

            var weaponSounds = activeWeapon.GetComponentsInChildren<FPSWeaponSound>(true);
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
                return;
            }

            var settings = activeWeapon.weaponSettings;
            _activeWeaponSoundKey = KinemationSoundIdUtility.BuildWeaponSoundKey(settings, activeWeapon.name);
            _activeWeaponFireSoundId = settings != null && HasAnyValidAudioClip(settings.fireSounds)
                ? KinemationSoundIdUtility.BuildFireSoundId(_activeWeaponSoundKey)
                : string.Empty;
        }

        private void SuppressInternalMuzzleFx(FPSWeapon activeWeapon) {
            if(!disableKinemationInternalMuzzleFx || activeWeapon == null) return;

            var disabledParticles = 0;
            var disabledVfx = 0;
            var disabledLights = 0;

            var particleSystems = activeWeapon.GetComponentsInChildren<ParticleSystem>(true);
            foreach(var ps in particleSystems) {
                if(ps == null || !IsLikelyMuzzleFxNode(ps.transform)) continue;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var emission = ps.emission;
                emission.enabled = false;
                disabledParticles++;
            }

            var vfxComponents = activeWeapon.GetComponentsInChildren<VisualEffect>(true);
            foreach(var vfx in vfxComponents) {
                if(vfx == null || !IsLikelyMuzzleFxNode(vfx.transform)) continue;
                vfx.Stop();
                vfx.enabled = false;
                disabledVfx++;
            }

            var lights = activeWeapon.GetComponentsInChildren<Light>(true);
            foreach(var light in lights) {
                if(light == null || !IsLikelyMuzzleFxNode(light.transform)) continue;
                light.enabled = false;
                disabledLights++;
            }

            if(disabledParticles > 0 || disabledVfx > 0 || disabledLights > 0) {
                var weaponId = activeWeapon.gameObject.GetInstanceID();
                _suppressedMuzzleFxWeaponIds.Add(weaponId);
            }
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
            for(var i = 0; i < clips.Count; i++) {
                if(clips[i] != null) {
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

            if(_activeWeapon.weaponSettings != null) {
                maxAmmo = Mathf.Max(1, _activeWeapon.weaponSettings.ammo);
            } else {
                maxAmmo = Mathf.Max(1, authoritativeAmmo);
            }

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
            var pdwAnimations = _activeWeapon.GetComponentsInChildren<Pdw90Animation>(true);
            foreach(var pdwAnimation in pdwAnimations) {
                if(pdwAnimation == null) continue;
                Pdw90SmoothAmmoWeightField?.SetValue(pdwAnimation, targetWeight);
            }
        }

        private static void SnapAnimatorToIdle(Animator animator) {
            if(animator == null || animator.runtimeAnimatorController == null) return;

            if(animator.HasState(0, IdleHash)) {
                animator.Play(IdleHash, 0, 0f);
            } else {
                animator.Rebind();
            }

            animator.Update(0f);
        }

        private void StopActiveWeaponAudioPlayback() {
            if(_weaponAudioSource != null) {
                _weaponAudioSource.Stop();
            }

            if(_activeWeapon == null) return;
            var audioSources = _activeWeapon.GetComponentsInChildren<AudioSource>(true);
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

        private bool TryGetEquipStateProgress(out float normalizedProgress) {
            normalizedProgress = 0f;

            if(TryGetAnimatorEquipProgress(_fpsAnimator, out var characterProgress)) {
                normalizedProgress = characterProgress;
                return true;
            }

            if(_activeWeapon == null) {
                return false;
            }

            var weaponAnimators = _activeWeapon.GetComponentsInChildren<Animator>(true);
            foreach(var weaponAnimator in weaponAnimators) {
                if(weaponAnimator == null || weaponAnimator == _fpsAnimator) continue;
                if(TryGetAnimatorEquipProgress(weaponAnimator, out var weaponProgress)) {
                    normalizedProgress = Mathf.Max(normalizedProgress, weaponProgress);
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
                if(nextState.shortNameHash == EquipHash || nextState.shortNameHash == EquipOverrideHash) {
                    normalizedProgress = Mathf.Max(normalizedProgress, Mathf.Clamp01(nextState.normalizedTime));
                    return true;
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
            ApplyWristCorrectionLayerWeight();
        }

        private void LateUpdate() {
            ApplyRuntimeWristDebugOverride();
        }

        private void OnDestroy() {
            if(_runtimePlayerSettings != null) {
                Destroy(_runtimePlayerSettings);
                _runtimePlayerSettings = null;
            }
        }
    }
}

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
        [SerializeField] private bool syncLookPitchWithPlayer;
        [SerializeField] private bool syncAirborneState;
        [SerializeField] private bool freezeLocomotionInAir = true;
        [SerializeField] private bool forceWalkAnimationWhileSprinting = true;
        [SerializeField, Range(0f, 1.99f)] private float sprintWalkGaitValue = 1.2f;
        [SerializeField, Range(0f, 1f)] private float equipUnlockNormalizedTime = 0.82f;
        [SerializeField] private bool logDrakeAmmoEjectDebug;

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
        private bool _drakeTopShellEjectedSinceReloadComplete;
        private bool _drakeShotCanceledReloadAfterAmmoEject;
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
        private float _equipTrackStartTime;
        private float _lastEquipSignalTime;
        private bool _hasCachedWristDebugBones;
        private Transform _wristDebugUpperarmLeft;
        private Transform _wristDebugTwistLeft;
        private Transform _wristDebugHandLeft;
        private readonly HashSet<int> _suppressedMuzzleFxWeaponIds = new();
        private bool _suppressDrakeTopShellEjectOnNextReload;
        private Transform _suppressedDrakeTopShellTransform;
        private Vector3 _suppressedDrakeTopShellOriginalLocalPosition;
        private bool _hasSuppressedDrakeTopShellOriginalLocalPosition;
        private Vector3 _suppressedDrakeTopShellOriginalLocalScale;
        private bool _hasSuppressedDrakeTopShellOriginalLocalScale;
        private Renderer[] _suppressedDrakeTopShellRenderers;
        private bool[] _suppressedDrakeTopShellRendererEnabledStates;
        private bool _isDrakeTopShellSuppressionApplied;
        private const float ReloadEnterGraceSeconds = 0.2f;
        private const float ReloadSignalGraceSeconds = 0.25f;
        private const float EquipEnterGraceSeconds = 0.2f;
        private const float EquipSignalGraceSeconds = 0.05f;
        private const float DrakeTopShellHideOffset = 0.75f;
        private const string DrakeTopShellName = "12Gauge1";
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
        private static readonly Vector3 FixedUpperarmLeftPositionOffset = new(0f, 0.027f, 0f);
        private static readonly Vector3 FixedTwistLeftEulerOffset = new(0f, -7.5f, 0f);

        public void Configure(GameObject playerPrefab, GameObject fpWeaponPrefab, bool disableWeaponSounds,
            bool disablePlayerSounds, bool routeWeaponSoundEvents, bool syncLookPitch,
            bool syncInAirState, bool freezeAirLocomotion, bool forceWalkWhileSprinting,
            float sprintGaitValue,
            float equipUnlockNormalizedProgress,
            bool enableDrakeAmmoEjectDebug = false) {
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
            logDrakeAmmoEjectDebug = enableDrakeAmmoEjectDebug;
            _hasCachedWristDebugBones = false;
        }

        public bool IsDrakeShellDebugEnabled() {
            return logDrakeAmmoEjectDebug;
        }

        private void LogDrakeDebug(string message) {
            if(!logDrakeAmmoEjectDebug) return;
            Debug.Log($"[KinemationFpWeaponDriver] {message}", this);
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
            LogDrakeDebug(
                $"PlayReloadAnimation start. frame={Time.frameCount} time={Time.time:F3} " +
                $"suppressNextReload={_suppressDrakeTopShellEjectOnNextReload} " +
                $"ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete} " +
                $"shotCanceledAfterEject={_drakeShotCanceledReloadAfterAmmoEject} appliedNow={_isDrakeTopShellSuppressionApplied}");

            var shouldHideTopShellForThisReload = IsActiveWeaponLikelyDrake() &&
                                                  _drakeTopShellEjectedSinceReloadComplete &&
                                                  _drakeShotCanceledReloadAfterAmmoEject;
            if(_suppressDrakeTopShellEjectOnNextReload || shouldHideTopShellForThisReload) {
                SuppressDrakeTopShellForReloadStart();
                LogDrakeDebug(
                    $"PlayReloadAnimation applying suppressed top shell. frame={Time.frameCount} time={Time.time:F3} " +
                    $"appliedNow={_isDrakeTopShellSuppressionApplied}");
            }

            _suppressDrakeTopShellEjectOnNextReload = false;
            _drakeShotCanceledReloadAfterAmmoEject = false;

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
            LogDrakeDebug(
                $"MarkReloadCanceledByShot. frame={Time.frameCount} time={Time.time:F3} " +
                $"ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete}");
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

            var weaponAnimators = _activeWeapon.GetComponentsInChildren<Animator>(true);
            foreach(var weaponAnimator in weaponAnimators) {
                AddUniqueAnimator(animators, weaponAnimator);
            }

            for(var i = 0; i < animators.Count; i++) {
                SnapAnimatorToIdle(animators[i], forceRebindIfReloadStillActive: true);
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
                if(logDrakeAmmoEjectDebug) {
                    Debug.LogWarning(
                        $"[KinemationFpWeaponDriver] Drake suppression target not found. frame={Time.frameCount} time={Time.time:F3}",
                        this);
                }
                return;
            }

            // Keep top shell hidden for this reload start when consumed by the two-flag rule.
            ApplyDrakeTopShellSuppressionNow();

            if(logDrakeAmmoEjectDebug) {
                Debug.Log(
                    $"[KinemationFpWeaponDriver] Drake reload start. topShellHidden={_isDrakeTopShellSuppressionApplied}. " +
                    $"target={_suppressedDrakeTopShellTransform.name} frame={Time.frameCount} time={Time.time:F3} " +
                    $"suppressNextReload={_suppressDrakeTopShellEjectOnNextReload} " +
                    $"ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete} " +
                    $"shotCanceledAfterEject={_drakeShotCanceledReloadAfterAmmoEject}",
                    this);
            }
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
            if(shellRenderers != null && shellRenderers.Length > 0) {
                _suppressedDrakeTopShellRenderers = shellRenderers;
                _suppressedDrakeTopShellRendererEnabledStates = new bool[shellRenderers.Length];
                for(var i = 0; i < shellRenderers.Length; i++) {
                    var renderer = shellRenderers[i];
                    if(renderer == null) continue;
                    _suppressedDrakeTopShellRendererEnabledStates[i] = renderer.enabled;
                }
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
                for(var i = 0; i < _suppressedDrakeTopShellRenderers.Length; i++) {
                    var renderer = _suppressedDrakeTopShellRenderers[i];
                    if(renderer == null) continue;
                    renderer.enabled = false;
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
                    var renderer = _suppressedDrakeTopShellRenderers[i];
                    if(renderer == null) continue;
                    renderer.enabled = _suppressedDrakeTopShellRendererEnabledStates[i];
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

        private bool TryResolveDrakeTopShellTransform(out Transform topShellTransform) {
            topShellTransform = null;
            if(_activeWeapon == null) return false;

            var transforms = _activeWeapon.GetComponentsInChildren<Transform>(true);
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
                if(!string.Equals(candidate.name, targetName, System.StringComparison.OrdinalIgnoreCase)) continue;
                resolved = candidate;
                return true;
            }

            return false;
        }

        private bool IsActiveWeaponLikelyDrake() {
            if(_activeWeapon == null) return false;

            if(!string.IsNullOrWhiteSpace(_activeWeaponSoundKey) &&
               _activeWeaponSoundKey.IndexOf("drake", System.StringComparison.OrdinalIgnoreCase) >= 0) {
                return true;
            }

            if(!string.IsNullOrWhiteSpace(_activeWeapon.name) &&
               _activeWeapon.name.IndexOf("drake", System.StringComparison.OrdinalIgnoreCase) >= 0) {
                return true;
            }

            if(_activeWeapon.weaponSettings != null &&
               !string.IsNullOrWhiteSpace(_activeWeapon.weaponSettings.name) &&
               _activeWeapon.weaponSettings.name.IndexOf("drake", System.StringComparison.OrdinalIgnoreCase) >= 0) {
                return true;
            }

            return false;
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

            if(_activeWeapon.weaponSettings != null) {
                return Mathf.Max(0, _activeWeapon.weaponSettings.ammo);
            }

            return 0;
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
            _lastReloadSingleEventFrame = -1;
            LogDrakeDebug(
                $"ResetReloadTracking. frame={Time.frameCount} time={Time.time:F3} " +
                $"ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete} appliedNow={_isDrakeTopShellSuppressionApplied} " +
                $"suppressNextReload={_suppressDrakeTopShellEjectOnNextReload}");
        }

        public void NotifyReloadSingleEvent() {
            if(!_isTrackingReload) return;
            if(Time.frameCount == _lastReloadSingleEventFrame) return;

            _lastReloadSingleEventFrame = Time.frameCount;
            _reloadHasReceivedAnyEvent = true;
            _reloadHasBeenActive = true;
            _lastReloadSignalTime = Time.time;
            _pendingReloadSingleEvents++;
        }

        public void NotifyAmmoEjectEvent() {
            if(IsActiveWeaponLikelyDrake()) {
                _drakeTopShellEjectedSinceReloadComplete = true;
                _drakeShotCanceledReloadAfterAmmoEject = false;
            }

            if(logDrakeAmmoEjectDebug && IsActiveWeaponLikelyDrake()) {
                var targetName = _suppressedDrakeTopShellTransform != null
                    ? _suppressedDrakeTopShellTransform.name
                    : "(none)";
                Debug.Log(
                    $"[KinemationFpWeaponDriver] AmmoEject(pre) frame={Time.frameCount} time={Time.time:F3} " +
                    $"trackingReload={_isTrackingReload} " +
                    $"applied={_isDrakeTopShellSuppressionApplied} target={targetName} " +
                    $"ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete} " +
                    $"shotCanceledAfterEject={_drakeShotCanceledReloadAfterAmmoEject}",
                    this);
            }

            if(logDrakeAmmoEjectDebug && IsActiveWeaponLikelyDrake()) {
                var targetName = _suppressedDrakeTopShellTransform != null
                    ? _suppressedDrakeTopShellTransform.name
                    : "(none)";
                Debug.Log(
                    $"[KinemationFpWeaponDriver] AmmoEject(post) frame={Time.frameCount} time={Time.time:F3} " +
                    $"trackingReload={_isTrackingReload} " +
                    $"applied={_isDrakeTopShellSuppressionApplied} target={targetName} " +
                    $"ejectedSinceComplete={_drakeTopShellEjectedSinceReloadComplete} " +
                    $"shotCanceledAfterEject={_drakeShotCanceledReloadAfterAmmoEject}",
                    this);
            }

            if(!_isTrackingReload) return;
            _reloadHasReceivedAnyEvent = true;
            _reloadHasBeenActive = true;
            _lastReloadSignalTime = Time.time;
        }

        public void NotifyShellShowEvent() {
            if(!IsActiveWeaponLikelyDrake()) return;

            LogDrakeDebug(
                $"NotifyShellShowEvent. frame={Time.frameCount} time={Time.time:F3} " +
                $"appliedBeforeShow={_isDrakeTopShellSuppressionApplied}");

            _drakeTopShellEjectedSinceReloadComplete = false;
            _drakeShotCanceledReloadAfterAmmoEject = false;
            _suppressDrakeTopShellEjectOnNextReload = false;
            RestoreDrakeTopShellImmediate();
        }

        public void NotifyReloadCompleteEvent() {
            if(IsActiveWeaponLikelyDrake()) {
                LogDrakeDebug(
                    $"NotifyReloadCompleteEvent restoring shell. frame={Time.frameCount} time={Time.time:F3} " +
                    $"appliedBeforeRestore={_isDrakeTopShellSuppressionApplied}");
                _drakeTopShellEjectedSinceReloadComplete = false;
                _drakeShotCanceledReloadAfterAmmoEject = false;
                _suppressDrakeTopShellEjectOnNextReload = false;
                RestoreDrakeTopShellImmediate();
            }

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
            TryFindChildByName(root, "upperarm_l", out _wristDebugUpperarmLeft);
            TryFindChildByName(root, "lowerarm_twist_01_l", out _wristDebugTwistLeft);
            TryFindChildByName(root, "hand_l", out _wristDebugHandLeft);
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
        }

        private void LateUpdate() {
            ApplyFixedWristOffsets();
            ApplySuppressedDrakeTopShellPose();
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
            for(var i = 0; i < _suppressedDrakeTopShellRenderers.Length; i++) {
                var renderer = _suppressedDrakeTopShellRenderers[i];
                if(renderer == null) continue;
                if(renderer.enabled) {
                    renderer.enabled = false;
                }
            }
        }

        private void OnDestroy() {
            RestoreDrakeTopShellImmediate();
            if(_runtimePlayerSettings != null) {
                Destroy(_runtimePlayerSettings);
                _runtimePlayerSettings = null;
            }
        }
    }
}

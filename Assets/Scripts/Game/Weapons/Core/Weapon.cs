using System.Collections.Generic;
using Audio.Networking;
using Game.Menu;
using Game.Player;
using Game.UI;
using Network.Events;
using Network.Rpc;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.VFX;

namespace Game.Weapons {
    public partial class Weapon : NetworkBehaviour {
        public const float MaxDamageMultiplier = 3f;

        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private CinemachineCamera _fpCamera;
        private Animator _playerAnimator;
        private LayerMask _enemyLayer;
        private LayerMask _worldLayer;
        private NetworkDamageRelay _damageRelay;
        private NetworkFxRelay _networkFXRelay;
        private NetworkAudioRelay _audioRelay;
        private WeaponManager _weaponManager;

        private int _currentMagCapacity = 1;

        private GameObject _currentFpWeaponInstance;
        private GameObject _currentWorldWeaponInstance;
        private WorldWeaponBinding _currentWorldWeaponBinding;
        private Transform _fpMuzzleTransform;
        private Transform _worldMuzzleTransform;
        private KinemationFpWeaponDriver _kinemationFpWeaponDriver;
        private GameObject _fpMuzzleLight;
        private GameObject _worldMuzzleLight;
        private GameObject _kinemationLocalMuzzleFxInstance;
        private VisualEffect _kinemationLocalMuzzleVfx;
        private GameObject _kinemationLocalMuzzleSourcePrefab;

        [Header("Runtime State")]
        public int currentAmmo;

        private bool IsReloading { get; set; }
        public bool IsReloadInProgress => IsReloading;

        public NetworkVariable<float> netCurrentDamageMultiplier = new(1f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public float CurrentDamageMultiplier {
            get => netCurrentDamageMultiplier.Value;
            set {
                if(!IsOwner) return;
                // Throttle network updates - only send if enough time has passed or value changed significantly
                // At 90Hz: 5 ticks = ~55ms
                const float damageMultiplierUpdateInterval = 0.055f;
                const float changeThreshold = 0.05f; // 5% change threshold

                var shouldUpdate = _lastDamageMultiplierUpdateTime == 0f ||
                                   Time.time - _lastDamageMultiplierUpdateTime >= damageMultiplierUpdateInterval ||
                                   Mathf.Abs(netCurrentDamageMultiplier.Value - value) > changeThreshold;

                if(!shouldUpdate) return;
                netCurrentDamageMultiplier.Value = value;
                _lastDamageMultiplierUpdateTime = Time.time;
            }
        }

        // Throttling for damage multiplier updates
        private float _lastDamageMultiplierUpdateTime;

        [Header("Speed Damage Scaling")]
        private const float MinSpeedThreshold = 15f;

        private const float MaxSpeedThreshold = 28f;

        private const float MultiplierDecayRate = 4.5f;
        private const float MultiplierGainRate = 2f;

        private const float MultiplierGracePeriod = 1f;

        [Header("Visual Settings")]
        private const float BulletSpeed = 500f;

        private const float MuzzleLightTime = 5f;
        private float _fpLightOffTime;
        private float _worldLightOffTime;

        #region Private Fields

        private float _lastFireTime;
        private float _peakDamageMultiplier = 1f;
        private float _lastPeakTime;
        private bool _autoReloadArmed;
        private float _reloadExpectedCompleteTime;
        private float _nextReloadRecoveryAllowedTime;
        private readonly List<int> _kinemationWeaponSoundEventBuffer = new();
        private float _kinemationReloadFallbackDeadline;

        private const float ReloadRecoveryCooldownSeconds = 0.5f;
        private const float KinemationReloadFallbackSeconds = 5f;
        private const float TracerPerpendicularVelocityInheritanceScale = 1f;
        private const float TracerPerpendicularVelocityInheritanceMax = 24f;
        private const float TracerPerpendicularVelocityFadeExponent = 1f;

        // Bullet trail pooling
        private readonly Queue<TrailRenderer> _trailPool = new();
        private const int TrailPoolSize = 30;
        private bool _hasPrewarmedKinemationMuzzleForCurrentWeapon;
        private bool _hasLocalMuzzleFlashSpawnPositionForShot;
        private Vector3 _localMuzzleFlashSpawnPositionForShot;

        #endregion

        #region Animation Hashes

        private static readonly int RecoilHash = Animator.StringToHash("Recoil");
        private static readonly int ReloadHash = Animator.StringToHash("Reload");

        #endregion

        #region Unity Lifecycle

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[Weapon] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_fpCamera == null) _fpCamera = playerController.FpCamera;
            if(_playerAnimator == null) _playerAnimator = playerController.PlayerAnimator;
            _enemyLayer = playerController.EnemyLayer;
            _worldLayer = playerController.WorldLayer;
            if(_damageRelay == null) _damageRelay = playerController.DamageRelay;
            if(_networkFXRelay == null) _networkFXRelay = playerController.FxRelay;
            if(_audioRelay == null) _audioRelay = playerController.AudioRelay;
            if(_weaponManager == null) _weaponManager = playerController.WeaponManager;

            _lastFireTime = Time.time;

            if(_damageRelay == null) return;
            _damageRelay.OnHitConfirm -= OnHitConfirm;
            _damageRelay.OnHitConfirm += OnHitConfirm;
        }

        private void LateUpdate() {
            UpdateKinemationReloadState();
            ProcessKinemationSoundEvents();
            RunReloadWatchdog();

            if(_fpMuzzleLight != null && _fpMuzzleLight.activeSelf && Time.time >= _fpLightOffTime) {
                _fpMuzzleLight.SetActive(false);
            }

            // Turn off 3P light when time is up
            if(_worldMuzzleLight != null && _worldMuzzleLight.activeSelf && Time.time >= _worldLightOffTime) {
                _worldMuzzleLight.SetActive(false);
            }
        }

        private void Update() {
            TryPrewarmKinemationMuzzleIfNeeded();
            SyncKinemationLocomotion();
        }

        private void TryPrewarmKinemationMuzzleIfNeeded() {
            if(_hasPrewarmedKinemationMuzzleForCurrentWeapon) return;
            if(_kinemationFpWeaponDriver == null) return;
            if(playerController == null || !playerController.IsOwner) return;
            PrewarmKinemationLocalMuzzleFxInstance();
        }

        public override void OnDestroy() {
            if(_damageRelay != null) {
                _damageRelay.OnHitConfirm -= OnHitConfirm;
            }

            ClearKinemationLocalMuzzleFxInstance();
            base.OnDestroy();
        }

        private void SyncKinemationLocomotion() {
            if(_kinemationFpWeaponDriver == null || playerController == null || !playerController.IsOwner) {
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

            _kinemationFpWeaponDriver.SyncLocomotion(
                moveInput,
                sprintInput,
                tacticalSprinting: false,
                isGrounded: treatedGrounded,
                lookPitchDegrees: lookPitch
            );
        }

        private static void OnHitConfirm(bool wasKill) {
            if(Audio2.AudioService.Instance == null) return;

            var soundId = wasKill ? "ui.hit.hitmarker.kill" : "ui.hit.hitmarker.hit";
            Audio2.AudioService.Instance.Play(soundId, Vector3.zero);
        }

        #endregion

        #region Weapon Switching

        /// <summary>
        /// Called from FP weapon animation event when pull out animation completes.
        /// Releases control by clearing IsPullingOut flag.
        /// </summary>
        public void OnPullOutCompleted() {
            if(_weaponManager != null)
                _weaponManager.HandlePullOutCompleted();
        }

        /// <summary>
        /// Switch to a new weapon by loading its data
        /// </summary>
        public void SwitchToWeapon(WeaponData newWeaponData, GameObject fpWeaponInstance,
            GameObject worldWeaponInstance, int restoredAmmo, int magCapacity) {
            // Cancel any ongoing reload (this will also stop the reload sound)
            if(IsReloading) {
                CancelReload();
            }

            ClearKinemationLocalMuzzleFxInstance();

            // Set new weapon data
            CurrentWeaponData = newWeaponData;
            _currentFpWeaponInstance = fpWeaponInstance;
            _currentWorldWeaponInstance = worldWeaponInstance;
            _currentWorldWeaponBinding = _currentWorldWeaponInstance != null
                ? _currentWorldWeaponInstance.GetComponent<WorldWeaponBinding>()
                : null;
            _currentMagCapacity = Mathf.Max(1, magCapacity);
            _kinemationFpWeaponDriver = _currentFpWeaponInstance != null
                ? _currentFpWeaponInstance.GetComponent<KinemationFpWeaponDriver>()
                : null;

            if(_kinemationFpWeaponDriver != null) {
                var fpLayer = playerController != null && playerController.IsOwner
                    ? LayerMask.NameToLayer("Weapon")
                    : LayerMask.NameToLayer("Masked");
                _kinemationFpWeaponDriver.InitializeIfNeeded(fpLayer);
                _kinemationFpWeaponDriver.ClearPendingWeaponSoundEvents();
            }

            _fpMuzzleTransform = _kinemationFpWeaponDriver != null
                ? _kinemationFpWeaponDriver.GetMuzzleTransform()
                : null;
            _worldMuzzleTransform = null;
            _worldMuzzleLight = null;
            if(_currentWorldWeaponBinding != null &&
               _currentWorldWeaponBinding.TryGetRuntimeReferences(
                   out var boundWorldMuzzle,
                   out var boundWorldMuzzleLight)) {
                _worldMuzzleTransform = boundWorldMuzzle;
                _worldMuzzleLight = boundWorldMuzzleLight;
            }

            _hasPrewarmedKinemationMuzzleForCurrentWeapon = false;

            if(_kinemationFpWeaponDriver != null) {
                PrewarmKinemationLocalMuzzleFxInstance();
            }

            // Restore ammo
            currentAmmo = Mathf.Clamp(restoredAmmo, 0, _currentMagCapacity);
            if(_kinemationFpWeaponDriver != null) {
                _kinemationFpWeaponDriver.SyncActiveAmmo(currentAmmo);
            }

            IsReloading = false;
            _autoReloadArmed = false;
            _reloadExpectedCompleteTime = float.PositiveInfinity;

            // Get animator from FP weapon
            _fpMuzzleLight = null;
            if(_currentFpWeaponInstance && _kinemationFpWeaponDriver != null) {
                var fpLight = _currentFpWeaponInstance.GetComponentInChildren<Light>(true);
                _fpMuzzleLight = fpLight != null ? fpLight.gameObject : null;
                if(_fpMuzzleLight) _fpMuzzleLight.SetActive(false);
            }

            if(_worldMuzzleLight) {
                _worldMuzzleLight.SetActive(false);
            }

            // Initialize trail pool for new weapon
            if(CurrentWeaponData != null && CurrentWeaponData.bulletTrail != null) {
                InitializeTrailPool();
            }

            // Update HUD
            if(CurrentWeaponData != null) {
                PublishOwnerAmmoToHud();
            }
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

        private bool TryGetStrictWorldMuzzleTransform(out Transform muzzleTransform, string context,
            bool allowOwnerInstance = false, bool logErrors = true) {
            muzzleTransform = null;

            if(playerController != null && playerController.IsOwner && !allowOwnerInstance) {
                if(logErrors) {
                    Debug.LogError(
                        $"[Weapon][RemoteMuzzleStrict][{context}] Called on owner instance. " +
                        $"weapon={(CurrentWeaponData != null ? CurrentWeaponData.weaponName : "(none)")}",
                        this);
                }
                return false;
            }

            if(_currentWorldWeaponInstance == null) {
                if(logErrors) {
                    Debug.LogError(
                        $"[Weapon][RemoteMuzzleStrict][{context}] Missing current world weapon instance. " +
                        $"weapon={(CurrentWeaponData != null ? CurrentWeaponData.weaponName : "(none)")}",
                        this);
                }
                return false;
            }

            if(_currentWorldWeaponBinding == null ||
               _currentWorldWeaponBinding.gameObject != _currentWorldWeaponInstance) {
                _currentWorldWeaponBinding = _currentWorldWeaponInstance.GetComponent<WorldWeaponBinding>();
            }

            if(_currentWorldWeaponBinding == null) {
                if(logErrors) {
                    Debug.LogError(
                        $"[Weapon][RemoteMuzzleStrict][{context}] Missing WorldWeaponBinding on world weapon. " +
                        $"weapon={(CurrentWeaponData != null ? CurrentWeaponData.weaponName : "(none)")} " +
                        $"worldWeapon={_currentWorldWeaponInstance.name}",
                        this);
                }
                return false;
            }

            if(!_currentWorldWeaponInstance.activeInHierarchy) {
                if(logErrors) {
                    Debug.LogError(
                        $"[Weapon][RemoteMuzzleStrict][{context}] World weapon inactive. " +
                        $"weapon={(CurrentWeaponData != null ? CurrentWeaponData.weaponName : "(none)")} " +
                        $"worldWeapon={_currentWorldWeaponInstance.name}",
                        this);
                }
                return false;
            }

            if(!_currentWorldWeaponBinding.TryGetRuntimeReferences(
                   out _worldMuzzleTransform,
                   out var boundMuzzleLight)) {
                if(logErrors) {
                    Debug.LogError(
                        $"[Weapon][RemoteMuzzleStrict][{context}] Assigned muzzle reference is null. " +
                        $"weapon={(CurrentWeaponData != null ? CurrentWeaponData.weaponName : "(none)")} " +
                        $"worldWeapon={_currentWorldWeaponInstance.name}",
                        this);
                }
                return false;
            }

            if(boundMuzzleLight != null) _worldMuzzleLight = boundMuzzleLight;

            if(!_worldMuzzleTransform.gameObject.activeInHierarchy) {
                if(logErrors) {
                    Debug.LogError(
                        $"[Weapon][RemoteMuzzleStrict][{context}] Muzzle transform inactive. " +
                        $"weapon={(CurrentWeaponData != null ? CurrentWeaponData.weaponName : "(none)")} " +
                        $"worldWeapon={_currentWorldWeaponInstance.name} " +
                        $"muzzlePath={GetTransformPath(_worldMuzzleTransform)}",
                        this);
                }
                return false;
            }

            muzzleTransform = _worldMuzzleTransform;
            return true;
        }

        public bool TryGetRemoteWorldMuzzlePosition(out Vector3 muzzlePosition) {
            muzzlePosition = default;
            if(!TryGetStrictWorldMuzzleTransform(out var muzzleTransform, "TryGetRemoteWorldMuzzlePosition")) {
                return false;
            }

            muzzlePosition = muzzleTransform.position;
            return true;
        }

        #endregion

        #region Public Methods

        public void Shoot() {
            if(!CanFire()) {
                HandleCannotFire();
                return;
            }

            PerformShot();
            PlayFireSound();
        }

        public bool TryAutoReloadFromEmptyClick() {
            if(currentAmmo != 0) return false;
            if(IsReloading) return false;
            if(_autoReloadArmed == false) return false;
            if(!CanReload()) return false;

            _autoReloadArmed = false;
            StartReload();
            return true;
        }

        public void StartReload() {
            if(!CanReload()) return;

            _autoReloadArmed = false;
            IsReloading = true;

            if(_kinemationFpWeaponDriver == null) {
                Debug.LogError(
                    $"[Weapon][KIN-Strict] Reload blocked: missing KinemationFpWeaponDriver for '{(CurrentWeaponData != null ? CurrentWeaponData.weaponName : "(none)")}'.",
                    this);
                IsReloading = false;
                return;
            }

            _reloadExpectedCompleteTime = Time.time + KinemationReloadFallbackSeconds;
            _kinemationReloadFallbackDeadline = _reloadExpectedCompleteTime;
            SyncServerWeaponState(WeaponManager.AmmoSyncReason.ReloadStarted);
            PlayReloadEffects();
        }

        public void CancelReloadForWeaponSwitch() {
            CancelReload();
        }

        private void CancelReload() {
            if(!IsReloading) return;

            // Cancel reload sound when switching weapons or canceling reload
            if(!UseKinemationInternalSounds() && !ShouldSuppressLegacyReloadSound() &&
               playerController.IsOwner && _audioRelay != null) {
                var soundId = CurrentWeaponData != null ? CurrentWeaponData.reloadSoundId : "";
                if(!string.IsNullOrWhiteSpace(soundId)) {
                    _audioRelay.RequestStop(soundId);
                }
            }

            if(_kinemationFpWeaponDriver != null) {
                StopKinemationEventSoundsForCurrentWeapon();
                _kinemationFpWeaponDriver.AbortReloadAndSyncAmmo(currentAmmo);
            }

            IsReloading = false;
            _reloadExpectedCompleteTime = float.PositiveInfinity;
            _kinemationReloadFallbackDeadline = float.PositiveInfinity;
            if(_kinemationFpWeaponDriver != null) _kinemationFpWeaponDriver.ResetReloadTracking();

            ExitReloadAnimation();
            SyncServerWeaponState(WeaponManager.AmmoSyncReason.ReloadCanceled);
        }

        private void SyncServerWeaponState(WeaponManager.AmmoSyncReason reason) {
            if(_weaponManager != null) {
                _weaponManager.ReportWeaponStateSync(_weaponManager.CurrentWeaponIndex, reason, currentAmmo);
            }
        }

        private void PublishOwnerAmmoToHud(int maxAmmoOverride = -1) {
            if(playerController == null || !playerController.IsOwner) return;
            if(HUDManager.Instance == null) return;
            var maxAmmo = maxAmmoOverride > 0 ? maxAmmoOverride : GetCurrentMagCapacity();
            EventBus.Publish(new UpdateAmmoEvent(currentAmmo, maxAmmo));
        }

        public void ResetWeapon() {
            if(!CurrentWeaponData) return;
            currentAmmo = GetCurrentMagCapacity();
            IsReloading = false;
            _lastFireTime = Time.time;
            _autoReloadArmed = false;
            _reloadExpectedCompleteTime = float.PositiveInfinity;
            _kinemationReloadFallbackDeadline = float.PositiveInfinity;
            if(_kinemationFpWeaponDriver != null) _kinemationFpWeaponDriver.ResetReloadTracking();

            if(IsOwner) {
                netCurrentDamageMultiplier.Value = 1f;
            }

            SyncServerWeaponState(WeaponManager.AmmoSyncReason.RefillCurrentWeapon);
        }

        public void PrepareForPostMatchPodium() {
            if(CurrentWeaponData == null) return;

            // Ensure no stale reload sounds/state leak into podium.
            if(IsReloading) {
                CancelReload();
            } else {
                if(!UseKinemationInternalSounds() && !ShouldSuppressLegacyReloadSound() &&
                   playerController != null && playerController.IsOwner && _audioRelay != null) {
                    var soundId = CurrentWeaponData.reloadSoundId;
                    if(!string.IsNullOrWhiteSpace(soundId)) {
                        _audioRelay.RequestStop(soundId);
                    }
                }

                if(_kinemationFpWeaponDriver != null) {
                    StopKinemationEventSoundsForCurrentWeapon();
                    _kinemationFpWeaponDriver.AbortReloadAndSyncAmmo(currentAmmo);
                    _kinemationFpWeaponDriver.ResetReloadTracking();
                }

                IsReloading = false;
                _reloadExpectedCompleteTime = float.PositiveInfinity;
                _kinemationReloadFallbackDeadline = float.PositiveInfinity;
                ExitReloadAnimation();
            }

            currentAmmo = GetCurrentMagCapacity();
            if(_kinemationFpWeaponDriver != null) {
                _kinemationFpWeaponDriver.SyncActiveAmmo(currentAmmo);
            }

            PublishOwnerAmmoToHud();
            SyncServerWeaponState(WeaponManager.AmmoSyncReason.RefillCurrentWeapon);
        }

        #endregion

        #region Getters

        private bool TryGetMuzzlePosition(out Vector3 muzzlePosition) {
            muzzlePosition = default;
            if(CurrentWeaponData == null) return false;

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

        /// <summary>
        /// Get muzzle position directly from weapon transform at current moment
        /// Called immediately in PerformShot() before LateUpdate, so weapon transform is accurate
        /// This avoids lag from queuing FX for LateUpdate
        /// </summary>
        private bool TryGetMuzzlePositionFromCamera(out Vector3 muzzlePosition) {
            muzzlePosition = default;
            if(!playerController || !playerController.IsOwner || CurrentWeaponData == null) {
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

        private bool TryGetOwnerTracerStartPosition(out Vector3 tracerStartPosition) {
            tracerStartPosition = default;
            if(!TryGetMuzzlePositionFromCamera(out var muzzlePosition)) {
                return false;
            }

            tracerStartPosition = muzzlePosition;
            TryRemapOwnerWeaponCameraPointToMainCamera(muzzlePosition, out tracerStartPosition);
            return true;
        }

        private void TryRemapOwnerWeaponCameraPointToMainCamera(Vector3 sourcePoint, out Vector3 remappedPoint) {
            remappedPoint = sourcePoint;
            if(playerController == null) return;
            if(!playerController.IsOwner) return;
            if(GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch) return;
            if(playerController.PlayerInput != null && playerController.PlayerInput.IsSniperOverlayActive) return;

            var weaponCamera = playerController.WeaponCamera;
            var mainSceneCamera = Camera.main;
            if(weaponCamera == null || mainSceneCamera == null) return;
            if(weaponCamera == mainSceneCamera) return;

            // KIN viewmodel points are authored relative to WeaponCamera. Convert the same viewport location
            // into main-camera world space so world-rendered tracers align with FP on-screen origin.
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

        public int GetWeaponSlot() {
            return CurrentWeaponData == null ? 0 : CurrentWeaponData.WeaponSlotIndex;
        }

        public float GetFireRate() {
            return CurrentWeaponData == null ? 0.1f : CurrentWeaponData.fireRate;
        }

        [field: Header("Current Weapon State")]
        public WeaponData CurrentWeaponData { get; private set; }

        private int GetCurrentMagCapacity() {
            return Mathf.Max(1, _currentMagCapacity);
        }

        public int GetMagSize() {
            return GetCurrentMagCapacity();
        }

        public GameObject GetWeaponPrefab() => _currentFpWeaponInstance;

        private bool TryGetStrictFpMuzzleTransform(out Transform muzzleTransform, string context, bool logErrors = true) {
            muzzleTransform = null;

            if(playerController == null || !playerController.IsOwner) {
                if(logErrors) {
                    Debug.LogError($"[Weapon][MuzzleStrict][{context}] FP muzzle requested by non-owner.", this);
                }
                return false;
            }

            if(_kinemationFpWeaponDriver == null) {
                if(logErrors) {
                    Debug.LogError(
                        $"[Weapon][MuzzleStrict][{context}] Missing KinemationFpWeaponDriver for owner weapon " +
                        $"'{(CurrentWeaponData != null ? CurrentWeaponData.weaponName : "(none)")}'.",
                        this);
                }
                return false;
            }

            _fpMuzzleTransform = _kinemationFpWeaponDriver.GetMuzzleTransform();
            if(_fpMuzzleTransform == null) {
                if(logErrors) {
                    Debug.LogError(
                        $"[Weapon][MuzzleStrict][{context}] FP muzzle transform missing for weapon " +
                        $"'{(CurrentWeaponData != null ? CurrentWeaponData.weaponName : "(none)")}'.",
                        this);
                }
                return false;
            }

            if(!_fpMuzzleTransform.gameObject.activeInHierarchy) {
                if(logErrors) {
                    Debug.LogError(
                        $"[Weapon][MuzzleStrict][{context}] FP muzzle transform inactive. " +
                        $"weapon={(CurrentWeaponData != null ? CurrentWeaponData.weaponName : "(none)")} " +
                        $"muzzlePath={GetTransformPath(_fpMuzzleTransform)}",
                        this);
                }
                return false;
            }

            muzzleTransform = _fpMuzzleTransform;
            return true;
        }

        private bool TryGetRequiredOwnerMuzzleTransform(out Transform muzzleTransform, string context, bool logErrors = true) {
            muzzleTransform = null;

            if(playerController == null || !playerController.IsOwner) {
                if(logErrors) {
                    Debug.LogError(
                        $"[Weapon][MuzzleStrict][{context}] Owner-only muzzle query called on non-owner.",
                        this);
                }
                return false;
            }

            var isPostMatch = GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch;
            return isPostMatch
                ? TryGetStrictWorldMuzzleTransform(out muzzleTransform, context, allowOwnerInstance: true,
                    logErrors: logErrors)
                : TryGetStrictFpMuzzleTransform(out muzzleTransform, context, logErrors: logErrors);
        }

        #endregion

        

        
    }
}


using System.Collections;
using System.Collections.Generic;
using Audio.Networking;
using Game.Match;
using Game.Menu;
using Game.Player;
using Game.Progression;
using Game.UI;
using Network.Events;
using Network.Rpc;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.VFX;

namespace Game.Weapons {
    public class Weapon : NetworkBehaviour {
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

        [Header("Current Weapon State")]
        private WeaponData _currentWeaponData;
        private int _currentMagCapacity = 30;

        private GameObject _currentFpWeaponInstance;
        private GameObject _currentWorldWeaponInstance;
        private Transform _fpMuzzleTransform;
        private Transform _worldMuzzleTransform;
        private KinemationFpWeaponDriver _kinemationFpWeaponDriver;
        private GameObject _fpMuzzleLight;
        private GameObject _worldMuzzleLight;
        private Coroutine _fpMuzzleLightCoroutine;
        private Coroutine _worldMuzzleLightCoroutine;
        private GameObject _kinemationLocalMuzzleFxInstance;
        private VisualEffect _kinemationLocalMuzzleVfx;
        private GameObject _kinemationLocalMuzzleSourcePrefab;

        [Header("Runtime State")]
        public int currentAmmo;

        private bool IsReloading { get; set; }

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
        private Coroutine _reloadCoroutine;
        private bool _autoReloadArmed;
        private float _reloadExpectedCompleteTime;
        private float _nextReloadRecoveryAllowedTime;
        private readonly List<int> _kinemationWeaponSoundEventBuffer = new();
        private float _kinemationReloadFallbackDeadline;

        private const float ReloadTimeoutGraceSeconds = 0.35f;
        private const float ReloadRecoveryCooldownSeconds = 0.5f;
        private const float KinemationReloadFallbackSeconds = 5f;

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
            _currentWeaponData = newWeaponData;
            _currentFpWeaponInstance = fpWeaponInstance;
            _currentWorldWeaponInstance = worldWeaponInstance;
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
            _worldMuzzleTransform = ResolveMuzzleTransform(_currentWorldWeaponInstance);
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
                _fpMuzzleLight = ResolveMuzzleLightObject(_currentFpWeaponInstance);
                if(_fpMuzzleLight) {
                    _fpMuzzleLight.SetActive(false);
                }
            }

            _worldMuzzleLight = null;
            if(_currentWorldWeaponInstance != null) {
                _worldMuzzleLight = ResolveMuzzleLightObject(_currentWorldWeaponInstance);
            }

            if(_worldMuzzleLight) {
                _worldMuzzleLight.SetActive(false);
            }

            // Initialize trail pool for new weapon
            if(_currentWeaponData != null && _currentWeaponData.bulletTrail != null) {
                InitializeTrailPool();
            }

            // Update HUD
            if(playerController == null || !playerController.IsOwner) return;
            if(_currentWeaponData != null && HUDManager.Instance != null) {
                EventBus.Publish(new UpdateAmmoEvent(currentAmmo, GetCurrentMagCapacity()));
            }
        }

        private static GameObject ResolveMuzzleLightObject(GameObject weaponInstanceRoot) {
            if(weaponInstanceRoot == null) return null;
            var light = weaponInstanceRoot.GetComponentInChildren<Light>(true);
            return light != null ? light.gameObject : null;
        }

        private static Transform ResolveMuzzleTransform(GameObject weaponInstanceRoot) {
            if(weaponInstanceRoot == null) return null;

            var allTransforms = weaponInstanceRoot.GetComponentsInChildren<Transform>(true);
            foreach(var candidate in allTransforms) {
                if(candidate != null && candidate.name == "Muzzle") {
                    return candidate;
                }
            }

            return null;
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

            if(_kinemationFpWeaponDriver != null) {
                _reloadExpectedCompleteTime = Time.time + KinemationReloadFallbackSeconds;
                _kinemationReloadFallbackDeadline = _reloadExpectedCompleteTime;
                PlayReloadEffects();
                return;
            }

            _reloadExpectedCompleteTime = Time.time + GetExpectedReloadDuration() + ReloadTimeoutGraceSeconds;
            _kinemationReloadFallbackDeadline = float.PositiveInfinity;

            if(_currentWeaponData.useMagReload) {
                PlayReloadEffects();
                _reloadCoroutine = StartCoroutine(MagReloadCoroutine());
            } else {
                _reloadCoroutine = StartCoroutine(PerRoundReloadCoroutine());
            }
        }

        private IEnumerator MagReloadCoroutine() {
            yield return new WaitForSeconds(_currentWeaponData.reloadTime);
            CompleteReload();
        }

        private IEnumerator PerRoundReloadCoroutine() {
            var perRoundTime = Mathf.Max(0.05f, _currentWeaponData.perRoundReloadTime);

            // Play reload animation only once at the start (FP weapon animator only)
            PlayReloadAnimationForCurrentWeapon();

            var magCapacity = GetCurrentMagCapacity();
            while(IsReloading && currentAmmo < magCapacity) {
                // Play reload sound for each round (audio feedback)
                if(!UseKinemationInternalSounds() && !ShouldSuppressLegacyReloadSound() &&
                   playerController.IsOwner && _audioRelay != null) {
                    var soundId = _currentWeaponData != null ? _currentWeaponData.reloadSoundId : "";
                    if(!string.IsNullOrWhiteSpace(soundId)) {
                        _audioRelay.RequestPlayAttached(soundId, new NetworkObjectReference(playerController.NetworkObject),
                            allowOverlap: false);
                    }
                }

                yield return new WaitForSeconds(perRoundTime);
                if(!IsReloading) yield break;

                currentAmmo = Mathf.Min(currentAmmo + 1, magCapacity);

                if(playerController.IsOwner && HUDManager.Instance != null) {
                    EventBus.Publish(new UpdateAmmoEvent(currentAmmo, magCapacity));
                }

                SyncServerAmmo();

                if(currentAmmo < magCapacity) continue;
                // Trigger reload complete animation (shotgun-style reloads when mag is full)
                PlayReloadCompleteAnimationForCurrentWeapon();
                break;
            }

            IsReloading = false;
            _reloadCoroutine = null;
        }

        private void CancelReload() {
            if(!IsReloading) return;
            if(_reloadCoroutine != null) {
                StopCoroutine(_reloadCoroutine);
            }

            // Cancel reload sound when switching weapons or canceling reload
            if(!UseKinemationInternalSounds() && !ShouldSuppressLegacyReloadSound() &&
               playerController.IsOwner && _audioRelay != null) {
                var soundId = _currentWeaponData != null ? _currentWeaponData.reloadSoundId : "";
                if(!string.IsNullOrWhiteSpace(soundId)) {
                    _audioRelay.RequestStop(soundId);
                }
            }

            if(_kinemationFpWeaponDriver != null) {
                StopKinemationEventSoundsForCurrentWeapon();
                _kinemationFpWeaponDriver.AbortReloadAndSyncAmmo(currentAmmo);
            }

            IsReloading = false;
            _reloadCoroutine = null;
            _reloadExpectedCompleteTime = float.PositiveInfinity;
            _kinemationReloadFallbackDeadline = float.PositiveInfinity;
            if(_kinemationFpWeaponDriver != null) _kinemationFpWeaponDriver.ResetReloadTracking();

            ExitReloadAnimation();
        }

        private void SyncServerAmmo() {
            if(_weaponManager != null) {
                _weaponManager.ReportAmmoSync(_weaponManager.CurrentWeaponIndex, currentAmmo);
            }
        }

        public void ResetWeapon() {
            if(!_currentWeaponData) return;
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
            SyncServerAmmo();
        }

        #endregion

        #region Getters

        public bool TryGetMuzzlePosition(out Vector3 muzzlePosition) {
            muzzlePosition = default;
            if(_currentWeaponData == null) return false;

            var isPostMatch = GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch;
            var preferWorld = playerController == null || !playerController.IsOwner || isPostMatch;

            if(playerController != null &&
               playerController.IsOwner &&
               playerController.PlayerInput != null &&
               playerController.PlayerInput.IsSniperOverlayActive) {
                var fpCameraTransform = playerController.FpCameraTransform;
                if(fpCameraTransform == null) return false;

                muzzlePosition = fpCameraTransform.TransformPoint(playerController.PlayerInput.SniperMuzzleCameraOffset);
                return true;
            }

            if(!TryGetPreferredMuzzleTransform(preferWorld, out var muzzleTransform) || muzzleTransform == null) {
                return false;
            }

            muzzlePosition = muzzleTransform.position;
            return true;
        }

        /// <summary>
        /// Get muzzle position directly from weapon transform at current moment
        /// Called immediately in PerformShot() before LateUpdate, so weapon transform is accurate
        /// This avoids lag from queuing FX for LateUpdate
        /// </summary>
        private bool TryGetMuzzlePositionFromCamera(out Vector3 muzzlePosition) {
            muzzlePosition = default;
            if(!playerController || !playerController.IsOwner || _currentWeaponData == null) {
                return TryGetMuzzlePosition(out muzzlePosition);
            }

            if(playerController.PlayerInput == null || !playerController.PlayerInput.IsSniperOverlayActive) {
                var useWorldParent = GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch;
                if(!TryGetPreferredMuzzleTransform(useWorldParent, out var muzzleTransform) || muzzleTransform == null) {
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

        public Quaternion GetMuzzleRotation() {
            if(!playerController || !playerController.IsOwner)
                return _currentWorldWeaponInstance
                    ? _currentWorldWeaponInstance.transform.rotation
                    : transform.rotation;
            if(GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch) {
                return _currentWorldWeaponInstance
                    ? _currentWorldWeaponInstance.transform.rotation
                    : transform.rotation;
            }

            var fpCameraTransform = playerController != null ? playerController.FpCameraTransform : null;
            return _currentFpWeaponInstance
                ? _currentFpWeaponInstance.transform.rotation
                : fpCameraTransform != null ? fpCameraTransform.rotation : transform.rotation;
        }

        public int GetWeaponSlot() {
            return _currentWeaponData == null ? 0 : _currentWeaponData.WeaponSlotIndex;
        }
        public float GetFireRate() {
            return _currentWeaponData == null ? 0.1f : _currentWeaponData.fireRate;
        }

        private int GetCurrentMagCapacity() {
            if(_currentMagCapacity > 0) {
                return _currentMagCapacity;
            }

            return _currentWeaponData == null ? 30 : Mathf.Max(1, _currentWeaponData.magSize);
        }

        public int GetMagSize() {
            return GetCurrentMagCapacity();
        }
        public GameObject GetWeaponPrefab() => _currentFpWeaponInstance;
        public Vector3 GetSpawnPosition() {
            return _currentWeaponData == null ? Vector3.zero : _currentWeaponData.spawnPosition;
        }
        public Vector3 GetSpawnRotation() {
            return _currentWeaponData == null ? Vector3.zero : _currentWeaponData.spawnRotation;
        }

        private bool TryGetPreferredMuzzleTransform(bool preferWorldModel, out Transform muzzleTransform) {
            muzzleTransform = null;
            if(_fpMuzzleTransform == null && _kinemationFpWeaponDriver != null) {
                _fpMuzzleTransform = _kinemationFpWeaponDriver.GetMuzzleTransform();
            }

            if(preferWorldModel) {
                if(_worldMuzzleTransform != null) {
                    muzzleTransform = _worldMuzzleTransform;
                    return true;
                }

                if(_fpMuzzleTransform == null) return false;
                muzzleTransform = _fpMuzzleTransform;
            } else {
                if(_fpMuzzleTransform != null) {
                    muzzleTransform = _fpMuzzleTransform;
                    return true;
                }

                if(_worldMuzzleTransform == null) return false;
                muzzleTransform = _worldMuzzleTransform;
            }

            return true;
        }

        #endregion

        #region Private Methods - Shooting

        /// <summary>
        /// Check if the target is a teammate (friendly fire check)
        /// </summary>
        private bool IsFriendlyFire(NetworkObject target) {
            // Only check in team-based game modes
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null) return false;

            var isTeamBased = MatchSettingsManager.IsTeamBasedMode(matchSettings.selectedGameModeId);
            if(!isTeamBased) return false; // FFA modes allow friendly fire

            // Get shooter's team
            if(playerController == null) return false;
            var shooterTeamMgr = playerController.TeamManager;
            if(shooterTeamMgr == null) return false;

            // Get target's team
            var targetTeamMgr = target.GetComponent<PlayerTeamManager>();
            if(targetTeamMgr == null) return false;

            // Check if same team
            return shooterTeamMgr.netTeam.Value == targetTeamMgr.netTeam.Value;
        }


        private bool CanFire() {
            if(!_currentWeaponData || _weaponManager.IsPullingOut) return false;

            if(!IsReloading || _currentWeaponData.useMagReload || currentAmmo <= 0)
                return Time.time >= _lastFireTime + _currentWeaponData.fireRate && currentAmmo > 0 && !IsReloading;
            ConsumePendingKinemationReloadSingleEvents();
            if(_kinemationFpWeaponDriver != null) {
                _kinemationFpWeaponDriver.NotifyDrakeReloadCanceledByShot();
            }
            // For shell-by-shell reloads, allow cancel only after at least one round was inserted.
            CancelReload();

            return Time.time >= _lastFireTime + _currentWeaponData.fireRate && currentAmmo > 0 && !IsReloading;
        }

        private void HandleCannotFire() {
            if(!_currentWeaponData) return;
            if(Time.time < _lastFireTime + _currentWeaponData.fireRate || IsReloading || currentAmmo != 0) return;

            _lastFireTime = Time.time;
            PlayDryFireSound();
            _autoReloadArmed = true;
        }

        private ulong _shotSequence;

        private void PerformShot() {
            var fpCameraTransform = playerController != null ? playerController.FpCameraTransform : null;
            if(fpCameraTransform == null) return;
            
            var origin = fpCameraTransform.position;
            var forward = fpCameraTransform.forward;
            var authoritativeAmmoBeforeShot = Mathf.Max(0, currentAmmo);

            currentAmmo--;
            _lastFireTime = Time.time;

            if(playerController != null && playerController.IsOwner) {
                if(HUDManager.Instance != null) {
                    EventBus.Publish(new UpdateAmmoEvent(currentAmmo, GetCurrentMagCapacity()));
                }
            }

            var weaponIndex = _weaponManager != null ? _weaponManager.CurrentWeaponIndex : -1;
            if(weaponIndex < 0) return;

            var shotId = ++_shotSequence;

            var pelletCount = 1;
            if(_currentWeaponData != null && _currentWeaponData.usePelletSpread) {
                pelletCount = Mathf.Max(1, _currentWeaponData.pelletCount);
            }

            var spreadDegrees = _currentWeaponData != null ? _currentWeaponData.bulletSpread : 0f;
            
            // If sniper overlay is active and weapon uses sniper overlay, remove all spread for perfect accuracy
            if(_currentWeaponData != null && _currentWeaponData.useSniperOverlay && 
               playerController != null && playerController.PlayerInput != null && 
               playerController.PlayerInput.IsSniperOverlayActive) {
                spreadDegrees = 0f;
            }

            _hasLocalMuzzleFlashSpawnPositionForShot = false;
            _localMuzzleFlashSpawnPositionForShot = Vector3.zero;

            if (playerController != null && playerController.IsOwner) {
                PlayLocalMuzzleFlash(authoritativeAmmoBeforeShot);
                // Record Stats
                if (ProgressionManager.Instance != null) {
                    ProgressionManager.Instance.RecordShotFired();
                }
            }

            // Capture tracer start after local fire animation/muzzle flash so KIN pose updates
            // are reflected in the same frame as the spawned tracer.
            bool hasMuzzlePosition;
            Vector3 capturedMuzzlePos;
            if(_hasLocalMuzzleFlashSpawnPositionForShot) {
                capturedMuzzlePos = _localMuzzleFlashSpawnPositionForShot;
                hasMuzzlePosition = true;
                TryRemapOwnerWeaponCameraPointToMainCamera(capturedMuzzlePos, out capturedMuzzlePos);
            } else {
                hasMuzzlePosition = TryGetOwnerTracerStartPosition(out capturedMuzzlePos);
            }

            var anyPelletHitPlayer = false;

            for(var i = 0; i < pelletCount; i++) {
                var direction = ApplySpread(forward, spreadDegrees);
                FirePellet(origin, direction, out var endPoint, out var hitNormal, out var madeImpact,
                    out var hitPlayer, out var hitPlayerRef, weaponIndex, shotId);

                if (hitPlayer) anyPelletHitPlayer = true;

                if(playerController != null && playerController.IsOwner && hasMuzzlePosition) {
                    StartCoroutine(SpawnOwnerTracerLocalAfterViewUpdate(capturedMuzzlePos, endPoint, hitNormal,
                        madeImpact, hitPlayer, hitPlayerRef));
                }

                var playMuzzleFlash = i == 0;
                _networkFXRelay.RequestShotFx(endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef, playMuzzleFlash);
            }

            // If any pellet hit a player, count it as a "Shot Hit" (accuracy = Shots Hit / Shots Fired)
            // This prevents shotguns from giving > 100% accuracy
            if (anyPelletHitPlayer && playerController != null && playerController.IsOwner && ProgressionManager.Instance != null) {
                ProgressionManager.Instance.RecordShotHit();
            }
        }

        private void FirePellet(Vector3 origin, Vector3 direction, out Vector3 endPoint, out Vector3 hitNormal,
            out bool madeImpact, out bool hitPlayer, out NetworkObjectReference hitPlayerRef, int weaponIndex, ulong shotId) {
            var hitLayer = _enemyLayer | _worldLayer;
            var shotHit = false;
            RaycastHit hit = default;
            
            // Default max distance for raycast
            var maxDist = 1000f;
            
            // Check if we should use the new hybrid sphere/cone cast system
            var useHybridSystem = _currentWeaponData != null && _currentWeaponData.useSphereCast 
                                  || _currentWeaponData != null && _currentWeaponData.useSniperOverlay && 
                                  playerController != null && playerController.PlayerInput != null && 
                                  playerController.PlayerInput.IsSniperOverlayActive;
            
            // Legacy/Sniper Override check (maintain support for old sniper bool if needed, but prefer hybrid)

            if(useHybridSystem) {
                // HYBRID HIT REGISTRATION SYSTEM
                // 1. Cast a strict Ray against World Geometry to find the "hard stop" distance.
                // 2. Cast a Sphere against Players (and World) to find forgiving hits.
                // 3. Validate that Sphere hits are:
                //    a) BEFORE the World Ray hit (occlusion check)
                //    b) Within the "Cone" variance at that distance (scoping down the sphere)

                // Step 1: Geometry Check (Raycast against World Only)
                // We use _worldLayer to find where the bullet would strictly stop on a wall.
                if(Physics.Raycast(origin, direction, out var worldHit, maxDist, _worldLayer)) {
                    maxDist = worldHit.distance; // This is our hard stop.
                }

                // Step 2: Forgiving Check (SphereCast against Everyone)
                // We use the MAX radius for the sphere cast to catch anything that *might* be a hit.
                // Then we validate if it falls within the *current* radius at that distance.
                // Step 2: Forgiving Check (SphereCast against Everyone)
                // We use the MAX radius for the sphere cast to catch anything that *might* be a hit.
                // Then we validate if it falls within the *current* radius at that distance.
                var maxRadius = _currentWeaponData.sphereCastMaxRadius;
                var baseRadius = _currentWeaponData.sphereCastRadius;
                var growthStart = _currentWeaponData.sphereCastGrowthStartDist;
                // Use minDamageRange when falloff is enabled; otherwise scale over the shot's effective trace distance.
                var growthEnd = _currentWeaponData.useDamageFalloff
                    ? Mathf.Max(growthStart + 0.1f, _currentWeaponData.minDamageRange)
                    : Mathf.Max(growthStart + 0.1f, maxDist);
                
                // Perform the SphereCast with the strict limit of maxDist (or slightly more to catch edge cases, filtering later)
                // Note: SphereCastAll is better here to find the *first valid player* even if a closer player is missed by the cone but hit by the sphere.
                // For simplicity/perf, we'll stick to SphereCast and assume the first hit is the intended one if valid.
                if(Physics.SphereCast(origin, maxRadius, direction, out var sphereHit, maxDist, hitLayer)) {
                    // Step 3: Validation
                    // Calculate what the allowed radius is at this specific distance
                    var dist = sphereHit.distance;
                    
                    float allowedRadius;
                    if(dist <= growthStart) {
                        allowedRadius = baseRadius;
                    } else if(dist >= growthEnd) {
                        allowedRadius = maxRadius;
                    } else {
                        var t = Mathf.InverseLerp(growthStart, growthEnd, dist);
                        allowedRadius = Mathf.Lerp(baseRadius, maxRadius, t);
                    }
                    
                    // Check if the hit point is within this allowable radius from the central ray
                    // Project hit point onto the ray axis to find the perpendicular distance
                    var hitPoint = sphereHit.point; // Note: sphereHit.point is on the surface of the collider, not center of sphere
                    // However, Physics.SphereCast returns the point on the collider surface.
                    // Accurate perpendicular distance check:
                    var projectedPoint = origin + direction * Vector3.Dot(hitPoint - origin, direction);
                    var distFromRay = Vector3.Distance(hitPoint, projectedPoint);

                    // If the actual contact point is within our "Cone" at this distance, it's a valid hit!
                    // Also, strictly enforce that it is NOT behind the wall (distance check)
                    if(distFromRay <= allowedRadius && sphereHit.distance <= maxDist) {
                        shotHit = true;
                        hit = sphereHit;
                        
                        // DEBUG VISUALIZATION
                        #if UNITY_EDITOR
                        if(playerController.IsOwner) {
                            DrawHitRegistrationDebug(origin, direction, maxDist, sphereHit.point, true, baseRadius, maxRadius, growthStart, growthEnd);
                        }
                        #endif
                    } else {
                        // We hit something with the sphere, but it was too far from center (outside cone) or behind a wall.
                        // Fallback: Did we hit the wall with the Raycast earlier?
                        // If so, that's our hit. If not, line trace failed.
                        // Actually, if Sphere failed, we should fallback to a strict Raycast to ensure
                        // completely center shots always hit even if Sphere math gets wonky.
                        if(Physics.Raycast(origin, direction, out var strictHit, maxDist, hitLayer)) {
                            shotHit = true;
                            hit = strictHit;
                             #if UNITY_EDITOR
                            if(playerController.IsOwner) 
                                DrawHitRegistrationDebug(origin, direction, maxDist, strictHit.point, true, baseRadius, maxRadius, growthStart, growthEnd);
                            #endif
                        } else {
                             #if UNITY_EDITOR
                            if(playerController.IsOwner) 
                                DrawHitRegistrationDebug(origin, direction, maxDist, Vector3.zero, false, baseRadius, maxRadius, growthStart, growthEnd);
                            #endif
                        }
                    }
                } else {
                    // Sphere hit nothing. Fallback to Raycast (e.g. shooting through a tiny gap the sphere couldn't fit?)
                    if(Physics.Raycast(origin, direction, out var strictHit, maxDist, hitLayer)) {
                         shotHit = true;
                         hit = strictHit;
                         #if UNITY_EDITOR
                         if(playerController.IsOwner) 
                             DrawHitRegistrationDebug(origin, direction, maxDist, strictHit.point, true, baseRadius, maxRadius, growthStart, growthEnd);
                         #endif
                    } else {
                         #if UNITY_EDITOR
                         if(playerController.IsOwner) 
                             DrawHitRegistrationDebug(origin, direction, maxDist, Vector3.zero, false, baseRadius, maxRadius, growthStart, growthEnd);
                         #endif
                    }
                }
            } else {
                // Standard strict raycast (Legacy/Shotgun/Hipfire if configured)
                shotHit = Physics.Raycast(origin, direction, out hit, maxDist, hitLayer);
            }
            
            hitPlayerRef = default;

            if(shotHit) {
                endPoint = hit.point;
                hitNormal = hit.normal;
                madeImpact = true;
                
                // Check if a player was hit and get their NetworkObjectReference
                var hitPlayerController = hit.collider.GetComponentInParent<PlayerController>();
                hitPlayer = hitPlayerController != null;
                if(hitPlayer && hitPlayerController.NetworkObject != null) {
                    hitPlayerRef = new NetworkObjectReference(hitPlayerController.NetworkObject);
                }
                
                var damage = CalculateDamage(hit.distance);
                ApplyDamageToHit(hit, origin, damage, weaponIndex, shotId);
            } else {
                endPoint = origin + direction * 600f;
                hitNormal = direction;
                madeImpact = false;
                hitPlayer = false;
            }
        }

        private void ApplyDamageToHit(RaycastHit hit, Vector3 origin, float damage, int weaponIndex, ulong shotId) {
            if(damage <= 0f) return;

            var shooterPosition = playerController != null ? playerController.transform.position : origin;
            var hitDirection = (hit.point - shooterPosition).normalized;

            var hitRigidbody = hit.collider.attachedRigidbody;
            var bodyPartTag = string.Empty;
            var isHeadshot = false;
            NetworkObject target;

            if(hitRigidbody != null) {
                bodyPartTag = hitRigidbody.tag;
                isHeadshot = !string.IsNullOrEmpty(bodyPartTag) && bodyPartTag == "Head";
                target = hitRigidbody.GetComponent<NetworkObject>();
                if(target == null) {
                    target = hitRigidbody.GetComponentInParent<NetworkObject>();
                }
            } else {
                target = hit.collider.GetComponent<NetworkObject>();
            }

            if(target == null || !target.IsSpawned) return;

            if(IsFriendlyFire(target)) {
                return;
            }

            var targetRef = new NetworkObjectReference(target);
            _damageRelay.RequestDamageServerRpc(targetRef, damage, hit.point, hitDirection,
                hitRigidbody != null ? bodyPartTag : null, hitRigidbody != null && isHeadshot, weaponIndex,
                shotId);
        }

        private Vector3 ApplySpread(Vector3 forward, float spreadDegrees) {
            var fpCameraTransform = playerController != null ? playerController.FpCameraTransform : null;
            if(fpCameraTransform == null || spreadDegrees <= 0f) {
                return forward;
            }

            var spreadRad = spreadDegrees * Mathf.Deg2Rad;
            var randomOffset = Random.insideUnitCircle;
            var spreadAmount = Mathf.Tan(spreadRad * 0.5f);
            var offset = (fpCameraTransform.right * randomOffset.x + fpCameraTransform.up * randomOffset.y) *
                         spreadAmount;
            var direction = (forward + offset).normalized;
            return direction;
        }

        private float CalculateDamage(float distance) {
            if(!_currentWeaponData) return 0f;

            var baseDamage = _currentWeaponData.baseDamage;

            if(_currentWeaponData.useDamageFalloff) {
                var startRange = Mathf.Max(0f, _currentWeaponData.maxDamageRange);
                var endRange = Mathf.Max(startRange, _currentWeaponData.minDamageRange);
                var minDamage = Mathf.Clamp(_currentWeaponData.minDamage, 0f, baseDamage);

                if(distance <= startRange) {
                    // baseDamage = baseDamage;
                } else if(distance >= endRange) {
                    baseDamage = minDamage;
                } else {
                    var t = Mathf.InverseLerp(startRange, endRange, distance);
                    baseDamage = Mathf.Lerp(baseDamage, minDamage, t);
                }
            }

            if(_currentWeaponData.usePelletSpread) {
                baseDamage *= Mathf.Max(0f, _currentWeaponData.pelletDamageMultiplier);
            }

            var scaledDamage = baseDamage * CurrentDamageMultiplier;
            return Mathf.Min(scaledDamage, _currentWeaponData.damageCap);
        }

        public void UpdateDamageMultiplier() {
            if(!_currentWeaponData) return;

            // Check if player is dead - if so, only allow decay, not gain
            var isDead = playerController != null && playerController.IsDead;

            // If player is dead, force decay as if they stopped moving (target = 1f)
            if(isDead) {
                // Decay towards 1f (as if player stopped moving), ignoring current speed
                CurrentDamageMultiplier = Mathf.MoveTowards(CurrentDamageMultiplier, 1f,
                    MultiplierDecayRate * Time.deltaTime);
                // Reset peak to current so grace period doesn't hold it
                _peakDamageMultiplier = CurrentDamageMultiplier;
                // Reset grace period timer so it doesn't hold at peak
                _lastPeakTime = 0f;
                CurrentDamageMultiplier =
                    Mathf.Clamp(CurrentDamageMultiplier, 1f, MaxDamageMultiplier);
                return;
            }

            if(playerController != null) {
                var currentSpeed = playerController.GetFullVelocity.magnitude;
                float targetMultiplier;

                // Calculate target multiplier based on current velocity
                if(currentSpeed < MinSpeedThreshold) {
                    targetMultiplier = 1f;
                } else {
                    var scaleFactor = Mathf.InverseLerp(MinSpeedThreshold, MaxSpeedThreshold, currentSpeed);
                    targetMultiplier = Mathf.Lerp(1f, MaxDamageMultiplier, scaleFactor);
                }

                // If target is higher than current, jump to it immediately and start grace period
                if(targetMultiplier >= CurrentDamageMultiplier) {
                    CurrentDamageMultiplier = Mathf.Lerp(CurrentDamageMultiplier, targetMultiplier,
                        MultiplierGainRate * Time.deltaTime);
                    _peakDamageMultiplier = CurrentDamageMultiplier;
                    _lastPeakTime = Time.time;
                }
                // During grace period, hold at peak
                else if(Time.time - _lastPeakTime < MultiplierGracePeriod) {
                    CurrentDamageMultiplier = _peakDamageMultiplier;
                }
                // After grace period, decay
                else {
                    CurrentDamageMultiplier = Mathf.MoveTowards(CurrentDamageMultiplier, targetMultiplier,
                        MultiplierDecayRate * Time.deltaTime);
                    _peakDamageMultiplier = CurrentDamageMultiplier;
                }
            }

            CurrentDamageMultiplier = Mathf.Clamp(CurrentDamageMultiplier, 1f, MaxDamageMultiplier);
        }

        #if UNITY_EDITOR
        private static void DrawHitRegistrationDebug(Vector3 origin, Vector3 direction, float maxDist, Vector3 hitPoint, bool hitSomething, 
            float baseRadius, float maxRadius, float startDist, float endDist) // Debug Visualization
        {
            const float duration = 5.0f; // Persist for 5 seconds

            // 1. Draw the Central Ray (Geometry Check)
            Debug.DrawLine(origin, origin + direction * maxDist, Color.red, duration);

            // 2. Draw "Cone" Rings at intervals
            const int steps = 50; // Increased frequency for better visibility
            for(var i = 0; i <= steps; i++) {
                var t = (float)i / steps;
                var currentDist = Mathf.Lerp(0, maxDist, t); // Draw full length to wall hit
                
                if (currentDist > maxDist) break; // Redundant but safe

                // Calculate radius at this distance
                float currentRadius;
                if(currentDist <= startDist) currentRadius = baseRadius;
                else if(currentDist >= endDist) currentRadius = maxRadius;
                else currentRadius = Mathf.Lerp(baseRadius, maxRadius, Mathf.InverseLerp(startDist, endDist, currentDist));

                var center = origin + direction * currentDist;
                // Draw a simple cross or diamond to represent the ring since DrawWireDisc isn't standard
                var up = Vector3.up * currentRadius;
                var right = Vector3.right * currentRadius;
                
                Debug.DrawLine(center - up, center + up, Color.yellow, duration);
                Debug.DrawLine(center - right, center + right, Color.yellow, duration);
            }
            
            // 3. Draw Hit Point
            if(!hitSomething) return;
            Debug.DrawLine(hitPoint, hitPoint + Vector3.up * 0.2f, Color.green, duration);
            // Draw a small sphere at hit
            // Since we can't do DrawSphere easily in standard Debug, we'll just use a distinctive cross marker
            Debug.DrawLine(hitPoint - Vector3.up*0.1f, hitPoint + Vector3.up*0.1f, Color.green, duration);
            Debug.DrawLine(hitPoint - Vector3.right*0.1f, hitPoint + Vector3.right*0.1f, Color.green, duration);
            Debug.DrawLine(hitPoint - Vector3.forward*0.1f, hitPoint + Vector3.forward*0.1f, Color.green, duration);
        }
        #endif

        #endregion

        #region Private Methods - Reloading

        private bool CanReload() {
            if(!_currentWeaponData || _weaponManager.IsPullingOut) return false;
            return currentAmmo < GetCurrentMagCapacity() && _reloadCoroutine == null && !IsReloading;
        }

        private void CompleteReload() {
            if(!_currentWeaponData) return;
            currentAmmo = GetCurrentMagCapacity();
            IsReloading = false;
            _reloadCoroutine = null;
            _autoReloadArmed = false;
            _reloadExpectedCompleteTime = float.PositiveInfinity;
            _kinemationReloadFallbackDeadline = float.PositiveInfinity;
            if(_kinemationFpWeaponDriver != null) _kinemationFpWeaponDriver.ResetReloadTracking();

            // Trigger reload complete animation (mag-style reloads)
            ExitReloadAnimation();

            if(playerController.IsOwner && HUDManager.Instance != null) {
                EventBus.Publish(new UpdateAmmoEvent(currentAmmo, GetCurrentMagCapacity()));
            }

            SyncServerAmmo();
        }

        private void HandleKinemationReloadSingleRound() {
            if(!IsReloading || _currentWeaponData == null) return;
            if(_currentWeaponData.useMagReload) return;
            var magCapacity = GetCurrentMagCapacity();
            if(currentAmmo >= magCapacity) return;

            var ammoBefore = currentAmmo;
            currentAmmo = Mathf.Min(currentAmmo + 1, magCapacity);
            if(_kinemationFpWeaponDriver != null && _kinemationFpWeaponDriver.IsReloadSingleDebugEnabled()) {
                Debug.Log(
                    $"[Weapon][ReloadSingle] Applied +1 frame={Time.frameCount} time={Time.time:F3} " +
                    $"weapon={_currentWeaponData.weaponName} ammo={ammoBefore}->{currentAmmo} cap={magCapacity}");
            }

            if(playerController != null && playerController.IsOwner && HUDManager.Instance != null) {
                EventBus.Publish(new UpdateAmmoEvent(currentAmmo, magCapacity));
            }

            SyncServerAmmo();
        }

        private void CompleteKinemationPartialReloadWithoutFilling() {
            IsReloading = false;
            _reloadCoroutine = null;
            _autoReloadArmed = false;
            _reloadExpectedCompleteTime = float.PositiveInfinity;
            _kinemationReloadFallbackDeadline = float.PositiveInfinity;
            if(_kinemationFpWeaponDriver != null) _kinemationFpWeaponDriver.ResetReloadTracking();

            ExitReloadAnimation();
            SyncServerAmmo();

            if(playerController != null && playerController.IsOwner && _currentWeaponData != null && HUDManager.Instance != null) {
                EventBus.Publish(new UpdateAmmoEvent(currentAmmo, GetCurrentMagCapacity()));
            }
        }

        #endregion

        #region Private Methods - Effects

        private void PlayFireAnimationForCurrentWeapon(int authoritativeAmmoBeforeShot) {
            if(_kinemationFpWeaponDriver == null) return;
            _kinemationFpWeaponDriver.PlayFireAnimation(authoritativeAmmoBeforeShot);
        }

        private void PlayReloadAnimationForCurrentWeapon() {
            if(_kinemationFpWeaponDriver != null) _kinemationFpWeaponDriver.PlayReloadAnimation();
        }

        private void PlayReloadCompleteAnimationForCurrentWeapon() {
            if(_kinemationFpWeaponDriver != null) _kinemationFpWeaponDriver.PlayReloadCompleteAnimation();
        }

        private bool UseKinemationInternalSounds() {
            return _kinemationFpWeaponDriver != null && _kinemationFpWeaponDriver.AreKinemationSoundsEnabled();
        }

        private bool UseKinemationEventSoundRouting() {
            return _kinemationFpWeaponDriver != null && _kinemationFpWeaponDriver.IsKinemationSoundEventRoutingEnabled();
        }

        private bool ShouldSuppressLegacyReloadSound() {
            return UseKinemationEventSoundRouting() &&
                   _kinemationFpWeaponDriver != null &&
                   _kinemationFpWeaponDriver.HasAnyKinemationEventSound();
        }

        private Quaternion ResolveKinemationMuzzleFxRotation(Transform muzzleTransform, Vector3 preferredDirection) {
            var direction = preferredDirection;
            if(direction.sqrMagnitude <= 0.0001f && muzzleTransform != null) {
                direction = muzzleTransform.forward;
            }

            if(direction.sqrMagnitude <= 0.0001f) {
                direction = transform.forward;
            }

            direction.Normalize();
            if(!DoesCurrentMuzzleFlashUseForwardAxis()) {
                direction = -direction;
            }

            var up = Vector3.up;
            var cameraTransform = playerController != null ? playerController.FpCameraTransform : null;
            if(cameraTransform != null) {
                up = cameraTransform.up;
            } else if(muzzleTransform != null) {
                up = muzzleTransform.up;
            }

            if(Mathf.Abs(Vector3.Dot(up, direction)) > 0.98f) {
                up = Vector3.right;
            }

            return Quaternion.LookRotation(direction, up);
        }

        private bool DoesCurrentMuzzleFlashUseForwardAxis() {
            var muzzleFlashPrefab = _currentWeaponData != null ? _currentWeaponData.muzzleFlashPrefab : null;
            if(muzzleFlashPrefab == null) {
                return false;
            }

            var prefabName = muzzleFlashPrefab.name.ToLowerInvariant();
            return prefabName.Contains("180");
        }

        private GameObject EnsureKinemationLocalMuzzleFxInstance(Transform muzzleTransform, Quaternion spawnRotation) {
            if(_currentWeaponData == null || _currentWeaponData.muzzleFlashPrefab == null || muzzleTransform == null) {
                return null;
            }

            var sourcePrefab = _currentWeaponData.muzzleFlashPrefab;
            var needsRecreate = _kinemationLocalMuzzleFxInstance == null || _kinemationLocalMuzzleSourcePrefab != sourcePrefab;
            if(needsRecreate) {
                if(_kinemationLocalMuzzleFxInstance != null) {
                    QuiesceMuzzleFxInstance(_kinemationLocalMuzzleFxInstance, _kinemationLocalMuzzleVfx);
                    Destroy(_kinemationLocalMuzzleFxInstance);
                }

                _kinemationLocalMuzzleFxInstance = Instantiate(sourcePrefab, muzzleTransform.position, spawnRotation);
                _kinemationLocalMuzzleSourcePrefab = sourcePrefab;
                _kinemationLocalMuzzleVfx = _kinemationLocalMuzzleFxInstance.GetComponent<VisualEffect>();
                if(_kinemationLocalMuzzleVfx != null) {
                    _kinemationLocalMuzzleVfx.Stop();
                    _kinemationLocalMuzzleVfx.Reinit();
                }
            } else {
                _kinemationLocalMuzzleFxInstance.transform.SetPositionAndRotation(muzzleTransform.position, spawnRotation);
            }

            AttachMuzzleFollow(_kinemationLocalMuzzleFxInstance, muzzleTransform, followRotation: false);
            ApplyLayerRecursive(_kinemationLocalMuzzleFxInstance, muzzleTransform.gameObject.layer);
            return _kinemationLocalMuzzleFxInstance;
        }

        private void TriggerKinemationLocalMuzzleFx() {
            if(_kinemationLocalMuzzleFxInstance == null) return;
            ReactivateMuzzleFxInstance(_kinemationLocalMuzzleFxInstance, _kinemationLocalMuzzleVfx);

            if(_kinemationLocalMuzzleVfx != null) {
                _kinemationLocalMuzzleVfx.Stop();
                _kinemationLocalMuzzleVfx.Reinit();
                _kinemationLocalMuzzleVfx.Play();
                return;
            }

            var particleSystems = _kinemationLocalMuzzleFxInstance.GetComponentsInChildren<ParticleSystem>(true);
            foreach(var system in particleSystems) {
                if(system == null) continue;
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                system.Play(true);
            }
        }

        private void ClearKinemationLocalMuzzleFxInstance() {
            if(_kinemationLocalMuzzleFxInstance != null) {
                QuiesceMuzzleFxInstance(_kinemationLocalMuzzleFxInstance, _kinemationLocalMuzzleVfx);
                Destroy(_kinemationLocalMuzzleFxInstance);
            }

            _kinemationLocalMuzzleFxInstance = null;
            _kinemationLocalMuzzleVfx = null;
            _kinemationLocalMuzzleSourcePrefab = null;
        }

        private static void QuiesceMuzzleFxInstance(GameObject fxInstance, VisualEffect cachedVisualEffect) {
            if(fxInstance == null) return;

            if(cachedVisualEffect != null) {
                cachedVisualEffect.Stop();
                cachedVisualEffect.Reinit();
            }

            var vfxComponents = fxInstance.GetComponentsInChildren<VisualEffect>(true);
            foreach(var vfx in vfxComponents) {
                if(vfx == null) continue;
                vfx.Stop();
                vfx.Reinit();
            }

            var particleSystems = fxInstance.GetComponentsInChildren<ParticleSystem>(true);
            foreach(var particleSystem in particleSystems) {
                if(particleSystem == null) continue;
                particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        private static void ReactivateMuzzleFxInstance(GameObject fxInstance, VisualEffect cachedVisualEffect) {
            if(fxInstance == null) return;
            _ = cachedVisualEffect;
            if(!fxInstance.activeSelf) {
                fxInstance.SetActive(true);
            }
        }

        private void PrewarmKinemationLocalMuzzleFxInstance() {
            if(_hasPrewarmedKinemationMuzzleForCurrentWeapon) return;
            if(_kinemationFpWeaponDriver == null) return;
            if(_currentWeaponData == null || _currentWeaponData.muzzleFlashPrefab == null) return;

            const bool useWorldParent = false;
            if(!TryGetPreferredMuzzleTransform(useWorldParent, out var muzzleTransform) || muzzleTransform == null) {
                return;
            }

            var preferredDirection = Vector3.zero;
            var fpCameraTransform = playerController != null ? playerController.FpCameraTransform : null;
            if(fpCameraTransform != null) {
                preferredDirection = fpCameraTransform.forward;
            }

            var desiredWorldRotation = ResolveKinemationMuzzleFxRotation(muzzleTransform, preferredDirection);
            var fxGo = EnsureKinemationLocalMuzzleFxInstance(muzzleTransform, desiredWorldRotation);
            if(fxGo == null) return;

            QuiesceMuzzleFxInstance(fxGo, _kinemationLocalMuzzleVfx);
            _hasPrewarmedKinemationMuzzleForCurrentWeapon = true;
        }

        /// <summary>
        /// Play muzzle flash locally (owner only, FP)
        /// Muzzle flash tracks the weapon muzzle each frame to avoid drift while moving fast.
        /// </summary>
        private void PlayLocalMuzzleFlash(int authoritativeAmmoBeforeShot) {
            PlayFireAnimationForCurrentWeapon(authoritativeAmmoBeforeShot);

            PlayShootAnimationServerRpc();

            if(_currentWeaponData != null && _currentWeaponData.muzzleFlashPrefab != null) {
                var useWorldParent = GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch;
                if(TryGetPreferredMuzzleTransform(useWorldParent, out var muzzleTransform) && muzzleTransform != null) {
                    if(_kinemationFpWeaponDriver != null) {
                        var preferredDirection = Vector3.zero;
                        var fpCameraTransform = playerController != null ? playerController.FpCameraTransform : null;
                        if(!useWorldParent && fpCameraTransform != null) {
                            preferredDirection = fpCameraTransform.forward;
                        }

                        var desiredWorldRotation = ResolveKinemationMuzzleFxRotation(muzzleTransform, preferredDirection);
                        var fxGo = EnsureKinemationLocalMuzzleFxInstance(muzzleTransform, desiredWorldRotation);
                        if(fxGo != null) {
                            _localMuzzleFlashSpawnPositionForShot = fxGo.transform.position;
                            _hasLocalMuzzleFlashSpawnPositionForShot = true;
                            TriggerKinemationLocalMuzzleFx();
                        }
                    }
                }
            }

            if(!_fpMuzzleLight) return;
            _fpMuzzleLight.SetActive(true);
            _fpLightOffTime = Time.time + MuzzleLightTime;
        }

        private void ConsumePendingKinemationReloadSingleEvents() {
            if(!IsReloading || _kinemationFpWeaponDriver == null) return;
            if(_currentWeaponData == null || _currentWeaponData.useMagReload) return;

            var reloadSingleEvents = _kinemationFpWeaponDriver.ConsumeReloadSingleEventCount();
            if(reloadSingleEvents > 0 && _kinemationFpWeaponDriver.IsReloadSingleDebugEnabled()) {
                Debug.Log(
                    $"[Weapon][ReloadSingle] Dequeued during CanFire events={reloadSingleEvents} " +
                    $"frame={Time.frameCount} time={Time.time:F3} " +
                    $"weapon={_currentWeaponData.weaponName} ammoBefore={currentAmmo}");
            }
            for(var i = 0; i < reloadSingleEvents; i++) {
                HandleKinemationReloadSingleRound();
            }
        }

        private bool IsCurrentWeaponDrake() {
            if(_currentWeaponData == null || string.IsNullOrWhiteSpace(_currentWeaponData.weaponName)) return false;
            return _currentWeaponData.weaponName.IndexOf("drake", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        [Rpc(SendTo.Everyone)]
        private void PlayShootAnimationServerRpc() {
            if(_playerAnimator != null) {
                _playerAnimator.SetTrigger(RecoilHash);
            }
        }

        /// <summary>
        /// Play muzzle flash from network (non-owners only, 3P)
        /// Called via NetworkFxRelay RPC
        /// Muzzle flash tracks the weapon muzzle each frame to avoid drift while moving fast.
        /// </summary>
        public void PlayNetworkedMuzzleFlash(Vector3 endPoint) {
            if(playerController != null && playerController.IsOwner) {
                return;
            }

            // NON-OWNER ONLY: Play 3P world muzzle flash
            if(_currentWeaponData != null &&
               _currentWeaponData.muzzleFlashPrefab != null &&
               TryGetPreferredMuzzleTransform(true, out var muzzleTransform) &&
               muzzleTransform != null) {
                var position = muzzleTransform.position;
                var directionHint = endPoint - position;
                var desiredWorldRotation = ResolveKinemationMuzzleFxRotation(muzzleTransform, directionHint);

                var fxGo = Instantiate(_currentWeaponData.muzzleFlashPrefab, position,
                    desiredWorldRotation);
                AttachMuzzleFollow(fxGo, muzzleTransform, followRotation: false);
                ApplyLayerRecursive(fxGo, muzzleTransform.gameObject.layer);

                var fx = fxGo.GetComponent<VisualEffect>();
                if(fx != null) {
                    fx.Play();
                }
                Destroy(fxGo, 1f);
            }

            if(!_worldMuzzleLight) return;
            _worldMuzzleLight.SetActive(true);
            _worldLightOffTime = Time.time + MuzzleLightTime;
        }

        private static void ApplyLayerRecursive(GameObject root, int layer) {
            if(root == null) return;
            root.layer = layer;
            foreach(Transform child in root.transform) {
                if(child != null) {
                    ApplyLayerRecursive(child.gameObject, layer);
                }
            }
        }

        private static void AttachMuzzleFollow(GameObject fxGo, Transform muzzleTransform, bool followRotation) {
            if(fxGo == null || muzzleTransform == null) return;

            var follower = fxGo.GetComponent<MuzzleFlashFollow>();
            if(follower == null) {
                follower = fxGo.AddComponent<MuzzleFlashFollow>();
            }

            follower.Bind(muzzleTransform, followRotation);
        }

        private sealed class MuzzleFlashFollow : MonoBehaviour {
            private Transform _muzzleTransform;
            private bool _followRotation;

            public void Bind(Transform muzzleTransform, bool followRotation) {
                _muzzleTransform = muzzleTransform;
                _followRotation = followRotation;
            }

            private void LateUpdate() {
                if(_muzzleTransform == null) return;
                transform.position = _muzzleTransform.position;
                if(_followRotation) {
                    transform.rotation = _muzzleTransform.rotation;
                }
            }
        }

        public void SpawnTracerLocal(Vector3 start, Vector3 end, Vector3 hitNormal, bool madeImpact, bool hitPlayer, NetworkObjectReference hitPlayerRef = default) {
            if(!_currentWeaponData || !_currentWeaponData.bulletTrail) return;

            // Get trail from pool
            var trail = GetTrailFromPool();
            if(trail == null) return;

            // Set up trail
            trail.transform.position = start;
            trail.transform.rotation = Quaternion.LookRotation(end - start);
            trail.gameObject.SetActive(true);
            trail.enabled = true;
            trail.emitting = true;
            trail.Clear(); // Clear any previous trail data

            // Disable AudioSource on trail if it exists (we'll play sound manually only on misses)
            var trailAudioSource = trail.GetComponent<AudioSource>();
            if(trailAudioSource != null) {
                trailAudioSource.enabled = false;
            }

            // Play trail sound immediately on spawn when bullet misses (no impact)
            // When hitting world or players, impact sounds are already played
            if(!madeImpact && playerController != null && playerController.IsOwner && _audioRelay != null) {
                _audioRelay.RequestPlay("weapons.bullet.trail", start, allowOverlap: true);
            }

            StartCoroutine(SpawnTrail(trail, end, hitNormal, madeImpact, hitPlayer, hitPlayerRef));
        }

        private void PlayFireSound() {
            if(UseKinemationEventSoundRouting() && _kinemationFpWeaponDriver != null &&
               _kinemationFpWeaponDriver.HasKinemationFireSound()) {
                if(playerController == null || !playerController.IsOwner) return;
                if(_audioRelay == null || !playerController.NetworkObject) return;

                var kinemationFireSoundId = _kinemationFpWeaponDriver.GetKinemationFireSoundId();
                if(!string.IsNullOrWhiteSpace(kinemationFireSoundId)) {
                    _audioRelay.RequestPlayAttached(kinemationFireSoundId, new NetworkObjectReference(playerController.NetworkObject),
                        allowOverlap: true);
                }
                return;
            }

            if(UseKinemationInternalSounds()) return;
            if(!playerController.IsOwner) return;
            if(_audioRelay == null) return;

            var soundId = _currentWeaponData != null ? _currentWeaponData.shootSoundId : "";
            if(!string.IsNullOrWhiteSpace(soundId)) {
                _audioRelay.RequestPlayAttached(soundId, new NetworkObjectReference(playerController.NetworkObject), allowOverlap: true);
            }
        }

        private void PlayDryFireSound() {
            if(!playerController.IsOwner) return;
            if(_audioRelay == null) return;
            _audioRelay.RequestPlayAttached("weapons.bullet.dry",
                new NetworkObjectReference(playerController.NetworkObject), allowOverlap: true);
        }

        private void PlayReloadEffects() {
            PlayReloadAnimationForCurrentWeapon();

            PlayReloadAnimationServerRpc();

            if(ShouldSuppressLegacyReloadSound()) return;
            if(UseKinemationInternalSounds()) return;
            if(!playerController.IsOwner) return;
            if(_audioRelay == null) return;
            var soundId = _currentWeaponData != null ? _currentWeaponData.reloadSoundId : "";
            if(!string.IsNullOrWhiteSpace(soundId)) {
                _audioRelay.RequestPlayAttached(soundId, new NetworkObjectReference(playerController.NetworkObject), allowOverlap: false);
            }
        }

        [Rpc(SendTo.Everyone)]
        private void PlayReloadAnimationServerRpc() {
            _playerAnimator.SetTrigger(ReloadHash);
        }

        private float GetExpectedReloadDuration() {
            if(_currentWeaponData == null) return 0.5f;
            if(_kinemationFpWeaponDriver != null) {
                return KinemationReloadFallbackSeconds;
            }
            if(_currentWeaponData.useMagReload) {
                return Mathf.Max(0.05f, _currentWeaponData.reloadTime);
            }

            var perRoundTime = Mathf.Max(0.05f, _currentWeaponData.perRoundReloadTime);
            var roundsMissing = Mathf.Max(1, GetCurrentMagCapacity() - currentAmmo);
            return perRoundTime * roundsMissing;
        }

        private void ExitReloadAnimation() {
            if(_kinemationFpWeaponDriver != null) _kinemationFpWeaponDriver.PlayReloadCompleteAnimation();
        }

        private void RunReloadWatchdog() {
            if(Time.time < _nextReloadRecoveryAllowedTime) return;

            if(!IsReloading) return;
            if(Time.time <= _reloadExpectedCompleteTime) return;

            if(_currentWeaponData != null && !_currentWeaponData.useMagReload) {
                CompleteKinemationPartialReloadWithoutFilling();
            } else {
                CompleteReload();
            }

            _nextReloadRecoveryAllowedTime = Time.time + ReloadRecoveryCooldownSeconds;
        }

        private void UpdateKinemationReloadState() {
            if(!IsReloading || _kinemationFpWeaponDriver == null) return;

            var reloadSingleEvents = _kinemationFpWeaponDriver.ConsumeReloadSingleEventCount();
            if(reloadSingleEvents > 0 && _kinemationFpWeaponDriver.IsReloadSingleDebugEnabled()) {
                Debug.Log(
                    $"[Weapon][ReloadSingle] Dequeued events={reloadSingleEvents} frame={Time.frameCount} time={Time.time:F3} " +
                    $"weapon={(_currentWeaponData != null ? _currentWeaponData.weaponName : "(null)")} ammoBefore={currentAmmo}");
            }
            for(var i = 0; i < reloadSingleEvents; i++) {
                HandleKinemationReloadSingleRound();
            }

            if(_kinemationFpWeaponDriver.ConsumeReloadCompleteEvent()) {
                if(_kinemationFpWeaponDriver.IsReloadSingleDebugEnabled()) {
                    Debug.Log(
                        $"[Weapon][ReloadSingle] ReloadComplete consumed -> full fill frame={Time.frameCount} time={Time.time:F3} " +
                        $"weapon={(_currentWeaponData != null ? _currentWeaponData.weaponName : "(null)")} " +
                        $"ammoBeforeFill={currentAmmo} cap={GetCurrentMagCapacity()}");
                }
                CompleteReload();
                return;
            }

            if(!_kinemationFpWeaponDriver.IsReloadSequenceInProgress()) {
                if(_currentWeaponData != null && !_currentWeaponData.useMagReload) {
                    CompleteKinemationPartialReloadWithoutFilling();
                } else {
                    CompleteReload();
                }
                return;
            }

            if(Time.time <= _kinemationReloadFallbackDeadline) return;
            if(_currentWeaponData != null && !_currentWeaponData.useMagReload) {
                CompleteKinemationPartialReloadWithoutFilling();
            } else {
                CompleteReload();
            }
            _nextReloadRecoveryAllowedTime = Time.time + ReloadRecoveryCooldownSeconds;
        }

        private void ProcessKinemationSoundEvents() {
            if(_kinemationFpWeaponDriver == null) return;

            // Always drain queues to avoid stale events if ownership/state changed.
            _kinemationFpWeaponDriver.ConsumeWeaponFireSoundEventCount();
            _kinemationWeaponSoundEventBuffer.Clear();
            _kinemationFpWeaponDriver.ConsumeWeaponEventSoundIndices(_kinemationWeaponSoundEventBuffer);

            if(_kinemationWeaponSoundEventBuffer.Count == 0) return;
            if(!UseKinemationEventSoundRouting()) return;
            if(playerController == null || !playerController.IsOwner) return;
            if(_audioRelay == null || !playerController.NetworkObject) return;

            var attachRef = new NetworkObjectReference(playerController.NetworkObject);
            foreach(var clipIndex in _kinemationWeaponSoundEventBuffer) {
                if(!_kinemationFpWeaponDriver.TryGetKinemationEventSoundId(clipIndex, out var eventSoundId)) continue;
                if(string.IsNullOrWhiteSpace(eventSoundId)) continue;
                _audioRelay.RequestPlayAttached(eventSoundId, attachRef, allowOverlap: true);
            }
        }

        private void StopKinemationEventSoundsForCurrentWeapon() {
            if(_kinemationFpWeaponDriver == null) return;
            if(!UseKinemationEventSoundRouting()) return;
            if(playerController == null || !playerController.IsOwner) return;
            if(_audioRelay == null) return;

            var eventClipCount = _kinemationFpWeaponDriver.GetKinemationEventSoundClipCount();
            for(var clipIndex = 0; clipIndex < eventClipCount; clipIndex++) {
                if(!_kinemationFpWeaponDriver.IsLikelyReloadEventSoundClip(clipIndex)) continue;
                if(!_kinemationFpWeaponDriver.TryGetKinemationEventSoundId(clipIndex, out var eventSoundId)) continue;
                if(string.IsNullOrWhiteSpace(eventSoundId)) continue;
                _audioRelay.RequestStop(eventSoundId);
            }
        }

        private IEnumerator SpawnTrail(TrailRenderer trail, Vector3 hitPoint, Vector3 hitNormal, bool madeImpact,
            bool hitPlayer, NetworkObjectReference hitPlayerRef = default) {
            var position = trail.transform.position;
            var distance = Vector3.Distance(position, hitPoint);

            var remainingDistance = distance;

            while(remainingDistance > 0) {
                var t = 1f - remainingDistance / distance;
                trail.transform.position = Vector3.Lerp(position, hitPoint, t);
                remainingDistance -= BulletSpeed * Time.deltaTime;
                yield return null;
            }

            trail.transform.position = hitPoint;
            
            // Check if the local player is the one being hit - if so, don't spawn impact effect
            var isLocalPlayerHit = false;
            if(hitPlayer && hitPlayerRef.TryGet(out var hitNetworkObject) && hitNetworkObject != null) {
                var hitPlayerController = hitNetworkObject.GetComponent<PlayerController>();
                if(hitPlayerController != null && hitPlayerController.IsOwner) {
                    isLocalPlayerHit = true;
                }
            }
            
            if(madeImpact && _currentWeaponData && _currentWeaponData.bulletImpact && !isLocalPlayerHit) {
                var rotation = hitNormal.sqrMagnitude > 0.0001f
                    ? Quaternion.LookRotation(hitNormal)
                    : Quaternion.identity;

                var spawnPos = hitPoint + hitNormal.normalized * 0.005f;

                var impactInstance = Instantiate(_currentWeaponData.bulletImpact.gameObject, spawnPos, rotation);
                switch(hitPlayer) {
                    case true: {
                        var decal = impactInstance.transform.Find("Decal");
                        if(decal != null) {
                            decal.gameObject.SetActive(false);
                        }

                        break;
                    }
                    // Don't play bullet impact sound when hitting a player (hitmarker and hurt sounds handle this)
                    case false when playerController.IsOwner && _audioRelay != null:
                        _audioRelay.RequestPlay("weapons.bullet.impact", hitPoint, allowOverlap: true);
                        break;
                }
            }

            // Wait for trail to fade out, then return to pool
            yield return new WaitForSeconds(trail.time);

            ReturnTrailToPool(trail);
        }

        private IEnumerator SpawnOwnerTracerLocalAfterViewUpdate(Vector3 fallbackStart, Vector3 end, Vector3 hitNormal,
            bool madeImpact, bool hitPlayer, NetworkObjectReference hitPlayerRef) {
            // Wait until end-of-frame so camera/viewmodel transforms settle before we sample muzzle position.
            // This keeps local tracer origin aligned with the rendered FP muzzle during fast look updates.
            yield return new WaitForEndOfFrame();

            var start = fallbackStart;
            if(playerController != null && playerController.IsOwner) {
                if(!TryGetOwnerTracerStartPosition(out start)) {
                    start = fallbackStart;
                }
            }

            SpawnTracerLocal(start, end, hitNormal, madeImpact, hitPlayer, hitPlayerRef);
        }

        /// <summary>
        /// Initializes the trail pool with pre-allocated TrailRenderer objects.
        /// Only clears inactive trails from the pool - active trails are allowed to finish naturally.
        /// </summary>
        private void InitializeTrailPool() {
            // Clear existing pool (only inactive trails - active trails will finish and be cleaned up naturally)
            while(_trailPool.Count > 0) {
                var oldTrail = _trailPool.Dequeue();
                // Only destroy if it's inactive - active trails are still animating and will finish on their own
                if(oldTrail != null && !oldTrail.gameObject.activeInHierarchy) {
                    Destroy(oldTrail.gameObject);
                }
            }

            // Create new pool
            if(_currentWeaponData == null || _currentWeaponData.bulletTrail == null) return;
            for(var i = 0; i < TrailPoolSize; i++) {
                var trailObj = Instantiate(_currentWeaponData.bulletTrail);
                trailObj.emitting = false;
                trailObj.gameObject.SetActive(false);
                _trailPool.Enqueue(trailObj);
            }
        }

        /// <summary>
        /// Gets an available trail from the pool, or creates a new one if pool is empty.
        /// </summary>
        private TrailRenderer GetTrailFromPool() {
            // Try to find an inactive trail in the pool
            TrailRenderer trail = null;
            var attempts = 0;

            while(attempts < _trailPool.Count && _trailPool.Count > 0) {
                var candidate = _trailPool.Dequeue();
                _trailPool.Enqueue(candidate); // Put it back at the end

                if(candidate != null && !candidate.gameObject.activeInHierarchy) {
                    trail = candidate;
                    break;
                }

                attempts++;
            }

            // If no available trail found, create a new one
            if(trail == null && _currentWeaponData != null && _currentWeaponData.bulletTrail != null) {
                trail = Instantiate(_currentWeaponData.bulletTrail);
                trail.emitting = false;
            }

            return trail;
        }

        /// <summary>
        /// Returns a trail to the pool after it's finished.
        /// Only returns trails that are still valid and match the current weapon.
        /// </summary>
        private void ReturnTrailToPool(TrailRenderer trail) {
            // Check if trail was destroyed (e.g., during weapon switch)
            if(trail == null) return;
            
            // Check if trail's GameObject still exists
            if(trail.gameObject == null) return;
            
            // Don't return trails to pool if weapon has changed (let them be destroyed naturally)
            // Active trails from previous weapon will just be cleaned up by Unity
            if(_currentWeaponData == null || _currentWeaponData.bulletTrail == null) return;

            trail.emitting = false;
            trail.gameObject.SetActive(false);
            trail.Clear(); // Clear the trail data
            _trailPool.Enqueue(trail);
        }

        #endregion
    }
}

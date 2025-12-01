using System.Collections;
using System.Collections.Generic;
using Game.Audio;
using Game.Match;
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
    public class Weapon : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private CinemachineCamera _fpCamera;
        private Animator _playerAnimator;
        private LayerMask _enemyLayer;
        private LayerMask _worldLayer;
        private NetworkDamageRelay _damageRelay;
        private NetworkFxRelay _networkFXRelay;
        private NetworkSfxRelay _sfxRelay;
        private WeaponManager _weaponManager;

        [Header("Current Weapon State")]
        private WeaponData _currentWeaponData;

        private GameObject _currentFpWeaponInstance;
        private GameObject _currentWorldWeaponInstance;
        private Animator _weaponAnimator;
        private GameObject _fpMuzzleLight;
        private GameObject _worldMuzzleLight;
        private Coroutine _fpMuzzleLightCoroutine;
        private Coroutine _worldMuzzleLightCoroutine;

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

        // Bullet trail pooling
        private readonly Queue<TrailRenderer> _trailPool = new();
        private const int TrailPoolSize = 30;

        #endregion

        #region Animation Hashes

        private static readonly int RecoilHash = Animator.StringToHash("Recoil");
        private static readonly int ReloadHash = Animator.StringToHash("Reload");
        private static readonly int ReloadCompleteHash = Animator.StringToHash("ReloadComplete");

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
            if(_sfxRelay == null) _sfxRelay = playerController.SfxRelay;
            if(_weaponManager == null) _weaponManager = playerController.WeaponManager;

            _lastFireTime = Time.time;

            if(_damageRelay != null) {
                _damageRelay.OnHitConfirm -= OnHitConfirm;
                _damageRelay.OnHitConfirm += OnHitConfirm;
            }
        }

        private void LateUpdate() {
            if(_fpMuzzleLight != null && _fpMuzzleLight.activeSelf && Time.time >= _fpLightOffTime) {
                _fpMuzzleLight.SetActive(false);
            }

            // Turn off 3P light when time is up
            if(_worldMuzzleLight != null && _worldMuzzleLight.activeSelf && Time.time >= _worldLightOffTime) {
                _worldMuzzleLight.SetActive(false);
            }
        }

        private static void OnHitConfirm(bool wasKill) {
            var soundKey = wasKill ? SfxKey.Kill : SfxKey.Hit;
            EventBus.Publish(new PlayUISoundEvent(soundKey));
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
            GameObject worldWeaponInstance, int restoredAmmo) {
            // Cancel any ongoing reload (this will also stop the reload sound)
            if(IsReloading) {
                CancelReload();
            }

            // Set new weapon data
            _currentWeaponData = newWeaponData;
            _currentFpWeaponInstance = fpWeaponInstance;
            _currentWorldWeaponInstance = worldWeaponInstance;

            // Restore ammo
            currentAmmo = restoredAmmo;
            IsReloading = false;

            // Get animator from FP weapon
            if(_currentFpWeaponInstance) {
                _weaponAnimator = _currentFpWeaponInstance.GetComponent<Animator>();
                var lightTransform = _currentFpWeaponInstance.transform.Find(_currentWeaponData.fpMuzzleLightChildName);
                _fpMuzzleLight = lightTransform != null ? lightTransform.gameObject : null;

                if(_fpMuzzleLight) {
                    _fpMuzzleLight.SetActive(false);
                }
            }

            if(_currentWorldWeaponInstance != null) {
                var lightTransform =
                    _currentWorldWeaponInstance.transform.Find(_currentWeaponData.worldMuzzleLightChildName);
                _worldMuzzleLight = lightTransform != null ? lightTransform.gameObject : null;
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
                EventBus.Publish(new UpdateAmmoEvent(currentAmmo, _currentWeaponData.magSize));
            }
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

        public void StartReload() {
            if(!CanReload()) return;

            IsReloading = true;

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
            if(_weaponAnimator != null) {
                _weaponAnimator.SetTrigger(ReloadHash);
            }

            while(IsReloading && currentAmmo < _currentWeaponData.magSize) {
                // Play reload sound for each round (audio feedback)
                if(playerController.IsOwner && _sfxRelay != null) {
                    _sfxRelay.RequestWorldSfx(GetReloadSfxKey(), attachToSelf: true);
                }

                yield return new WaitForSeconds(perRoundTime);
                if(!IsReloading) yield break;

                currentAmmo = Mathf.Min(currentAmmo + 1, _currentWeaponData.magSize);

                if(playerController.IsOwner && HUDManager.Instance != null) {
                    EventBus.Publish(new UpdateAmmoEvent(currentAmmo, _currentWeaponData.magSize));
                }

                SyncServerAmmo();

                if(currentAmmo < _currentWeaponData.magSize) continue;
                // Trigger reload complete animation (shotgun-style reloads when mag is full)
                if(_weaponAnimator != null) {
                    _weaponAnimator.SetTrigger(ReloadCompleteHash);
                }
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
            if(playerController.IsOwner && _sfxRelay != null) {
                _sfxRelay.StopWorldSfx(GetReloadSfxKey());
            }

            IsReloading = false;
            _reloadCoroutine = null;
        }

        private void SyncServerAmmo() {
            if(_weaponManager != null) {
                _weaponManager.ReportAmmoSync(_weaponManager.CurrentWeaponIndex, currentAmmo);
            }
        }

        public void ResetWeapon() {
            if(!_currentWeaponData) return;
            currentAmmo = _currentWeaponData.magSize;
            IsReloading = false;
            _lastFireTime = Time.time;
            if(IsOwner) {
                netCurrentDamageMultiplier.Value = 1f;
            }
            SyncServerAmmo();
        }

        #endregion

        #region Getters

        public Vector3 GetMuzzlePosition() {
            if(_currentWeaponData == null) {
                var fpCameraTransform = playerController != null ? playerController.FpCameraTransform : null;
                if(fpCameraTransform != null && playerController.PlayerInput != null &&
                   playerController.PlayerInput.IsSniperOverlayActive) {
                    return fpCameraTransform.TransformPoint(playerController.PlayerInput.SniperMuzzleCameraOffset);
                }

                return fpCameraTransform != null ? fpCameraTransform.position : transform.position;
            }

            var isPostMatch = false;
            if(GameMenuManager.Instance != null) {
                isPostMatch = GameMenuManager.Instance.IsPostMatch;
            }
            var preferWorld = playerController == null ||
                              !playerController.IsOwner ||
                              isPostMatch;

            if(playerController != null && playerController.IsOwner &&
               playerController.PlayerInput != null &&
               playerController.PlayerInput.IsSniperOverlayActive) {
                var fpCameraTransform = playerController.FpCameraTransform;
                return fpCameraTransform != null ? fpCameraTransform.position : transform.position;
            }

            return ResolveMuzzlePosition(preferWorld);
        }

        /// <summary>
        /// Get muzzle position directly from weapon transform at current moment
        /// Called immediately in PerformShot() before LateUpdate, so weapon transform is accurate
        /// This avoids lag from queuing FX for LateUpdate
        /// </summary>
        private Vector3 GetMuzzlePositionFromCamera() {
            if(!playerController || !playerController.IsOwner || _currentWeaponData == null) return GetMuzzlePosition();
            if(playerController.PlayerInput != null && playerController.PlayerInput.IsSniperOverlayActive) {
                var fpCameraTransform = playerController.FpCameraTransform;
                return fpCameraTransform != null
                    ? fpCameraTransform.TransformPoint(playerController.PlayerInput.SniperMuzzleCameraOffset)
                    : playerController.Position;
            }

            return ResolveMuzzlePosition(false);
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
                : (fpCameraTransform != null ? fpCameraTransform.rotation : transform.rotation);
        }

        public int GetWeaponSlot() {
            if(_currentWeaponData == null) return 0;
            return _currentWeaponData.weaponSlot;
        }
        public float GetFireRate() {
            if(_currentWeaponData == null) return 0.1f;
            return _currentWeaponData.fireRate;
        }
        public int GetMagSize() {
            if(_currentWeaponData == null) return 30;
            return _currentWeaponData.magSize;
        }
        public GameObject GetWeaponPrefab() => _currentFpWeaponInstance;
        public Vector3 GetSpawnPosition() {
            if(_currentWeaponData == null) return Vector3.zero;
            return _currentWeaponData.spawnPosition;
        }
        public Vector3 GetSpawnRotation() {
            if(_currentWeaponData == null) return Vector3.zero;
            return _currentWeaponData.spawnRotation;
        }

        private Vector3 ResolveMuzzlePosition(bool preferWorldModel) {
            var sourceTransform = GetPreferredWeaponTransform(preferWorldModel);
            if(sourceTransform != null && _currentWeaponData != null) {
                return sourceTransform.TransformPoint(_currentWeaponData.muzzleLocalOffset);
            }

            if(preferWorldModel) {
                return playerController != null ? playerController.transform.position : transform.position;
            }

            var fpCameraTransform = playerController != null ? playerController.FpCameraTransform : null;
            return fpCameraTransform != null ? fpCameraTransform.position : transform.position;
        }

        private Transform GetPreferredWeaponTransform(bool preferWorldModel) {
            if(preferWorldModel) {
                if(_currentWorldWeaponInstance != null) return _currentWorldWeaponInstance.transform;
                if(_currentFpWeaponInstance != null) return _currentFpWeaponInstance.transform;
            } else {
                if(_currentFpWeaponInstance != null) return _currentFpWeaponInstance.transform;
                if(_currentWorldWeaponInstance != null) return _currentWorldWeaponInstance.transform;
            }

            return null;
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

            if(IsReloading && !_currentWeaponData.useMagReload) {
                CancelReload();
            }

            return Time.time >= _lastFireTime + _currentWeaponData.fireRate && currentAmmo > 0 && !IsReloading;
        }

        private void HandleCannotFire() {
            if(!_currentWeaponData) return;
            if(Time.time < _lastFireTime + _currentWeaponData.fireRate || IsReloading || currentAmmo != 0) return;

            _lastFireTime = Time.time;
            PlayDryFireSound();
        }

        private ulong _shotSequence;

        private void PerformShot() {
            var fpCameraTransform = playerController != null ? playerController.FpCameraTransform : null;
            if(fpCameraTransform == null) return;
            
            var origin = fpCameraTransform.position;
            var forward = fpCameraTransform.forward;

            currentAmmo--;
            _lastFireTime = Time.time;

            if(playerController != null && playerController.IsOwner) {
                if(HUDManager.Instance != null) {
                    EventBus.Publish(new UpdateAmmoEvent(currentAmmo, _currentWeaponData.magSize));
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

            // Calculate muzzle position directly from camera to bypass weapon transform lag
            var capturedMuzzlePos = GetMuzzlePositionFromCamera();

            if(playerController != null && playerController.IsOwner) {
                PlayLocalMuzzleFlash();
            }

            for(var i = 0; i < pelletCount; i++) {
                var direction = ApplySpread(forward, spreadDegrees);
                FirePellet(origin, direction, out var endPoint, out var hitNormal, out var madeImpact,
                    out var hitPlayer, out var hitPlayerRef, weaponIndex, shotId);

                if(playerController != null && playerController.IsOwner) {
                    SpawnTracerLocal(capturedMuzzlePos, endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef);
                }

                var playMuzzleFlash = i == 0;
                _networkFXRelay.RequestShotFx(endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef, playMuzzleFlash);
            }
        }

        private void FirePellet(Vector3 origin, Vector3 direction, out Vector3 endPoint, out Vector3 hitNormal,
            out bool madeImpact, out bool hitPlayer, out NetworkObjectReference hitPlayerRef, int weaponIndex, ulong shotId) {
            var hitLayer = _enemyLayer | _worldLayer;
            bool shotHit;
            RaycastHit hit;
            
            // Check if we should use spherecast for sniper weapons
            var useSniperSpherecast = _currentWeaponData != null && _currentWeaponData.useSniperOverlay &&
                                      playerController != null && playerController.PlayerInput != null &&
                                      playerController.PlayerInput.IsSniperOverlayActive;
            
            if(useSniperSpherecast) {
                // Use spherecast with distance-based radius scaling for sniper weapons
                // Conservative values: 3cm base, 15cm max, scales with distance using exponential curve
                const float baseRadius = 0.03f; // 3cm base radius
                const float maxRadius = 0.15f;  // 15cm max radius
                const float maxDistance = 300f; // 300m max distance for scaling
                
                // First do a raycast to get distance, then use that to calculate sphere radius
                // This gives us distance-based scaling
                var hasTempHit = Physics.Raycast(origin, direction, out var tempHit, Mathf.Infinity, hitLayer);
                
                if(hasTempHit) {
                    // Calculate radius based on distance with exponential curve
                    // t*t makes it scale more aggressively at longer distances
                    var t = Mathf.Clamp01(tempHit.distance / maxDistance);
                    var radius = Mathf.Lerp(baseRadius, maxRadius, t * t);
                    
                    // Now do spherecast with calculated radius
                    shotHit = Physics.SphereCast(origin, radius, direction, out hit, Mathf.Infinity, hitLayer);
                } else {
                    // No hit found, use base radius for spherecast
                    shotHit = Physics.SphereCast(origin, baseRadius, direction, out hit, Mathf.Infinity, hitLayer);
                }
            } else {
                // Standard raycast for non-sniper weapons
                shotHit = Physics.Raycast(origin, direction, out hit, Mathf.Infinity, hitLayer);
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
                    Mathf.Clamp(CurrentDamageMultiplier, 1f, _currentWeaponData.maxDamageMultiplier);
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
                    targetMultiplier = Mathf.Lerp(1f, _currentWeaponData.maxDamageMultiplier, scaleFactor);
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

            CurrentDamageMultiplier = Mathf.Clamp(CurrentDamageMultiplier, 1f, _currentWeaponData.maxDamageMultiplier);
        }

        #endregion

        #region Private Methods - Reloading

        private bool CanReload() {
            if(!_currentWeaponData || _weaponManager.IsPullingOut) return false;
            return currentAmmo < _currentWeaponData.magSize && _reloadCoroutine == null;
        }

        private void CompleteReload() {
            if(!_currentWeaponData) return;
            currentAmmo = _currentWeaponData.magSize;
            IsReloading = false;
            _reloadCoroutine = null;

            // Trigger reload complete animation (mag-style reloads)
            if(_weaponAnimator != null) {
                _weaponAnimator.SetTrigger(ReloadCompleteHash);
            }

            if(playerController.IsOwner && HUDManager.Instance != null) {
                EventBus.Publish(new UpdateAmmoEvent(currentAmmo, _currentWeaponData.magSize));
            }

            SyncServerAmmo();
        }

        #endregion

        #region Private Methods - Effects

        /// <summary>
        /// Play muzzle flash locally (owner only, FP)
        /// Muzzle flash is parented to weapon muzzle so it follows the player when moving fast.
        /// </summary>
        private void PlayLocalMuzzleFlash() {
            if(_weaponAnimator != null) {
                _weaponAnimator.SetTrigger(RecoilHash);
            }

            PlayShootAnimationServerRpc();

            if(_currentWeaponData != null && _currentWeaponData.muzzleFlashPrefab != null) {
                var useWorldParent = GameMenuManager.Instance.IsPostMatch;
                var parentTransform = GetPreferredWeaponTransform(useWorldParent);
                if(parentTransform == null) {
                    parentTransform = GetPreferredWeaponTransform(!useWorldParent);
                }

                if(parentTransform != null) {
                    var fxGo = Instantiate(_currentWeaponData.muzzleFlashPrefab, parentTransform);
                    fxGo.transform.localPosition = _currentWeaponData.muzzleLocalOffset;
                    fxGo.transform.localRotation = Quaternion.identity;

                    var fx = fxGo.GetComponent<VisualEffect>();
                    if(fx != null) {
                        fx.Play();
                    }
                    Destroy(fxGo, 1f);
                }
            }

            if(!_fpMuzzleLight) return;
            _fpMuzzleLight.SetActive(true);
            _fpLightOffTime = Time.time + MuzzleLightTime;
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
        /// Muzzle flash is parented to weapon muzzle so it follows the player when moving fast.
        /// </summary>
        public void PlayNetworkedMuzzleFlash() {
            // NON-OWNER ONLY: Play 3P world muzzle flash
            if(_currentWeaponData != null && _currentWeaponData.muzzleFlashPrefab != null && _currentWorldWeaponInstance != null) {
                var fxGo = Instantiate(_currentWeaponData.muzzleFlashPrefab, _currentWorldWeaponInstance.transform);
                fxGo.transform.localPosition = _currentWeaponData.muzzleLocalOffset;
                fxGo.transform.localRotation = Quaternion.identity;

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

        public void SpawnTracerLocal(Vector3 start, Vector3 end, Vector3 hitNormal, bool madeImpact, bool hitPlayer, NetworkObjectReference hitPlayerRef = default) {
            if(!_currentWeaponData || !_currentWeaponData.bulletTrail) return;

            // Get trail from pool
            var trail = GetTrailFromPool();
            if(trail == null) return;

            // Set up trail
            trail.transform.position = start;
            trail.transform.rotation = Quaternion.LookRotation(end - start);
            trail.gameObject.SetActive(true);
            trail.Clear(); // Clear any previous trail data

            // Disable AudioSource on trail if it exists (we'll play sound manually only on misses)
            var trailAudioSource = trail.GetComponent<AudioSource>();
            if(trailAudioSource != null) {
                trailAudioSource.enabled = false;
            }

            // Play trail sound immediately on spawn when bullet misses (no impact)
            // When hitting world or players, impact sounds are already played
            if(!madeImpact && playerController != null && playerController.IsOwner && _sfxRelay != null) {
                // Play at start position for immediate audio feedback
                _sfxRelay.RequestWorldSfxAtPosition(SfxKey.BulletTrail, start, allowOverlap: true);
            }

            StartCoroutine(SpawnTrail(trail, end, hitNormal, madeImpact, hitPlayer, hitPlayerRef));
        }

        private SfxKey GetShootSfxKey() {
            if(_currentWeaponData == null) return SfxKey.Shoot;
            return _currentWeaponData.shootSfx;
        }
        private SfxKey GetReloadSfxKey() {
            if(_currentWeaponData == null) return SfxKey.Reload;
            return _currentWeaponData.reloadSfx;
        }

        private void PlayFireSound() {
            if(playerController.IsOwner)
                _sfxRelay.RequestWorldSfx(GetShootSfxKey(), attachToSelf: true, true);
        }

        private void PlayDryFireSound() {
            if(playerController.IsOwner)
                _sfxRelay.RequestWorldSfx(SfxKey.Dry, attachToSelf: true, true);
        }

        private void PlayReloadEffects() {
            if(_weaponAnimator != null) {
                _weaponAnimator.SetTrigger(ReloadHash);
            }

            PlayReloadAnimationServerRpc();

            if(playerController.IsOwner)
                _sfxRelay.RequestWorldSfx(GetReloadSfxKey(), attachToSelf: true);
        }

        [Rpc(SendTo.Everyone)]
        private void PlayReloadAnimationServerRpc() {
            _playerAnimator.SetTrigger(ReloadHash);
        }

        private IEnumerator SpawnTrail(TrailRenderer trail, Vector3 hitPoint, Vector3 hitNormal, bool madeImpact,
            bool hitPlayer, NetworkObjectReference hitPlayerRef = default) {
            var position = trail.transform.position;
            var distance = Vector3.Distance(position, hitPoint);

            var remainingDistance = distance;

            while(remainingDistance > 0) {
                var t = 1f - (remainingDistance / distance);
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
                if(hitPlayer) {
                    var decal = impactInstance.transform.Find("Decal");
                    if(decal != null) {
                        decal.gameObject.SetActive(false);
                    }
                }

                // Don't play bullet impact sound when hitting a player (hitmarker and hurt sounds handle this)
                if(!hitPlayer && playerController.IsOwner && _sfxRelay != null) {
                    _sfxRelay.RequestWorldSfxAtPosition(SfxKey.BulletImpact, hitPoint, allowOverlap: true);
                }
            }

            // Wait for trail to fade out, then return to pool
            yield return new WaitForSeconds(trail.time);

            ReturnTrailToPool(trail);
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

            trail.gameObject.SetActive(false);
            trail.Clear(); // Clear the trail data
            _trailPool.Enqueue(trail);
        }

        #endregion
    }
}
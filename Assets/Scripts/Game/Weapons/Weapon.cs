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
        private bool _autoReloadArmed;

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
            if(_audioRelay == null) _audioRelay = playerController.AudioRelay;
            if(_weaponManager == null) _weaponManager = playerController.WeaponManager;

            _lastFireTime = Time.time;

            if(_damageRelay == null) return;
            _damageRelay.OnHitConfirm -= OnHitConfirm;
            _damageRelay.OnHitConfirm += OnHitConfirm;
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
            _autoReloadArmed = false;

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
                if(playerController.IsOwner && _audioRelay != null) {
                    var soundId = _currentWeaponData != null ? _currentWeaponData.reloadSoundId : "";
                    if(!string.IsNullOrWhiteSpace(soundId)) {
                        _audioRelay.RequestPlayAttached(soundId, new NetworkObjectReference(playerController.NetworkObject),
                            allowOverlap: false);
                    }
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
            if(playerController.IsOwner && _audioRelay != null) {
                var soundId = _currentWeaponData != null ? _currentWeaponData.reloadSoundId : "";
                if(!string.IsNullOrWhiteSpace(soundId)) {
                    _audioRelay.RequestStop(soundId);
                }
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
            _autoReloadArmed = false;
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

            if(playerController == null || !playerController.IsOwner ||
               playerController.PlayerInput == null ||
               !playerController.PlayerInput.IsSniperOverlayActive) return ResolveMuzzlePosition(preferWorld);
            {
                var fpCameraTransform = playerController.FpCameraTransform;
                return fpCameraTransform != null ? fpCameraTransform.position : transform.position;
            }

        }

        /// <summary>
        /// Get muzzle position directly from weapon transform at current moment
        /// Called immediately in PerformShot() before LateUpdate, so weapon transform is accurate
        /// This avoids lag from queuing FX for LateUpdate
        /// </summary>
        private Vector3 GetMuzzlePositionFromCamera() {
            if(!playerController || !playerController.IsOwner || _currentWeaponData == null) return GetMuzzlePosition();
            if(playerController.PlayerInput == null || !playerController.PlayerInput.IsSniperOverlayActive)
                return ResolveMuzzlePosition(false);
            var fpCameraTransform = playerController.FpCameraTransform;
            return fpCameraTransform != null
                ? fpCameraTransform.TransformPoint(playerController.PlayerInput.SniperMuzzleCameraOffset)
                : playerController.Position;

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
            return _currentWeaponData == null ? 0 : _currentWeaponData.weaponSlot;
        }
        public float GetFireRate() {
            return _currentWeaponData == null ? 0.1f : _currentWeaponData.fireRate;
        }
        public int GetMagSize() {
            return _currentWeaponData == null ? 30 : _currentWeaponData.magSize;
        }
        public GameObject GetWeaponPrefab() => _currentFpWeaponInstance;
        public Vector3 GetSpawnPosition() {
            return _currentWeaponData == null ? Vector3.zero : _currentWeaponData.spawnPosition;
        }
        public Vector3 GetSpawnRotation() {
            return _currentWeaponData == null ? Vector3.zero : _currentWeaponData.spawnRotation;
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
            _autoReloadArmed = true;
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

            if (playerController != null && playerController.IsOwner) {
                PlayLocalMuzzleFlash();
                // Record Stats
                if (ProgressionManager.Instance != null) {
                    ProgressionManager.Instance.RecordShotFired();
                }
            }

            var anyPelletHitPlayer = false;

            for(var i = 0; i < pelletCount; i++) {
                var direction = ApplySpread(forward, spreadDegrees);
                FirePellet(origin, direction, out var endPoint, out var hitNormal, out var madeImpact,
                    out var hitPlayer, out var hitPlayerRef, weaponIndex, shotId);

                if (hitPlayer) anyPelletHitPlayer = true;

                if(playerController != null && playerController.IsOwner) {
                    SpawnTracerLocal(capturedMuzzlePos, endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef);
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
                // Use minDamageRange (where damage is lowest/furthest) as the growth end distance if falloff is used
                // Otherwise use maxServerRange as a fallbackcap
                var growthEnd = _currentWeaponData.useDamageFalloff 
                    ? Mathf.Max(growthStart + 0.1f, _currentWeaponData.minDamageRange) 
                    : _currentWeaponData.maxServerRange;
                
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
            return currentAmmo < _currentWeaponData.magSize && _reloadCoroutine == null;
        }

        private void CompleteReload() {
            if(!_currentWeaponData) return;
            currentAmmo = _currentWeaponData.magSize;
            IsReloading = false;
            _reloadCoroutine = null;
            _autoReloadArmed = false;

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
            if(!madeImpact && playerController != null && playerController.IsOwner && _audioRelay != null) {
                _audioRelay.RequestPlay("weapons.bullet.trail", start, allowOverlap: true);
            }

            StartCoroutine(SpawnTrail(trail, end, hitNormal, madeImpact, hitPlayer, hitPlayerRef));
        }

        private void PlayFireSound() {
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
            if(_weaponAnimator != null) {
                _weaponAnimator.SetTrigger(ReloadHash);
            }

            PlayReloadAnimationServerRpc();

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
using Game.Match;
using Game.Player.Core;
using Game.Progression;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Game.Weapons {
    public partial class Weapon {
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
            if(!CurrentWeaponData || _weaponManager.IsPullingOut) return false;

            if(!IsReloading || CurrentWeaponData.useMagReload || currentAmmo <= 0)
                return Time.time >= _lastFireTime + CurrentWeaponData.fireRate && currentAmmo > 0 && !IsReloading;
            ConsumePendingKinemationReloadSingleEvents();
            if(_kinemationFpWeaponDriver != null) {
                _kinemationFpWeaponDriver.NotifyDrakeReloadCanceledByShot();
            }

            // For shell-by-shell reloads, allow cancel only after at least one round was inserted.
            CancelReload();

            return Time.time >= _lastFireTime + CurrentWeaponData.fireRate && currentAmmo > 0 && !IsReloading;
        }

        private void HandleCannotFire() {
            if(!CurrentWeaponData) return;
            if(Time.time < _lastFireTime + CurrentWeaponData.fireRate || IsReloading || currentAmmo != 0) return;

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
            var clientShotTime = Time.time;
            var authoritativeAmmoBeforeShot = Mathf.Max(0, currentAmmo);

            currentAmmo--;
            _lastFireTime = Time.time;

            if(playerController != null && playerController.IsOwner) {
                PublishOwnerAmmoToHud();
            }

            var weaponIndex = _weaponManager != null ? _weaponManager.CurrentWeaponIndex : -1;
            if(weaponIndex < 0) return;

            var shotId = ++_shotSequence;
            var shooterVelocityAtShot = playerController != null ? playerController.GetFullVelocity : Vector3.zero;

            _weaponManager.ReportShotFired(weaponIndex, shotId, clientShotTime);

            var pelletCount = 1;
            if(CurrentWeaponData != null && CurrentWeaponData.usePelletSpread) {
                pelletCount = Mathf.Max(1, CurrentWeaponData.pelletCount);
            }

            var spreadDegrees = CurrentWeaponData != null ? CurrentWeaponData.bulletSpread : 0f;

            // If sniper overlay is active and weapon uses sniper overlay, remove all spread for perfect accuracy
            if(CurrentWeaponData != null && CurrentWeaponData.useSniperOverlay &&
               playerController != null && playerController.PlayerInput != null &&
               playerController.PlayerInput.IsSniperOverlayActive) {
                spreadDegrees = 0f;
            }

            _hasLocalMuzzleFlashSpawnPositionForShot = false;
            _localMuzzleFlashSpawnPositionForShot = Vector3.zero;

            if(playerController != null && playerController.IsOwner) {
                PlayLocalMuzzleFlash(authoritativeAmmoBeforeShot);
                // Record Stats
                if(ProgressionManager.Instance != null) {
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

                if(hitPlayer) anyPelletHitPlayer = true;

                if(playerController != null && playerController.IsOwner && hasMuzzlePosition) {
                    StartCoroutine(SpawnOwnerTracerLocalAfterViewUpdate(capturedMuzzlePos, endPoint, hitNormal,
                        madeImpact, hitPlayer, hitPlayerRef, shooterVelocityAtShot));
                }

                var playMuzzleFlash = i == 0;
                _networkFXRelay.RequestShotFx(endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef,
                    playMuzzleFlash, shooterVelocityAtShot);
            }

            // If any pellet hit a player, count it as a "Shot Hit" (accuracy = Shots Hit / Shots Fired)
            // This prevents shotguns from giving > 100% accuracy
            if(anyPelletHitPlayer && playerController != null && playerController.IsOwner &&
               ProgressionManager.Instance != null) {
                ProgressionManager.Instance.RecordShotHit();
            }
        }

        private void FirePellet(Vector3 origin, Vector3 direction, out Vector3 endPoint, out Vector3 hitNormal,
            out bool madeImpact, out bool hitPlayer, out NetworkObjectReference hitPlayerRef, int weaponIndex,
            ulong shotId) {
            var hitLayer = _enemyLayer | _worldLayer;
            var shotHit = false;
            RaycastHit hit = default;

            // Default max distance for raycast
            var maxDist = 1000f;

            // Check if we should use the new hybrid sphere/cone cast system
            var useHybridSystem = CurrentWeaponData != null && CurrentWeaponData.useSphereCast
                                  || CurrentWeaponData != null && CurrentWeaponData.useSniperOverlay &&
                                  playerController != null && playerController.PlayerInput != null &&
                                  playerController.PlayerInput.IsSniperOverlayActive;

            // Legacy/Sniper Override check (maintain support for old sniper bool if needed, but prefer hybrid)

            if(useHybridSystem) {
                // HYBRID HIT REGISTRATION SYSTEM
                // 1. Raycast world to establish hard stop distance.
                // 2. SphereCast players up to that stop for forgiving hits.
                // 3. Fallback to world ray hit if no valid player hit.
                var hasWorldHit = Physics.Raycast(origin, direction, out var worldHit, maxDist, _worldLayer);
                if(hasWorldHit) {
                    maxDist = worldHit.distance;
                }

                var maxRadius = CurrentWeaponData.sphereCastMaxRadius;
                var baseRadius = CurrentWeaponData.sphereCastRadius;
                var growthStart = CurrentWeaponData.sphereCastGrowthStartDist;
                var growthEnd = CurrentWeaponData.useDamageFalloff
                    ? Mathf.Max(growthStart + 0.1f, CurrentWeaponData.minDamageRange)
                    : Mathf.Max(growthStart + 0.1f, maxDist);

                // Only sphere-check enemies; world uses strict ray so grazing surfaces always resolve.
                if(Physics.SphereCast(origin, maxRadius, direction, out var sphereHit, maxDist, _enemyLayer)) {
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

                    var hitPoint = sphereHit.point;
                    var projectedPoint = origin + direction * Vector3.Dot(hitPoint - origin, direction);
                    var distFromRay = Vector3.Distance(hitPoint, projectedPoint);

                    if(distFromRay <= allowedRadius && sphereHit.distance <= maxDist) {
                        shotHit = true;
                        hit = sphereHit;
                    }
                }

                if(!shotHit) {
                    if(Physics.Raycast(origin, direction, out var strictHit, maxDist, hitLayer)) {
                        shotHit = true;
                        hit = strictHit;
                    } else if(hasWorldHit) {
                        shotHit = true;
                        hit = worldHit;
                    }
                }

#if UNITY_EDITOR
                if(playerController.IsOwner) {
                    var debugPoint = shotHit ? hit.point : Vector3.zero;
                    DrawHitRegistrationDebug(origin, direction, maxDist, debugPoint, shotHit, baseRadius, maxRadius,
                        growthStart, growthEnd);
                }
#endif
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

                ApplyDamageToHit(hit, origin, weaponIndex, shotId);
            } else {
                endPoint = origin + direction * 600f;
                hitNormal = direction;
                madeImpact = false;
                hitPlayer = false;
            }
        }

        private void ApplyDamageToHit(RaycastHit hit, Vector3 origin, int weaponIndex, ulong shotId) {
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
            if(MatchCombatAuthority.Instance != null) {
                MatchCombatAuthority.Instance.RequestDamageAuthorityServerRpc(
                    targetRef,
                    hit.point,
                    hitDirection,
                    hitRigidbody != null ? bodyPartTag : null,
                    hitRigidbody != null && isHeadshot,
                    weaponIndex,
                    Time.time,
                    shotId
                );
            } else {
                Debug.LogError("[Weapon] MatchCombatAuthority is missing in the active gameplay scene. Damage requests cannot be processed.");
            }
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

        private void UpdateDamageMultiplier() {
            if(!IsOwner) return;
            if(!CurrentWeaponData) return;

            var isDead = playerController != null && playerController.IsDead;
            var currentSpeed = playerController != null ? playerController.GetFullVelocity.magnitude : 0f;
            CurrentDamageMultiplier = AdvanceDamageMultiplier(CurrentDamageMultiplier, ref _peakDamageMultiplier,
                ref _lastPeakTime, currentSpeed, isDead);
        }

        private static float AdvanceDamageMultiplier(float currentMultiplier, ref float peakMultiplier, ref float lastPeakTime,
            float currentSpeed, bool isDead) {
            if(isDead) {
                currentMultiplier = Mathf.MoveTowards(currentMultiplier, 1f, MultiplierDecayRate * Time.deltaTime);
                peakMultiplier = currentMultiplier;
                lastPeakTime = 0f;
                return Mathf.Clamp(currentMultiplier, 1f, MaxDamageMultiplier);
            }

            var targetMultiplier = CalculateTargetDamageMultiplier(currentSpeed);

            if(targetMultiplier >= currentMultiplier) {
                currentMultiplier = Mathf.Lerp(currentMultiplier, targetMultiplier, MultiplierGainRate * Time.deltaTime);
                peakMultiplier = currentMultiplier;
                lastPeakTime = Time.time;
            } else if(Time.time - lastPeakTime < MultiplierGracePeriod) {
                currentMultiplier = peakMultiplier;
            } else {
                currentMultiplier =
                    Mathf.MoveTowards(currentMultiplier, targetMultiplier, MultiplierDecayRate * Time.deltaTime);
                peakMultiplier = currentMultiplier;
            }

            return Mathf.Clamp(currentMultiplier, 1f, MaxDamageMultiplier);
        }

        private static float CalculateTargetDamageMultiplier(float currentSpeed) {
            if(currentSpeed < MinSpeedThreshold) {
                return 1f;
            }

            var scaleFactor = Mathf.InverseLerp(MinSpeedThreshold, MaxSpeedThreshold, currentSpeed);
            return Mathf.Lerp(1f, MaxDamageMultiplier, scaleFactor);
        }

#if UNITY_EDITOR
        private static void DrawHitRegistrationDebug(Vector3 origin, Vector3 direction, float maxDist, Vector3 hitPoint,
            bool hitSomething,
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

                if(currentDist > maxDist) break; // Redundant but safe

                // Calculate radius at this distance
                float currentRadius;
                if(currentDist <= startDist) currentRadius = baseRadius;
                else if(currentDist >= endDist) currentRadius = maxRadius;
                else
                    currentRadius = Mathf.Lerp(baseRadius, maxRadius,
                        Mathf.InverseLerp(startDist, endDist, currentDist));

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
            Debug.DrawLine(hitPoint - Vector3.up * 0.1f, hitPoint + Vector3.up * 0.1f, Color.green, duration);
            Debug.DrawLine(hitPoint - Vector3.right * 0.1f, hitPoint + Vector3.right * 0.1f, Color.green, duration);
            Debug.DrawLine(hitPoint - Vector3.forward * 0.1f, hitPoint + Vector3.forward * 0.1f, Color.green, duration);
        }
#endif

        #endregion
    }
}

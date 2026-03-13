using Game.Match;
using Game.Player.Core;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Game.Weapons.Core {
    internal sealed class WeaponCombatCoordinator {
        private readonly Weapon _weapon;

        public WeaponCombatCoordinator(Weapon weapon) {
            _weapon = weapon;
        }

        public bool CanFire() {
            if(!_weapon.CurrentWeaponData || _weapon.Manager == null || _weapon.Manager.IsPullingOut) return false;
            if(_weapon.Reloading && (!_weapon.CurrentWeaponData.useMagReload || _weapon.CurrentAmmo <= 0)) {
                return Time.time >= _weapon.LastFireTime + _weapon.CurrentWeaponData.fireRate &&
                       _weapon.CurrentAmmo > 0 &&
                       !_weapon.Reloading;
            }

            return Time.time >= _weapon.LastFireTime + _weapon.CurrentWeaponData.fireRate &&
                   _weapon.CurrentAmmo > 0 &&
                   !_weapon.Reloading;
        }

        public void HandleCannotFire() {
            if(!_weapon.CurrentWeaponData) return;
            if(Time.time < _weapon.LastFireTime + _weapon.CurrentWeaponData.fireRate || _weapon.Reloading ||
               _weapon.CurrentAmmo != 0) return;

            _weapon.PlayDryFireSoundInternal();
            _weapon.AutoReloadArmed = true;
        }

        public void PerformShot() {
            var playerController = _weapon.PlayerController;
            if(playerController == null || _weapon.CurrentWeaponData == null) return;

            var fpCameraTransform = playerController.FpCameraTransform;
            if(fpCameraTransform == null) return;

            _weapon.LastFireTime = Time.time;
            _weapon.CurrentAmmo = Mathf.Max(0, _weapon.CurrentAmmo - 1);
            _weapon.PublishOwnerAmmoToHudInternal();

            var authoritativeAmmoBeforeShot = Mathf.Clamp(_weapon.CurrentAmmo + 1, 0, _weapon.GetCurrentMagCapacityInternal());

            _weapon.PlayLocalMuzzleFlashInternal(authoritativeAmmoBeforeShot);

            var startPosition = fpCameraTransform.position;
            var baseDirection = fpCameraTransform.forward;
            var weaponIndex = _weapon.Manager != null ? _weapon.Manager.CurrentWeaponIndex : -1;
            var shotId = _weapon.Manager != null
                ? _weapon.Manager.AmmoAuthorityRef.GetLastShotId(
                    _weapon.Manager.CurrentWeaponIndex,
                    _weapon.Manager.GetWeaponDataByIndex,
                    _weapon.Manager.ResolveWeaponCapacity) + 1UL
                : 0UL;
            if(_weapon.Manager != null) {
                _weapon.Manager.ReportShotFired(_weapon.Manager.CurrentWeaponIndex, shotId, Time.time);
            }
            var localMuzzlePosition = _weapon.HasLocalMuzzleFlashSpawnPositionForShot
                ? _weapon.LocalMuzzleFlashSpawnPositionForShot
                : startPosition;
            var shooterVelocityAtShot = playerController.GetFullVelocity;

            var pelletCount = 1;
            if(_weapon.CurrentWeaponData.usePelletSpread) {
                pelletCount = Mathf.Max(1, _weapon.CurrentWeaponData.pelletCount);
            }

            var spreadDegrees = _weapon.CurrentWeaponData.bulletSpread;
            var shouldApplySpread = spreadDegrees > 0f;

            if(_weapon.CurrentWeaponData.useSniperOverlay &&
               playerController.PlayerInput != null &&
               playerController.PlayerInput.IsSniperOverlayActive) {
                shouldApplySpread = false;
            }

            for(var pelletIndex = 0; pelletIndex < pelletCount; pelletIndex++) {
                var shotDirection = baseDirection;
                if(shouldApplySpread) {
                    shotDirection = ApplySpread(baseDirection, spreadDegrees);
                }

                FirePellet(startPosition, localMuzzlePosition, shotDirection, shooterVelocityAtShot, weaponIndex, shotId);
            }

            _weapon.AutoReloadArmed = _weapon.CurrentAmmo == 0;
            _weapon.HasLocalMuzzleFlashSpawnPositionForShot = false;
        }

        public void UpdateLocalDamageMultiplier() {
            if(!_weapon.IsOwner) return;
            if(!_weapon.CurrentWeaponData) return;

            var isDead = _weapon.PlayerController != null && _weapon.PlayerController.IsDead;
            var currentSpeed = _weapon.PlayerController != null ? _weapon.PlayerController.GetFullVelocity.magnitude : 0f;
            var peakMultiplier = _weapon.PeakDamageMultiplier;
            var lastPeakTime = _weapon.LastPeakTime;
            _weapon.CurrentDamageMultiplierValue = AdvanceDamageMultiplier(
                _weapon.CurrentDamageMultiplierValue,
                ref peakMultiplier,
                ref lastPeakTime,
                currentSpeed,
                isDead);
            _weapon.PeakDamageMultiplier = peakMultiplier;
            _weapon.LastPeakTime = lastPeakTime;
        }

        public void UpdateAuthoritativeDamageMultiplier() {
            if(!Network.Core.NetworkAuthority.HasGlobalAuthority(_weapon)) return;
            if(!_weapon.CurrentWeaponData) return;

            var isDead = _weapon.PlayerController != null && _weapon.PlayerController.IsDead;
            var observedSpeed = SampleAuthorityObservedSpeed();
            var authoritativePeakMultiplier = _weapon.AuthoritativePeakDamageMultiplier;
            var authoritativeLastPeakTime = _weapon.AuthoritativeLastPeakTime;
            _weapon.AuthoritativeDamageMultiplier = AdvanceDamageMultiplier(
                _weapon.AuthoritativeDamageMultiplier,
                ref authoritativePeakMultiplier,
                ref authoritativeLastPeakTime,
                observedSpeed,
                isDead);
            _weapon.AuthoritativePeakDamageMultiplier = authoritativePeakMultiplier;
            _weapon.AuthoritativeLastPeakTime = authoritativeLastPeakTime;

            if(_weapon.PlayerController != null && _weapon.PlayerController.PlayerState != null) {
                _weapon.PlayerController.PlayerState.replicatedDamageMultiplier.Value =
                    _weapon.GetAuthoritativeDamageMultiplier();
            }
        }

        public void ResetAuthorityObservedMotionBaseline() {
            var sampleTransform = _weapon.PlayerController != null ? _weapon.PlayerController.PlayerTransform : _weapon.transform;
            _weapon.LastAuthorityObservedPosition = sampleTransform != null ? sampleTransform.position : _weapon.transform.position;
            _weapon.LastAuthorityObservedTime = Time.time;
            _weapon.HasAuthorityObservedPosition = false;
        }

        private bool IsFriendlyFire(NetworkObject target) {
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null) return false;

            var isTeamBased = MatchSettingsManager.IsTeamBasedMode(matchSettings.selectedGameModeId);
            if(!isTeamBased) return false;

            var playerController = _weapon.PlayerController;
            if(playerController == null) return false;
            var shooterTeamMgr = playerController.TeamManager;
            if(shooterTeamMgr == null) return false;

            var targetController = target.GetComponent<PlayerController>();
            if(targetController == null) {
                targetController = target.GetComponentInParent<PlayerController>();
            }

            if(targetController == null) return false;
            var targetTeamMgr = targetController.TeamManager;
            if(targetTeamMgr == null) return false;

            return shooterTeamMgr.netTeam.Value == targetTeamMgr.netTeam.Value;
        }

        private void FirePellet(Vector3 origin, Vector3 tracerStartPosition, Vector3 direction, Vector3 shooterVelocityAtShot,
            int weaponIndex, ulong shotId) {
            var hitLayer = _weapon.WorldLayerMask | _weapon.EnemyLayerMask;
            var maxDist = 600f;

            var endPoint = origin + direction * maxDist;
            var hitNormal = direction;
            var madeImpact = false;
            var hitPlayer = false;
            NetworkObjectReference hitPlayerRef = default;

            var shotHit = false;
            RaycastHit hit = default;
            var useHybridSystem = _weapon.CurrentWeaponData != null && _weapon.CurrentWeaponData.useSphereCast
                                  || _weapon.CurrentWeaponData != null && _weapon.CurrentWeaponData.useSniperOverlay &&
                                  _weapon.PlayerController != null && _weapon.PlayerController.PlayerInput != null &&
                                  _weapon.PlayerController.PlayerInput.IsSniperOverlayActive;

            if(useHybridSystem) {
                var hasWorldHit = Physics.Raycast(origin, direction, out var worldHit, maxDist, _weapon.WorldLayerMask);

                if(hasWorldHit) {
                    maxDist = worldHit.distance;
                }

                var maxRadius = _weapon.CurrentWeaponData.sphereCastMaxRadius;
                var baseRadius = _weapon.CurrentWeaponData.sphereCastRadius;
                var growthStart = _weapon.CurrentWeaponData.sphereCastGrowthStartDist;
                var growthEnd = _weapon.CurrentWeaponData.useDamageFalloff
                    ? Mathf.Max(growthStart + 0.1f, _weapon.CurrentWeaponData.minDamageRange)
                    : Mathf.Max(growthStart + 0.1f, maxDist);

                if(Physics.SphereCast(origin, maxRadius, direction, out var sphereHit, maxDist, _weapon.EnemyLayerMask)) {
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
                if(_weapon.PlayerController != null && _weapon.PlayerController.IsOwner) {
                    var debugPoint = shotHit ? hit.point : Vector3.zero;
                    DrawHitRegistrationDebug(origin, direction, maxDist, debugPoint, shotHit, baseRadius, maxRadius,
                        growthStart, growthEnd);
                }
#endif
            } else {
                shotHit = Physics.Raycast(origin, direction, out hit, maxDist, hitLayer);
            }

            if(shotHit) {
                endPoint = hit.point;
                hitNormal = hit.normal;
                madeImpact = true;

                var hitPlayerController = hit.collider.GetComponentInParent<PlayerController>();
                hitPlayer = hitPlayerController != null;
                if(hitPlayer && hitPlayerController.NetworkObject != null) {
                    hitPlayerRef = new NetworkObjectReference(hitPlayerController.NetworkObject);
                }

                ApplyDamageToHit(hit, origin, weaponIndex, shotId);
            }

            if(_weapon.PlayerController != null && _weapon.PlayerController.IsOwner) {
                _weapon.StartCoroutine(_weapon.SpawnOwnerTracerLocalAfterViewUpdateInternal(
                    tracerStartPosition,
                    endPoint,
                    hitNormal,
                    madeImpact,
                    hitPlayer,
                    hitPlayerRef,
                    shooterVelocityAtShot));
            } else {
                _weapon.SpawnTracerLocalInternal(
                    tracerStartPosition,
                    endPoint,
                    hitNormal,
                    madeImpact,
                    hitPlayer,
                    hitPlayerRef,
                    shooterVelocityAtShot);
            }

            if(_weapon.FxRelay != null && _weapon.PlayerController != null && _weapon.PlayerController.IsOwner) {
                _weapon.FxRelay.RequestShotFx(
                    endPoint,
                    hitNormal,
                    madeImpact,
                    hitPlayer,
                    hitPlayerRef,
                    true,
                    shooterVelocityAtShot);
            }
        }

        private void ApplyDamageToHit(RaycastHit hit, Vector3 origin, int weaponIndex, ulong shotId) {
            var shooterPosition = _weapon.PlayerController != null ? _weapon.PlayerController.transform.position : origin;
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
            if(IsFriendlyFire(target)) return;

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
                    shotId);
            } else {
                Debug.LogError(
                    "[Weapon] MatchCombatAuthority is missing in the active gameplay scene. Damage requests cannot be processed.");
            }
        }

        private Vector3 ApplySpread(Vector3 forward, float spreadDegrees) {
            var fpCameraTransform = _weapon.PlayerController != null ? _weapon.PlayerController.FpCameraTransform : null;
            if(fpCameraTransform == null || spreadDegrees <= 0f) {
                return forward;
            }

            var spreadRad = spreadDegrees * Mathf.Deg2Rad;
            var randomOffset = Random.insideUnitCircle;
            var spreadAmount = Mathf.Tan(spreadRad * 0.5f);
            var offset = (fpCameraTransform.right * randomOffset.x + fpCameraTransform.up * randomOffset.y) *
                         spreadAmount;
            return (forward + offset).normalized;
        }

        private float SampleAuthorityObservedSpeed() {
            var sampleTransform = _weapon.PlayerController != null ? _weapon.PlayerController.PlayerTransform : _weapon.transform;
            var currentPosition = sampleTransform != null ? sampleTransform.position : _weapon.transform.position;
            var now = Time.time;

            if(!_weapon.HasAuthorityObservedPosition) {
                _weapon.LastAuthorityObservedPosition = currentPosition;
                _weapon.LastAuthorityObservedTime = now;
                _weapon.HasAuthorityObservedPosition = true;
                return 0f;
            }

            var dt = Mathf.Max(0.0001f, now - _weapon.LastAuthorityObservedTime);
            var distance = Vector3.Distance(currentPosition, _weapon.LastAuthorityObservedPosition);
            _weapon.LastAuthorityObservedPosition = currentPosition;
            _weapon.LastAuthorityObservedTime = now;

            if(distance > 25f) {
                return 0f;
            }

            return distance / dt;
        }

        private static float AdvanceDamageMultiplier(float currentMultiplier, ref float peakMultiplier, ref float lastPeakTime,
            float currentSpeed, bool isDead) {
            if(isDead) {
                currentMultiplier = Mathf.MoveTowards(currentMultiplier, 1f, Weapon.MultiplierDecayRate * Time.deltaTime);
                peakMultiplier = currentMultiplier;
                lastPeakTime = 0f;
                return Mathf.Clamp(currentMultiplier, 1f, Weapon.MaxDamageMultiplier);
            }

            var targetMultiplier = CalculateTargetDamageMultiplier(currentSpeed);

            if(targetMultiplier >= currentMultiplier) {
                currentMultiplier = Mathf.Lerp(currentMultiplier, targetMultiplier, Weapon.MultiplierGainRate * Time.deltaTime);
                peakMultiplier = currentMultiplier;
                lastPeakTime = Time.time;
            } else if(Time.time - lastPeakTime < Weapon.MultiplierGracePeriod) {
                currentMultiplier = peakMultiplier;
            } else {
                currentMultiplier = Mathf.MoveTowards(currentMultiplier, targetMultiplier, Weapon.MultiplierDecayRate * Time.deltaTime);
                peakMultiplier = currentMultiplier;
            }

            return Mathf.Clamp(currentMultiplier, 1f, Weapon.MaxDamageMultiplier);
        }

        private static float CalculateTargetDamageMultiplier(float currentSpeed) {
            if(currentSpeed < Weapon.MinSpeedThreshold) {
                return 1f;
            }

            var scaleFactor = Mathf.InverseLerp(Weapon.MinSpeedThreshold, Weapon.MaxSpeedThreshold, currentSpeed);
            return Mathf.Lerp(1f, Weapon.MaxDamageMultiplier, scaleFactor);
        }

#if UNITY_EDITOR
        private static void DrawHitRegistrationDebug(Vector3 origin, Vector3 direction, float maxDist, Vector3 hitPoint,
            bool hitSomething, float baseRadius, float maxRadius, float startDist, float endDist) {
            const float duration = 5.0f;
            Debug.DrawLine(origin, origin + direction * maxDist, Color.red, duration);

            const int steps = 50;
            for(var i = 0; i <= steps; i++) {
                var t = (float)i / steps;
                var currentDist = Mathf.Lerp(0, maxDist, t);
                if(currentDist > maxDist) break;

                float currentRadius;
                if(currentDist <= startDist) currentRadius = baseRadius;
                else if(currentDist >= endDist) currentRadius = maxRadius;
                else currentRadius = Mathf.Lerp(baseRadius, maxRadius, Mathf.InverseLerp(startDist, endDist, currentDist));

                var center = origin + direction * currentDist;
                var up = Vector3.up * currentRadius;
                var right = Vector3.right * currentRadius;
                Debug.DrawLine(center - up, center + up, Color.yellow, duration);
                Debug.DrawLine(center - right, center + right, Color.yellow, duration);
            }

            if(!hitSomething) return;
            Debug.DrawLine(hitPoint, hitPoint + Vector3.up * 0.2f, Color.green, duration);
            Debug.DrawLine(hitPoint - Vector3.up * 0.1f, hitPoint + Vector3.up * 0.1f, Color.green, duration);
            Debug.DrawLine(hitPoint - Vector3.right * 0.1f, hitPoint + Vector3.right * 0.1f, Color.green, duration);
            Debug.DrawLine(hitPoint - Vector3.forward * 0.1f, hitPoint + Vector3.forward * 0.1f, Color.green, duration);
        }
#endif
    }
}

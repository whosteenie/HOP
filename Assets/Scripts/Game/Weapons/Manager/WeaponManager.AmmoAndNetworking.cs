using System;
using System.Collections.Generic;
using Game.Match;
using Game.Player;
using Game.UI;
using Network.AntiCheat;
using Network.Events;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapons {
    public partial class WeaponManager {
        private const float DefaultCombatRewindWindowSeconds = 0.2f;
        private const float DefaultCombatRewindFutureToleranceSeconds = 0.03f;

        public enum AmmoSyncReason : byte {
            Reload = 0,
            RefillCurrentWeapon = 1
        }

        public void RefreshOwnerAmmoHudFromCurrentWeapon() {
            if(!IsOwner) return;
            if(CurrentWeapon == null) return;

            var currentAmmo = Mathf.Max(0, CurrentWeapon.currentAmmo);
            var magSize = Mathf.Max(1, CurrentWeapon.GetMagSize());
            EventBus.Publish(new UpdateAmmoEvent(currentAmmo, magSize));
        }

        public void ResetAllWeaponAmmo() {
            if(!IsServer) {
                ResetAllWeaponAmmoServerRpc();
            }

            _ammoAuthority.ResetAllWeaponAmmo(weaponDataList, ResolveWeaponCapacity);
        }

        public void PrepareCurrentWeaponForPostMatchPodium() {
            if(CurrentWeapon == null) return;
            if(CurrentWeaponIndex < 0) return;

            CurrentWeapon.PrepareForPostMatchPodium();
            _ammoAuthority.SetLocalAmmo(CurrentWeaponIndex, CurrentWeapon.currentAmmo);
        }

        /// <summary>
        /// Drains ammo for the currently equipped weapon for this player.
        /// Server-authoritative: updates server validation ammo and syncs owner's FP/HUD state.
        /// </summary>
        public void DrainCurrentWeaponAmmoForTag() {
            if(!IsServer) return;
            if(CurrentWeaponIndex < 0 || CurrentWeaponIndex >= weaponDataList.Count) return;

            var data = weaponDataList[CurrentWeaponIndex];
            if(data == null) return;
            var magCapacity = ResolveWeaponCapacity(data);
            if(magCapacity <= 0) {
                Debug.LogError(
                    $"[WeaponManager][KIN-Strict] Invalid KIN ammo capacity while draining ammo for '{data.weaponName}'.");
                return;
            }

            _ammoAuthority.SetLocalAmmo(CurrentWeaponIndex, 0);
            UpdateServerAmmo(CurrentWeaponIndex, 0);
            ApplyDrainedAmmoOwnerClientRpc(CurrentWeaponIndex, 0, magCapacity);
        }

        [Rpc(SendTo.Owner)]
        private void ApplyDrainedAmmoOwnerClientRpc(int weaponIndex, int ammo, int magSize) {
            _ammoAuthority.SetLocalAmmo(weaponIndex, Mathf.Max(0, ammo));

            if(CurrentWeapon != null && CurrentWeaponIndex == weaponIndex) {
                CurrentWeapon.currentAmmo = Mathf.Max(0, ammo);
            }

            if(IsOwner && HUDManager.Instance != null && CurrentWeaponIndex == weaponIndex) {
                EventBus.Publish(new UpdateAmmoEvent(Mathf.Max(0, ammo), Mathf.Max(0, magSize)));
            }
        }

        private bool TryConsumeWeaponSwitchQuota() {
            var config = AntiCheatConfig.Instance;
            if(config == null) return true;
            if(RpcRateLimiter.TryConsume(OwnerClientId, RpcRateLimiter.Keys.WeaponSwitch, config.weaponSwitchLimit,
                    config.rpcWindowSeconds)) {
                return true;
            }

            AntiCheatLogger.LogRateLimit(OwnerClientId, RpcRateLimiter.Keys.WeaponSwitch);
            return false;
        }

        public bool ValidateServerShot(int weaponIndex, ulong shotId, int pelletIndex, out string reason) {
            reason = null;
            if(!IsServer) return true;

            var config = AntiCheatConfig.Instance;
            return _ammoAuthority.ValidateServerShot(
                weaponIndex,
                shotId,
                pelletIndex,
                Time.time,
                config != null ? config.fireRateGraceSeconds : 0f,
                GetWeaponDataByIndex,
                ResolveWeaponCapacity,
                out reason
            );
        }

        public bool TryComputeServerDamage(int weaponIndex, Vector3 shotOrigin, Vector3 hitPoint, out float damage,
            out string reason) {
            damage = 0f;
            reason = null;

            var data = GetWeaponDataByIndex(weaponIndex);
            if(data == null) {
                reason = "unknown weapon";
                return false;
            }

            if(CurrentWeaponIndex != weaponIndex) {
                reason = "weapon index mismatch";
                return false;
            }

            if(playerController == null) {
                reason = "shooter controller missing";
                return false;
            }

            var distance = Vector3.Distance(shotOrigin, hitPoint);

            var baseDamage = data.baseDamage;
            if(data.useDamageFalloff) {
                var startRange = Mathf.Max(0f, data.maxDamageRange);
                var endRange = Mathf.Max(startRange, data.minDamageRange);
                var minDamage = Mathf.Clamp(data.minDamage, 0f, baseDamage);

                if(distance >= endRange) {
                    baseDamage = minDamage;
                } else if(distance > startRange) {
                    var t = Mathf.InverseLerp(startRange, endRange, distance);
                    baseDamage = Mathf.Lerp(baseDamage, minDamage, t);
                }
            }

            if(data.usePelletSpread) {
                baseDamage *= Mathf.Max(0f, data.pelletDamageMultiplier);
            }

            var multiplier = 1f;
            if(CurrentWeapon != null && CurrentWeaponIndex == weaponIndex) {
                multiplier = Mathf.Clamp(CurrentWeapon.netCurrentDamageMultiplier.Value, 1f, Weapon.MaxDamageMultiplier);
            }

            damage = Mathf.Min(baseDamage * multiplier, data.damageCap);
            return damage > 0f;
        }

        public bool TryVerifyServerHit(int weaponIndex, ulong shotId, int pelletIndex, bool precisionAim,
            float claimedShotServerTime, out PlayerController victim, out Vector3 verifiedHitPoint,
            out Vector3 verifiedHitNormal, out string bodyPartTag, out bool isHeadshot, out Vector3 verifiedShotOrigin,
            out string reason) {
            victim = null;
            verifiedHitPoint = default;
            verifiedHitNormal = default;
            bodyPartTag = null;
            isHeadshot = false;
            verifiedShotOrigin = default;
            reason = null;

            var data = GetWeaponDataByIndex(weaponIndex);
            if(data == null) {
                reason = "unknown weapon";
                return false;
            }

            if(playerController == null) {
                reason = "shooter controller missing";
                return false;
            }

            var rewindServerTime = ResolveCombatRewindServerTime(claimedShotServerTime);
            if(!playerController.TryGetHistoricalShotBasis(rewindServerTime, out var shotBasis) || shotBasis == null) {
                reason = "missing shot basis";
                return false;
            }

            verifiedShotOrigin = shotBasis.Origin;
            var usePrecisionAim = precisionAim && data.useSniperOverlay;
            var spreadDegrees = usePrecisionAim ? 0f : data.bulletSpread;
            var direction = ComputeDeterministicShotDirection(shotBasis.AimRotation, spreadDegrees, shotId, pelletIndex);
            if(direction.sqrMagnitude <= 0.0001f) {
                reason = "invalid reconstructed direction";
                return false;
            }

            const float maxVerificationDistance = 1000f;
            var worldMask = playerController.WorldLayer;
            var playerMask = playerController.PlayerLayer | playerController.EnemyLayer;

            var hasWorldHit = Physics.Raycast(verifiedShotOrigin, direction, out var worldHit, maxVerificationDistance, worldMask,
                QueryTriggerInteraction.Ignore);
            var maxDist = hasWorldHit ? worldHit.distance : maxVerificationDistance;
            var useHybridSystem = data.useSphereCast || usePrecisionAim;

            var rewoundPlayers = BeginCombatRewind(rewindServerTime);
            try {
                if(useHybridSystem) {
                    if(!TryGetFirstVerifiedSphereHit(verifiedShotOrigin, direction, maxDist, playerMask, data, out var playerHit,
                           out victim)) {
                        reason = hasWorldHit ? "shot blocked by world" : "server sphere verification missed";
                        return false;
                    }

                    PopulateVerifiedHit(playerHit, out verifiedHitPoint, out verifiedHitNormal, out bodyPartTag,
                        out isHeadshot, ref victim);
                    return true;
                }

                if(!TryGetFirstVerifiedRayHit(verifiedShotOrigin, direction, maxDist, playerMask, out var strictHit, out victim)) {
                    reason = hasWorldHit ? "shot blocked by world" : "server ray verification missed";
                    return false;
                }

                PopulateVerifiedHit(strictHit, out verifiedHitPoint, out verifiedHitNormal, out bodyPartTag,
                    out isHeadshot, ref victim);
                return true;
            } finally {
                EndCombatRewind(rewoundPlayers);
            }
        }

        public bool IsFriendlyFireServer(PlayerController shooter, PlayerController victim) {
            if(shooter == null || victim == null) return false;

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null || !MatchSettingsManager.IsTeamBasedMode(matchSettings.selectedGameModeId)) {
                return false;
            }

            var shooterTeamManager = shooter.TeamManager;
            var victimTeamManager = victim.TeamManager;
            if(shooterTeamManager == null || victimTeamManager == null) {
                return false;
            }

            return shooterTeamManager.netTeam.Value == victimTeamManager.netTeam.Value;
        }

        public void ReportAmmoSync(int weaponIndex, int newAmmo, AmmoSyncReason reason) {
            if(!IsServer) {
                ReportAmmoSyncServerRpc(weaponIndex, newAmmo, reason);
                return;
            }

            UpdateServerAmmo(weaponIndex, newAmmo, reason);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void ReportAmmoSyncServerRpc(int weaponIndex, int newAmmo, AmmoSyncReason reason,
            RpcParams rpcParams = default) {
            if(rpcParams.Receive.SenderClientId != OwnerClientId) {
                AntiCheatLogger.LogAuthorityViolation("WeaponManager.ReportAmmoSyncServerRpc",
                    rpcParams.Receive.SenderClientId);
                return;
            }

            UpdateServerAmmo(weaponIndex, newAmmo, reason);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void ResetAllWeaponAmmoServerRpc(RpcParams rpcParams = default) {
            if(rpcParams.Receive.SenderClientId != OwnerClientId) {
                AntiCheatLogger.LogAuthorityViolation("WeaponManager.ResetAllWeaponAmmoServerRpc",
                    rpcParams.Receive.SenderClientId);
                return;
            }

            _ammoAuthority.ResetAllWeaponAmmo(weaponDataList, ResolveWeaponCapacity);
        }

        private void UpdateServerAmmo(int weaponIndex, int ammo, AmmoSyncReason reason) {
            if(!IsServer) return;

            switch(reason) {
                case AmmoSyncReason.Reload:
                case AmmoSyncReason.RefillCurrentWeapon:
                    break;
                default:
                    AntiCheatLogger.LogInvalidDamage(OwnerClientId, $"invalid ammo sync reason {reason}");
                    return;
            }

            _ammoAuthority.UpdateServerAmmo(
                weaponIndex,
                ammo,
                GetWeaponDataByIndex,
                ResolveWeaponCapacity
            );
        }

        private void UpdateServerAmmo(int weaponIndex, int ammo) {
            if(!IsServer) return;
            _ammoAuthority.UpdateServerAmmo(
                weaponIndex,
                ammo,
                GetWeaponDataByIndex,
                ResolveWeaponCapacity
            );
        }

        private bool TryGetFirstVerifiedRayHit(Vector3 origin, Vector3 direction, float maxDist, LayerMask playerMask,
            out RaycastHit verifiedHit, out PlayerController victim) {
            verifiedHit = default;
            victim = null;

            var hits = Physics.RaycastAll(origin, direction, maxDist, playerMask, QueryTriggerInteraction.Ignore);
            if(hits == null || hits.Length == 0) {
                return false;
            }

            Array.Sort(hits, static (a, b) => a.distance.CompareTo(b.distance));
            foreach(var hit in hits) {
                var candidate = ResolveHitPlayer(hit);
                if(candidate == null || candidate.OwnerClientId == OwnerClientId || candidate.IsDead) {
                    continue;
                }

                verifiedHit = hit;
                victim = candidate;
                return true;
            }

            return false;
        }

        private bool TryGetFirstVerifiedSphereHit(Vector3 origin, Vector3 direction, float maxDist, LayerMask playerMask,
            WeaponData data, out RaycastHit verifiedHit, out PlayerController victim) {
            verifiedHit = default;
            victim = null;

            var maxRadius = Mathf.Max(0f, data.sphereCastMaxRadius);
            var baseRadius = Mathf.Max(0f, data.sphereCastRadius);
            var growthStart = Mathf.Max(0f, data.sphereCastGrowthStartDist);
            var growthEnd = data.useDamageFalloff
                ? Mathf.Max(growthStart + 0.1f, data.minDamageRange)
                : Mathf.Max(growthStart + 0.1f, maxDist);

            var hits = Physics.SphereCastAll(origin, maxRadius, direction, maxDist, playerMask,
                QueryTriggerInteraction.Ignore);
            if(hits == null || hits.Length == 0) {
                return false;
            }

            Array.Sort(hits, static (a, b) => a.distance.CompareTo(b.distance));
            foreach(var hit in hits) {
                var candidate = ResolveHitPlayer(hit);
                if(candidate == null || candidate.OwnerClientId == OwnerClientId || candidate.IsDead) {
                    continue;
                }

                var dist = hit.distance;
                float allowedRadius;
                if(dist <= growthStart) {
                    allowedRadius = baseRadius;
                } else if(dist >= growthEnd) {
                    allowedRadius = maxRadius;
                } else {
                    var t = Mathf.InverseLerp(growthStart, growthEnd, dist);
                    allowedRadius = Mathf.Lerp(baseRadius, maxRadius, t);
                }

                var hitPoint = hit.point;
                var projectedPoint = origin + direction * Vector3.Dot(hitPoint - origin, direction);
                var distFromRay = Vector3.Distance(hitPoint, projectedPoint);
                if(distFromRay > allowedRadius || dist > maxDist) {
                    continue;
                }

                verifiedHit = hit;
                victim = candidate;
                return true;
            }

            return false;
        }

        private static PlayerController ResolveHitPlayer(RaycastHit hit) {
            if(hit.collider == null) {
                return null;
            }

            var hitRigidbody = hit.collider.attachedRigidbody;
            if(hitRigidbody != null) {
                var rbPlayer = hitRigidbody.GetComponent<PlayerController>();
                if(rbPlayer != null) {
                    return rbPlayer;
                }

                return hitRigidbody.GetComponentInParent<PlayerController>();
            }

            var colliderPlayer = hit.collider.GetComponent<PlayerController>();
            return colliderPlayer != null ? colliderPlayer : hit.collider.GetComponentInParent<PlayerController>();
        }

        private static void PopulateVerifiedHit(RaycastHit hit, out Vector3 hitPoint, out Vector3 hitNormal,
            out string bodyPartTag, out bool isHeadshot, ref PlayerController victim) {
            hitPoint = hit.point;
            hitNormal = hit.normal;
            bodyPartTag = null;
            isHeadshot = false;

            var hitRigidbody = hit.collider != null ? hit.collider.attachedRigidbody : null;
            if(hitRigidbody != null) {
                if(!string.IsNullOrWhiteSpace(hitRigidbody.tag) && !string.Equals(hitRigidbody.tag, "Untagged",
                       StringComparison.Ordinal)) {
                    bodyPartTag = hitRigidbody.tag;
                    isHeadshot = string.Equals(bodyPartTag, "Head", StringComparison.Ordinal);
                }

                var rbVictim = hitRigidbody.GetComponent<PlayerController>();
                if(rbVictim != null) {
                    victim = rbVictim;
                    return;
                }

                var parentVictim = hitRigidbody.GetComponentInParent<PlayerController>();
                if(parentVictim != null) {
                    victim = parentVictim;
                    return;
                }
            }

            if(hit.collider != null && !string.IsNullOrWhiteSpace(hit.collider.tag) &&
               !string.Equals(hit.collider.tag, "Untagged", StringComparison.Ordinal)) {
                bodyPartTag = hit.collider.tag;
                isHeadshot = string.Equals(bodyPartTag, "Head", StringComparison.Ordinal);
            }

            if(victim == null && hit.collider != null) {
                victim = hit.collider.GetComponent<PlayerController>() ?? hit.collider.GetComponentInParent<PlayerController>();
            }
        }

        private float ResolveCombatRewindServerTime(float claimedShotServerTime) {
            var currentServerTime = GetCurrentServerTime();
            if(claimedShotServerTime <= 0f) {
                return currentServerTime;
            }

            var futureTolerance = GetCombatRewindFutureToleranceSeconds();
            if(claimedShotServerTime > currentServerTime + futureTolerance) {
                claimedShotServerTime = currentServerTime;
            }

            var earliestAllowedTime = currentServerTime - GetCombatRewindWindowSeconds();
            return Mathf.Clamp(claimedShotServerTime, earliestAllowedTime, currentServerTime);
        }

        private List<RewoundHitboxState> BeginCombatRewind(float rewindServerTime) {
            var rewoundPlayers = new List<RewoundHitboxState>();
            if(!IsServer || NetworkManager == null) {
                return rewoundPlayers;
            }

            foreach(var connectedClient in NetworkManager.ConnectedClientsList) {
                var playerObject = connectedClient.PlayerObject;
                if(playerObject == null) {
                    continue;
                }

                var targetController = playerObject.GetComponent<PlayerController>();
                if(targetController == null || targetController == playerController || targetController.IsDead) {
                    continue;
                }

                var targetRagdoll = targetController.PlayerRagdoll;
                if(targetRagdoll == null || targetRagdoll.IsRagdoll) {
                    continue;
                }

                var restorePose = targetRagdoll.CaptureCurrentHitboxPose();
                if(restorePose == null) {
                    continue;
                }

                if(!targetRagdoll.TryGetHistoricalHitboxPose(rewindServerTime, out var rewindPose) || rewindPose == null) {
                    continue;
                }

                targetRagdoll.ApplyHitboxPose(rewindPose);
                rewoundPlayers.Add(new RewoundHitboxState(targetRagdoll, restorePose));
            }

            if(rewoundPlayers.Count > 0) {
                Physics.SyncTransforms();
            }

            return rewoundPlayers;
        }

        private static void EndCombatRewind(List<RewoundHitboxState> rewoundPlayers) {
            if(rewoundPlayers == null || rewoundPlayers.Count == 0) {
                return;
            }

            foreach(var rewoundPlayer in rewoundPlayers) {
                if(rewoundPlayer.Ragdoll == null || rewoundPlayer.RestorePose == null) {
                    continue;
                }

                rewoundPlayer.Ragdoll.ApplyHitboxPose(rewoundPlayer.RestorePose);
            }

            Physics.SyncTransforms();
        }

        private float GetCurrentServerTime() {
            return NetworkManager != null ? NetworkManager.ServerTime.TimeAsFloat : Time.time;
        }

        private static float GetCombatRewindWindowSeconds() {
            var config = AntiCheatConfig.Instance;
            if(config != null && config.combatRewindWindowSeconds > 0f) {
                return config.combatRewindWindowSeconds;
            }

            return DefaultCombatRewindWindowSeconds;
        }

        private static float GetCombatRewindFutureToleranceSeconds() {
            var config = AntiCheatConfig.Instance;
            if(config != null && config.combatRewindFutureToleranceSeconds > 0f) {
                return config.combatRewindFutureToleranceSeconds;
            }

            return DefaultCombatRewindFutureToleranceSeconds;
        }

        private readonly struct RewoundHitboxState {
            public RewoundHitboxState(PlayerRagdoll ragdoll, PlayerRagdoll.HitboxPoseSnapshot restorePose) {
                Ragdoll = ragdoll;
                RestorePose = restorePose;
            }

            public PlayerRagdoll Ragdoll { get; }
            public PlayerRagdoll.HitboxPoseSnapshot RestorePose { get; }
        }

        public static Vector3 ComputeDeterministicShotDirection(Quaternion aimRotation, float spreadDegrees,
            ulong shotId, int pelletIndex) {
            var forward = aimRotation * Vector3.forward;
            if(spreadDegrees <= 0f) {
                return forward.normalized;
            }

            var spreadRad = spreadDegrees * Mathf.Deg2Rad;
            var spreadAmount = Mathf.Tan(spreadRad * 0.5f);
            var randomOffset = GetDeterministicSpreadOffset(shotId, pelletIndex);
            var right = aimRotation * Vector3.right;
            var up = aimRotation * Vector3.up;
            var offset = (right * randomOffset.x + up * randomOffset.y) * spreadAmount;
            return (forward + offset).normalized;
        }

        private static Vector2 GetDeterministicSpreadOffset(ulong shotId, int pelletIndex) {
            const float maxRadius = 0.999f;
            var baseHash = (uint)(shotId ^ ((ulong)(pelletIndex + 1) * 0x9E3779B97F4A7C15UL));
            var angleHash = HashSpread(baseHash ^ 0xA511E9B3u);
            var radiusHash = HashSpread(baseHash ^ 0x63D83595u);
            var angle = angleHash * Mathf.PI * 2f;
            var radius = Mathf.Sqrt(radiusHash) * maxRadius;
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
        }

        private static float HashSpread(uint value) {
            value ^= 2747636419u;
            value *= 2654435769u;
            value ^= value >> 16;
            value *= 2654435769u;
            value ^= value >> 16;
            value *= 2654435769u;
            return (value & 0x00FFFFFFu) / 16777215f;
        }
    }
}

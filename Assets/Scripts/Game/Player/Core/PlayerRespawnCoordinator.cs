using System;
using Game.Match;
using UnityEngine;

namespace Game.Player.Core {
    internal sealed class PlayerRespawnCoordinator {
        private readonly Func<bool> _hasGlobalAuthority;
        private readonly Func<ulong> _getOwnerClientId;
        private readonly Func<SpawnPoint.Team> _getCurrentTeam;
        private SpawnPoint _reservedRespawnPoint;

        public PlayerRespawnCoordinator(
            Func<bool> hasGlobalAuthority,
            Func<ulong> getOwnerClientId,
            Func<SpawnPoint.Team> getCurrentTeam) {
            _hasGlobalAuthority = hasGlobalAuthority;
            _getOwnerClientId = getOwnerClientId;
            _getCurrentTeam = getCurrentTeam;
        }

        public void ReserveRespawnPoint() {
            if(_hasGlobalAuthority == null || !_hasGlobalAuthority()) return;
            if(_getOwnerClientId == null || _getCurrentTeam == null) return;

            var ownerClientId = _getOwnerClientId();
            var currentTeam = _getCurrentTeam();

            SpawnPoint reservedPoint = null;
            if(PlayerMatchRules.IsTeamBasedMode) {
                if(SpawnManager.Instance != null) {
                    reservedPoint = SpawnManager.Instance.ReserveSpawnPoint(ownerClientId, currentTeam);
                }
            } else if(SpawnManager.Instance != null) {
                reservedPoint = SpawnManager.Instance.ReserveSpawnPoint(ownerClientId);
            }

            _reservedRespawnPoint = reservedPoint;
        }

        public bool TryGetReservedRespawnPose(out Vector3 position, out Quaternion rotation) {
            if(_reservedRespawnPoint != null) {
                var reservedSpawnTransform = _reservedRespawnPoint.transform;
                position = reservedSpawnTransform.position;
                rotation = reservedSpawnTransform.rotation;
                return true;
            }

            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        public void GetFallbackRespawnPose(out Vector3 position, out Quaternion rotation) {
            var currentTeam = _getCurrentTeam != null ? _getCurrentTeam() : SpawnPoint.Team.None;
            SpawnPoint point = null;
            if(SpawnManager.Instance != null) {
                point = PlayerMatchRules.IsTeamBasedMode
                    ? SpawnManager.Instance.GetNextSpawnForRespawn(currentTeam)
                    : SpawnManager.Instance.GetNextSpawnForRespawn();
            }

            if(point == null) {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return;
            }

            var pointTransform = point.transform;
            position = pointTransform.position;
            rotation = pointTransform.rotation;
        }

        public void ReleaseRespawnReservation() {
            if(_hasGlobalAuthority == null || !_hasGlobalAuthority() || _reservedRespawnPoint == null) return;
            if(_getOwnerClientId == null) return;

            if(SpawnManager.Instance != null) {
                SpawnManager.Instance.ReleaseReservation(_getOwnerClientId());
            }

            _reservedRespawnPoint = null;
        }
    }
}

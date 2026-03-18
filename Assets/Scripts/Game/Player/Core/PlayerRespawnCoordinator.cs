using Game.Match;
using Network.Core;
using UnityEngine;

namespace Game.Player.Core {
    internal sealed class PlayerRespawnCoordinator {
        private readonly PlayerController _player;
        private SpawnPoint _reservedRespawnPoint;

        public PlayerRespawnCoordinator(PlayerController player) {
            _player = player;
        }

        public void ReserveRespawnPoint() {
            if(!NetworkAuthority.HasGlobalAuthority(_player)) return;

            SpawnPoint reservedPoint = null;
            if(PlayerMatchRules.IsTeamBasedMode) {
                if(SpawnManager.Instance != null) {
                    reservedPoint = SpawnManager.Instance.ReserveSpawnPoint(_player.OwnerClientId, _player.CurrentTeam);
                }
            } else if(SpawnManager.Instance != null) {
                reservedPoint = SpawnManager.Instance.ReserveSpawnPoint(_player.OwnerClientId);
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
            SpawnPoint point = null;
            if(SpawnManager.Instance != null) {
                point = PlayerMatchRules.IsTeamBasedMode
                    ? SpawnManager.Instance.GetNextSpawnForRespawn(_player.CurrentTeam)
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
            if(!NetworkAuthority.HasGlobalAuthority(_player) || _reservedRespawnPoint == null) return;

            if(SpawnManager.Instance != null) {
                SpawnManager.Instance.ReleaseReservation(_player.OwnerClientId);
            }

            _reservedRespawnPoint = null;
        }
    }
}

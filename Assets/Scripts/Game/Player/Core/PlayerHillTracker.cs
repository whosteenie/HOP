using System.Collections.Generic;
using Events;
using Game.Match;
using UnityEngine;

namespace Game.Player.Core {
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerController))]
    public class PlayerHillTracker : MonoBehaviour {
        private const float KingTimeProgressionChunkSeconds = 1f;

        private readonly Dictionary<ulong, HillController> _activeHills = new();
        private PlayerController _player;
        private float _localKingProgressionSeconds;

        private void Awake() {
            _player = GetComponent<PlayerController>();
        }

        private void OnEnable() {
            EventBus.Subscribe<PlayerDiedEvent>(OnPlayerDied);
            EventBus.Subscribe<PlayerRespawnedEvent>(OnPlayerRespawned);
            EventBus.Subscribe<PlayerNetworkSpawnedEvent>(OnPlayerNetworkSpawned);
            EventBus.Subscribe<PlayerTeamChangedEvent>(OnPlayerTeamChanged);
            EventBus.Subscribe<HillOccupancySnapshotRequestedEvent>(OnOccupancySnapshotRequested);
        }

        private void OnDisable() {
            EventBus.Unsubscribe<PlayerDiedEvent>(OnPlayerDied);
            EventBus.Unsubscribe<PlayerRespawnedEvent>(OnPlayerRespawned);
            EventBus.Unsubscribe<PlayerNetworkSpawnedEvent>(OnPlayerNetworkSpawned);
            EventBus.Unsubscribe<PlayerTeamChangedEvent>(OnPlayerTeamChanged);
            EventBus.Unsubscribe<HillOccupancySnapshotRequestedEvent>(OnOccupancySnapshotRequested);
            ClearAllOccupancy();
            FlushLocalKingProgression();
        }

        private void Update() {
            PruneInactiveHills();

            if(_player == null || !_player.IsOwner || _player.IsDead || _activeHills.Count == 0) {
                FlushLocalKingProgression();
                return;
            }

            _localKingProgressionSeconds += Time.deltaTime;
            if(_localKingProgressionSeconds < KingTimeProgressionChunkSeconds) return;

            var wholeChunks = Mathf.Floor(_localKingProgressionSeconds / KingTimeProgressionChunkSeconds);
            var awardedSeconds = wholeChunks * KingTimeProgressionChunkSeconds;
            _localKingProgressionSeconds -= awardedSeconds;
            EventBus.Publish(new MatchKingTimeAwardedEvent(_player.OwnerClientId, awardedSeconds));
        }

        private void OnTriggerEnter(Collider other) {
            var hill = ResolveHill(other);
            if(hill == null) return;
            SetOccupancy(hill, true);
        }

        private void OnTriggerExit(Collider other) {
            var hill = ResolveHill(other);
            if(hill == null) return;
            SetOccupancy(hill, false);
        }

        private void OnPlayerDied(PlayerDiedEvent evt) {
            if(_player == null || evt == null || evt.PlayerId != _player.OwnerClientId) return;
            ClearAllOccupancy();
            FlushLocalKingProgression();
        }

        private void OnPlayerRespawned(PlayerRespawnedEvent evt) {
            if(_player == null || evt == null || evt.PlayerId != _player.OwnerClientId) return;
            RefreshOccupancyFromWorld();
        }

        private void OnPlayerNetworkSpawned(PlayerNetworkSpawnedEvent evt) {
            if(_player == null || evt == null || evt.ClientId != _player.OwnerClientId) return;
            RefreshOccupancyFromWorld();
        }

        private void OnOccupancySnapshotRequested(HillOccupancySnapshotRequestedEvent evt) {
            if(_player == null || evt == null) return;
            if(_player.IsSpawned == false || _player.IsDead) {
                ClearOccupancyForHill(evt.HillNetworkObjectId);
                return;
            }

            var hill = FindHillById(evt.HillNetworkObjectId);
            if(hill == null) {
                ClearOccupancyForHill(evt.HillNetworkObjectId);
                return;
            }

            SetOccupancy(hill, hill.ContainsPoint(transform.position));
        }

        private void OnPlayerTeamChanged(PlayerTeamChangedEvent evt) {
            if(_player == null || evt == null || evt.PlayerClientId != _player.OwnerClientId) return;
            if(_activeHills.Count == 0) return;
            if(_player.IsSpawned == false || _player.IsDead) return;

            foreach(var hillId in _activeHills.Keys) {
                PublishOccupancyChanged(hillId, true, evt.TeamId);
            }
        }

        private void RefreshOccupancyFromWorld() {
            if(_player == null || _player.IsSpawned == false) return;

            var seenHillIds = new HashSet<ulong>();
            var hills = FindObjectsByType<HillController>(FindObjectsSortMode.None);
            foreach(var hill in hills) {
                if(hill == null || hill.IsSpawned == false) continue;

                seenHillIds.Add(hill.NetworkObjectId);
                SetOccupancy(hill, !_player.IsDead && hill.ContainsPoint(transform.position));
            }

            if(_activeHills.Count == 0) return;

            var staleHillIds = ListPool<ulong>.Get();
            foreach(var hillId in _activeHills.Keys) {
                if(!seenHillIds.Contains(hillId)) {
                    staleHillIds.Add(hillId);
                }
            }

            foreach(var hillId in staleHillIds) {
                SetOccupancy(hillId, false, null);
            }

            ListPool<ulong>.Release(staleHillIds);
        }

        private void ClearAllOccupancy() {
            if(_activeHills.Count == 0) return;

            var hillIds = ListPool<ulong>.Get();
            foreach(var hillId in _activeHills.Keys) {
                hillIds.Add(hillId);
            }

            foreach(var hillId in hillIds) {
                SetOccupancy(hillId, false, null);
            }

            ListPool<ulong>.Release(hillIds);
        }

        private void FlushLocalKingProgression() {
            if(_localKingProgressionSeconds <= 0f || _player == null || !_player.IsOwner) {
                _localKingProgressionSeconds = 0f;
                return;
            }

            EventBus.Publish(new MatchKingTimeAwardedEvent(_player.OwnerClientId, _localKingProgressionSeconds));
            _localKingProgressionSeconds = 0f;
        }

        private void PruneInactiveHills() {
            if(_activeHills.Count == 0) return;

            var staleHillIds = ListPool<ulong>.Get();
            foreach(var (hillId, hill) in _activeHills) {
                if(hill == null || !hill.isActiveAndEnabled || hill.IsSpawned == false) {
                    staleHillIds.Add(hillId);
                }
            }

            foreach(var hillId in staleHillIds) {
                SetOccupancy(hillId, false, null);
            }

            ListPool<ulong>.Release(staleHillIds);
        }

        private void SetOccupancy(HillController hill, bool isInsideHill) {
            if(hill == null || hill.IsSpawned == false) return;
            SetOccupancy(hill.NetworkObjectId, isInsideHill, hill);
        }

        private void SetOccupancy(ulong hillNetworkObjectId, bool isInsideHill, HillController hill) {
            if(_player == null || _player.IsSpawned == false) return;

            var alreadyInside = _activeHills.ContainsKey(hillNetworkObjectId);
            if(isInsideHill == alreadyInside) {
                if(isInsideHill && hill != null) {
                    _activeHills[hillNetworkObjectId] = hill;
                }
                return;
            }

            if(isInsideHill) {
                if(hill == null) return;
                _activeHills[hillNetworkObjectId] = hill;
            } else {
                _activeHills.Remove(hillNetworkObjectId);
                if(_activeHills.Count == 0) {
                    FlushLocalKingProgression();
                }
            }

            PublishOccupancyChanged(hillNetworkObjectId, isInsideHill, (int)_player.CurrentTeam);
        }

        private void ClearOccupancyForHill(ulong hillNetworkObjectId) {
            if(_player == null) return;

            _activeHills.Remove(hillNetworkObjectId);
            if(_activeHills.Count == 0) {
                FlushLocalKingProgression();
            }

            PublishOccupancyChanged(hillNetworkObjectId, false, (int)SpawnPoint.Team.None);
        }

        private void PublishOccupancyChanged(ulong hillNetworkObjectId, bool isInsideHill, int teamId) {
            if(_player == null) return;

            EventBus.Publish(new PlayerHillOccupancyChangedEvent(_player.OwnerClientId, hillNetworkObjectId,
                teamId, isInsideHill));
        }

        private static HillController ResolveHill(Component component) {
            if(component == null) return null;

            var hill = component.GetComponent<HillController>();
            return hill != null ? hill : component.GetComponentInParent<HillController>();
        }

        private static HillController FindHillById(ulong hillNetworkObjectId) {
            var hills = FindObjectsByType<HillController>(FindObjectsSortMode.None);
            foreach(var hill in hills) {
                if(hill != null && hill.IsSpawned && hill.NetworkObjectId == hillNetworkObjectId) {
                    return hill;
                }
            }

            return null;
        }

        private static class ListPool<T> {
            private static readonly Stack<List<T>> Pool = new();

            public static List<T> Get() {
                return Pool.Count > 0 ? Pool.Pop() : new List<T>();
            }

            public static void Release(List<T> list) {
                if(list == null) return;
                list.Clear();
                Pool.Push(list);
            }
        }
    }
}

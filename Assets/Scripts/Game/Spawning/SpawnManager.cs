using System.Collections.Generic;
using System.Linq;
using Network.Core;
using Unity.Netcode;
using UnityEngine;

namespace Game.Spawning {
    public class SpawnManager : NetworkBehaviour {
        public static SpawnManager Instance { get; private set; }

        [Header("Team-Based Spawn Points")]
        [SerializeField] private List<SpawnPoint> allTdmPoints = new();

        [Header("FFA Spawn Points")]
        [SerializeField] private List<SpawnPoint> allFfaPoints = new();

        private readonly List<SpawnPoint> _teamAPoints = new();
        private readonly List<SpawnPoint> _teamBPoints = new();
        private readonly List<SpawnPoint> _ffaPoints = new();

        private const float SpawnClearRadius = 1.5f;
        private const int MaxSpawnAttempts = 30;
        private const float ReservationTimeout = 10f; // Release reservation after 10 seconds if not used

        // Spawn point reservations: maps SpawnPoint to the client ID that reserved it
        // Only accessed on server, protected by lock for thread safety
        private readonly Dictionary<SpawnPoint, ulong> _reservations = new();
        private readonly Dictionary<ulong, SpawnPoint> _playerReservations = new(); // Reverse lookup
        private readonly Dictionary<ulong, float> _reservationTimes = new(); // Track when reservation was made
        private readonly List<ulong> _expiredReservationClients = new();
        private readonly object _reservationLock = new();

        // Cached array for spawn point validation (non-allocating overlap check)
        private static readonly Collider[] SpawnClearanceHits = new Collider[10];
        private bool _sessionOwnerCallbacksRegistered;

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            // Cache spawn points by type
            CachePoints();
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            NetworkAuthority.TryConfigureSessionOwnerObject(this);
            RegisterSessionOwnerCallbacks();

            // Hook into client disconnect callback to release reservations
            if(NetworkAuthority.HasGlobalAuthority(this)) {
                NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
            }
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();

            if(NetworkManager.Singleton != null) {
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
            }

            UnregisterSessionOwnerCallbacks();
        }

        private void Update() {
            // Only run on server
            if(!NetworkAuthority.HasGlobalAuthority(this)) return;

            // Clean up expired reservations
            CleanupExpiredReservations();
        }

        private void RegisterSessionOwnerCallbacks() {
            if(_sessionOwnerCallbacksRegistered || NetworkManager == null) return;
            NetworkManager.OnSessionOwnerPromoted += OnSessionOwnerPromoted;
            _sessionOwnerCallbacksRegistered = true;
        }

        private void UnregisterSessionOwnerCallbacks() {
            if(!_sessionOwnerCallbacksRegistered || NetworkManager == null) return;
            NetworkManager.OnSessionOwnerPromoted -= OnSessionOwnerPromoted;
            _sessionOwnerCallbacksRegistered = false;
        }

        private void OnSessionOwnerPromoted(ulong _) {
            if(NetworkManager == null) return;

            NetworkManager.OnClientDisconnectCallback -= OnClientDisconnect;
            if(NetworkAuthority.HasGlobalAuthority(this)) {
                NetworkAuthority.TryConfigureSessionOwnerObject(this);
                NetworkManager.OnClientDisconnectCallback += OnClientDisconnect;
            }
        }

        private void CleanupExpiredReservations() {
            lock(_reservationLock) {
                _expiredReservationClients.Clear();
                var currentTime = Time.time;

                foreach(var kvp in _reservationTimes) {
                    if(currentTime - kvp.Value > ReservationTimeout) {
                        _expiredReservationClients.Add(kvp.Key);
                    }
                }

                foreach(var clientId in _expiredReservationClients) {
                    ReleaseReservationInternal(clientId);
                }
            }
        }

        private void CachePoints() {
            _teamAPoints.Clear();
            _teamBPoints.Clear();
            _ffaPoints.Clear();

            // Cache team-based spawn points
            foreach(var sp in allTdmPoints) {
                if(sp == null) continue;
                if(sp.AssignedTeam == SpawnPoint.Team.TeamA) _teamAPoints.Add(sp);
                else _teamBPoints.Add(sp);
            }

            // Cache FFA spawn points
            foreach(var sp in allFfaPoints) {
                if(sp == null) continue;
                _ffaPoints.Add(sp);
            }
        }

        /// <summary>
        /// Gets the next spawn point for a team. Returns the SpawnPoint so caller can get both position and rotation.
        /// Checks for physical clearance to prevent overlapping spawns.
        /// </summary>
        public SpawnPoint GetNextSpawnPoint(SpawnPoint.Team team) {
            var list = GetSpawnPointList(team);
            return FindClearSpawnPoint(list, $"Team {team}");
        }

        // Optional: expose for editor population
        [ContextMenu("Find All TDM SpawnPoints in Scene")]
        private void FindAllTdmInScene() {
            allTdmPoints = SpawnPoint.Instances.Where(p => p != null).ToList();
            CachePoints();
        }

        [ContextMenu("Find All FFA SpawnPoints in Scene")]
        private void FindAllFfaInScene() {
            allFfaPoints = SpawnPoint.Instances.Where(p => p != null).ToList();
            CachePoints();
        }

        /// <summary>
        /// Gets a random spawn point for FFA mode. Returns the SpawnPoint so caller can get both position and rotation.
        /// Checks for physical clearance to prevent overlapping spawns.
        /// </summary>
        public SpawnPoint GetNextSpawnPoint() {
            return FindClearSpawnPoint(_ffaPoints, "FFA");
        }

        /// <summary>
        /// Reserves a spawn point for a player when they die.
        /// Returns the reserved spawn point, or null if no spawn point is available.
        /// </summary>
        public SpawnPoint ReserveSpawnPoint(ulong clientId, SpawnPoint.Team team) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) return null;

            var list = GetSpawnPointList(team);
            return ReserveSpawnPointFromList(clientId, list, $"Team {team}");
        }

        /// <summary>
        /// Reserves a spawn point for a player in FFA mode.
        /// </summary>
        public SpawnPoint ReserveSpawnPoint(ulong clientId) {
            return !NetworkAuthority.HasGlobalAuthority(this) ? null : ReserveSpawnPointFromList(clientId, _ffaPoints, "FFA");
        }

        /// <summary>
        /// Gets the reserved spawn point for a player, or finds a new one if reservation was lost.
        /// This method is kept for backward compatibility but should not be used for respawning.
        /// Use ReserveSpawnPoint instead when player dies.
        /// </summary>
        public SpawnPoint GetNextSpawnPointForRespawn(SpawnPoint.Team team) {
            // This method is kept for backward compatibility but should not be used for respawning
            // Use ReserveSpawnPoint instead when player dies
            var list = GetSpawnPointList(team);
            if(list == null || list.Count == 0) return null;

            lock(_reservationLock) {
                return FindAvailableSpawnPoint(list, $"Team {team}");
            }
        }

        /// <summary>
        /// Gets the reserved spawn point for a player, or finds a new one if reservation was lost.
        /// This method is kept for backward compatibility but should not be used for respawning.
        /// Use ReserveSpawnPoint instead when player dies.
        /// </summary>
        public SpawnPoint GetNextSpawnPointForRespawn() {
            if(_ffaPoints.Count == 0) return null;

            lock(_reservationLock) {
                return FindAvailableSpawnPoint(_ffaPoints, "FFA");
            }
        }

        /// <summary>
        /// Releases a spawn point reservation for a player.
        /// Should be called when the player actually spawns or disconnects.
        /// </summary>
        public void ReleaseReservation(ulong clientId) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) return;

            lock(_reservationLock) {
                ReleaseReservationInternal(clientId);
            }
        }

        /// <summary>
        /// Releases all reservations for a disconnected client.
        /// Called when a client disconnects.
        /// </summary>
        private void OnClientDisconnect(ulong clientId) {
            ReleaseReservation(clientId);
        }

        #region Helper Methods

        /// <summary>
        /// Gets the appropriate spawn point list for a team, or null if invalid.
        /// </summary>
        private List<SpawnPoint> GetSpawnPointList(SpawnPoint.Team? team) {
            if(team == null) return _ffaPoints;
            return team == SpawnPoint.Team.TeamA ? _teamAPoints : _teamBPoints;
        }

        /// <summary>
        /// Finds a physically clear spawn point from a list (doesn't check reservations).
        /// Uses random selection for true randomness.
        /// </summary>
        private static SpawnPoint FindClearSpawnPoint(List<SpawnPoint> list, string context) {
            if(list == null || list.Count == 0) return null;

            // Create a shuffled copy of indices for random selection
            var indices = new List<int>();
            for(var i = 0; i < list.Count; i++) {
                indices.Add(i);
            }
            
            // Shuffle indices using Fisher-Yates
            var n = indices.Count;
            while(n > 1) {
                n--;
                var k = Random.Range(0, n + 1);
                (indices[k], indices[n]) = (indices[n], indices[k]);
            }

            // Try each spawn point in random order
            foreach(var idx in indices) {
                var point = list[idx];
                if(IsSpawnPointClear(point.transform.position)) {
                    return point;
                }
            }

            // Fallback: return first point even if occupied (better than no spawn)
            Debug.LogWarning(
                $"[SpawnManager] No clear spawn point found for {context} after checking all {list.Count} points. Using fallback.");
            return list[0];
        }

        /// <summary>
        /// Finds an available spawn point (not reserved and physically clear) from a list.
        /// Uses random selection for true randomness.
        /// Must be called within a lock(_reservationLock) block.
        /// </summary>
        private SpawnPoint FindAvailableSpawnPoint(List<SpawnPoint> list, string context) {
            if(list == null || list.Count == 0) return null;

            // Create a shuffled copy of indices for random selection
            var indices = new List<int>();
            for(var i = 0; i < list.Count; i++) {
                indices.Add(i);
            }
            
            // Shuffle indices using Fisher-Yates
            var n = indices.Count;
            while(n > 1) {
                n--;
                var k = Random.Range(0, n + 1);
                (indices[k], indices[n]) = (indices[n], indices[k]);
            }

            // Try each spawn point in random order
            foreach(var idx in indices) {
                var point = list[idx];
                var isReserved = _reservations.ContainsKey(point);
                var isPhysicallyClear = IsSpawnPointClear(point.transform.position);

                if(!isReserved && isPhysicallyClear) {
                    return point;
                }
            }

            // Final fallback: return first point even if occupied
            Debug.LogWarning(
                $"[SpawnManager] No safe spawn point found for {context} after checking all {list.Count} points. Using fallback.");
            return list[0];
        }

        /// <summary>
        /// Reserves a spawn point from a list for a player.
        /// </summary>
        private SpawnPoint ReserveSpawnPointFromList(ulong clientId, List<SpawnPoint> list, string context) {
            if(list == null || list.Count == 0) return null;

            lock(_reservationLock) {
                ReleaseExistingReservation(clientId);

                var point = FindAvailableSpawnPoint(list, context);
                if(point != null) {
                    CreateReservation(point, clientId);
                }

                return point;
            }
        }

        /// <summary>
        /// Releases any existing reservation for a player. Must be called within lock(_reservationLock).
        /// </summary>
        private void ReleaseExistingReservation(ulong clientId) {
            ReleaseReservationInternal(clientId);
        }

        /// <summary>
        /// Releases a reservation without taking a lock.
        /// Caller must already hold lock(_reservationLock).
        /// </summary>
        private void ReleaseReservationInternal(ulong clientId) {
            if(!_playerReservations.TryGetValue(clientId, out var point)) return;
            _reservations.Remove(point);
            _playerReservations.Remove(clientId);
            _reservationTimes.Remove(clientId);
        }

        /// <summary>
        /// Creates a reservation for a spawn point. Must be called within lock(_reservationLock).
        /// </summary>
        private void CreateReservation(SpawnPoint point, ulong clientId) {
            _reservations[point] = clientId;
            _playerReservations[clientId] = point;
            _reservationTimes[clientId] = Time.time;
        }

        /// <summary>
        /// Checks if a spawn point position is physically clear (no overlapping colliders).
        /// </summary>
        private static bool IsSpawnPointClear(Vector3 center) {
            var layerMask = LayerMask.GetMask("Player", "Enemy");
            var hitCount = Physics.OverlapSphereNonAlloc(center, SpawnClearRadius, SpawnClearanceHits, layerMask);
            return hitCount == 0;
        }

        #endregion
    }
}

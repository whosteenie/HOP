using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

namespace Network.Singletons {
    public class SpawnManager : NetworkBehaviour {
        public static SpawnManager Instance { get; private set; }

        private List<SpawnPoint> _spawnPoints;
        private NetworkVariable<int> _netNextSpawnIndex = new();

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
        private readonly object _reservationLock = new object();

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            // Cache spawn points by type
            CachePoints();
            
            // Initialize _spawnPoints from FFA points (used for round-robin in FFA mode)
            _spawnPoints = new List<SpawnPoint>(_ffaPoints);
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Hook into client disconnect callback to release reservations
            if(IsServer) {
                NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
            }
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();

            if(IsServer && NetworkManager.Singleton != null) {
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
            }
        }

        private void Update() {
            // Only run on server
            if(!IsServer) return;

            // Clean up expired reservations
            CleanupExpiredReservations();
        }

        private void CleanupExpiredReservations() {
            lock(_reservationLock) {
                var expiredClients = new List<ulong>();
                var currentTime = Time.time;

                foreach(var kvp in _reservationTimes) {
                    if(currentTime - kvp.Value > ReservationTimeout) {
                        expiredClients.Add(kvp.Key);
                    }
                }

                foreach(var clientId in expiredClients) {
                    ReleaseReservation(clientId);
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
            var list = team == SpawnPoint.Team.TeamA ? _teamAPoints : _teamBPoints;
            if(list.Count == 0) return null;

            // Try to find a clear spawn point (check physical occupancy)
            int attempts = 0;
            int startIdx = Random.Range(0, list.Count);

            while(attempts < MaxSpawnAttempts) {
                var point = list[startIdx];
                
                // Check if spawn point is physically clear
                if(IsSpawnPointClear(point.transform.position)) {
                    return point;
                }

                startIdx = (startIdx + 1) % list.Count;
                attempts++;
            }

            // Fallback: return first point even if occupied (better than no spawn)
            Debug.LogWarning(
                $"[SpawnManager] No clear spawn point found for Team {team} after {MaxSpawnAttempts} attempts. Using fallback.");
            return list[0];
        }

        // Optional: expose for editor population
        [ContextMenu("Find All TDM SpawnPoints in Scene")]
        private void FindAllTdmInScene() {
            allTdmPoints = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None).ToList();
            CachePoints();
        }
        
        [ContextMenu("Find All FFA SpawnPoints in Scene")]
        private void FindAllFfaInScene() {
            allFfaPoints = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None).ToList();
            CachePoints();
        }

        /// <summary>
        /// Gets a random spawn point for FFA mode. Returns the SpawnPoint so caller can get both position and rotation.
        /// Checks for physical clearance to prevent overlapping spawns.
        /// </summary>
        public SpawnPoint GetNextSpawnPoint() {
            // Use FFA spawn points
            if(_ffaPoints.Count == 0) {
                return null;
            }

            // Try to find a clear spawn point (check physical occupancy)
            int attempts = 0;
            int startIdx = Random.Range(0, _ffaPoints.Count);

            while(attempts < MaxSpawnAttempts) {
                var point = _ffaPoints[startIdx];
                
                // Check if spawn point is physically clear
                if(IsSpawnPointClear(point.transform.position)) {
                    return point;
                }

                startIdx = (startIdx + 1) % _ffaPoints.Count;
                attempts++;
            }

            // Fallback: return first point even if occupied (better than no spawn)
            Debug.LogWarning(
                $"[SpawnManager] No clear FFA spawn point found after {MaxSpawnAttempts} attempts. Using fallback.");
            return _ffaPoints[0];
        }

        /// <summary>
        /// Reserves a spawn point for a player when they die.
        /// Returns the reserved spawn point, or null if no spawn point is available.
        /// </summary>
        public SpawnPoint ReserveSpawnPoint(ulong clientId, SpawnPoint.Team team) {
            if(!IsServer) return null;

            var list = team == SpawnPoint.Team.TeamA ? _teamAPoints : _teamBPoints;
            if(list.Count == 0) return null;

            lock(_reservationLock) {
                // Release any existing reservation for this player
                if(_playerReservations.TryGetValue(clientId, out var oldReservation)) {
                    _reservations.Remove(oldReservation);
                    _playerReservations.Remove(clientId);
                    _reservationTimes.Remove(clientId);
                }

                int attempts = 0;
                int startIdx = Random.Range(0, list.Count);

                while(attempts < MaxSpawnAttempts) {
                    var point = list[startIdx];
                    
                    // Check if point is available (not reserved and physically clear)
                    bool isReserved = _reservations.ContainsKey(point);
                    bool isPhysicallyClear = IsSpawnPointClear(point.transform.position);
                    
                    if(!isReserved && isPhysicallyClear) {
                        // Reserve this point
                        _reservations[point] = clientId;
                        _playerReservations[clientId] = point;
                        _reservationTimes[clientId] = Time.time;
                        return point;
                    }

                    startIdx = (startIdx + 1) % list.Count;
                    attempts++;
                }

                // Final fallback: return first point even if occupied/reserved
                Debug.LogWarning(
                    $"[SpawnManager] No safe spawn point found for Team {team} after {MaxSpawnAttempts} attempts. Using fallback.");
                var fallbackPoint = list[0];
                _reservations[fallbackPoint] = clientId;
                _playerReservations[clientId] = fallbackPoint;
                _reservationTimes[clientId] = Time.time;
                return fallbackPoint;
            }
        }

        /// <summary>
        /// Reserves a spawn point for a player in FFA mode.
        /// </summary>
        public SpawnPoint ReserveSpawnPoint(ulong clientId) {
            if(!IsServer) return null;

            if(_ffaPoints.Count == 0) return null;

            lock(_reservationLock) {
                // Release any existing reservation for this player
                if(_playerReservations.TryGetValue(clientId, out var oldReservation)) {
                    _reservations.Remove(oldReservation);
                    _playerReservations.Remove(clientId);
                    _reservationTimes.Remove(clientId);
                }

                int attempts = 0;
                int startIdx = Random.Range(0, _ffaPoints.Count);

                while(attempts < MaxSpawnAttempts) {
                    var point = _ffaPoints[startIdx];
                    
                    // Check if point is available (not reserved and physically clear)
                    bool isReserved = _reservations.ContainsKey(point);
                    bool isPhysicallyClear = IsSpawnPointClear(point.transform.position);
                    
                    if(!isReserved && isPhysicallyClear) {
                        // Reserve this point
                        _reservations[point] = clientId;
                        _playerReservations[clientId] = point;
                        _reservationTimes[clientId] = Time.time;
                        return point;
                    }

                    startIdx = (startIdx + 1) % _ffaPoints.Count;
                    attempts++;
                }

                // Final fallback
                Debug.LogWarning(
                    $"[SpawnManager] No safe spawn point found in FFA after {MaxSpawnAttempts} attempts. Using fallback.");
                var fallbackPoint = _ffaPoints[0];
                _reservations[fallbackPoint] = clientId;
                _playerReservations[clientId] = fallbackPoint;
                _reservationTimes[clientId] = Time.time;
                return fallbackPoint;
            }
        }

        /// <summary>
        /// Gets the reserved spawn point for a player, or finds a new one if reservation was lost.
        /// This method is kept for backward compatibility but should not be used for respawning.
        /// Use ReserveSpawnPoint instead when player dies.
        /// </summary>
        public SpawnPoint GetNextSpawnPointForRespawn(SpawnPoint.Team team) {
            // This method is kept for backward compatibility but should not be used for respawning
            // Use ReserveSpawnPoint instead when player dies
            var list = team == SpawnPoint.Team.TeamA ? _teamAPoints : _teamBPoints;
            if(list.Count == 0) return null;

            int attempts = 0;
            int startIdx = Random.Range(0, list.Count);

            lock(_reservationLock) {
                while(attempts < MaxSpawnAttempts) {
                    var point = list[startIdx];
                    bool isReserved = _reservations.ContainsKey(point);
                    bool isPhysicallyClear = IsSpawnPointClear(point.transform.position);
                    
                    if(!isReserved && isPhysicallyClear)
                        return point;

                    startIdx = (startIdx + 1) % list.Count;
                    attempts++;
                }
            }

            // Final fallback: return first point even if occupied
            Debug.LogWarning(
                $"[SpawnManager] No safe spawn point found for Team {team} after {MaxSpawnAttempts} attempts. Using fallback.");
            return list[0];
        }

        /// <summary>
        /// Gets the reserved spawn point for a player, or finds a new one if reservation was lost.
        /// This method is kept for backward compatibility but should not be used for respawning.
        /// Use ReserveSpawnPoint instead when player dies.
        /// </summary>
        public SpawnPoint GetNextSpawnPointForRespawn() {
            if(_ffaPoints.Count == 0) return null;

            int attempts = 0;
            int startIdx = Random.Range(0, _ffaPoints.Count);

            lock(_reservationLock) {
                while(attempts < MaxSpawnAttempts) {
                    var point = _ffaPoints[startIdx];
                    bool isReserved = _reservations.ContainsKey(point);
                    bool isPhysicallyClear = IsSpawnPointClear(point.transform.position);
                    
                    if(!isReserved && isPhysicallyClear)
                        return point;

                    startIdx = (startIdx + 1) % _ffaPoints.Count;
                    attempts++;
                }
            }

            // Final fallback
            Debug.LogWarning(
                $"[SpawnManager] No safe spawn point found in FFA after {MaxSpawnAttempts} attempts. Using fallback.");
            return _ffaPoints[0];
        }

        /// <summary>
        /// Gets the reserved spawn point for a player, or null if no reservation exists.
        /// </summary>
        public SpawnPoint GetReservedSpawnPoint(ulong clientId) {
            if(!IsServer) return null;

            lock(_reservationLock) {
                if(_playerReservations.TryGetValue(clientId, out var point)) {
                    return point;
                }
            }

            return null;
        }

        /// <summary>
        /// Releases a spawn point reservation for a player.
        /// Should be called when the player actually spawns or disconnects.
        /// </summary>
        public void ReleaseReservation(ulong clientId) {
            if(!IsServer) return;

            lock(_reservationLock) {
                if(_playerReservations.TryGetValue(clientId, out var point)) {
                    _reservations.Remove(point);
                    _playerReservations.Remove(clientId);
                    _reservationTimes.Remove(clientId);
                }
            }
        }

        /// <summary>
        /// Releases all reservations for a disconnected client.
        /// Called when a client disconnects.
        /// </summary>
        public void OnClientDisconnect(ulong clientId) {
            ReleaseReservation(clientId);
        }

        private bool IsSpawnPointClear(Vector3 center) {
            var layerMask = LayerMask.GetMask("Player", "Enemy");
            return Physics.OverlapSphere(center, SpawnClearRadius, layerMask).Length == 0;
        }


        public int GetRandomSpawnIndex() {
            if(_spawnPoints.Count == 0) {
                return 0;
            }

            return Random.Range(0, _spawnPoints.Count);
        }

        public Vector3 GetSpawnPosition(int spawnIndex) {
            return _spawnPoints[spawnIndex].transform.position;
        }

        public Quaternion GetSpawnRotation(int spawnIndex) {
            return _spawnPoints[spawnIndex].transform.rotation;
        }
    }
}
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

namespace Network.Singletons {
    public class SpawnManager : NetworkBehaviour
    {
        public static SpawnManager Instance { get; private set; }

        private List<SpawnPoint> _spawnPoints;
        private NetworkVariable<int> _netNextSpawnIndex = new();
        
        [SerializeField] private List<SpawnPoint> allPoints = new();
        private readonly List<SpawnPoint> _teamAPoints = new();
        private readonly List<SpawnPoint> _teamBPoints = new();
        
        private const float SPAWN_CLEAR_RADIUS = 1.5f;
        private const int MAX_SPAWN_ATTEMPTS = 30;
    
        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }
        
            Instance = this;
        
            // Find all spawn points in the scene
            _spawnPoints = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None).ToList();
            CachePoints();
        }
        
        private void CachePoints()
        {
            _teamAPoints.Clear();
            _teamBPoints.Clear();

            foreach (var sp in allPoints)
            {
                if (sp == null) continue;
                if (sp.AssignedTeam == SpawnPoint.Team.TeamA) _teamAPoints.Add(sp);
                else _teamBPoints.Add(sp);
            }
        }
        
        public Vector3 GetNextSpawnPosition(SpawnPoint.Team team)
        {
            var list = team == SpawnPoint.Team.TeamA ? _teamAPoints : _teamBPoints;
            if (list.Count == 0) return Vector3.zero;

            // Simple round-robin (you can randomise later)
            var idx = Random.Range(0, list.Count);
            return list[idx].transform.position;
        }

        public Quaternion GetNextSpawnRotation(SpawnPoint.Team team)
        {
            var list = team == SpawnPoint.Team.TeamA ? _teamAPoints : _teamBPoints;
            if (list.Count == 0) return Quaternion.identity;

            var idx = Random.Range(0, list.Count);
            return list[idx].transform.rotation;
        }

        // Optional: expose for editor population
        [ContextMenu("Find All SpawnPoints in Scene")]
        private void FindAllInScene()
        {
            allPoints = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None).ToList();
            CachePoints();
        }
    
        public Vector3 GetNextSpawnPosition() {
            if(_spawnPoints.Count == 0) {
                // Fallback to default position
                return new Vector3(-60, 760, -20);
            }
        
            // Round-robin spawn
            var spawnPoint = _spawnPoints[_netNextSpawnIndex.Value];
            _netNextSpawnIndex.Value = (_netNextSpawnIndex.Value + 1) % _spawnPoints.Count;
        
            return spawnPoint.transform.position;
        }
        
        public SpawnPoint GetNextSpawnPoint(SpawnPoint.Team team)
        {
            var list = team == SpawnPoint.Team.TeamA ? _teamAPoints : _teamBPoints;
            if (list.Count == 0) return null;

            int attempts = 0;
            int startIdx = Random.Range(0, list.Count);

            while (attempts < MAX_SPAWN_ATTEMPTS)
            {
                var point = list[startIdx];
                if (IsSpawnPointClear(point.transform.position))
                    return point;

                startIdx = (startIdx + 1) % list.Count;
                attempts++;
            }

            // Final fallback: return first point even if occupied
            Debug.LogWarning($"[SpawnManager] No safe spawn point found for Team {team} after {MAX_SPAWN_ATTEMPTS} attempts. Using fallback.");
            return list[0];
        }

        public SpawnPoint GetNextSpawnPoint()
        {
            var all = _teamAPoints.Concat(_teamBPoints).ToList();
            if (all.Count == 0) return null;

            int attempts = 0;
            int startIdx = Random.Range(0, all.Count);

            while (attempts < MAX_SPAWN_ATTEMPTS)
            {
                var point = all[startIdx];
                if (IsSpawnPointClear(point.transform.position))
                    return point;

                startIdx = (startIdx + 1) % all.Count;
                attempts++;
            }

            // Final fallback
            Debug.LogWarning($"[SpawnManager] No safe spawn point found in FFA after {MAX_SPAWN_ATTEMPTS} attempts. Using fallback.");
            return all[0];
        }
        
        private bool IsSpawnPointClear(Vector3 center)
        {
            var layerMask = LayerMask.GetMask("Player", "Enemy");
            return Physics.OverlapSphere(center, SPAWN_CLEAR_RADIUS, layerMask).Length == 0;
        }
    
        public Quaternion GetNextSpawnRotation() {
            if(_spawnPoints.Count == 0) {
                return Quaternion.identity;
            }
        
            var index = (_netNextSpawnIndex.Value - 1 + _spawnPoints.Count) % _spawnPoints.Count;
            return _spawnPoints[index].transform.rotation;
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

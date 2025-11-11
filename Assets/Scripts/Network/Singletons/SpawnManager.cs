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
    
        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }
        
            Instance = this;
        
            // Find all spawn points in the scene
            _spawnPoints = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None).ToList();
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

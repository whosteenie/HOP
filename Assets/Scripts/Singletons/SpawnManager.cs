using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

public class SpawnManager : NetworkBehaviour
{
    public static SpawnManager Instance { get; private set; }

    private List<SpawnPoint> _spawnPoints;
    private int _nextSpawnIndex = 0;
    
    private void Awake() {
        if(Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }
        
        Instance = this;
        
        // Find all spawn points in the scene
        _spawnPoints = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None).ToList();
        
        if(_spawnPoints.Count == 0) {
            Debug.LogError("No spawn points found! Add SpawnPoint components to empty GameObjects in your scene.");
        } else {
            Debug.Log($"Found {_spawnPoints.Count} spawn points");
        }
    }
    
    public Vector3 GetNextSpawnPosition() {
        if(_spawnPoints.Count == 0) {
            // Fallback to default position
            return new Vector3(-60, 760, -20);
        }
        
        // Round-robin spawn
        var spawnPoint = _spawnPoints[_nextSpawnIndex];
        _nextSpawnIndex = (_nextSpawnIndex + 1) % _spawnPoints.Count;
        
        return spawnPoint.transform.position;
    }
    
    public Quaternion GetNextSpawnRotation() {
        if(_spawnPoints.Count == 0) {
            return Quaternion.identity;
        }
        
        var index = (_nextSpawnIndex - 1 + _spawnPoints.Count) % _spawnPoints.Count;
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

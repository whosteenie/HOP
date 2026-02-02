using System.Collections;
using System.Collections.Generic;
using Game.UI;
using Unity.Netcode;
using UnityEngine;

namespace Game.Match {
    /// <summary>
    /// Manages the King of the Hill game mode.
    /// Handles hill spawning, scoring, and game state.
    /// </summary>
    public class KingOfTheHillManager : NetworkBehaviour {
        public static KingOfTheHillManager Instance { get; private set; }

        [Header("Settings")]
        [SerializeField] private GameObject hillPrefab;
        [SerializeField] private float postPrematchSpawnDelay = 5f;
        [SerializeField] private float scoreInterval = 1f; // Points awarded every X seconds
        [SerializeField] private int pointsPerInterval = 1;
        [SerializeField] private int winScore = 200;
        
        [Header("Spawn Points")]
        [Tooltip("Additional Y offset when spawning to prevent floor clipping")]
        [SerializeField] private float spawnVerticalOffset = 2.0f;
        [Tooltip("If empty, will use Map Center or random NavMesh locations near center")]
        [SerializeField] private List<Transform> hillSpawnPoints = new();

        // Network Variables
        private readonly NetworkVariable<int> _teamAScore = new(value: 0);
        private readonly NetworkVariable<int> _teamBScore = new(value: 0);
        
        // Runtime
        private HillController _currentHill;
        private bool _isGameActive;
        private float _nextScoreTime;
        private Coroutine _spawnCoroutine;

        private void Awake() {
            if (Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            Debug.Log($"[KOTH] OnNetworkSpawn. IsServer: {IsServer}");

            if (IsServer) {
                _teamAScore.Value = 0;
                _teamBScore.Value = 0;
                
                NetworkManager.SceneManager.OnLoadEventCompleted += OnSceneLoaded;
                
                // Also check immediately in case we are spawned IN the game scene
                CheckAndStartGame();
            }

            _teamAScore.OnValueChanged += OnScoreChanged;
            _teamBScore.OnValueChanged += OnScoreChanged;
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            if (IsServer && NetworkManager.Singleton != null && NetworkManager.Singleton.SceneManager != null) {
                NetworkManager.SceneManager.OnLoadEventCompleted -= OnSceneLoaded;
            }
            _teamAScore.OnValueChanged -= OnScoreChanged;
            _teamBScore.OnValueChanged -= OnScoreChanged;
        }

        private void OnSceneLoaded(string sceneName, UnityEngine.SceneManagement.LoadSceneMode loadSceneMode, List<ulong> clientsCompleted, List<ulong> clientsTimedOut) {
            if (!IsServer) return;
            Debug.Log($"[KOTH] OnSceneLoaded: {sceneName}");
            CheckAndStartGame();
        }

        private void CheckAndStartGame() {
            // Stop any existing routine to prevent double-starts
            if (_spawnCoroutine != null) StopCoroutine(_spawnCoroutine);
            _isGameActive = false;
            
            // Only start logic if this is the selected gamemode
            var settings = MatchSettingsManager.Instance;
            Debug.Log($"[KOTH] CheckAndStartGame. Settings: {settings != null}, Mode: {settings?.selectedGameModeId}");

            if (settings != null && settings.selectedGameModeId == "KOTH") {
                // Refresh spawn points for the new scene
                FindSpawnPoints();
                _spawnCoroutine = StartCoroutine(GameStartRoutine());
            }
        }

        private void OnScoreChanged(int previous, int current) {
            // Update UI
            if (ScoreboardManager.Instance != null) {
                ScoreboardManager.Instance.UpdateScoreboard();
            }
        }
        
        public int GetTeamAScore() => _teamAScore.Value;
        public int GetTeamBScore() => _teamBScore.Value;

        private IEnumerator GameStartRoutine() {
            Debug.Log("[KOTH] GameStartRoutine started.");
            // Wait for pre-match countdown
            var settings = MatchSettingsManager.Instance;
            float countdown = settings != null ? settings.GetPreMatchCountdownSeconds() : 5f;
            
            yield return new WaitForSeconds(countdown);
            
            // Post-countdown delay
            yield return new WaitForSeconds(postPrematchSpawnDelay);
            
            SpawnHill();
            _isGameActive = true;
        }

        private void SpawnHill() {
            Debug.Log($"[KOTH] Attempting to Spawn Hill. Prefab: {hillPrefab}, CurrentHill: {_currentHill}");
            if (!IsServer || hillPrefab == null) return;
            // Clean up existing hill if any (e.g. from previous round, though usually scene reload handles this)
            if (_currentHill != null) return;

            // Determine spawn position
            Vector3 spawnPos = Vector3.zero;
            if (hillSpawnPoints != null && hillSpawnPoints.Count > 0) {
                var randomPoint = hillSpawnPoints[Random.Range(0, hillSpawnPoints.Count)];
                spawnPos = randomPoint.position;
            } else {
                // Fallback: search for points again or use default
                FindSpawnPoints();
                if (hillSpawnPoints != null && hillSpawnPoints.Count > 0) {
                     spawnPos = hillSpawnPoints[Random.Range(0, hillSpawnPoints.Count)].position;
                } else {
                    Debug.LogWarning("[KingOfTheHillManager] No spawn points found. Ensure HillSpawnPoint components are in the scene.");
                    spawnPos = new Vector3(0, 10, 0); 
                }
            }
            
            // Apply vertical offset
            spawnPos.y += spawnVerticalOffset;
            
            Debug.Log($"[KOTH] Spawning Hill at {spawnPos}");
            var hillObj = Instantiate(hillPrefab, spawnPos, Quaternion.identity);
            var netObj = hillObj.GetComponent<NetworkObject>();
            if (netObj != null) {
                netObj.Spawn();
            }

            _currentHill = hillObj.GetComponent<HillController>();
        }

        private void Update() {
            if (!IsServer || !_isGameActive) return;
            
            // Scoring Logic
            if (Time.time >= _nextScoreTime) {
                _nextScoreTime = Time.time + scoreInterval;
                ProcessScoring();
            }
        }
        
        private void FindSpawnPoints() {
            hillSpawnPoints.Clear();
            var points = FindObjectsByType<HillSpawnPoint>(FindObjectsSortMode.None);
            foreach (var point in points) {
                hillSpawnPoints.Add(point.transform);
            }
            Debug.Log($"[KOTH] FindSpawnPoints found {hillSpawnPoints.Count} points.");
        }

        private void ProcessScoring() {
            if (_currentHill == null) return;

            var controllingTeam = _currentHill.ControllingTeam;

            if (controllingTeam == null) {
                return; // No points
            }

            if (controllingTeam.Value == Game.Spawning.SpawnPoint.Team.TeamA) {
                _teamAScore.Value += pointsPerInterval;
            } else if (controllingTeam.Value == Game.Spawning.SpawnPoint.Team.TeamB) {
                _teamBScore.Value += pointsPerInterval;
            }

            CheckWinCondition();
        }

        private void CheckWinCondition() {
            if (_teamAScore.Value >= winScore) {
                EndGame(Game.Spawning.SpawnPoint.Team.TeamA);
            } else if (_teamBScore.Value >= winScore) {
                EndGame(Game.Spawning.SpawnPoint.Team.TeamB);
            }
        }

        private void EndGame(Game.Spawning.SpawnPoint.Team winningTeam) {
            _isGameActive = false;
            
            if (PostMatchManager.Instance != null) {
                PostMatchManager.Instance.BeginPostMatchFromScore(winningTeam);
            }
        }
        

    }
}

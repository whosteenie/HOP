using System;
using System.Collections;
using System.Collections.Generic;
using Game.Spawning;
using Network.Diagnostics;
using Network.Events;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

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

        private int EffectiveWinScore =>
            MatchSettingsManager.Instance != null ? MatchSettingsManager.Instance.GetScoreToWin() : winScore;
        private int EffectivePointsPerInterval =>
            MatchSettingsManager.Instance != null
                ? Mathf.Max(1, MatchSettingsManager.Instance.GetKothHillSpeed())
                : Mathf.Max(1, pointsPerInterval);

        [Header("Spawn Points")]
        [Tooltip("Additional Y offset when spawning to prevent floor clipping")] [SerializeField]
        private float spawnVerticalOffset = 2.0f;

        [Tooltip("If empty, will use Map Center or random NavMesh locations near center")] [SerializeField]
        private List<Transform> hillSpawnPoints = new();

        // Network Variables
        private readonly NetworkVariable<int> _teamAScore = new(value: 0);
        private readonly NetworkVariable<int> _teamBScore = new(value: 0);

        // Runtime
        private HillController _currentHill;
        private bool _isGameActive;
        private float _nextScoreTime;
        private Coroutine _spawnCoroutine;
        private bool _queuedMatchStart;
        private bool _matchStartedEventsBound;

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            // This manager is authored in Init.unity and must survive the transition into Game.
            DontDestroyOnLoad(gameObject);
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Keep singleton reference valid across NGO despawn/respawn cycles.
            if(Instance == null) {
                Instance = this;
            }

            if(IsServer) {
                _teamAScore.Value = 0;
                _teamBScore.Value = 0;
                BindMatchStartedEvent();

                NetworkManager.SceneManager.OnLoadEventCompleted += OnSceneLoaded;

                // Also check immediately in case we are spawned IN the game scene
                CheckAndStartGame();
                if(_queuedMatchStart) {
                    HandleMatchStartedServer("QueuedBeforeNetworkSpawn");
                }
            }

            _teamAScore.OnValueChanged += OnScoreChanged;
            _teamBScore.OnValueChanged += OnScoreChanged;

            if(Progression.ProgressionManager.Instance != null) {
                Progression.ProgressionManager.Instance.StartMatch();
            }
        }

        public override void OnNetworkDespawn() {
            UnbindMatchStartedEvent();
            base.OnNetworkDespawn();
            if(IsServer && NetworkManager.Singleton != null && NetworkManager.Singleton.SceneManager != null) {
                NetworkManager.SceneManager.OnLoadEventCompleted -= OnSceneLoaded;
            }

            if(_spawnCoroutine != null) {
                StopCoroutine(_spawnCoroutine);
                _spawnCoroutine = null;
            }

            _isGameActive = false;
            _queuedMatchStart = false;
            CleanupActiveHill();
            _teamAScore.OnValueChanged -= OnScoreChanged;
            _teamBScore.OnValueChanged -= OnScoreChanged;
        }

        public override void OnDestroy() {
            UnbindMatchStartedEvent();
            base.OnDestroy();
            if(Instance == this) {
                Instance = null;
            }
        }

        private void BindMatchStartedEvent() {
            if(!IsServer || _matchStartedEventsBound) return;
            EventBus.Subscribe<MatchStartedEvent>(OnMatchStarted);
            _matchStartedEventsBound = true;
        }

        private void UnbindMatchStartedEvent() {
            if(!_matchStartedEventsBound) return;
            EventBus.Unsubscribe<MatchStartedEvent>(OnMatchStarted);
            _matchStartedEventsBound = false;
        }

        private void OnMatchStarted(MatchStartedEvent _) {
            HandleMatchStartedServer("EventBusMatchStarted");
        }

        private void OnSceneLoaded(string sceneName, UnityEngine.SceneManagement.LoadSceneMode loadSceneMode,
            List<ulong> clientsCompleted, List<ulong> clientsTimedOut) {
            if(!IsServer) return;
            CheckAndStartGame();
        }

        private void HandleMatchStartedServer(string source = "Unknown") {
            var isServerContext = IsServer || (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer);
            if(!isServerContext) return;

            if(!IsSpawned) {
                _queuedMatchStart = true;
                Debug.Log($"[KOTH] Deferring round start from '{source}' until OnNetworkSpawn completes.");
                return;
            }

            _queuedMatchStart = false;
            BeginKothRound();
        }

        private void CheckAndStartGame() {
            // Stop any existing routine to prevent double-starts
            if(_spawnCoroutine != null) {
                StopCoroutine(_spawnCoroutine);
                _spawnCoroutine = null;
            }

            _isGameActive = false;

            // Only start logic if this is the selected gamemode
            var settings = MatchSettingsManager.Instance;

            if(settings == null || settings.selectedGameModeId != "KOTH") {
                if(_currentHill != null) {
                    FlowLog.Emit(FlowEventIds.AnomalyModeMismatch,
                        ("selected", settings != null ? settings.selectedGameModeId : "Unknown"),
                        ("applied", settings != null ? settings.selectedGameModeId : "Unknown"),
                        ("objective", "KOTH"));
                }

                CleanupActiveHill();
                return;
            }

            // Refresh spawn points for the new scene
            FindSpawnPoints();
            if(MatchTimerManager.Instance != null && !MatchTimerManager.Instance.IsPreMatch) {
                BeginKothRound();
            }
        }

        private void BeginKothRound() {
            var settings = MatchSettingsManager.Instance;
            if(settings == null || settings.selectedGameModeId != "KOTH") {
                _isGameActive = false;
                CleanupActiveHill();
                return;
            }

            if(_spawnCoroutine != null) {
                StopCoroutine(_spawnCoroutine);
                _spawnCoroutine = null;
            }

            _isGameActive = false;
            _teamAScore.Value = 0;
            _teamBScore.Value = 0;
            CleanupActiveHill();
            FindSpawnPoints();
            _spawnCoroutine = StartCoroutine(GameStartRoutine());
        }

        private static void OnScoreChanged(int previous, int current) {
            EventBus.Publish(new ScoreboardRefreshRequestedEvent());
        }

        public int GetTeamAScore() => _teamAScore.Value;
        public int GetTeamBScore() => _teamBScore.Value;

        private IEnumerator GameStartRoutine() {
            if(postPrematchSpawnDelay > 0f) {
                yield return new WaitForSeconds(postPrematchSpawnDelay);
            }

            var settings = MatchSettingsManager.Instance;
            if(settings == null || settings.selectedGameModeId != "KOTH") {
                _spawnCoroutine = null;
                yield break;
            }

            SpawnHill();
            _isGameActive = _currentHill != null;
            if(_isGameActive) {
                _nextScoreTime = Time.time + scoreInterval;
            }

            _spawnCoroutine = null;
        }

        private void SpawnHill() {
            Debug.Log($"[KOTH] Attempting to Spawn Hill. Prefab: {hillPrefab}, CurrentHill: {_currentHill}");
            if(!IsServer || hillPrefab == null) return;

            var settings = MatchSettingsManager.Instance;
            if(settings == null || settings.selectedGameModeId != "KOTH") return;

            // Clean up existing hill if any (e.g. from previous round, though usually scene reload handles this)
            if(_currentHill != null) return;

            // Determine spawn position
            Vector3 spawnPos;
            if(hillSpawnPoints is { Count: > 0 }) {
                var randomPoint = hillSpawnPoints[Random.Range(0, hillSpawnPoints.Count)];
                spawnPos = randomPoint.position;
            } else {
                // Fallback: search for points again or use default
                FindSpawnPoints();
                if(hillSpawnPoints is { Count: > 0 }) {
                    spawnPos = hillSpawnPoints[Random.Range(0, hillSpawnPoints.Count)].position;
                } else {
                    Debug.LogWarning(
                        "[KingOfTheHillManager] No spawn points found. Ensure HillSpawnPoint components are in the scene.");
                    spawnPos = new Vector3(0, 10, 0);
                }
            }

            // Apply vertical offset
            spawnPos.y += spawnVerticalOffset;

            Debug.Log($"[KOTH] Spawning Hill at {spawnPos}");
            var hillObj = Instantiate(hillPrefab, spawnPos, Quaternion.identity);
            var netObj = hillObj.GetComponent<NetworkObject>();
            if(netObj != null) {
                netObj.Spawn(true);
            }

            _currentHill = hillObj.GetComponent<HillController>();
            FlowLog.Emit(FlowEventIds.ObjectiveSpawned,
                ("mode", "KOTH"),
                ("objectType", "Hill"),
                ("spawn", spawnPos));
        }

        private void CleanupActiveHill() {
            if(_currentHill == null) return;

            var hillNetworkObject = _currentHill.GetComponent<NetworkObject>();
            if(IsServer && hillNetworkObject != null && hillNetworkObject.IsSpawned) {
                hillNetworkObject.Despawn();
            } else if((hillNetworkObject == null || hillNetworkObject.IsSpawned == false) && _currentHill != null) {
                Destroy(_currentHill.gameObject);
            }

            _currentHill = null;
        }

        private void Update() {
            if(!IsServer || !_isGameActive) return;

            // Scoring Logic
            if(!(Time.time >= _nextScoreTime)) return;
            _nextScoreTime = Time.time + scoreInterval;
            ProcessScoring();
        }

        private void FindSpawnPoints() {
            hillSpawnPoints.Clear();
            foreach(var point in HillSpawnPoint.Instances) {
                if(point == null) continue;
                hillSpawnPoints.Add(point.transform);
            }

            Debug.Log($"[KOTH] FindSpawnPoints found {hillSpawnPoints.Count} points.");
        }

        private void ProcessScoring() {
            if(_currentHill == null) return;

            var controllingTeam = _currentHill.ControllingTeam;

            switch(controllingTeam) {
                case SpawnPoint.Team.None:
                    break;
                case null:
                    return; // No points
                case SpawnPoint.Team.TeamA:
                    _teamAScore.Value += EffectivePointsPerInterval;
                    break;
                case SpawnPoint.Team.TeamB:
                    _teamBScore.Value += EffectivePointsPerInterval;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            CheckWinCondition();
        }

        private void CheckWinCondition() {
            if(EffectiveWinScore <= 0) return; // Infinite score limit.
            if(_teamAScore.Value >= EffectiveWinScore) {
                EndGame(SpawnPoint.Team.TeamA);
            } else if(_teamBScore.Value >= EffectiveWinScore) {
                EndGame(SpawnPoint.Team.TeamB);
            }
        }

        private void EndGame(SpawnPoint.Team winningTeam) {
            _isGameActive = false;

            if(PostMatchManager.Instance != null) {
                PostMatchManager.Instance.BeginPostMatchFromScore(winningTeam);
            }
        }
    }
}

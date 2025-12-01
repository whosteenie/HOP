using System;
using System.Collections.Generic;
using Game.Audio;
using Game.Hopball;
using Game.Match;
using Game.Player;
using Game.Spawning;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Game.UI {
    /// <summary>
    /// Manages the scoreboard UI, including FFA and TDM scoreboards, player rows, and match timer.
    /// </summary>
    public class ScoreboardManager : MonoBehaviour {
        public static ScoreboardManager Instance { get; private set; }

        [Header("Player Icons")]
        [SerializeField] private Sprite[] playerIconSprites; // Order: white, red, orange, yellow, green, blue, purple

        // UI Elements
        private VisualElement _root;
        private VisualElement _scoreboardPanel;
        private VisualElement _playerRows;

        // FFA Scoreboard
        private VisualElement _scoreboardContainer;
        private Label _scoreboardTitle;

        // TDM Scoreboard
        private VisualElement _tdmScoreboardContainer;
        private Label _tdmScoreboardTitle;
        private VisualElement _enemyTeamRows;
        private VisualElement _yourTeamRows;
        private Label _enemyScoreValue;
        private Label _yourScoreValue;

        // Match timer
        private Label _matchTimerLabel;

        // Score display (next to timer)
        private VisualElement _leftScoreContainer;
        private VisualElement _rightScoreContainer;
        private Label _leftScoreValue;
        private Label _rightScoreValue;
        private float _lastScoreUpdateTime;
        private const float ScoreUpdateInterval = 0.1f; // Update every 0.1 seconds

        // Local player controller for score calculations
        private PlayerController _localController;

        // Cached references for performance
        private MatchSettingsManager _cachedMatchSettings;

        // Cache component references per player to avoid repeated GetComponent calls
        private readonly Dictionary<PlayerController, PlayerTagController> _cachedTagControllers = new();
        private readonly Dictionary<PlayerController, PlayerStatsController> _cachedStatsControllers = new();

        // Cache UI element references
        private VisualElement _cachedScoreboardHeader;
        private List<Label> _cachedHeaderLabels;
        private bool _headerLabelsCacheValid;

        // Scoreboard optimization: track previous state to avoid unnecessary rebuilds
        private HashSet<ulong> _previousPlayerIds = new();
        private Dictionary<ulong, int> _previousSortValues = new(); // kills or timeTagged
        private readonly Dictionary<ulong, Label> _cachedVelocityLabels = new(); // clientId -> velocity label
        private readonly Dictionary<ulong, float> _previousVelocityValues = new(); // Track previous velocity to avoid unnecessary updates
        
        // Cache scene name to avoid string allocations
        private string _cachedSceneName;

        public bool IsScoreboardVisible { get; private set; }

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        /// <summary>
        /// Initializes the scoreboard manager with UI element references.
        /// </summary>
        public void Initialize(VisualElement root) {
            _root = root;
            
            // Cache scene name to avoid allocations
            UpdateCachedSceneName();
            
            // Subscribe to scene changes to update cache
            SceneManager.sceneLoaded += OnSceneLoaded;

            // Find UI elements
            _scoreboardPanel = _root.Q<VisualElement>("scoreboard-panel");
            _playerRows = _root.Q<VisualElement>("player-rows");
            _scoreboardContainer = _root.Q<VisualElement>("scoreboard-container");
            _scoreboardTitle = _root.Q<Label>("scoreboard-title");

            // TDM Scoreboard
            _tdmScoreboardContainer = _root.Q<VisualElement>("tdm-scoreboard-container");
            _tdmScoreboardTitle = _root.Q<Label>("tdm-scoreboard-title");
            _enemyTeamRows = _root.Q<VisualElement>("enemy-team-rows");
            _yourTeamRows = _root.Q<VisualElement>("your-team-rows");
            _enemyScoreValue = _root.Q<Label>("enemy-score-value");
            _yourScoreValue = _root.Q<Label>("your-score-value");

            // Match timer
            _matchTimerLabel = _root.Q<Label>("match-timer-label");

            // Score display
            _leftScoreContainer = _root.Q<VisualElement>("left-score-container");
            _rightScoreContainer = _root.Q<VisualElement>("right-score-container");
            _leftScoreValue = _root.Q<Label>("left-score-value");
            _rightScoreValue = _root.Q<Label>("right-score-value");

            // Cache MatchSettingsManager
            _cachedMatchSettings = MatchSettingsManager.Instance;
            _headerLabelsCacheValid = false;

            // Subscribe to network callbacks for cleanup
            if(NetworkManager.Singleton != null) {
                NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
            }
        }

        private void Update() {
            if(_localController == null && _cachedSceneName == "Game") {
                FindLocalController();
            }

            // Update score display periodically
            if(_cachedSceneName != "Game" || !(Time.time - _lastScoreUpdateTime >= ScoreUpdateInterval)) return;
            UpdateScoreDisplay();
            _lastScoreUpdateTime = Time.time;
        }

        private void FindLocalController() {
            var allControllers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
            foreach(var controller in allControllers) {
                if(!controller.IsOwner) continue;
                _localController = controller.GetComponent<PlayerController>();
                break;
            }
        }

        private void OnDisable() {
            // Unsubscribe from network callbacks
            if(NetworkManager.Singleton != null) {
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
            }
            
            // Unsubscribe from scene changes
            SceneManager.sceneLoaded -= OnSceneLoaded;

            // Clear cached dictionaries when match ends
            ClearCachedPlayerData();
        }
        
        private void UpdateCachedSceneName() {
            var activeScene = SceneManager.GetActiveScene();
            if(activeScene.IsValid()) {
                _cachedSceneName = activeScene.name;
            }
        }
        
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            UpdateCachedSceneName();
        }

        /// <summary>
        /// Clears cached player data when players disconnect or match ends.
        /// </summary>
        private void ClearCachedPlayerData() {
            _cachedTagControllers.Clear();
            _cachedStatsControllers.Clear();
            _previousPlayerIds.Clear();
            _previousSortValues.Clear();
            _cachedVelocityLabels.Clear();
            _previousVelocityValues.Clear();
        }

        /// <summary>
        /// Called when a client disconnects. Cleans up cached data for that player.
        /// </summary>
        private void OnClientDisconnected(ulong clientId) {
            // Remove cached entries for disconnected players
            var playersToRemove = new List<PlayerController>();

            foreach(var kvp in _cachedTagControllers) {
                if(kvp.Key == null || !kvp.Key.IsSpawned || kvp.Key.OwnerClientId == clientId) {
                    playersToRemove.Add(kvp.Key);
                }
            }

            foreach(var player in playersToRemove) {
                _cachedTagControllers.Remove(player);
                _cachedStatsControllers.Remove(player);
            }
        }

        public void SetMatchTime(int secondsRemaining) {
            if(_matchTimerLabel == null) return;

            if(secondsRemaining < 0)
                secondsRemaining = 0;

            var minutes = secondsRemaining / 60;
            var seconds = secondsRemaining % 60;

            _matchTimerLabel.text = $"{minutes:00}:{seconds:00}";

            if(minutes == 0 && seconds is <= 5 and >= 1) {
                SoundFXManager.Instance.PlayUISound(SfxKey.TimerTick);
            }
        }

        public void ShowScoreboard() {
            if(_cachedSceneName != "Game") return;

            IsScoreboardVisible = true;
            // Ensure root-container is visible (in case it was hidden)
            var rootContainer = _root.Q<VisualElement>("root-container");
            if(rootContainer != null) {
                rootContainer.style.display = DisplayStyle.Flex;
            }

            // Update scoreboard title with current gamemode
            UpdateScoreboardTitle();

            // Show scoreboard panel
            _scoreboardPanel.style.display = DisplayStyle.Flex;
            _scoreboardPanel.RemoveFromClassList("hidden");
            UpdateScoreboardHeaders();
            UpdateScoreboard();
        }

        /// <summary>
        /// Checks if we're in Gun Tag mode. Always checks fresh to handle build initialization order issues.
        /// </summary>
        private bool IsTagMode() {
            // Refresh MatchSettingsManager cache if needed
            if(_cachedMatchSettings == null) _cachedMatchSettings = MatchSettingsManager.Instance;

            // Always check fresh - don't cache game mode as it may not be set yet during initialization
            return _cachedMatchSettings != null && _cachedMatchSettings.selectedGameModeId == "Gun Tag";
        }
        
        /// <summary>
        /// Checks if we're in a team-based mode. Always checks fresh to handle build initialization order issues.
        /// </summary>
        private bool IsTeamBased() {
            // Refresh MatchSettingsManager cache if needed
            if(_cachedMatchSettings == null) _cachedMatchSettings = MatchSettingsManager.Instance;

            // Always check fresh - don't cache game mode as it may not be set yet during initialization
            return _cachedMatchSettings != null && MatchSettingsManager.IsTeamBasedMode(_cachedMatchSettings.selectedGameModeId);
        }

        /// <summary>
        /// Updates the scoreboard title to show the current gamemode name.
        /// </summary>
        private void UpdateScoreboardTitle() {
            // Refresh MatchSettingsManager cache if needed
            if(_cachedMatchSettings == null) _cachedMatchSettings = MatchSettingsManager.Instance;

            var gamemodeName = "SCOREBOARD"; // Default fallback
            if(_cachedMatchSettings != null && !string.IsNullOrEmpty(_cachedMatchSettings.selectedGameModeId)) {
                gamemodeName = _cachedMatchSettings.selectedGameModeId.ToUpper();
            }

            // Update FFA scoreboard title
            if(_scoreboardTitle != null) {
                _scoreboardTitle.text = gamemodeName;
            }

            // Update TDM scoreboard title
            if(_tdmScoreboardTitle != null) {
                _tdmScoreboardTitle.text = gamemodeName;
            }
        }

        private void UpdateScoreboardHeaders() {
            // Always check fresh - don't cache game mode
            var isTagMode = IsTagMode();

            // Cache header elements and labels
            if(_cachedScoreboardHeader == null || !_headerLabelsCacheValid) {
                _cachedScoreboardHeader = _root.Q<VisualElement>("scoreboard-header");
                if(_cachedScoreboardHeader != null) {
                    _cachedHeaderLabels = _cachedScoreboardHeader.Query<Label>().ToList();
                    _headerLabelsCacheValid = true;
                } else {
                    return;
                }
            }

            if(_cachedHeaderLabels == null) return;

            if(isTagMode) {
                // Tag mode headers: PING, AVATAR, NAME, TT, Tags, Tagged, TTR, AV
                // Order: TT (first/main score), Tags (replaces K), Tagged (replaces D), TTR (replaces KDR)
                // Reuse existing columns: K -> TT, D -> Tags, A -> Tagged, KDR -> TTR
                // Hide HS% and DMG columns
                foreach(var label in _cachedHeaderLabels) {
                    var text = label.text;
                    switch(text) {
                        case "K":
                            label.text = "TT"; // TT is the main score, placed first
                            break;
                        case "D":
                            label.text = "Tags";
                            break;
                        case "A":
                            label.text = "Tagged";
                            break;
                        case "KDR":
                            label.text = "TTR";
                            break;
                        case "HS%":
                        case "DMG":
                            label.style.display = DisplayStyle.None;
                            break;
                    }
                }
            } else {
                // Normal mode headers: PING, AVATAR, NAME, K, D, A, KDR, DMG, HS%, AV
                // Restore all columns
                foreach(var label in _cachedHeaderLabels) {
                    var text = label.text;
                    label.text = text switch {
                        "TT" => "K",
                        "Tags" => "D",
                        "Tagged" => "A",
                        "TTR" => "KDR",
                        _ => label.text
                    };

                    label.style.display = DisplayStyle.Flex;
                }
            }
        }

        public void HideScoreboard() {
            if(_cachedSceneName != "Game") return;

            IsScoreboardVisible = false;
            // Remove inline display style so the hidden class can take effect
            _scoreboardPanel.style.display = StyleKeyword.Null;
            _scoreboardPanel.AddToClassList("hidden");
        }

        public void UpdateScoreboard() {
            var allControllers = GetAllPlayerControllers();

            // Always check fresh - don't cache game mode
            if(IsTeamBased()) {
                UpdateTdmScoreboard(allControllers);
            } else {
                UpdateFfaScoreboard(allControllers);
            }
        }


        private void UpdateFfaScoreboard(IReadOnlyCollection<PlayerController> allControllers) {
            // Null checks for UI elements
            if(_scoreboardContainer == null || _tdmScoreboardContainer == null || _playerRows == null) {
                Debug.LogWarning("[ScoreboardManager] FFA scoreboard UI elements not initialized");
                return;
            }

            // Show FFA scoreboard, hide TDM
            _scoreboardContainer.RemoveFromClassList("hidden");
            _tdmScoreboardContainer.AddToClassList("hidden");

            // Always check fresh - don't cache game mode
            var isTagMode = IsTagMode();

            // Build current player set and sort values
            var currentPlayerIds = new HashSet<ulong>();
            var currentSortValues = new Dictionary<ulong, int>();

            foreach(var player in allControllers) {
                if(player == null || !player.IsSpawned) continue;
                currentPlayerIds.Add(player.OwnerClientId);

                currentSortValues[player.OwnerClientId] = GetPlayerScore(player, isTagMode);
            }

            // Check if we need to rebuild (player list changed or sort order changed)
            var needsRebuild = !currentPlayerIds.SetEquals(_previousPlayerIds);

            if(!needsRebuild) {
                // Check if sort values changed (indicating reordering needed)
                foreach(var kvp in currentSortValues) {
                    if(_previousSortValues.TryGetValue(kvp.Key, out var oldValue) && oldValue == kvp.Value) continue;
                    
                    needsRebuild = true;
                    break;
                }
            }

            if(needsRebuild) {
                // Clear and rebuild scoreboard
                _playerRows.Clear();
                _cachedVelocityLabels.Clear();
                _previousVelocityValues.Clear();

                var sortedPlayers = BuildSortedPlayerList(allControllers, isTagMode);

                foreach(var player in sortedPlayers) {
                    if(player == null || !player.IsSpawned) continue;
                    
                    var row = CreatePlayerRow(player, _playerRows, isTagMode: isTagMode);
                    
                    if(row == null) continue;
                    // Cache velocity label for this player (last stat label in the row)
                    var labels = row.Query<Label>().ToList();
                    
                    if(labels.Count > 0) {
                        _cachedVelocityLabels[player.OwnerClientId] = labels[^1];
                    }
                }

                // Update cached state
                _previousPlayerIds = new HashSet<ulong>(currentPlayerIds);
                _previousSortValues = new Dictionary<ulong, int>(currentSortValues);
            } else {
                // Only update velocity labels for existing rows (only if value changed to avoid flashing)
                foreach(var player in allControllers) {
                    if(player == null || !player.IsSpawned) continue;
                    
                    if(!_cachedVelocityLabels.TryGetValue(player.OwnerClientId, out var velocityLabel)) continue;
                    var statsCtrl = GetCachedStatsController(player);
                    
                    if(statsCtrl == null || velocityLabel == null) continue;
                    var avgVelocity = statsCtrl.averageVelocity.Value;
                    // Only update if value actually changed (prevents unnecessary re-renders and flashing)
                    
                    if(_previousVelocityValues.TryGetValue(player.OwnerClientId, out var prevVelocity) &&
                       !(Mathf.Abs(prevVelocity - avgVelocity) > 0.05f)) continue;
                    velocityLabel.text = $"{avgVelocity:F1} u/s";
                    _previousVelocityValues[player.OwnerClientId] = avgVelocity;
                }
            }
        }

        private void UpdateTdmScoreboard(IReadOnlyCollection<PlayerController> allControllers) {
            // Null checks for UI elements
            if(_scoreboardContainer == null || _tdmScoreboardContainer == null ||
               _enemyTeamRows == null || _yourTeamRows == null) {
                Debug.LogWarning("[ScoreboardManager] TDM scoreboard UI elements not initialized, falling back to FFA");
                UpdateFfaScoreboard(allControllers);
                return;
            }

            // Show TDM scoreboard, hide FFA
            _scoreboardContainer.AddToClassList("hidden");
            _tdmScoreboardContainer.RemoveFromClassList("hidden");

            _enemyTeamRows.Clear();
            _yourTeamRows.Clear();

            // Get local player's team
            var networkManager = NetworkManager.Singleton;
            if(networkManager == null) {
                UpdateFfaScoreboard(allControllers);
                return;
            }
            if(networkManager.LocalClient == null) {
                UpdateFfaScoreboard(allControllers);
                return;
            }
            var localPlayer = networkManager.LocalClient.PlayerObject;
            if(localPlayer == null) {
                // Fallback to FFA if no local player
                UpdateFfaScoreboard(allControllers);
                return;
            }

            var localController = localPlayer.GetComponent<PlayerController>();
            PlayerTeamManager localTeamMgr = null;
            if(localController != null) {
                localTeamMgr = localController.TeamManager;
            }
            if(localTeamMgr == null) {
                UpdateFfaScoreboard(allControllers);
                return;
            }

            var localTeam = localTeamMgr.netTeam.Value;

            // Get all players and split by team
            var enemyPlayers = new List<PlayerController>();
            var yourTeamPlayers = new List<PlayerController>();

            foreach(var player in allControllers) {
                if(player == null || !player.IsSpawned) continue;
                var teamMgr = player.TeamManager;
                if(teamMgr == null) continue;

                if(teamMgr.netTeam.Value == localTeam) {
                    yourTeamPlayers.Add(player);
                } else {
                    enemyPlayers.Add(player);
                }
            }

            // Sort by kills (descending)
            enemyPlayers.Sort((a, b) => b.Kills.Value.CompareTo(a.Kills.Value));
            yourTeamPlayers.Sort((a, b) => b.Kills.Value.CompareTo(a.Kills.Value));

            // Create rows for each team (simplified stats for TDM)
            foreach(var player in enemyPlayers) {
                CreatePlayerRow(player, _enemyTeamRows, simplifiedStats: true, isYourTeam: false);
            }

            foreach(var player in yourTeamPlayers) {
                CreatePlayerRow(player, _yourTeamRows, simplifiedStats: true, isYourTeam: true);
            }

            // Update team scores
            // Check if we're in Hopball mode and get scores from HopballSpawnManager
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings != null && matchSettings.selectedGameModeId == "Hopball" &&
               HopballSpawnManager.Instance != null) {
                var teamAScore = HopballSpawnManager.Instance.GetTeamAScore();
                var teamBScore = HopballSpawnManager.Instance.GetTeamBScore();
                
                // Determine which score is enemy vs your team
                if(localTeam == SpawnPoint.Team.TeamA) {
                    if(_yourScoreValue != null) {
                        _yourScoreValue.text = teamAScore.ToString();
                    }
                    if(_enemyScoreValue != null) {
                        _enemyScoreValue.text = teamBScore.ToString();
                    }
                } else {
                    if(_yourScoreValue != null) {
                        _yourScoreValue.text = teamBScore.ToString();
                    }
                    if(_enemyScoreValue != null) {
                        _enemyScoreValue.text = teamAScore.ToString();
                    }
                }
            } else {
                var (yourScore, enemyScore) = CalculateTeamKillScores(allControllers, localTeam);

                if(_enemyScoreValue != null) {
                    _enemyScoreValue.text = enemyScore.ToString();
                }

                if(_yourScoreValue != null) {
                    _yourScoreValue.text = yourScore.ToString();
                }
            }
        }

        private Label CreateAverageVelocityLabel(PlayerController player) {
            var statsCtrl = GetCachedStatsController(player);
            var avgVelocity = statsCtrl != null ? statsCtrl.averageVelocity.Value : 0f;
            var avgVelocityLabel = new Label($"{avgVelocity:F1} u/s");
            avgVelocityLabel.AddToClassList("player-stat");
            return avgVelocityLabel;
        }

        private int GetPlayerScore(PlayerController player, bool isTagMode) {
            if(player == null) return isTagMode ? int.MaxValue : 0;
            if(!isTagMode) return player.Kills.Value;

            var tagCtrl = GetCachedTagController(player);
            return tagCtrl != null ? tagCtrl.timeTagged.Value : int.MaxValue;
        }

        private List<PlayerController> BuildSortedPlayerList(IReadOnlyCollection<PlayerController> players, bool isTagMode) {
            var sortedPlayers = new List<PlayerController>();
            foreach(var player in players) {
                if(player == null || !player.IsSpawned) continue;
                sortedPlayers.Add(player);
            }

            sortedPlayers.Sort((a, b) => ComparePlayers(a, b, isTagMode));
            return sortedPlayers;
        }

        private List<(PlayerController player, int score)> BuildSortedScoreList(
            IReadOnlyCollection<PlayerController> players, bool isTagMode) {
            var list = new List<(PlayerController player, int score)>();
            foreach(var player in players) {
                if(player == null || !player.IsSpawned) continue;
                list.Add((player, GetPlayerScore(player, isTagMode)));
            }

            list.Sort((a, b) => isTagMode ? a.score.CompareTo(b.score) : b.score.CompareTo(a.score));
            return list;
        }

        private int ComparePlayers(PlayerController a, PlayerController b, bool isTagMode) {
            var aScore = GetPlayerScore(a, isTagMode);
            var bScore = GetPlayerScore(b, isTagMode);
            return isTagMode ? aScore.CompareTo(bScore) : bScore.CompareTo(aScore);
        }

        private static PlayerController[] GetAllPlayerControllers() {
            return FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
        }

        private static (int yourScore, int enemyScore) CalculateTeamKillScores(
            IReadOnlyCollection<PlayerController> players, SpawnPoint.Team localTeam) {
            var yourTeamKills = 0;
            var enemyTeamKills = 0;

            foreach(var player in players) {
                if(player == null || !player.IsSpawned) continue;
                var teamMgr = player.TeamManager;
                if(teamMgr == null) continue;

                if(teamMgr.netTeam.Value == localTeam) {
                    yourTeamKills += player.Kills.Value;
                } else {
                    enemyTeamKills += player.Kills.Value;
                }
            }

            return (yourTeamKills, enemyTeamKills);
        }

        /// <summary>
        /// Creates the base row structure (row element, ping, avatar, name) shared by all scoreboard rows.
        /// </summary>
        private VisualElement CreatePlayerRowBase(PlayerController player, VisualElement parentContainer, bool isYourTeam = false) {
            var row = new VisualElement();
            row.AddToClassList("player-row");

            // Highlight local player
            if(player.IsOwner) {
                row.AddToClassList("player-row-local");
                if(isYourTeam) {
                    row.AddToClassList("player-row-local-your-team");
                }
            }

            // Add to parent container
            parentContainer.Add(row);

            // Ping
            var ping = new Label(GetPingText(player));
            ping.AddToClassList("player-ping");
            ping.AddToClassList(GetPingColorClass(player));
            row.Add(ping);

            // Avatar (player icon based on color)
            var avatar = new VisualElement();
            avatar.AddToClassList("player-avatar");
            var baseColor = player != null ? player.CurrentBaseColor : Color.white;
            var playerIcon = GetPlayerIconSprite(baseColor);
            if(playerIcon != null) {
                avatar.style.backgroundImage = new StyleBackground(playerIcon);
            }

            row.Add(avatar);

            // Name
        var playerName = new Label(player.PlayerName.Value.ToString());
            playerName.AddToClassList("player-name");
            row.Add(playerName);

            return row;
        }

        /// <summary>
        /// Adds normal mode stats (K, D, A, KDR, DMG, HS%, AV) to a player row.
        /// </summary>
        private void AddNormalModeStats(VisualElement row, PlayerController player) {
            // Kills
            var kills = new Label(player.Kills.Value.ToString());
            kills.AddToClassList("player-stat");
            row.Add(kills);

            // Deaths
            var deaths = new Label(player.Deaths.Value.ToString());
            deaths.AddToClassList("player-stat");
            row.Add(deaths);

            // Assists
            var assists = new Label(player.Assists.Value.ToString());
            assists.AddToClassList("player-stat");
            row.Add(assists);

            // KDA
            var kda = CalculateKdr(player.Kills.Value, player.Deaths.Value, player.Assists.Value);
            var kdaLabel = new Label(kda.ToString("F2"));
            kdaLabel.AddToClassList("player-stat");
            if(kda >= 2.0f) {
                kdaLabel.AddToClassList("player-stat-highlight");
            }

            row.Add(kdaLabel);

            // Damage
        var damage = Mathf.RoundToInt(player.DamageDealt.Value);
            var damageLabel = new Label($"{damage:N0}");
            damageLabel.AddToClassList("player-stat");
            row.Add(damageLabel);

            // Headshot % (placeholder)
            var headshotPct = new Label("0%");
            headshotPct.AddToClassList("player-stat");
            row.Add(headshotPct);

            // Average Velocity
            row.Add(CreateAverageVelocityLabel(player));
        }

        private VisualElement CreatePlayerRow(PlayerController player, VisualElement parentContainer, bool isTagMode) {
            var row = CreatePlayerRowBase(player, parentContainer);

            if(isTagMode) {
                // Tag mode stats: TT, Tags, Tagged, TTR, DMG, AV
                // Order matches header: PING, AVATAR, NAME, TT, Tags, Tagged, TTR, DMG, AV
                // TT (Time Tagged) - main score, shown first (replaces K)
                var tagCtrl = GetCachedTagController(player);
                var timeTaggedVal = tagCtrl != null ? tagCtrl.timeTagged.Value : 0;
                var tagsVal = tagCtrl != null ? tagCtrl.tags.Value : 0;
                var taggedVal = tagCtrl != null ? tagCtrl.tagged.Value : 0;

                var timeTagged = new Label(timeTaggedVal.ToString());
                timeTagged.AddToClassList("player-stat");
                row.Add(timeTagged);

                // Tags (replaces D)
                var tags = new Label(tagsVal.ToString());
                tags.AddToClassList("player-stat");
                row.Add(tags);

                // Tagged (replaces A)
                var tagged = new Label(taggedVal.ToString());
                tagged.AddToClassList("player-stat");
                row.Add(tagged);

                // TTR (Tag-Tagged Ratio) instead of KDR
                var ttr = CalculateTtr(tagsVal, taggedVal);
                var ttrLabel = new Label(ttr.ToString("F2"));
                ttrLabel.AddToClassList("player-stat");
                if(ttr >= 2.0f) {
                    ttrLabel.AddToClassList("player-stat-highlight");
                }

                row.Add(ttrLabel);

                // Skip Damage and HS% for Tag mode (no damage dealt in Tag mode)

                // Average Velocity (skip HS% and DMG for Tag mode)
                row.Add(CreateAverageVelocityLabel(player));
            } else {
                // Normal mode stats
                AddNormalModeStats(row, player);
            }

            return row;
        }

        // Overload for TDM (includes K, D, A, KDR, DMG, HS%, AV)
        private void CreatePlayerRow(PlayerController player, VisualElement parentContainer, bool simplifiedStats, bool isYourTeam) {
            if(!simplifiedStats) {
                // Call the FFA version with isTagMode = false
                CreatePlayerRow(player, parentContainer, isTagMode: false);
                return;
            }

            var row = CreatePlayerRowBase(player, parentContainer, isYourTeam);
            AddNormalModeStats(row, player);
        }

        private static string GetPingText(PlayerController player) {
            var ping = 0;
            if(player != null) {
                ping = player.PingMs;
            }
            return $"{ping}ms";
        }

        private static string GetPingColorClass(PlayerController player) {
            var ping = 0;
            if(player != null) {
                ping = player.PingMs;
            }

            return ping switch {
                > 100 => "player-ping-critical",
                > 50 => "player-ping-high",
                _ => ""
            };
        }

        private static float CalculateKdr(int kills, int deaths, int assists) {
            if(deaths == 0) return kills + assists;
            return (kills + assists) / (float)deaths;
        }

        private static float CalculateTtr(int tags, int tagged) {
            if(tagged == 0) return tags;
            return tags / (float)tagged;
        }

        /// <summary>
        /// Gets cached PlayerTagController for a player, or retrieves and caches it if not found.
        /// </summary>
        private PlayerTagController GetCachedTagController(PlayerController player) {
            return GetCachedComponent(player, _cachedTagControllers, pc => pc.TagController);
        }

        /// <summary>
        /// Gets cached PlayerStatsController for a player, or retrieves and caches it if not found.
        /// </summary>
        private PlayerStatsController GetCachedStatsController(PlayerController player) {
            return GetCachedComponent(player, _cachedStatsControllers, pc => pc.StatsController);
        }

        private static T GetCachedComponent<T>(PlayerController player, Dictionary<PlayerController, T> cache,
            Func<PlayerController, T> directAccessor) where T : Component {
            if(player == null) return null;

            var direct = directAccessor(player);
            if(direct != null) return direct;

            if(cache.TryGetValue(player, out var cached)) return cached;
            var component = player.GetComponent<T>();
            if(component != null) {
                cache[player] = component;
            }

            return component;
        }

        /// <summary>
        /// Gets the player icon sprite based on the player's material index.
        /// Material index order: 0=white, 1=red, 2=orange, 3=yellow, 4=green, 5=blue, 6=purple
        /// </summary>
        private Sprite GetPlayerIconSprite(Color baseColor) {
            if(playerIconSprites == null || playerIconSprites.Length == 0) {
                return null;
            }
            var paletteIndex = GetClosestIconIndex(baseColor);
            var clampedIndex = Mathf.Clamp(paletteIndex, 0, playerIconSprites.Length - 1);
            return playerIconSprites[clampedIndex];
        }
        
        private int GetClosestIconIndex(Color baseColor) {
            if(playerIconSprites == null || playerIconSprites.Length == 0) return 0;

            // Use the legacy palette order: white, red, orange, yellow, green, blue, purple
            var palette = new[]
            {
                new Color(1f, 1f, 1f),
                new Color(1f, 0f, 0f),
                new Color(1f, 0.5f, 0f),
                new Color(1f, 1f, 0f),
                new Color(0f, 1f, 0f),
                new Color(0f, 0f, 1f),
                new Color(0.5f, 0f, 1f)
            };

            var bestIndex = 0;
            var bestDistance = float.MaxValue;

            for(var i = 0; i < palette.Length; i++) {
                var diff = palette[i] - baseColor;
                var distSq = diff.r * diff.r + diff.g * diff.g + diff.b * diff.b;

                if(!(distSq < bestDistance)) continue;
                bestDistance = distSq;
                bestIndex = i;
            }

            return bestIndex;
        }

        /// <summary>
        /// Updates the score display next to the timer based on game mode.
        /// </summary>
        private void UpdateScoreDisplay() {
            if(_leftScoreContainer == null || _rightScoreContainer == null ||
               _leftScoreValue == null || _rightScoreValue == null) {
                return;
            }

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null) return;

            var isTeamBased = MatchSettingsManager.IsTeamBasedMode(matchSettings.selectedGameModeId);

            var controllers = GetAllPlayerControllers();

            if(isTeamBased) {
                UpdateTeamBasedScore(controllers);
            } else {
                UpdateFfaScore(controllers);
            }
        }

        /// <summary>
        /// Updates score display for team-based modes.
        /// </summary>
        private void UpdateTeamBasedScore(IReadOnlyCollection<PlayerController> allControllers = null) {
            if(allControllers == null) throw new ArgumentNullException(nameof(allControllers));
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null) return;

            int yourScore;
            int enemyScore;

            // Get local player's team
            var networkManager = NetworkManager.Singleton;
            if(networkManager == null) return;
            if(networkManager.LocalClient == null) return;
            var localPlayer = networkManager.LocalClient.PlayerObject;
            if(localPlayer == null) return;

            var localController = localPlayer.GetComponent<PlayerController>();
            PlayerTeamManager localTeamMgr = null;
            if(localController != null) {
                localTeamMgr = localController.TeamManager;
            }
            if(localTeamMgr == null) return;

            var localTeam = localTeamMgr.netTeam.Value;

            // Get scores based on game mode
            if(matchSettings.selectedGameModeId == "Hopball" && HopballSpawnManager.Instance != null) {
                var teamAScore = HopballSpawnManager.Instance.GetTeamAScore();
                var teamBScore = HopballSpawnManager.Instance.GetTeamBScore();

                if(localTeam == SpawnPoint.Team.TeamA) {
                    yourScore = teamAScore;
                    enemyScore = teamBScore;
                } else {
                    yourScore = teamBScore;
                    enemyScore = teamAScore;
                }
            } else {
                // For other team modes, use total kills
                allControllers = GetAllPlayerControllers();
                (yourScore, enemyScore) = CalculateTeamKillScores(allControllers, localTeam);
            }

            _leftScoreValue.text = yourScore.ToString();
            _rightScoreValue.text = enemyScore.ToString();
        }

        /// <summary>
        /// Updates score display for FFA modes (Deathmatch, Gun Tag, etc.).
        /// </summary>
        private void UpdateFfaScore(IReadOnlyCollection<PlayerController> allControllers = null) {
            if(allControllers == null) throw new ArgumentNullException(nameof(allControllers));
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null) return;

            var isTagMode = matchSettings.selectedGameModeId == "Gun Tag";

            // Get local player
            if(_localController == null) {
                FindLocalController();
            }

            if(_localController == null) return;

            // Get local player's score
            var localScore = GetPlayerScore(_localController, isTagMode);

            // Get all players and find the next highest (or highest if local is not first)
            allControllers = GetAllPlayerControllers();
            var sortedPlayers = BuildSortedScoreList(allControllers, isTagMode);

            // Find next highest/lowest score
            var nextScore = 0;
            var foundNext = false;

            if(isTagMode) {
                // For Gun Tag, find next LOWEST (or lowest if local is lowest)
                for(var i = 0; i < sortedPlayers.Count; i++) {
                    if(sortedPlayers[i].player != _localController) continue;
                    // If we're the lowest (1st place), show the next lowest (2nd place)
                    if(i == 0) {
                        // Show 2nd place (next lowest)
                        nextScore = sortedPlayers.Count > 1 ? sortedPlayers[1].score : 0; // Only one player

                        foundNext = true;
                        break;
                    }

                    // Otherwise show the lowest (1st place)
                    nextScore = sortedPlayers[0].score;
                    foundNext = true;
                    break;
                }
            } else {
                // For Deathmatch, find next HIGHEST (or highest if local is highest)
                for(var i = 0; i < sortedPlayers.Count; i++) {
                    if(sortedPlayers[i].player != _localController) continue;
                    // If we're the highest (1st place), show the next highest (2nd place)
                    if(i == 0) {
                        // Show 2nd place (next highest)
                        nextScore = sortedPlayers.Count > 1 ? sortedPlayers[1].score : 0; // Only one player

                        foundNext = true;
                        break;
                    }

                    // Otherwise show the highest (1st place)
                    nextScore = sortedPlayers[0].score;
                    foundNext = true;
                    break;
                }
            }

            if(!foundNext && sortedPlayers.Count > 0) {
                // Fallback: use first place score
                nextScore = sortedPlayers[0].score;
            }

            _leftScoreValue.text = localScore.ToString();
            _rightScoreValue.text = nextScore.ToString();
        }

        public void HideScoreDisplay() {
            if(_leftScoreContainer != null)
                _leftScoreContainer.style.display = DisplayStyle.None;
            if(_rightScoreContainer != null)
                _rightScoreContainer.style.display = DisplayStyle.None;
        }

        public void ShowScoreDisplay() {
            if(_leftScoreContainer != null)
                _leftScoreContainer.style.display = DisplayStyle.Flex;
            if(_rightScoreContainer != null)
                _rightScoreContainer.style.display = DisplayStyle.Flex;
        }
    }
}


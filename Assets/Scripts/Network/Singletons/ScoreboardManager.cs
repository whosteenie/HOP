using System.Collections.Generic;
using Game.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Network.Singletons {
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
        private VisualElement _enemyTeamSection;
        private VisualElement _enemyTeamRows;
        private VisualElement _yourTeamSection;
        private VisualElement _yourTeamRows;
        private Label _enemyScoreValue;
        private Label _yourScoreValue;

        // Match timer
        private Label _matchTimerLabel;
        private int _lastTickSecond = -1;
        private float _lastTickTime = -1f; // Track when we last played a tick to prevent duplicates in same frame

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
        private HashSet<ulong> _previousPlayerIds = new HashSet<ulong>();
        private Dictionary<ulong, int> _previousSortValues = new Dictionary<ulong, int>(); // kills or timeTagged
        private Dictionary<ulong, Label> _cachedVelocityLabels = new Dictionary<ulong, Label>(); // clientId -> velocity label
        private Dictionary<ulong, float> _previousVelocityValues = new Dictionary<ulong, float>(); // Track previous velocity to avoid unnecessary updates
        
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
            _enemyTeamSection = _root.Q<VisualElement>("enemy-team-section");
            _enemyTeamRows = _root.Q<VisualElement>("enemy-team-rows");
            _yourTeamSection = _root.Q<VisualElement>("your-team-section");
            _yourTeamRows = _root.Q<VisualElement>("your-team-rows");
            _enemyScoreValue = _root.Q<Label>("enemy-score-value");
            _yourScoreValue = _root.Q<Label>("your-score-value");

            // Match timer
            _matchTimerLabel = _root.Q<Label>("match-timer-label");

            // Cache MatchSettingsManager
            _cachedMatchSettings = MatchSettingsManager.Instance;
            _headerLabelsCacheValid = false;

            // Subscribe to network callbacks for cleanup
            if(NetworkManager.Singleton != null) {
                NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
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

            int minutes = secondsRemaining / 60;
            int seconds = secondsRemaining % 60;

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
            if(_cachedMatchSettings == null) {
                _cachedMatchSettings = MatchSettingsManager.Instance;
            }
            
            // Always check fresh - don't cache game mode as it may not be set yet during initialization
            return _cachedMatchSettings != null && _cachedMatchSettings.selectedGameModeId == "Gun Tag";
        }
        
        /// <summary>
        /// Checks if we're in a team-based mode. Always checks fresh to handle build initialization order issues.
        /// </summary>
        private bool IsTeamBased() {
            // Refresh MatchSettingsManager cache if needed
            if(_cachedMatchSettings == null) {
                _cachedMatchSettings = MatchSettingsManager.Instance;
            }
            
            // Always check fresh - don't cache game mode as it may not be set yet during initialization
            return _cachedMatchSettings != null && IsTeamBasedMode(_cachedMatchSettings.selectedGameModeId);
        }

        /// <summary>
        /// Updates the scoreboard title to show the current gamemode name.
        /// </summary>
        private void UpdateScoreboardTitle() {
            // Refresh MatchSettingsManager cache if needed
            if(_cachedMatchSettings == null) {
                _cachedMatchSettings = MatchSettingsManager.Instance;
            }

            string gamemodeName = "SCOREBOARD"; // Default fallback
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
            bool isTagMode = IsTagMode();

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
                    if(text == "K") {
                        label.text = "TT"; // TT is the main score, placed first
                    } else if(text == "D") {
                        label.text = "Tags";
                    } else if(text == "A") {
                        label.text = "Tagged";
                    } else if(text == "KDR") {
                        label.text = "TTR";
                    } else if(text == "HS%") {
                        label.style.display = DisplayStyle.None;
                    } else if(text == "DMG") {
                        label.style.display = DisplayStyle.None;
                    }
                }
            } else {
                // Normal mode headers: PING, AVATAR, NAME, K, D, A, KDR, DMG, HS%, AV
                // Restore all columns
                foreach(var label in _cachedHeaderLabels) {
                    var text = label.text;
                    if(text == "TT") {
                        label.text = "K";
                    } else if(text == "Tags") {
                        label.text = "D";
                    } else if(text == "Tagged") {
                        label.text = "A";
                    } else if(text == "TTR") {
                        label.text = "KDR";
                    }

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
            // Always check fresh - don't cache game mode
            if(IsTeamBased()) {
                UpdateTdmScoreboard();
            } else {
                UpdateFfaScoreboard();
            }
        }

        private bool IsTeamBasedMode(string modeId) => modeId switch {
            "Team Deathmatch" => true,
            "Hopball" => true,
            "CTF" => true,
            "Oddball" => true,
            "KOTH" => true,
            _ => false
        };

        private void UpdateFfaScoreboard() {
            // Null checks for UI elements
            if(_scoreboardContainer == null || _tdmScoreboardContainer == null || _playerRows == null) {
                Debug.LogWarning("[ScoreboardManager] FFA scoreboard UI elements not initialized");
                return;
            }

            // Show FFA scoreboard, hide TDM
            _scoreboardContainer.RemoveFromClassList("hidden");
            _tdmScoreboardContainer.AddToClassList("hidden");

            // Always check fresh - don't cache game mode
            bool isTagMode = IsTagMode();

            // Get all player controllers
            var allControllers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);

            // Build current player set and sort values
            var currentPlayerIds = new HashSet<ulong>();
            var currentSortValues = new Dictionary<ulong, int>();

            foreach(var player in allControllers) {
                if(player == null || !player.IsSpawned) continue;
                currentPlayerIds.Add(player.OwnerClientId);

                if(isTagMode) {
                    var tagCtrl = GetCachedTagController(player);
                    currentSortValues[player.OwnerClientId] = tagCtrl != null ? tagCtrl.timeTagged.Value : int.MaxValue;
                } else {
                    currentSortValues[player.OwnerClientId] = player.kills.Value;
                }
            }

            // Check if we need to rebuild (player list changed or sort order changed)
            bool needsRebuild = !currentPlayerIds.SetEquals(_previousPlayerIds);

            if(!needsRebuild) {
                // Check if sort values changed (indicating reordering needed)
                foreach(var kvp in currentSortValues) {
                    if(!_previousSortValues.TryGetValue(kvp.Key, out var oldValue) || oldValue != kvp.Value) {
                        needsRebuild = true;
                        break;
                    }
                }
            }

            if(needsRebuild) {
                // Clear and rebuild scoreboard
                _playerRows.Clear();
                _cachedVelocityLabels.Clear();
                _previousVelocityValues.Clear();

                // Sort by appropriate stat
                var sortedPlayers = new List<PlayerController>(allControllers);
                if(isTagMode) {
                    // Tag mode: sort by time tagged (lowest first)
                    sortedPlayers.Sort((a, b) => {
                        var aTag = GetCachedTagController(a);
                        var bTag = GetCachedTagController(b);
                        var aVal = aTag != null ? aTag.timeTagged.Value : int.MaxValue;
                        var bVal = bTag != null ? bTag.timeTagged.Value : int.MaxValue;
                        return aVal.CompareTo(bVal);
                    });
                } else {
                    // Normal mode: sort by kills (descending)
                    sortedPlayers.Sort((a, b) => b.kills.Value.CompareTo(a.kills.Value));
                }

                foreach(var player in sortedPlayers) {
                    if(player == null || !player.IsSpawned) continue;
                    var row = CreatePlayerRow(player, _playerRows, isTagMode: isTagMode);
                    if(row != null) {
                        // Cache velocity label for this player (last stat label in the row)
                        var labels = row.Query<Label>().ToList();
                        if(labels.Count > 0) {
                            _cachedVelocityLabels[player.OwnerClientId] = labels[labels.Count - 1];
                        }
                    }
                }

                // Update cached state
                _previousPlayerIds = new HashSet<ulong>(currentPlayerIds);
                _previousSortValues = new Dictionary<ulong, int>(currentSortValues);
            } else {
                // Only update velocity labels for existing rows (only if value changed to avoid flashing)
                foreach(var player in allControllers) {
                    if(player == null || !player.IsSpawned) continue;
                    if(_cachedVelocityLabels.TryGetValue(player.OwnerClientId, out var velocityLabel)) {
                        var statsCtrl = GetCachedStatsController(player);
                        if(statsCtrl != null && velocityLabel != null) {
                            var avgVelocity = statsCtrl.averageVelocity.Value;
                            // Only update if value actually changed (prevents unnecessary re-renders and flashing)
                            if(!_previousVelocityValues.TryGetValue(player.OwnerClientId, out var prevVelocity) || 
                               Mathf.Abs(prevVelocity - avgVelocity) > 0.05f) {
                                velocityLabel.text = $"{avgVelocity:F1} u/s";
                                _previousVelocityValues[player.OwnerClientId] = avgVelocity;
                            }
                        }
                    }
                }
            }
        }

        private void UpdateTdmScoreboard() {
            // Null checks for UI elements
            if(_scoreboardContainer == null || _tdmScoreboardContainer == null ||
               _enemyTeamRows == null || _yourTeamRows == null) {
                Debug.LogWarning("[ScoreboardManager] TDM scoreboard UI elements not initialized, falling back to FFA");
                UpdateFfaScoreboard();
                return;
            }

            // Show TDM scoreboard, hide FFA
            _scoreboardContainer.AddToClassList("hidden");
            _tdmScoreboardContainer.RemoveFromClassList("hidden");

            _enemyTeamRows.Clear();
            _yourTeamRows.Clear();

            // Get local player's team
            var localPlayer = NetworkManager.Singleton?.LocalClient?.PlayerObject;
            if(localPlayer == null) {
                // Fallback to FFA if no local player
                UpdateFfaScoreboard();
                return;
            }

            var localTeamMgr = localPlayer.GetComponent<PlayerTeamManager>();
            if(localTeamMgr == null) {
                UpdateFfaScoreboard();
                return;
            }

            var localTeam = localTeamMgr.netTeam.Value;

            // Get all players and split by team
            var allControllers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
            var enemyPlayers = new List<PlayerController>();
            var yourTeamPlayers = new List<PlayerController>();

            foreach(var player in allControllers) {
                if(player == null || !player.IsSpawned) continue;
                var teamMgr = player.GetComponent<PlayerTeamManager>();
                if(teamMgr == null) continue;

                if(teamMgr.netTeam.Value == localTeam) {
                    yourTeamPlayers.Add(player);
                } else {
                    enemyPlayers.Add(player);
                }
            }

            // Sort by kills (descending)
            enemyPlayers.Sort((a, b) => b.kills.Value.CompareTo(a.kills.Value));
            yourTeamPlayers.Sort((a, b) => b.kills.Value.CompareTo(a.kills.Value));

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
            if(matchSettings != null && matchSettings.selectedGameModeId == "Hopball" && HopballSpawnManager.Instance != null) {
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
                // For other team modes, use total kills
                int enemyScore = CalculateTeamScore(enemyPlayers);
                int yourScore = CalculateTeamScore(yourTeamPlayers);

                if(_enemyScoreValue != null) {
                    _enemyScoreValue.text = enemyScore.ToString();
                }

                if(_yourScoreValue != null) {
                    _yourScoreValue.text = yourScore.ToString();
                }
            }
        }

        private int CalculateTeamScore(List<PlayerController> players) {
            int totalKills = 0;
            foreach(var player in players) {
                totalKills += player.kills.Value;
            }

            return totalKills;
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
            var playerIcon = GetPlayerIconSprite(player.playerMaterialIndex.Value);
            if(playerIcon != null) {
                avatar.style.backgroundImage = new StyleBackground(playerIcon);
            }

            row.Add(avatar);

            // Name
            var playerName = new Label(player.playerName.Value.ToString());
            playerName.AddToClassList("player-name");
            row.Add(playerName);

            return row;
        }

        /// <summary>
        /// Adds normal mode stats (K, D, A, KDR, DMG, HS%, AV) to a player row.
        /// </summary>
        private void AddNormalModeStats(VisualElement row, PlayerController player) {
            // Kills
            var kills = new Label(player.kills.Value.ToString());
            kills.AddToClassList("player-stat");
            row.Add(kills);

            // Deaths
            var deaths = new Label(player.deaths.Value.ToString());
            deaths.AddToClassList("player-stat");
            row.Add(deaths);

            // Assists
            var assists = new Label(player.assists.Value.ToString());
            assists.AddToClassList("player-stat");
            row.Add(assists);

            // KDA
            var kda = CalculateKdr(player.kills.Value, player.deaths.Value, player.assists.Value);
            var kdaLabel = new Label(kda.ToString("F2"));
            kdaLabel.AddToClassList("player-stat");
            if(kda >= 2.0f) {
                kdaLabel.AddToClassList("player-stat-highlight");
            }

            row.Add(kdaLabel);

            // Damage
            var damage = Mathf.RoundToInt(player.damageDealt.Value);
            var damageLabel = new Label($"{damage:N0}");
            damageLabel.AddToClassList("player-stat");
            row.Add(damageLabel);

            // Headshot % (placeholder)
            var headshotPct = new Label("0%");
            headshotPct.AddToClassList("player-stat");
            row.Add(headshotPct);

            // Average Velocity
            var statsCtrl = GetCachedStatsController(player);
            var avgVelocity = statsCtrl != null ? statsCtrl.averageVelocity.Value : 0f;
            var avgVelocityLabel = new Label($"{avgVelocity:F1} u/s");
            avgVelocityLabel.AddToClassList("player-stat");
            row.Add(avgVelocityLabel);
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
                var statsCtrl = GetCachedStatsController(player);
                var avgVelocity = statsCtrl != null ? statsCtrl.averageVelocity.Value : 0f;
                var avgVelocityLabel = new Label($"{avgVelocity:F1} u/s");
                avgVelocityLabel.AddToClassList("player-stat");
                row.Add(avgVelocityLabel);
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

        private string GetPingText(PlayerController player) {
            var statsCtrl = GetCachedStatsController(player);
            var ping = statsCtrl != null ? statsCtrl.pingMs.Value : 0;
            return $"{ping}ms";
        }

        private string GetPingColorClass(PlayerController player) {
            var statsCtrl = GetCachedStatsController(player);
            var ping = statsCtrl != null ? statsCtrl.pingMs.Value : 0;

            return ping switch {
                > 100 => "player-ping-critical",
                > 50 => "player-ping-high",
                _ => ""
            };
        }

        private float CalculateKdr(int kills, int deaths, int assists) {
            if(deaths == 0) return kills + assists;
            return (kills + assists) / (float)deaths;
        }

        private float CalculateTtr(int tags, int tagged) {
            if(tagged == 0) return tags;
            return tags / (float)tagged;
        }

        /// <summary>
        /// Gets cached PlayerTagController for a player, or retrieves and caches it if not found.
        /// </summary>
        private PlayerTagController GetCachedTagController(PlayerController player) {
            if(player == null) return null;

            if(!_cachedTagControllers.TryGetValue(player, out var tagCtrl)) {
                tagCtrl = player.GetComponent<PlayerTagController>();
                if(tagCtrl != null) {
                    _cachedTagControllers[player] = tagCtrl;
                }
            }

            return tagCtrl;
        }

        /// <summary>
        /// Gets cached PlayerStatsController for a player, or retrieves and caches it if not found.
        /// </summary>
        private PlayerStatsController GetCachedStatsController(PlayerController player) {
            if(player == null) return null;

            if(!_cachedStatsControllers.TryGetValue(player, out var statsCtrl)) {
                statsCtrl = player.GetComponent<PlayerStatsController>();
                if(statsCtrl != null) {
                    _cachedStatsControllers[player] = statsCtrl;
                }
            }

            return statsCtrl;
        }

        /// <summary>
        /// Gets the player icon sprite based on the player's material index.
        /// Material index order: 0=white, 1=red, 2=orange, 3=yellow, 4=green, 5=blue, 6=purple
        /// </summary>
        private Sprite GetPlayerIconSprite(int materialIndex) {
            if(playerIconSprites == null || playerIconSprites.Length == 0) {
                return null;
            }

            // Clamp index to valid range
            var clampedIndex = Mathf.Clamp(materialIndex, 0, playerIconSprites.Length - 1);
            return playerIconSprites[clampedIndex];
        }
    }
}


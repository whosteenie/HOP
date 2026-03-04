using System;
using System.Collections.Generic;
using Game.Hopball;
using Game.Match;
using Game.Player;
using Game.Spawning;
using Network.Events;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;
using SessionManager = Network.Session.SessionManager;

namespace Game.UI {
    /// <summary>
    /// Manages the scoreboard UI, including FFA and TDM scoreboards, player rows, and match timer.
    /// </summary>
    public partial class ScoreboardManager : MonoBehaviour {
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
        private Label _scoreboardMapTitle;

        // TDM Scoreboard
        private VisualElement _tdmScoreboardContainer;
        private Label _tdmScoreboardTitle;
        private Label _tdmScoreboardMapTitle;
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
        private bool _missingGamemodeTitleLogged;

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

        private readonly Dictionary<ulong, float>
            _previousVelocityValues = new(); // Track previous velocity to avoid unnecessary updates

        // Speaking Indicators Cache
        private readonly Dictionary<ulong, VisualElement>
            _cachedSpeakingIndicators = new(); // clientId -> indicator element


        // Cache scene name to avoid string allocations
        private string _cachedSceneName;

        public bool IsScoreboardVisible { get; private set; }

        // Mouse unlock state for context menu interaction
        private bool _hoverDisabledForMouseLook;

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        private void OnEnable() {
            // Subscribe to UI events
            EventBus.Subscribe<SetMatchTimeEvent>(OnSetMatchTime);
            EventBus.Subscribe<ShowScoreboardEvent>(OnShowScoreboard);
            EventBus.Subscribe<HideScoreboardEvent>(OnHideScoreboard);
            EventBus.Subscribe<ScoreboardRefreshRequestedEvent>(OnScoreboardRefreshRequested);
            EventBus.Subscribe<ScoreboardGamemodeChangedEvent>(OnScoreboardGamemodeChanged);
            EventBus.Subscribe<HideScoreDisplayEvent>(OnHideScoreDisplay);
            EventBus.Subscribe<ShowScoreDisplayEvent>(OnShowScoreDisplay);
            EventBus.Subscribe<PlayerNetworkSpawnedEvent>(OnPlayerNetworkSpawned);
            EventBus.Subscribe<PlayerNetworkDespawnedEvent>(OnPlayerNetworkDespawned);
        }

        private void OnDisable() {
            // Unsubscribe from UI events
            EventBus.Unsubscribe<SetMatchTimeEvent>(OnSetMatchTime);
            EventBus.Unsubscribe<ShowScoreboardEvent>(OnShowScoreboard);
            EventBus.Unsubscribe<HideScoreboardEvent>(OnHideScoreboard);
            EventBus.Unsubscribe<ScoreboardRefreshRequestedEvent>(OnScoreboardRefreshRequested);
            EventBus.Unsubscribe<ScoreboardGamemodeChangedEvent>(OnScoreboardGamemodeChanged);
            EventBus.Unsubscribe<HideScoreDisplayEvent>(OnHideScoreDisplay);
            EventBus.Unsubscribe<ShowScoreDisplayEvent>(OnShowScoreDisplay);
            EventBus.Unsubscribe<PlayerNetworkSpawnedEvent>(OnPlayerNetworkSpawned);
            EventBus.Unsubscribe<PlayerNetworkDespawnedEvent>(OnPlayerNetworkDespawned);

            // Unsubscribe from network callbacks
            if(NetworkManager.Singleton != null) {
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
            }

            // Unsubscribe from scene changes
            SceneManager.sceneLoaded -= OnSceneLoaded;

            // Clear cached dictionaries when match ends
            ClearCachedPlayerData();
        }


        #region Event Handlers

        private void OnSetMatchTime(SetMatchTimeEvent evt) {
            SetMatchTime(evt.Seconds);
        }

        private void OnShowScoreboard(ShowScoreboardEvent evt) {
            ShowScoreboard();
        }

        private void OnHideScoreboard(HideScoreboardEvent evt) {
            HideScoreboard();
        }

        private void OnScoreboardRefreshRequested(ScoreboardRefreshRequestedEvent evt) {
            UpdateScoreboard();
        }

        private void OnScoreboardGamemodeChanged(ScoreboardGamemodeChangedEvent evt) {
            RefreshGamemode();
        }

        private void OnHideScoreDisplay(HideScoreDisplayEvent evt) {
            HideScoreDisplay();
        }

        private void OnShowScoreDisplay(ShowScoreDisplayEvent evt) {
            ShowScoreDisplay();
        }

        private void OnPlayerNetworkSpawned(PlayerNetworkSpawnedEvent evt) {
            RegisterPlayer(evt.Player);
        }

        private void OnPlayerNetworkDespawned(PlayerNetworkDespawnedEvent evt) {
            UnregisterPlayer(evt.Player);
        }

        #endregion

        /// <summary>
        /// Initializes the scoreboard manager with UI element references.
        /// </summary>
        public void Initialize(VisualElement root) {
            _root = root;

            // Cache scene name to avoid allocations
            UpdateCachedSceneName();

            // Idempotent subscription: prevent duplicate handlers if Initialize is called more than once.
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;

            // Find UI elements
            _scoreboardPanel = _root.Q<VisualElement>("scoreboard-panel");
            _playerRows = _root.Q<VisualElement>("player-rows");
            _scoreboardContainer = _root.Q<VisualElement>("scoreboard-container");
            _scoreboardTitle = _root.Q<Label>("scoreboard-title");
            _scoreboardMapTitle = _root.Q<Label>("scoreboard-map-title");

            // TDM Scoreboard
            _tdmScoreboardContainer = _root.Q<VisualElement>("tdm-scoreboard-container");
            _tdmScoreboardTitle = _root.Q<Label>("tdm-scoreboard-title");
            _tdmScoreboardMapTitle = _root.Q<Label>("tdm-scoreboard-map-title");
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
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
                NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
            }

            BootstrapPlayerRegistry();
            ApplyInitialMatchTimerState();
        }

        private void BootstrapPlayerRegistry() {
            foreach(var player in PlayerController.SpawnedPlayers) {
                RegisterPlayer(player);
            }
        }

        private void Update() {
            if(_localController == null && SessionManager.IsGameplaySceneName(_cachedSceneName)) {
                FindLocalController();
            }

            // Update score display periodically
            if(!SessionManager.IsGameplaySceneName(_cachedSceneName) ||
               !(Time.time - _lastScoreUpdateTime >= ScoreUpdateInterval)) return;
            UpdateScoreDisplay();
            _lastScoreUpdateTime = Time.time;

            // Update speaking indicators if scoreboard is visible
            if(!IsScoreboardVisible) return;
            RefreshHoverStateForCursorMode();
            UpdateSpeakingIndicators();
        }


        private void UpdateSpeakingIndicators() {
            if(Social.VoiceManager.Instance == null) return;
            var voiceMgr = Social.VoiceManager.Instance;

            var controllers = GetAllPlayerControllers();
            foreach(var player in controllers) {
                if(player == null ||
                   !_cachedSpeakingIndicators.TryGetValue(player.OwnerClientId, out var indicator)) continue;

                // Get SteamID
                var steamId = player.steamId.Value;
                if(steamId == 0) continue; // Invalid steam ID

                var isSpeaking = voiceMgr.IsSpeaking(steamId.ToString());
                if(isSpeaking) {
                    indicator.AddToClassList("active");
                } else {
                    indicator.RemoveFromClassList("active");
                }
            }
        }


        private void FindLocalController() {
            var allControllers = GetAllPlayerControllers();
            foreach(var controller in allControllers) {
                if(!controller.IsOwner) continue;
                _localController = controller;
                break;
            }
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
            _cachedSpeakingIndicators.Clear();
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

        private void SetMatchTime(int secondsRemaining, bool playTickSfx = true) {
            if(_matchTimerLabel == null) return;

            if(secondsRemaining < 0) {
                _matchTimerLabel.text = "INFINITE";
                return;
            }

            var minutes = secondsRemaining / 60;
            var seconds = secondsRemaining % 60;

            _matchTimerLabel.text = $"{minutes:00}:{seconds:00}";

            if(!playTickSfx || minutes != 0 || seconds is > 5 or < 1) return;
            if(Audio2.AudioService.Instance != null) {
                Audio2.AudioService.Instance.Play("ui.timer", Vector3.zero);
            }
        }

        private void ApplyInitialMatchTimerState() {
            if(_matchTimerLabel == null) return;

            const int defaultPreMatchSeconds = 5;
            const int defaultMatchSeconds = 600;

            var matchTimer = MatchTimerManager.Instance;
            var matchSettings = MatchSettingsManager.Instance;

            if(matchTimer != null) {
                if(matchTimer.IsPreMatch) {
                    var preMatchSeconds = matchTimer.PreMatchCountdownSeconds;
                    if(preMatchSeconds <= 0) {
                        preMatchSeconds = matchSettings != null
                            ? matchSettings.GetPreMatchCountdownSeconds()
                            : defaultPreMatchSeconds;
                    }

                    SetMatchTime(Mathf.Max(0, preMatchSeconds), playTickSfx: false);
                    return;
                }

                if(matchSettings != null && matchSettings.IsInfiniteMatchTimer()) {
                    SetMatchTime(-1, playTickSfx: false);
                    return;
                }

                var activeSeconds = matchTimer.TimeRemainingSeconds;
                if(activeSeconds > 0) {
                    SetMatchTime(activeSeconds, playTickSfx: false);
                    return;
                }

                var fallbackSeconds = matchSettings != null
                    ? matchSettings.GetMatchDurationSeconds()
                    : defaultMatchSeconds;
                SetMatchTime(Mathf.Max(0, fallbackSeconds), playTickSfx: false);
                return;
            }

            if(matchSettings != null) {
                if(matchSettings.IsPreMatchCountdownEnabled()) {
                    SetMatchTime(Mathf.Max(0, matchSettings.GetPreMatchCountdownSeconds()), playTickSfx: false);
                    return;
                }

                if(matchSettings.IsInfiniteMatchTimer()) {
                    SetMatchTime(-1, playTickSfx: false);
                    return;
                }

                SetMatchTime(Mathf.Max(0, matchSettings.GetMatchDurationSeconds()), playTickSfx: false);
                return;
            }

            SetMatchTime(defaultMatchSeconds, playTickSfx: false);
        }

        private void ShowScoreboard() {
            if(!SessionManager.IsGameplaySceneName(_cachedSceneName)) return;

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
            RefreshHoverStateForCursorMode(force: true);
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
            return _cachedMatchSettings != null &&
                   MatchSettingsManager.IsTeamBasedMode(_cachedMatchSettings.selectedGameModeId);
        }

        /// <summary>
        /// Forces a refresh of the scoreboard title and cached match settings.
        /// Call this when gamemode changes.
        /// </summary>
        private void RefreshGamemode() {
            _cachedMatchSettings = null; // Clear cache to force refresh
            UpdateScoreboardTitle();
        }

        /// <summary>
        /// Updates the scoreboard title to show the current gamemode name.
        /// </summary>
        private void UpdateScoreboardTitle() {
            // Always refresh MatchSettingsManager cache to get latest gamemode
            _cachedMatchSettings = MatchSettingsManager.Instance;

            var gamemodeName = ResolveScoreboardTitle();
            var mapName = ResolveScoreboardMapTitle();

            // Update FFA scoreboard title
            if(_scoreboardTitle != null) {
                _scoreboardTitle.text = gamemodeName;
            }

            if(_scoreboardMapTitle != null) {
                _scoreboardMapTitle.text = mapName;
            }

            // Update TDM scoreboard title
            if(_tdmScoreboardTitle != null) {
                _tdmScoreboardTitle.text = gamemodeName;
            }

            if(_tdmScoreboardMapTitle != null) {
                _tdmScoreboardMapTitle.text = mapName;
            }
        }

        private string ResolveScoreboardTitle() {
            if(_cachedMatchSettings == null) {
                if(_missingGamemodeTitleLogged) return "UNKNOWN MODE";
                Debug.LogError(
                    "[ScoreboardManager] MatchSettingsManager.Instance is null while updating scoreboard title.",
                    this);
                _missingGamemodeTitleLogged = true;

                return "UNKNOWN MODE";
            }

            var selectedGameModeId = _cachedMatchSettings.selectedGameModeId;
            if(string.IsNullOrEmpty(selectedGameModeId)) {
                if(_missingGamemodeTitleLogged) return "UNKNOWN MODE";
                Debug.LogError("[ScoreboardManager] selectedGameModeId is empty while updating scoreboard title.",
                    this);
                _missingGamemodeTitleLogged = true;

                return "UNKNOWN MODE";
            }

            _missingGamemodeTitleLogged = false;
            return selectedGameModeId.ToUpperInvariant();
        }

        private string ResolveScoreboardMapTitle() {
            var sessionManager = SessionManager.Instance;
            if(sessionManager != null && string.IsNullOrWhiteSpace(sessionManager.SelectedMapId) == false) {
                return FormatMapTitle(sessionManager.SelectedMapId);
            }

            if(TryResolveMapIdFromScene(_cachedSceneName, out var mapIdFromScene)) {
                return FormatMapTitle(mapIdFromScene);
            }

            return string.IsNullOrWhiteSpace(_cachedSceneName) == false
                ? FormatMapTitle(_cachedSceneName)
                : "UNKNOWN MAP";
        }

        private static bool TryResolveMapIdFromScene(string sceneName, out string mapId) {
            mapId = string.Empty;
            if(string.IsNullOrWhiteSpace(sceneName)) {
                return false;
            }

            var pool = Resources.Load<MapPoolDefinition>("MatchMapPoolDefinition");
            if(pool == null || pool.Maps == null) {
                return false;
            }

            foreach(var map in pool.Maps) {
                if(map == null || string.IsNullOrWhiteSpace(map.SceneName)) {
                    continue;
                }

                if(string.Equals(map.SceneName, sceneName, StringComparison.OrdinalIgnoreCase) == false) {
                    continue;
                }

                mapId = string.IsNullOrWhiteSpace(map.MapId) ? map.name : map.MapId;
                return string.IsNullOrWhiteSpace(mapId) == false;
            }

            return false;
        }

        private static string FormatMapTitle(string value) {
            return string.IsNullOrWhiteSpace(value) ? "UNKNOWN MAP" : value.Trim().ToUpperInvariant();
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

        private void HideScoreboard() {
            if(!SessionManager.IsGameplaySceneName(_cachedSceneName)) return;

            IsScoreboardVisible = false;
            // Remove inline display style so the hidden class can take effect
            _scoreboardPanel.style.display = StyleKeyword.Null;
            _scoreboardPanel.AddToClassList("hidden");
            _scoreboardPanel.EnableInClassList("scoreboard-hover-disabled", false);
            _hoverDisabledForMouseLook = false;

            // Re-lock mouse and unlock camera when hiding scoreboard
            if(Cursor.lockState == CursorLockMode.None) {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                if(PlayerController.LocalPlayer != null) {
                    PlayerController.LocalPlayer.LockLook = false;
                }
            }

            // Hide context menu if open
            if(InGameContextMenuManager.Instance != null) {
                InGameContextMenuManager.Instance.Hide();
            }
        }

        private void RefreshHoverStateForCursorMode(bool force = false) {
            if(_scoreboardPanel == null) {
                return;
            }

            var shouldDisableHover = Cursor.lockState == CursorLockMode.Locked || !Cursor.visible;
            if(!force && shouldDisableHover == _hoverDisabledForMouseLook) {
                return;
            }

            _hoverDisabledForMouseLook = shouldDisableHover;
            _scoreboardPanel.EnableInClassList("scoreboard-hover-disabled", shouldDisableHover);
        }

        private void UpdateScoreboard() {
            // Skip if UI not initialized yet (happens during early player spawn)
            if(_root == null) {
                return;
            }

            var allControllers = GetAllPlayerControllers();

            // Always check fresh - don't cache game mode
            if(IsTeamBased()) {
                UpdateTdmScoreboard(allControllers);
            } else {
                UpdateFfaScoreboard(allControllers);
            }
        }

        public bool GetLocalPlayerPlacement(out int placement, out int totalPlayers) {
            placement = 0;
            totalPlayers = 0;

            var allControllers = GetAllPlayerControllers();
            totalPlayers = allControllers.Count;
            if(totalPlayers == 0) return false;

            // Sort players just like the scoreboard does
            var isTagMode = IsTagMode();
            var sortedPlayers = BuildSortedPlayerList(allControllers, isTagMode);

            // Find local player index
            var localClientId = NetworkManager.Singleton.LocalClientId;
            for(var i = 0; i < sortedPlayers.Count; i++) {
                if(sortedPlayers[i].OwnerClientId != localClientId) continue;
                placement = i + 1; // 1-based rank
                return true;
            }

            return false;
        }


        private void UpdateFfaScoreboard(IReadOnlyCollection<PlayerController> allControllers) {
            // Null checks for UI elements (only warn if root is initialized but elements are missing)
            if(_scoreboardContainer == null || _tdmScoreboardContainer == null || _playerRows == null) {
                if(_root != null) {
                    Debug.LogWarning("[ScoreboardManager] FFA scoreboard UI elements not initialized");
                }

                return;
            }

            if(!EnsureScoreboardRowTemplateAssigned()) {
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
                var rowCount = 0;

                foreach(var player in sortedPlayers) {
                    if(player == null || !player.IsSpawned) continue;

                    var row = CreatePlayerRow(player, _playerRows, isTagMode: isTagMode);
                    if(row == null) continue;
                    if(rowCount % 2 == 1) row.AddToClassList("player-row-alt");
                    rowCount++;

                    // Cache velocity label for this player (last stat label in the row)
                    var labels = row.Query<Label>().ToList();

                    if(labels.Count > 0) {
                        _cachedVelocityLabels[player.OwnerClientId] = labels[^1];
                    }
                }

                // Pad with empty rows
                while(rowCount < 10) {
                    var row = CreateEmptyRow(_playerRows, isTagMode);
                    if(row == null) break;
                    if(rowCount % 2 == 1) row.AddToClassList("player-row-alt");
                    rowCount++;
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
            // Null checks for UI elements (only warn if root is initialized but elements are missing)
            if(_scoreboardContainer == null || _tdmScoreboardContainer == null || _enemyTeamRows == null || _yourTeamRows == null) {
                if(_root != null) {
                    Debug.LogWarning(
                        "[ScoreboardManager] TDM scoreboard UI elements not initialized, falling back to FFA");
                }

                UpdateFfaScoreboard(allControllers);
                return;
            }

            if(!EnsureScoreboardRowTemplateAssigned()) {
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
            var enemyCount = 0;
            foreach(var player in enemyPlayers) {
                var row = CreatePlayerRow(player, _enemyTeamRows, simplifiedStats: true, isYourTeam: false);
                if(row == null) continue;
                if(enemyCount % 2 == 1) row.AddToClassList("player-row-alt");
                enemyCount++;
            }

            // Pad enemy team
            while(enemyCount < 5) {
                var row = CreateEmptyRow(_enemyTeamRows, isTagMode: false);
                if(row == null) break;
                if(enemyCount % 2 == 1) row.AddToClassList("player-row-alt");
                enemyCount++;
            }

            var yourCount = 0;
            foreach(var player in yourTeamPlayers) {
                var row = CreatePlayerRow(player, _yourTeamRows, simplifiedStats: true, isYourTeam: true);
                if(row == null) continue;
                if(yourCount % 2 == 1) row.AddToClassList("player-row-alt");
                yourCount++;
            }

            // Pad your team
            while(yourCount < 5) {
                var row = CreateEmptyRow(_yourTeamRows, isTagMode: false);
                if(row == null) break;
                if(yourCount % 2 == 1) row.AddToClassList("player-row-alt");
                yourCount++;
            }

            // Update team scores
            // Always refresh MatchSettingsManager cache to get latest gamemode
            _cachedMatchSettings = MatchSettingsManager.Instance;
            var matchSettings = _cachedMatchSettings;
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
            } else if(matchSettings != null && matchSettings.selectedGameModeId == "KOTH" && KingOfTheHillManager.Instance != null) {
                var teamAScore = KingOfTheHillManager.Instance.GetTeamAScore();
                var teamBScore = KingOfTheHillManager.Instance.GetTeamBScore();

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
    }
}

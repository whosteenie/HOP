using System;
using System.Collections;
using System.Collections.Generic;
using Game.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Network.Singletons {
    public class GameMenuManager : MonoBehaviour {
        #region Serialized Fields

        [SerializeField] private UIDocument uiDocument;
        // AudioMixer is now handled by OptionsMenuManager


        [Header("Kill Feed")]
        [SerializeField] private KillFeedManager killFeedManager;

        [Header("Scoreboard")]
        [SerializeField] private ScoreboardManager scoreboardManager;

        [Header("Options")]
        [SerializeField] private OptionsMenuManager optionsMenuManager;

        #endregion

        #region UI Elements - Kill Feed

        private VisualElement _killFeedContainer;

        #endregion

        #region UI Elements - Scoreboard

        // Scoreboard UI elements are now managed by ScoreboardManager

        #endregion

        #region Private Fields

        private VisualElement _pauseMenuPanel;
        private VisualElement _optionsPanel;
        private PlayerController _localController;

        // Options functionality is now handled by OptionsMenuManager component

        private Button _resumeButton;
        private Button _optionsButton;
        private Button _quitButton;

        // Pause menu join code
        private Label _pauseJoinCodeLabel;
        private Button _pauseCopyCodeButton;

        private VisualElement _root;

        // Scoreboard caching is now handled by ScoreboardManager

        // HUD / Match UI
        private VisualElement _hudPanel;
        private VisualElement _matchTimerContainer;

        // Score display
        private VisualElement _leftScoreContainer;
        private VisualElement _rightScoreContainer;
        private Label _leftScoreValue;
        private Label _rightScoreValue;
        private float _lastScoreUpdateTime;
        private const float ScoreUpdateInterval = 0.1f; // Update every 0.1 seconds

        // Post-match state

        // Podium UI
        private VisualElement _podiumContainer;
        private VisualElement _podiumFirstSlot;
        private VisualElement _podiumSecondSlot;
        private VisualElement _podiumThirdSlot;

        private Label _podiumFirstName;
        private Label _podiumSecondName;
        private Label _podiumThirdName;

        private Label _podiumFirstKills;
        private Label _podiumSecondKills;
        private Label _podiumThirdKills;

        // Cache scene name to avoid string allocations
        private string _cachedSceneName;

        #endregion

        #region Properties

        public bool IsPaused { get; private set; }

        public bool IsScoreboardVisible => scoreboardManager != null && scoreboardManager.IsScoreboardVisible;

        public bool IsPostMatch { get; set; }

        public bool IsPreMatch => MatchTimerManager.Instance != null && MatchTimerManager.Instance.IsPreMatch;

        #endregion

        public static GameMenuManager Instance { get; private set; }

        #region Unity Lifecycle

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            // Removed DontDestroyOnLoad - GameMenuManager should be in Game scene only
        }

        private void OnEnable() {
            if(uiDocument == null) {
                Debug.LogError("[GameMenuManager] UIDocument is not assigned!");
                return;
            }

            _root = uiDocument.rootVisualElement;

            // Cache scene name to avoid allocations
            UpdateCachedSceneName();

            // Subscribe to scene changes to update cache
            SceneManager.sceneLoaded += OnSceneLoaded;

            FindUIElements();
            RegisterUIEvents();

            SetupOptionsMenuManager();
            SetupKillFeedManager();
            SetupScoreboardManager();

            // Subscribe to join code updates
            if(SessionManager.Instance != null) {
                SessionManager.Instance.RelayCodeAvailable += OnRelayCodeAvailable;
            }
        }

        private void OnDisable() {
            // Unsubscribe from join code updates
            if(SessionManager.Instance != null) {
                SessionManager.Instance.RelayCodeAvailable -= OnRelayCodeAvailable;
            }

            // Unsubscribe from scene changes
            SceneManager.sceneLoaded -= OnSceneLoaded;
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

        private void OnRelayCodeAvailable(string joinCode) {
            // Update join code display if pause menu is visible
            if(IsPaused && _pauseJoinCodeLabel != null) {
                _pauseJoinCodeLabel.text = $"Join Code: {joinCode}";
                if(_pauseCopyCodeButton != null) {
                    _pauseCopyCodeButton.SetEnabled(true);
                }
            }
        }

        private void Update() {
            if(_localController == null && _cachedSceneName == "Game") {
                FindLocalController();
            }
            
            // Update score display periodically
            if(_cachedSceneName == "Game" && Time.time - _lastScoreUpdateTime >= ScoreUpdateInterval) {
                UpdateScoreDisplay();
                _lastScoreUpdateTime = Time.time;
            }
        }

        private void FindLocalController() {
            var allControllers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
            foreach(var controller in allControllers) {
                if(controller.IsOwner) {
                    _localController = controller.GetComponent<PlayerController>();
                    break;
                }
            }
        }

        private void FindUIElements() {
            // Get panels
            _pauseMenuPanel = _root.Q<VisualElement>("pause-menu-panel");
            _optionsPanel = _root.Q<VisualElement>("options-panel");

            _resumeButton = _root.Q<Button>("resume-button");
            _optionsButton = _root.Q<Button>("options-button");
            _quitButton = _root.Q<Button>("quit-button");

            // Pause menu join code
            _pauseJoinCodeLabel = _root.Q<Label>("pause-join-code-label");
            _pauseCopyCodeButton = _root.Q<Button>("pause-copy-code-button");

            // Kill Feed
            _killFeedContainer = _root.Q<VisualElement>("kill-feed-container");

            // HUD root + containers
            _hudPanel = _root.Q<VisualElement>("hud-panel");
            _matchTimerContainer = _root.Q<VisualElement>("match-timer-container");
            
            // Score display
            _leftScoreContainer = _root.Q<VisualElement>("left-score-container");
            _rightScoreContainer = _root.Q<VisualElement>("right-score-container");
            _leftScoreValue = _root.Q<Label>("left-score-value");
            _rightScoreValue = _root.Q<Label>("right-score-value");

            // Podium nameplates
            _podiumContainer = _root.Q<VisualElement>("podium-nameplates-container");
            _podiumFirstSlot = _root.Q<VisualElement>("podium-first-slot");
            _podiumSecondSlot = _root.Q<VisualElement>("podium-second-slot");
            _podiumThirdSlot = _root.Q<VisualElement>("podium-third-slot");

            _podiumFirstName = _root.Q<Label>("podium-first-name");
            _podiumSecondName = _root.Q<Label>("podium-second-name");
            _podiumThirdName = _root.Q<Label>("podium-third-name");

            _podiumFirstKills = _root.Q<Label>("podium-first-kills");
            _podiumSecondKills = _root.Q<Label>("podium-second-kills");
            _podiumThirdKills = _root.Q<Label>("podium-third-kills");
        }

        private void RegisterUIEvents() {
            // Setup main menu buttons
            _resumeButton.clicked += () => {
                OnButtonClicked();
                ResumeGame();
            };
            _resumeButton.RegisterCallback<MouseOverEvent>(MouseHover);

            _optionsButton.clicked += () => {
                OnButtonClicked();
                ShowOptions();
            };
            _optionsButton.RegisterCallback<MouseOverEvent>(MouseHover);

            _quitButton.clicked += () => {
                OnButtonClicked(true);
                QuitToMenu();
            };
            _quitButton.RegisterCallback<MouseOverEvent>(MouseHover);

            // Setup pause menu copy button
            if(_pauseCopyCodeButton != null) {
                _pauseCopyCodeButton.clicked += CopyPauseJoinCodeToClipboard;
                _pauseCopyCodeButton.RegisterCallback<MouseEnterEvent>(MouseHover);
            }
        }

        private void OnButtonClicked(bool isBack = false) {
            if(SoundFXManager.Instance != null) {
                var soundKey = !isBack ? SfxKey.ButtonClick : SfxKey.BackButton;
                SoundFXManager.Instance.PlayUISound(soundKey);
            }
        }

        private void MouseEnter(MouseEnterEvent evt) {
            if(SoundFXManager.Instance != null) {
                SoundFXManager.Instance.PlayUISound(SfxKey.ButtonHover);
            }
        }

        private void MouseHover(MouseOverEvent evt) {
            if(SoundFXManager.Instance != null) {
                SoundFXManager.Instance.PlayUISound(SfxKey.ButtonHover);
            }
        }

        private void MouseHover(MouseEnterEvent evt) {
            if(SoundFXManager.Instance != null) {
                SoundFXManager.Instance.PlayUISound(SfxKey.ButtonHover);
            }
        }

        public void TogglePause() {
            // Only allow pausing in Game scene
            if(_cachedSceneName == null || !_cachedSceneName.Contains("Game")) return;

            if(IsPaused) {
                if(!_optionsPanel.ClassListContains("hidden")) {
                    HideOptions();
                } else {
                    ResumeGame();
                }
            } else {
                PauseGame();
            }
        }

        #endregion

        #region Setup Methods

        private void SetupOptionsMenuManager() {
            if(optionsMenuManager == null) {
                Debug.LogError("[GameMenuManager] OptionsMenuManager not assigned!");
                return;
            }

            // Set up callbacks
            optionsMenuManager.OnButtonClickedCallback = OnButtonClicked;
            optionsMenuManager.MouseEnterCallback = MouseEnter;
            optionsMenuManager.MouseHoverCallback = MouseHover;
            optionsMenuManager.OnBackFromOptionsCallback = HideOptions;

            // Initialize the options menu manager
            optionsMenuManager.Initialize();
        }

        private void SetupKillFeedManager() {
            if(killFeedManager == null) {
                Debug.LogError("[GameMenuManager] KillFeedManager not assigned!");
                return;
            }

            // Initialize kill feed manager with the container
            killFeedManager.Initialize(_killFeedContainer);
        }

        private void SetupScoreboardManager() {
            if(scoreboardManager == null) {
                Debug.LogError("[GameMenuManager] ScoreboardManager not assigned!");
                return;
            }

            // Initialize scoreboard manager with the root
            scoreboardManager.Initialize(_root);
        }

        #endregion

        #region Menu Navigation

        private void PauseGame() {
            IsPaused = true;
            _pauseMenuPanel.RemoveFromClassList("hidden");
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            // Update join code display when pausing
            UpdatePauseJoinCodeDisplay();

            if(_localController) {
                _localController.moveInput = Vector2.zero;
            }
        }

        private void UpdatePauseJoinCodeDisplay() {
            if(_pauseJoinCodeLabel == null) return;

            // Try to get join code from SessionManager
            var sessionManager = SessionManager.Instance;
            if(sessionManager?.ActiveSession != null) {
                // Try to get relay code from session properties first
                string joinCode = null;
                if(sessionManager.ActiveSession.Properties.TryGetValue("relayCode", out var prop) &&
                   !string.IsNullOrEmpty(prop.Value)) {
                    joinCode = prop.Value;
                } else if(!string.IsNullOrEmpty(sessionManager.ActiveSession.Code)) {
                    // Fallback to UGS session code
                    joinCode = sessionManager.ActiveSession.Code;
                }

                if(!string.IsNullOrEmpty(joinCode)) {
                    _pauseJoinCodeLabel.text = $"Join Code: {joinCode}";
                    if(_pauseCopyCodeButton != null) {
                        _pauseCopyCodeButton.SetEnabled(true);
                    }
                } else {
                    _pauseJoinCodeLabel.text = "Join Code: - - - - - -";
                    if(_pauseCopyCodeButton != null) {
                        _pauseCopyCodeButton.SetEnabled(false);
                    }
                }
            } else {
                _pauseJoinCodeLabel.text = "Join Code: - - - - - -";
                if(_pauseCopyCodeButton != null) {
                    _pauseCopyCodeButton.SetEnabled(false);
                }
            }
        }

        private void CopyPauseJoinCodeToClipboard() {
            OnButtonClicked();
            if(_pauseJoinCodeLabel == null) return;

            // Extract code from "Join Code: ABC123" → "ABC123"
            var fullText = _pauseJoinCodeLabel.text;
            var code = fullText.Replace("Join Code: ", "").Trim();

            if(string.IsNullOrEmpty(code) || code == "- - - - - -") {
                return; // No code to copy
            }

            GUIUtility.systemCopyBuffer = code;
            Debug.Log($"[GameMenuManager] Copied join code to clipboard: {code}");
        }

        private void ResumeGame() {
            IsPaused = false;
            _pauseMenuPanel.AddToClassList("hidden");
            _optionsPanel.AddToClassList("hidden");
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;
            UnityEngine.Cursor.visible = false;
        }

        private void ShowOptions() {
            if(_cachedSceneName != "Game") return;
            if(optionsMenuManager != null) {
                optionsMenuManager.LoadSettings();
                optionsMenuManager.OnOptionsPanelShown();
            }

            _pauseMenuPanel.AddToClassList("hidden");
            _optionsPanel.RemoveFromClassList("hidden");
        }

        private void HideOptions() {
            if(_cachedSceneName != "Game") return;
            _optionsPanel.AddToClassList("hidden");
            _pauseMenuPanel.RemoveFromClassList("hidden");
        }

        private async void QuitToMenu() {
            try {
                await SessionManager.Instance.LeaveToMainMenuAsync();

                var root = uiDocument.rootVisualElement;
                var rootContainer = root.Q<VisualElement>("root-container");
                rootContainer.style.display = DisplayStyle.None;
                _pauseMenuPanel.AddToClassList("hidden");
                _optionsPanel.AddToClassList("hidden");
                IsPaused = false;
                UnityEngine.Cursor.lockState = CursorLockMode.None;
                UnityEngine.Cursor.visible = true;
            } catch(Exception e) {
                Debug.LogException(e);
            }
        }

        #endregion

        #region Match Flow / Post-Match

        /// <summary>
        /// Called when the server-authoritative match timer hits 0.
        /// Hides in-game HUD and uses SceneTransitionManager to fade to black.
        /// </summary>
        public void EnterPostMatch() {
            if(IsPostMatch)
                return;

            IsPostMatch = true;

            HUDManager.Instance.HideHUD();
            HideInGameHudForPostMatch();
        }

        /// <summary>
        /// Hides only the in-game HUD elements, but leaves pause/scoreboard usable.
        /// </summary>
        private void HideInGameHudForPostMatch() {
            // If for any reason the whole HUD panel is null, bail gracefully.
            if(_hudPanel == null)
                return;

            // Hide individual HUD elements
            if(killFeedManager != null)
                killFeedManager.HideKillFeed();
            if(_matchTimerContainer != null)
                _matchTimerContainer.style.display = DisplayStyle.None;
            // Hide score display
            if(_leftScoreContainer != null)
                _leftScoreContainer.style.display = DisplayStyle.None;
            if(_rightScoreContainer != null)
                _rightScoreContainer.style.display = DisplayStyle.None;
        }

        public void ShowInGameHudAfterPostMatch() {
            // If for any reason the whole HUD panel is null, bail gracefully.
            if(_hudPanel == null)
                return;

            // Show individual HUD elements
            if(killFeedManager != null)
                killFeedManager.ShowKillFeed();
            if(_matchTimerContainer != null)
                _matchTimerContainer.style.display = DisplayStyle.Flex;
            // Show score display
            if(_leftScoreContainer != null)
                _leftScoreContainer.style.display = DisplayStyle.Flex;
            if(_rightScoreContainer != null)
                _rightScoreContainer.style.display = DisplayStyle.Flex;

            _podiumContainer.style.display = DisplayStyle.None;
        }

        #endregion

        public void SetPodiumSlots(
            string firstName, int firstKills,
            string secondName, int secondKills,
            string thirdName, int thirdKills) {
            if(_podiumContainer == null)
                return;

            // Show the container as soon as we have data
            _podiumContainer.style.display = DisplayStyle.Flex;

            // Allow pointer events to pass through the container so pause menu is clickable
            // Only the actual podium slots should capture pointer events
            _podiumContainer.pickingMode = PickingMode.Ignore;

            SetPodiumSlot(_podiumFirstSlot, _podiumFirstName, _podiumFirstKills, firstName, firstKills);
            SetPodiumSlot(_podiumSecondSlot, _podiumSecondName, _podiumSecondKills, secondName, secondKills);
            SetPodiumSlot(_podiumThirdSlot, _podiumThirdName, _podiumThirdKills, thirdName, thirdKills);
        }

        private void SetPodiumSlot(
            VisualElement slotRoot,
            Label nameLabel,
            Label killsLabel,
            string playerName,
            int kills) {
            if(slotRoot == null || nameLabel == null || killsLabel == null)
                return;

            var hasPlayer = !string.IsNullOrEmpty(playerName);

            slotRoot.style.display = hasPlayer ? DisplayStyle.Flex : DisplayStyle.None;
            nameLabel.text = hasPlayer ? playerName : "---";
            killsLabel.text = hasPlayer ? kills.ToString() : "0";
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
            
            bool isTeamBased = IsTeamBasedMode(matchSettings.selectedGameModeId);
            
            if(isTeamBased) {
                UpdateTeamBasedScore();
            } else {
                UpdateFfaScore();
            }
        }
        
        /// <summary>
        /// Checks if a game mode is team-based.
        /// </summary>
        private bool IsTeamBasedMode(string modeId) => modeId switch {
            "Team Deathmatch" => true,
            "Hopball" => true,
            "CTF" => true,
            "Oddball" => true,
            "KOTH" => true,
            _ => false
        };
        
        /// <summary>
        /// Updates score display for team-based modes.
        /// </summary>
        private void UpdateTeamBasedScore() {
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null) return;
            
            int yourScore = 0;
            int enemyScore = 0;
            
            // Get local player's team
            var localPlayer = NetworkManager.Singleton?.LocalClient?.PlayerObject;
            if(localPlayer == null) return;
            
            var localTeamMgr = localPlayer.GetComponent<PlayerTeamManager>();
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
                var allControllers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
                int yourTeamKills = 0;
                int enemyTeamKills = 0;
                
                foreach(var player in allControllers) {
                    if(player == null || !player.IsSpawned) continue;
                    var teamMgr = player.GetComponent<PlayerTeamManager>();
                    if(teamMgr == null) continue;
                    
                    if(teamMgr.netTeam.Value == localTeam) {
                        yourTeamKills += player.kills.Value;
                    } else {
                        enemyTeamKills += player.kills.Value;
                    }
                }
                
                yourScore = yourTeamKills;
                enemyScore = enemyTeamKills;
            }
            
            _leftScoreValue.text = yourScore.ToString();
            _rightScoreValue.text = enemyScore.ToString();
        }
        
        /// <summary>
        /// Updates score display for FFA modes (Deathmatch, Gun Tag, etc.).
        /// </summary>
        private void UpdateFfaScore() {
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null) return;
            
            bool isTagMode = matchSettings.selectedGameModeId == "Gun Tag";
            
            // Get local player
            if(_localController == null) {
                FindLocalController();
            }
            
            if(_localController == null) return;
            
            // Get local player's score
            int localScore;
            if(isTagMode) {
                var tagCtrl = _localController.GetComponent<PlayerTagController>();
                localScore = tagCtrl != null ? tagCtrl.timeTagged.Value : int.MaxValue;
            } else {
                localScore = _localController.kills.Value;
            }
            
            // Get all players and find the next highest (or highest if local is not first)
            var allControllers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
            var sortedPlayers = new List<(PlayerController player, int score)>();
            
            foreach(var player in allControllers) {
                if(player == null || !player.IsSpawned) continue;
                
                int score;
                if(isTagMode) {
                    var tagCtrl = player.GetComponent<PlayerTagController>();
                    score = tagCtrl != null ? tagCtrl.timeTagged.Value : int.MaxValue;
                } else {
                    score = player.kills.Value;
                }
                
                sortedPlayers.Add((player, score));
            }
            
            // Sort based on mode
            if(isTagMode) {
                // Gun Tag: sort by time tagged ascending (lowest is best)
                sortedPlayers.Sort((a, b) => a.score.CompareTo(b.score));
            } else {
                // Deathmatch: sort by kills descending (highest is best)
                sortedPlayers.Sort((a, b) => b.score.CompareTo(a.score));
            }
            
            // Find next highest/lowest score
            int nextScore = 0;
            bool foundNext = false;
            
            if(isTagMode) {
                // For Gun Tag, find next LOWEST (or lowest if local is lowest)
                for(int i = 0; i < sortedPlayers.Count; i++) {
                    if(sortedPlayers[i].player == _localController) {
                        // If we're the lowest (1st place), show the next lowest (2nd place)
                        if(i == 0) {
                            // Show 2nd place (next lowest)
                            if(sortedPlayers.Count > 1) {
                                nextScore = sortedPlayers[1].score;
                            } else {
                                nextScore = 0; // Only one player
                            }
                            foundNext = true;
                            break;
                        }
                        // Otherwise show the lowest (1st place)
                        nextScore = sortedPlayers[0].score;
                        foundNext = true;
                        break;
                    }
                }
            } else {
                // For Deathmatch, find next HIGHEST (or highest if local is highest)
                for(int i = 0; i < sortedPlayers.Count; i++) {
                    if(sortedPlayers[i].player == _localController) {
                        // If we're the highest (1st place), show the next highest (2nd place)
                        if(i == 0) {
                            // Show 2nd place (next highest)
                            if(sortedPlayers.Count > 1) {
                                nextScore = sortedPlayers[1].score;
                            } else {
                                nextScore = 0; // Only one player
                            }
                            foundNext = true;
                            break;
                        }
                        // Otherwise show the highest (1st place)
                        nextScore = sortedPlayers[0].score;
                        foundNext = true;
                        break;
                    }
                }
            }
            
            if(!foundNext && sortedPlayers.Count > 0) {
                // Fallback: use first place score
                nextScore = sortedPlayers[0].score;
            }
            
            _leftScoreValue.text = localScore.ToString();
            _rightScoreValue.text = nextScore.ToString();
        }
    }
}
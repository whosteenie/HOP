using System;
using Game.Player;
using Game.Progression;
using Game.UI;
using Game.Match;
using Network;
using Network.Events;
using Network.Services;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Game.Menu {
    public class GameMenuManager : MonoBehaviour {
        #region Serialized Fields

        [SerializeField] private UIDocument uiDocument;

        [Header("Kill Feed")]
        [SerializeField] private KillFeedManager killFeedManager;

        [Header("Scoreboard")]
        [SerializeField] private ScoreboardManager scoreboardManager;

        [Header("Options")]
        [SerializeField] private OptionsMenuManager optionsMenuManager;

        [Header("Sniper Overlay")]
        [SerializeField] private SniperOverlayManager sniperOverlayManager;

        #endregion

        #region UI Elements - Kill Feed

        private VisualElement _killFeedContainer;

        #endregion

        #region Private Fields

        private VisualElement _pauseMenuPanel;
        private VisualElement _optionsPanel;
        private PlayerController _localController;
        private Button _resumeButton;
        private Button _optionsButton;
        private Button _quitButton;

        // Pause menu join code
        private Label _pauseJoinCodeLabel;
        private Button _pauseCopyCodeButton;

        private VisualElement _root;

        // Cache scene name to avoid string allocations
        private string _cachedSceneName;

        // Pause menu challenge cards
        private VisualElement _pauseChallengesContainer;
        private VisualElement _dailyChallengesCard;
        private VisualElement _weeklyChallengesCard;
        private readonly Color _progressBarColor = new(1f, 0.392f, 0.392f); // #ff6464

        #endregion

        #region Properties

        public bool IsPaused { get; private set; }

        public bool IsPostMatch { get; set; }

        public static bool IsPreMatch => MatchTimerManager.Instance != null && MatchTimerManager.Instance.IsPreMatch;

        #endregion

        public static GameMenuManager Instance { get; private set; }

        #region Unity Lifecycle

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
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
            SetupSniperOverlayManager();

            // Subscribe to EventBus events
            EventBus.Subscribe<RelayCodeAvailableEvent>(OnRelayCodeAvailable);
            
            // Keep legacy subscription for backward compatibility during migration
            if(SessionManager.Instance != null) {
                SessionManager.Instance.RelayCodeAvailable += OnRelayCodeAvailableLegacy;
            }
        }

        private void OnDisable() {
            // Unsubscribe from EventBus events
            EventBus.Unsubscribe<RelayCodeAvailableEvent>(OnRelayCodeAvailable);
            
            // Unsubscribe from legacy events
            if(SessionManager.Instance != null) {
                SessionManager.Instance.RelayCodeAvailable -= OnRelayCodeAvailableLegacy;
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

        #region Event Handlers

        private void OnRelayCodeAvailable(RelayCodeAvailableEvent evt) {
            // Update join code display if pause menu is visible
            if(!IsPaused || _pauseJoinCodeLabel == null) return;
            JoinCodeService.UpdateJoinCodeDisplay(_pauseJoinCodeLabel, _pauseCopyCodeButton, evt.Code);
        }

        #endregion

        #region Legacy Event Handlers (for backward compatibility)

        private void OnRelayCodeAvailableLegacy(string joinCode) {
            // Update join code display if pause menu is visible
            if(!IsPaused || _pauseJoinCodeLabel == null) return;
            JoinCodeService.UpdateJoinCodeDisplay(_pauseJoinCodeLabel, _pauseCopyCodeButton, joinCode);
        }

        #endregion

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
            
            // Build challenge cards for pause menu
            BuildPauseChallengeCards();
        }

        private void Update() {
            // Real-time challenge updates while paused
            if (IsPaused && _pauseChallengesContainer != null) {
                UpdatePauseChallenges();
            }
        }

        private void BuildPauseChallengeCards() {
            if (_pauseMenuPanel == null) return;

            // Create container for challenges (horizontal layout around pause card)
            _pauseChallengesContainer = new VisualElement {
                name = "pause-challenges-container",
                style = {
                    position = Position.Absolute,
                    left = 0,
                    right = 0,
                    top = 0,
                    bottom = 0,
                    flexDirection = FlexDirection.Row,
                    justifyContent = Justify.Center,
                    alignItems = Align.Center
                }
            };
            
            // Daily Challenges Card (left side)
            _dailyChallengesCard = CreateChallengeCard("Daily Challenges");
            _dailyChallengesCard.style.marginRight = 50; // More spacing from pause menu
            
            // Spacer for the existing pause card (we don't move it)
            var spacer = new VisualElement {
                style = {
                    width = 420, // Match pause menu .menu-container width
                    height = 10
                }
            };
            
            // Weekly Challenges Card (right side)
            _weeklyChallengesCard = CreateChallengeCard("Weekly Challenges");
            _weeklyChallengesCard.style.marginLeft = 50; // More spacing from pause menu

            _pauseChallengesContainer.Add(_dailyChallengesCard);
            _pauseChallengesContainer.Add(spacer);
            _pauseChallengesContainer.Add(_weeklyChallengesCard);
            
            // Add before pause menu panel so it's behind it
            _pauseMenuPanel.parent.Insert(0, _pauseChallengesContainer);
            _pauseChallengesContainer.AddToClassList("hidden");
        }

        private VisualElement CreateChallengeCard(string title) {
            // Match HOP pause menu style from GameMenu.uss
            // .menu-container uses: rgba(12, 12, 18, 0.85) bg, rgba(200, 60, 60, 0.4) border
            var card = new VisualElement {
                style = {
                    width = 280,
                    minHeight = 180,
                    backgroundColor = new Color(12f/255f, 12f/255f, 18f/255f, 0.85f), // rgba(12,12,18,0.85)
                    borderTopLeftRadius = 4,
                    borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4,
                    borderBottomRightRadius = 4,
                    borderTopWidth = 1,
                    borderBottomWidth = 1,
                    borderLeftWidth = 1,
                    borderRightWidth = 1,
                    borderTopColor = new Color(200f/255f, 60f/255f, 60f/255f, 0.4f), // rgba(200,60,60,0.4)
                    borderBottomColor = new Color(200f/255f, 60f/255f, 60f/255f, 0.4f),
                    borderLeftColor = new Color(200f/255f, 60f/255f, 60f/255f, 0.4f),
                    borderRightColor = new Color(200f/255f, 60f/255f, 60f/255f, 0.4f),
                    paddingTop = 16,
                    paddingBottom = 16,
                    paddingLeft = 18,
                    paddingRight = 18
                }
            };
            
            var titleLabel = new Label(title) {
                style = {
                    fontSize = 14,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    color = Color.white,
                    marginBottom = 12,
                    unityTextAlign = TextAnchor.MiddleCenter
                }
            };
            card.Add(titleLabel);
            
            // Separator line (red accent matching border)
            var separator = new VisualElement {
                style = {
                    height = 1,
                    backgroundColor = new Color(200f/255f, 60f/255f, 60f/255f, 0.6f),
                    marginBottom = 12
                }
            };
            card.Add(separator);
            
            var listContainer = new VisualElement {
                name = "challenge-list"
            };
            card.Add(listContainer);
            
            return card;
        }

        private void UpdatePauseChallenges() {
            var pm = ProgressionManager.Instance;
            if (pm == null || pm.Data == null) return;

            RenderChallengeList(_dailyChallengesCard, pm.Data.dailyChallenges);
            RenderChallengeList(_weeklyChallengesCard, pm.Data.weeklyChallenges);
        }

        private void RenderChallengeList(VisualElement card, System.Collections.Generic.List<ActiveChallengeData> challenges) {
            if (card == null) return;
            
            var list = card.Q<VisualElement>("challenge-list");
            if (list == null) return;
            
            list.Clear();
            if (challenges == null) return;
            
            var pm = ProgressionManager.Instance;
            if (pm == null) return;

            foreach (var activeChallenge in challenges) {
                var def = pm.GetChallengeDefinition(activeChallenge.challengeID);
                if (def == null) continue;
                    
                var row = new VisualElement {
                    style = {
                        marginBottom = 12,
                        paddingBottom = 8,
                        borderBottomWidth = 1,
                        borderBottomColor = new Color(200f/255f, 60f/255f, 60f/255f, 0.15f)
                    }
                };

                var progress = activeChallenge.currentProgress;
                var target = activeChallenge.targetProgress;
                if (progress > target) progress = target;
                    
                // Format description, handling dynamic filterID for play_matches_of etc.
                string descText = def.Description;
                try {
                    // If filterID is set, use it as {1} in the format string
                    if (!string.IsNullOrEmpty(activeChallenge.filterID)) {
                        var displayFilter = pm.GetFilterDisplayName(activeChallenge.filterID);
                        descText = string.Format(def.Description, target, displayFilter);
                    } else {
                        descText = string.Format(def.Description, target);
                    }
                } catch {
                    // Fallback
                }

                // Title row: Description (progress/target) ... +XP
                var titleRow = new VisualElement {
                    style = {
                        flexDirection = FlexDirection.Row,
                        justifyContent = Justify.SpaceBetween,
                        alignItems = Align.FlexStart,
                        marginBottom = 4
                    }
                };
                
                // Description with progress inline
                var titleLabel = new Label($"{descText} ({progress}/{target})") {
                    style = {
                        fontSize = 11,
                        color = new Color(0.9f, 0.9f, 0.9f),
                        whiteSpace = WhiteSpace.Normal,
                        flexShrink = 1
                    }
                };
                titleRow.Add(titleLabel);
                
                var xpLabel = new Label($"+{activeChallenge.xpReward}") {
                    style = {
                        fontSize = 10,
                        color = new Color(0.5f, 0.8f, 0.5f),
                        flexShrink = 0,
                        marginLeft = 8
                    }
                };
                titleRow.Add(xpLabel);
                
                row.Add(titleRow);
                    
                var progressBar = new ProgressBar {
                    lowValue = 0,
                    highValue = target,
                    value = progress,
                    style = {
                        height = 5
                    }
                };
                StyleProgressBar(progressBar);
                row.Add(progressBar);
                    
                list.Add(row);
            }
        }

        private void StyleProgressBar(ProgressBar bar) {
            bar.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f);
            bar.style.borderTopWidth = 0;
            bar.style.borderBottomWidth = 0;
            bar.style.borderLeftWidth = 0;
            bar.style.borderRightWidth = 0;
            var fill = bar.Q<VisualElement>(className: "unity-progress-bar__progress");
            if (fill != null) {
                fill.style.backgroundColor = _progressBarColor;
            }
        }

        private void RegisterUIEvents() {
            // Setup main menu buttons
            _resumeButton.clicked += () => {
                UISoundService.PlayButtonClick();
                ResumeGame();
            };
            UISoundService.RegisterButtonHover(_resumeButton);

            _optionsButton.clicked += () => {
                UISoundService.PlayButtonClick();
                ShowOptions();
            };
            UISoundService.RegisterButtonHover(_optionsButton);

            _quitButton.clicked += () => {
                UISoundService.PlayButtonClick(isBack: true);
                QuitToMenu();
            };
            UISoundService.RegisterButtonHover(_quitButton);

            // Setup pause menu copy button
            if(_pauseCopyCodeButton == null) return;
            _pauseCopyCodeButton.clicked += CopyPauseJoinCodeToClipboard;
            UISoundService.RegisterButtonHover(_pauseCopyCodeButton);
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

            // Set up callbacks using UISoundService
            optionsMenuManager.OnButtonClickedCallback = UISoundService.PlayButtonClick;
            optionsMenuManager.MouseEnterCallback = _ => UISoundService.PlayButtonHover();
            optionsMenuManager.MouseHoverCallback = _ => UISoundService.PlayButtonHover();
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

        private void SetupSniperOverlayManager() {
            if(sniperOverlayManager == null) {
                Debug.LogError("[GameMenuManager] SniperOverlayManager not assigned!");
                return;
            }

            // Initialize sniper overlay manager with the root
            sniperOverlayManager.Initialize(_root);
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

            // Show challenge cards
            if (_pauseChallengesContainer != null) {
                _pauseChallengesContainer.RemoveFromClassList("hidden");
            }

            if(_localController) {
                _localController.moveInput = Vector2.zero;
            }
        }

        private void UpdatePauseJoinCodeDisplay() {
            if(_pauseJoinCodeLabel == null) return;

            // Try to get join code from SessionManager
            var sessionManager = SessionManager.Instance;
            string joinCode = null;

            if(sessionManager != null && sessionManager.ActiveSession != null) {
                // Try to get relay code from session properties first
                if(sessionManager.ActiveSession.Properties.TryGetValue("relayCode", out var prop) &&
                   !string.IsNullOrEmpty(prop.Value)) {
                    joinCode = prop.Value;
                } else if(!string.IsNullOrEmpty(sessionManager.ActiveSession.Code)) {
                    // Fallback to UGS session code
                    joinCode = sessionManager.ActiveSession.Code;
                }
            }

            JoinCodeService.UpdateJoinCodeDisplay(_pauseJoinCodeLabel, _pauseCopyCodeButton, joinCode);
        }

        private void CopyPauseJoinCodeToClipboard() {
            UISoundService.PlayButtonClick();
            JoinCodeService.CopyFromLabel(_pauseJoinCodeLabel);
        }

        private void ResumeGame() {
            IsPaused = false;
            _pauseMenuPanel.AddToClassList("hidden");
            _optionsPanel.AddToClassList("hidden");
            
            // Hide challenge cards
            if (_pauseChallengesContainer != null) {
                _pauseChallengesContainer.AddToClassList("hidden");
            }
            
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
                // Save progression before quitting
                if (Game.Progression.ProgressionManager.Instance != null) {
                    Game.Progression.ProgressionManager.Instance.SaveData();
                }

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
    }
}
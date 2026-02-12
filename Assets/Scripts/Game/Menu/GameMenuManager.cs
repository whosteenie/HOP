using System;
using System.Collections.Generic;
using Discord;
using Game.Player;
using Game.Progression;
using Game.UI;
using Game.Match;
using Network;
using Network.Services;
using Network.Steam;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Game.Menu {
    public class GameMenuManager : UIElementBase {
        #region Serialized Fields

        [Header("Kill Feed")]
        [SerializeField] private KillFeedManager killFeedManager;

        [Header("Scoreboard")]
        [SerializeField] private ScoreboardManager scoreboardManager;

        [Header("Options")]
        [SerializeField] private OptionsMenuManager optionsMenuManager;

        [Header("Sniper Overlay")]
        [SerializeField] private SniperOverlayManager sniperOverlayManager;
        
        [Header("Social")]
        [SerializeField] private ChatUIManager chatUIManager;
        [SerializeField] private VoiceOverlayManager voiceOverlayManager;

        [Header("UI Templates")]
        [SerializeField] private VisualTreeAsset challengeCardTemplate;
        [SerializeField] private VisualTreeAsset challengeRowTemplate;

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

        // Quit confirmation modal
        private VisualElement _quitConfirmationModal;
        private Button _quitConfirmationYes;
        private Button _quitConfirmationNo;

        // Pause menu join code
        private Label _pauseJoinCodeLabel;
        private Button _pauseCopyCodeButton; // Will be "Invite" button

        private string _cachedSceneName;
        private VisualElement _matchTimerContainer;
        private VisualElement _pauseChallengesContainer;
        private VisualElement _pauseDailyCardSlot;
        private VisualElement _pauseWeeklyCardSlot;
        private VisualElement _dailyChallengesCard;
        private VisualElement _dailyChallengeList;
        private Label _dailyTimerLabel;
        private VisualElement _weeklyChallengesCard;
        private VisualElement _weeklyChallengeList;
        private Label _weeklyTimerLabel;
        private UIModalHost _modalHost;
        private bool _challengeCardTemplateErrorLogged;
        private bool _challengeRowTemplateErrorLogged;
        private bool _pauseChallengesDirty = true;
        private bool? _cachedOfflineState;
        private float _nextChallengeTimerUpdateAt;
        private ProgressionManager _progressionManager;
        private const float ChallengeTimerUpdateIntervalSeconds = 1f;

        #endregion

        #region Properties

        public bool IsPaused { get; private set; }
        public bool IsChatOpen => chatUIManager != null && chatUIManager.IsChatOpen;
        public bool IsPostMatch { get; set; }
        public static bool IsPreMatch => MatchTimerManager.Instance != null && MatchTimerManager.Instance.IsPreMatch;

        #endregion

        public static GameMenuManager Instance { get; private set; }

        #region Unity Lifecycle

        protected override void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            base.Awake();
        }

        protected override void Start() {
            base.Start();
            if(DiscordManager.Instance == null) return;
            var mode = "Deathmatch";
            if(MatchSettingsManager.Instance != null) {
                mode = MatchSettingsManager.Instance.selectedGameModeId;
                if(string.IsNullOrEmpty(mode)) mode = "Deathmatch";
            }
            DiscordManager.Instance.SetStatus("Playing " + mode, "In Match", 
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        protected override void OnEnable() {
            base.OnEnable();
            UpdateCachedSceneName();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            BindProgressionEvents();
        }

        protected override void OnDisable() {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            UnbindProgressionEvents();
            base.OnDisable();
        }

        protected override void OnInitialize() {
            FindUIElements();
            if(_modalHost == null && Root != null) {
                _modalHost = new UIModalHost(this, Root);
            }
            RegisterUIEvents();
            
            SetupOptionsMenuManager();
            SetupKillFeedManager();
            SetupScoreboardManager();
            SetupSniperOverlayManager();
            SetupSocialUI();
            BindProgressionEvents();
            
            // Clear chat history when initializing (new match)
            if (chatUIManager != null) {
                chatUIManager.ClearChatHistory();
            }

            // Restore HUD/post-match visibility only after UI Toolkit root has been initialized.
            if(_cachedSceneName != null && _cachedSceneName.Contains("Game")) {
                RestoreHudForMatchStart();
            }

            SetPauseChallengesDirty();
            UpdateChallengeTimers(force: true);
            _nextChallengeTimerUpdateAt = Time.unscaledTime + ChallengeTimerUpdateIntervalSeconds;
        }

        protected override Dictionary<string, Type> GetRequiredElements() {
            return new Dictionary<string, Type> {
                { "pause-menu-panel", typeof(VisualElement) },
                { "pause-challenges-container", typeof(VisualElement) },
                { "pause-daily-card-slot", typeof(VisualElement) },
                { "pause-weekly-card-slot", typeof(VisualElement) },
                { "resume-button", typeof(Button) },
                { "options-button", typeof(Button) },
                { "quit-button", typeof(Button) }
            };
        }
        
        #endregion

        private void UpdateCachedSceneName() {
            var activeScene = SceneManager.GetActiveScene();
            if(activeScene.IsValid()) _cachedSceneName = activeScene.name;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            UpdateCachedSceneName();
            
            // When loading a game scene, ensure pause menu and challenges are hidden
            if(_cachedSceneName == null || !_cachedSceneName.Contains("Game")) return;
            // Reset pause state
            IsPaused = false;
                
            // Hide pause menu
            if(_pauseMenuPanel != null) {
                _pauseMenuPanel.AddToClassList("hidden");
            }
                
            // Hide challenges container
            if(_pauseChallengesContainer != null) {
                _pauseChallengesContainer.AddToClassList("hidden");
            }
                
            // Hide options panel
            if(_optionsPanel != null) {
                _optionsPanel.AddToClassList("hidden");
            }

            // Restore timer visibility for each fresh game scene load.
            if(_matchTimerContainer != null) {
                _matchTimerContainer.style.display = DisplayStyle.Flex;
            }

            // Reset cursor state
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;
            UnityEngine.Cursor.visible = false;
                
            // Clear chat history for new match
            if(chatUIManager != null) {
                chatUIManager.ClearChatHistory();
            }
        }

        private void FindUIElements() {
            _pauseMenuPanel = QRequired<VisualElement>("pause-menu-panel");
            _optionsPanel = QOptional<VisualElement>("options-panel");
            _matchTimerContainer = QOptional<VisualElement>("match-timer-container");
            _pauseChallengesContainer = QRequired<VisualElement>("pause-challenges-container");
            _pauseDailyCardSlot = QRequired<VisualElement>("pause-daily-card-slot");
            _pauseWeeklyCardSlot = QRequired<VisualElement>("pause-weekly-card-slot");
            _resumeButton = QRequired<Button>("resume-button");
            _optionsButton = QRequired<Button>("options-button");
            _quitButton = QRequired<Button>("quit-button");
            _pauseJoinCodeLabel = QOptional<Label>("pause-join-code-label");
            _pauseCopyCodeButton = QOptional<Button>("pause-copy-code-button");
            _quitConfirmationModal = QOptional<VisualElement>("quit-confirmation-modal");
            _quitConfirmationYes = QOptional<Button>("quit-confirmation-yes");
            _quitConfirmationNo = QOptional<Button>("quit-confirmation-no");
            _killFeedContainer = QOptional<VisualElement>("kill-feed-container");
            
            BuildPauseChallengeCards();
            
            // Initial join code display
            UpdatePauseJoinCodeDisplay();
        }

        private void Update() {
            if(!IsPaused) return;
            UpdatePauseChallengesIfDirty();

            if(Time.unscaledTime < _nextChallengeTimerUpdateAt) return;
            UpdateChallengeTimers();
            _nextChallengeTimerUpdateAt = Time.unscaledTime + ChallengeTimerUpdateIntervalSeconds;
        }

        private void UpdateChallengeTimers(bool force = false) {
            var isOffline = IsOfflineMode();
            if(force || _cachedOfflineState != isOffline) {
                _cachedOfflineState = isOffline;
                SetChallengeOfflineState(isOffline);
                SetPauseChallengesDirty();
            }

            if(isOffline) {
                ChallengeUiRenderer.SetOfflineTimer(_dailyTimerLabel);
                ChallengeUiRenderer.SetOfflineTimer(_weeklyTimerLabel);
                return;
            }

            var pm = ProgressionManager.Instance;
            if(pm == null) return;

            if (_dailyTimerLabel != null) {
                var time = pm.GetTimeUntilDailyReset();
                ChallengeUiRenderer.SetDailyResetTimer(_dailyTimerLabel, time);
                if(time <= TimeSpan.Zero) {
                    SetPauseChallengesDirty();
                }
            }

            if(_weeklyTimerLabel == null) return;
            var weeklyTime = pm.GetTimeUntilWeeklyReset();
            ChallengeUiRenderer.SetWeeklyResetTimer(_weeklyTimerLabel, weeklyTime);
            if(weeklyTime <= TimeSpan.Zero) {
                SetPauseChallengesDirty();
            }
        }

        private void BuildPauseChallengeCards() {
            _pauseDailyCardSlot.Clear();
            _pauseWeeklyCardSlot.Clear();

            _dailyChallengesCard = ChallengeUiRenderer.CreateChallengeCard(
                challengeCardTemplate,
                "D A I L Y",
                ref _challengeCardTemplateErrorLogged,
                this,
                out _dailyTimerLabel
            );
            _weeklyChallengesCard = ChallengeUiRenderer.CreateChallengeCard(
                challengeCardTemplate,
                "W E E K L Y",
                ref _challengeCardTemplateErrorLogged,
                this,
                out _weeklyTimerLabel
            );

            if(_dailyChallengesCard != null) _pauseDailyCardSlot.Add(_dailyChallengesCard);
            if(_weeklyChallengesCard != null) _pauseWeeklyCardSlot.Add(_weeklyChallengesCard);
            _dailyChallengeList = _dailyChallengesCard?.Q<VisualElement>("challenge-list");
            _weeklyChallengeList = _weeklyChallengesCard?.Q<VisualElement>("challenge-list");
            _pauseChallengesContainer.AddToClassList("hidden");
        }

        private void UpdatePauseChallengesIfDirty() {
            if(!_pauseChallengesDirty) return;
            _pauseChallengesDirty = false;
            UpdatePauseChallenges();
        }

        private void SetPauseChallengesDirty() {
            _pauseChallengesDirty = true;
        }

        private void SetChallengeOfflineState(bool isOffline) {
            ChallengeUiRenderer.SetOfflineState(_dailyChallengesCard, isOffline);
            ChallengeUiRenderer.SetOfflineState(_weeklyChallengesCard, isOffline);
        }

        private void UpdatePauseChallenges() {
            if(_cachedOfflineState ?? IsOfflineMode()) {
                SetChallengeOfflineState(true);
                return;
            }

            var pm = ProgressionManager.Instance;
            if (pm == null || pm.Data == null) return;

            SetChallengeOfflineState(false);
            RenderChallengeList(_dailyChallengeList, pm.Data.dailyChallenges);
            RenderChallengeList(_weeklyChallengeList, pm.Data.weeklyChallenges);
        }

        private void RenderChallengeList(VisualElement listContainer, List<ActiveChallengeData> challenges) {
            if (listContainer == null) return;

            var pm = ProgressionManager.Instance;
            if (pm == null) return;

            ChallengeUiRenderer.RenderChallengeList(
                listContainer,
                challenges,
                challengeRowTemplate,
                pm,
                ref _challengeRowTemplateErrorLogged,
                this,
                showEmptyLabel: false,
                includeXpSuffix: false
            );
        }

        private void RegisterUIEvents() {
            Action resumeHandler = () => { UISoundService.PlayButtonClick(); ResumeGame(); };
            _resumeButton.clicked += resumeHandler;
            RegisterCleanup(() => _resumeButton.clicked -= resumeHandler);
            UISoundService.RegisterButtonHover(_resumeButton);

            Action optionsHandler = () => { UISoundService.PlayButtonClick(); ShowOptions(); };
            _optionsButton.clicked += optionsHandler;
            RegisterCleanup(() => _optionsButton.clicked -= optionsHandler);
            UISoundService.RegisterButtonHover(_optionsButton);

            Action quitHandler = () => { UISoundService.PlayButtonClick(isBack: true); ShowQuitConfirmation(); };
            _quitButton.clicked += quitHandler;
            RegisterCleanup(() => _quitButton.clicked -= quitHandler);
            UISoundService.RegisterButtonHover(_quitButton);

            if (_pauseCopyCodeButton != null) {
                Action inviteHandler = () => { UISoundService.PlayButtonClick(); InviteFriends(); };
                _pauseCopyCodeButton.clicked += inviteHandler;
                RegisterCleanup(() => _pauseCopyCodeButton.clicked -= inviteHandler);
                UISoundService.RegisterButtonHover(_pauseCopyCodeButton);
            }

            if(_quitConfirmationYes != null) {
                Action yesHandler = () => { 
                    UISoundService.PlayButtonClick(isBack: true); 
                    _modalHost.HideModal("quit-confirmation"); 
                    QuitToMenu(); 
                };
                _quitConfirmationYes.clicked += yesHandler;
                RegisterCleanup(() => _quitConfirmationYes.clicked -= yesHandler);
                UISoundService.RegisterButtonHover(_quitConfirmationYes);
            }

            if(_quitConfirmationNo == null) return;
            Action noHandler = () => { 
                UISoundService.PlayButtonClick(); 
                _modalHost.HideModal("quit-confirmation");
                // Show pause menu and challenges again when canceling quit
                if(_pauseMenuPanel != null) {
                    _pauseMenuPanel.RemoveFromClassList("hidden");
                }
                if(_pauseChallengesContainer != null) {
                    _pauseChallengesContainer.RemoveFromClassList("hidden");
                }
            };
            _quitConfirmationNo.clicked += noHandler;
            RegisterCleanup(() => _quitConfirmationNo.clicked -= noHandler);
            UISoundService.RegisterButtonHover(_quitConfirmationNo);
        }

        private void BindProgressionEvents() {
            if(_progressionManager != null) return;
            _progressionManager = ProgressionManager.Instance;
            if(_progressionManager == null) return;

            _progressionManager.OnChallengesUpdated -= OnChallengesUpdated;
            _progressionManager.OnChallengesUpdated += OnChallengesUpdated;
        }

        private void UnbindProgressionEvents() {
            if(_progressionManager == null) return;
            _progressionManager.OnChallengesUpdated -= OnChallengesUpdated;
            _progressionManager = null;
        }

        private void OnChallengesUpdated() {
            SetPauseChallengesDirty();
            if(!IsPaused) return;
            UpdatePauseChallengesIfDirty();
        }

        private static bool IsOfflineMode() {
            return Application.internetReachability == NetworkReachability.NotReachable;
        }

        public void RestoreHudForMatchStart() {
            if(_matchTimerContainer == null) return;
            IsPostMatch = false;
            _matchTimerContainer.style.display = DisplayStyle.Flex;
            if(PostMatchManager.Instance != null) {
                PostMatchManager.Instance.ShowInGameHudAfterPostMatch();
            }
        }

        public void TogglePause() {
            if(_cachedSceneName == null || !_cachedSceneName.Contains("Game")) return;

            if(IsPaused) {
                if(!_optionsPanel.ClassListContains("hidden")) HideOptions();
                else ResumeGame();
            } else {
                PauseGame();
            }
        }

        #region Setup Methods
        private void SetupOptionsMenuManager() {
            if(optionsMenuManager == null) return;
            optionsMenuManager.OnButtonClickedCallback = UISoundService.PlayButtonClick;
            optionsMenuManager.MouseEnterCallback = _ => UISoundService.PlayButtonHover();
            optionsMenuManager.MouseHoverCallback = _ => UISoundService.PlayButtonHover();
            optionsMenuManager.OnBackFromOptionsCallback = HideOptions;
            optionsMenuManager.Initialize();
        }

        private void SetupKillFeedManager() {
            if (killFeedManager != null) {
                killFeedManager.Initialize(_killFeedContainer);
            }
        }

        private void SetupScoreboardManager() {
            if (scoreboardManager != null) {
                scoreboardManager.Initialize(Root);
            }
        }

        private void SetupSniperOverlayManager() {
            if (sniperOverlayManager != null) {
                sniperOverlayManager.Initialize(Root);
            }
        }

        private void SetupSocialUI() {
            if(voiceOverlayManager == null) voiceOverlayManager = GetComponentInChildren<VoiceOverlayManager>();
            
            if (chatUIManager != null) {
                chatUIManager.Initialize(Root);
            }
            
            if (voiceOverlayManager != null) {
                voiceOverlayManager.Initialize(Root);
            }
        }
        #endregion

        #region Menu Navigation
        private void PauseGame() {
            IsPaused = true;
            _pauseMenuPanel.RemoveFromClassList("hidden");
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            UpdatePauseJoinCodeDisplay();
            UpdateChallengeTimers(force: true);
            SetPauseChallengesDirty();
            UpdatePauseChallengesIfDirty();
            _nextChallengeTimerUpdateAt = Time.unscaledTime + ChallengeTimerUpdateIntervalSeconds;
            
            if (_pauseChallengesContainer != null) _pauseChallengesContainer.RemoveFromClassList("hidden");
            if(_localController) _localController.moveInput = Vector2.zero;
        }
        
        private void InviteFriends() {
             UISoundService.PlayButtonClick();
             if(!SteamClient.IsValid || !SteamClient.IsLoggedOn) {
                 return;
             }

             if(SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                 SteamManager.Instance.OpenInviteOverlay(SessionManager.Instance.CurrentLobby.Value.Id);
             }
        }

        private void UpdatePauseJoinCodeDisplay() {
            if(_pauseJoinCodeLabel == null) return;
            
            var hasLobby = SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue;
            var steamLoggedOn = SteamClient.IsValid && SteamClient.IsLoggedOn;

            // Update label text
            _pauseJoinCodeLabel.text = hasLobby ? "Lobby Active" : "Single Player";

            // Show label and invite button only when in a lobby and Steam is online
            var showSocial = hasLobby && steamLoggedOn;
            _pauseJoinCodeLabel.style.display = showSocial ? DisplayStyle.Flex : DisplayStyle.None;
            if (_pauseCopyCodeButton != null) {
                _pauseCopyCodeButton.style.display = showSocial ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void ResumeGame() {
            IsPaused = false;
            _pauseMenuPanel.AddToClassList("hidden");
            _optionsPanel.AddToClassList("hidden");
            _modalHost.HideModal("quit-confirmation");
            if (_pauseChallengesContainer != null) _pauseChallengesContainer.AddToClassList("hidden");
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;
            UnityEngine.Cursor.visible = false;
        }

        private void ShowOptions() {
            if(_cachedSceneName != "Game") return;
            
            if (optionsMenuManager != null) {
                optionsMenuManager.LoadSettings();
            }
            
            _pauseMenuPanel.AddToClassList("hidden");
            // Hide challenges when showing options
            if(_pauseChallengesContainer != null) {
                _pauseChallengesContainer.AddToClassList("hidden");
            }
            _optionsPanel.RemoveFromClassList("hidden");
            
            // Call after panel is visible to ensure repaint works
            if (optionsMenuManager != null) {
                optionsMenuManager.OnOptionsPanelShown();
            }
        }

        private void HideOptions() {
            if(_cachedSceneName != "Game") return;
            _optionsPanel.AddToClassList("hidden");
            _pauseMenuPanel.RemoveFromClassList("hidden");
            // Show challenges again when returning to pause menu
            if(_pauseChallengesContainer != null) {
                _pauseChallengesContainer.RemoveFromClassList("hidden");
            }
        }

        private void ShowQuitConfirmation() {
            if(_modalHost == null) {
                Debug.LogWarning("[GameMenuManager] Quit modal host is not initialized yet.");
                return;
            }

            if(_quitConfirmationModal == null) return;
            // Hide pause menu and challenges when showing quit confirmation modal
            if(_pauseMenuPanel != null) {
                _pauseMenuPanel.AddToClassList("hidden");
            }
            if(_pauseChallengesContainer != null) {
                _pauseChallengesContainer.AddToClassList("hidden");
            }
            _modalHost.ShowExistingModal(_quitConfirmationModal, "quit-confirmation");
        }

        private async void QuitToMenu() {
            try {
                if (ProgressionManager.Instance != null) {
                    ProgressionManager.Instance.SaveData();
                }
                
                await SessionManager.Instance.LeaveToMainMenuAsync();
                
                var rootContainer = Root.Q<VisualElement>("root-container");
                if (rootContainer != null) rootContainer.style.display = DisplayStyle.None;
                
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

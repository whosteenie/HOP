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
        private VisualElement _pauseChallengesContainer;
        private VisualElement _dailyChallengesCard;
        private Label _dailyTimerLabel;
        private VisualElement _weeklyChallengesCard;
        private Label _weeklyTimerLabel;
        private UIModalHost _modalHost;

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
            SceneManager.sceneLoaded += OnSceneLoaded;
            RegisterCleanup(() => SceneManager.sceneLoaded -= OnSceneLoaded);
        }

        protected override void OnDisable() {
            SceneManager.sceneLoaded -= OnSceneLoaded;
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
            
            // Clear chat history when initializing (new match)
            if (chatUIManager != null) {
                chatUIManager.ClearChatHistory();
            }
        }

        protected override Dictionary<string, Type> GetRequiredElements() {
            return new Dictionary<string, Type> {
                { "pause-menu-panel", typeof(VisualElement) },
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
            if(_cachedSceneName != null && _cachedSceneName.Contains("Game")) {
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
                
                // Reset cursor state
                UnityEngine.Cursor.lockState = CursorLockMode.Locked;
                UnityEngine.Cursor.visible = false;
                
                // Clear chat history for new match
                if(chatUIManager != null) {
                    chatUIManager.ClearChatHistory();
                }
            }
        }

        private void FindUIElements() {
            _pauseMenuPanel = QRequired<VisualElement>("pause-menu-panel");
            _optionsPanel = QOptional<VisualElement>("options-panel");
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
            if (IsPaused && _pauseChallengesContainer != null) {
                UpdatePauseChallenges();
                UpdateChallengeTimers();
            }
        }

        private void UpdateChallengeTimers() {
             var pm = ProgressionManager.Instance;
             if(pm == null) return;

            if (_dailyTimerLabel != null) {
                var time = pm.GetTimeUntilDailyReset();
                _dailyTimerLabel.text = $"{time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
                _dailyTimerLabel.RemoveFromClassList("challenge-card-timer--long");
            }

            if (_weeklyTimerLabel != null) {
                var time = pm.GetTimeUntilWeeklyReset();
                if (time.TotalDays >= 1) {
                    _weeklyTimerLabel.text = $"{(int)time.TotalDays} days remaining";
                    _weeklyTimerLabel.AddToClassList("challenge-card-timer--long");
                } else {
                    _weeklyTimerLabel.text = $"{time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
                    _weeklyTimerLabel.RemoveFromClassList("challenge-card-timer--long");
                }
            }
        }

        private void BuildPauseChallengeCards() {
            if (_pauseMenuPanel == null) return;

            _pauseChallengesContainer = new VisualElement {
                name = "pause-challenges-container",
                style = {
                    position = Position.Absolute,
                    left = 0, right = 0, top = 0, bottom = 0,
                    flexDirection = FlexDirection.Row,
                    justifyContent = Justify.Center,
                    alignItems = Align.Center
                }
            };
            
            _dailyChallengesCard = CreateChallengeCard("D A I L Y", out _dailyTimerLabel);
            if(_dailyChallengesCard != null) {
                _dailyChallengesCard.style.marginRight = 50;
            }
            
            var spacer = new VisualElement { style = { width = 420, height = 10 } };
            
            _weeklyChallengesCard = CreateChallengeCard("W E E K L Y", out _weeklyTimerLabel);
            if(_weeklyChallengesCard != null) {
                _weeklyChallengesCard.style.marginLeft = 50;
            }

            if(_dailyChallengesCard != null) _pauseChallengesContainer.Add(_dailyChallengesCard);
            _pauseChallengesContainer.Add(spacer);
            if(_weeklyChallengesCard != null) _pauseChallengesContainer.Add(_weeklyChallengesCard);
            
            _pauseMenuPanel.parent.Insert(0, _pauseChallengesContainer);
            _pauseChallengesContainer.AddToClassList("hidden");
        }

        private VisualElement CreateChallengeCard(string title, out Label timerLabel) {
            VisualElement card;
            Label titleLabel;
            VisualElement separatorContainer;
            VisualElement listContainer;
            timerLabel = null;

            if(challengeCardTemplate != null) {
                card = challengeCardTemplate.CloneTree();
                titleLabel = card.Q<Label>("title-label");
                // Attempt to find timer label
                timerLabel = card.Q<Label>("timer-label");
                
                // If timer label is missing but we have a card, something is wrong with the template or clone
                if (timerLabel == null) {
                    Debug.LogWarning($"[GameMenuManager] Timer label not found in template for {title}. Check ChallengeCard.uxml.");
                }

                listContainer = card.Q<VisualElement>("challenge-list");

                // Ensure list container exists
                if(listContainer == null) {
                    listContainer = new VisualElement { name = "challenge-list" };
                    card.Add(listContainer);
                }
            } else {
                card = new VisualElement();
                card.AddToClassList("challenge-card");
                
                titleLabel = new Label();
                titleLabel.AddToClassList("challenge-card-title");
                card.Add(titleLabel);

                // Recreate the separator container structure manually
                separatorContainer = new VisualElement();
                separatorContainer.AddToClassList("challenge-card-separator-container");
                
                var line1 = new VisualElement();
                line1.AddToClassList("challenge-card-separator-line");
                separatorContainer.Add(line1);

                timerLabel = new Label();
                timerLabel.AddToClassList("challenge-card-timer");
                timerLabel.text = "--:--:--";
                separatorContainer.Add(timerLabel);
                
                var line2 = new VisualElement();
                line2.AddToClassList("challenge-card-separator-line");
                separatorContainer.Add(line2);

                card.Add(separatorContainer);
                
                listContainer = new VisualElement { name = "challenge-list" };
                listContainer.AddToClassList("challenge-list");
                card.Add(listContainer);
            }

            if(titleLabel != null) {
                titleLabel.text = title;
            }
            
            // Ensure timer label is visible
            if (timerLabel != null) {
                timerLabel.style.display = DisplayStyle.Flex;
            }

            return card;
        }

        private void UpdatePauseChallenges() {
            var pm = ProgressionManager.Instance;
            if (pm == null || pm.Data == null) return;
            RenderChallengeList(_dailyChallengesCard, pm.Data.dailyChallenges);
            RenderChallengeList(_weeklyChallengesCard, pm.Data.weeklyChallenges);
        }

        private void RenderChallengeList(VisualElement card, List<ActiveChallengeData> challenges) {
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

                VisualElement row;
                Label descriptionLabel;
                Label xpLabel;
                ProgressBar progressBar;

                if(challengeRowTemplate != null) {
                    row = challengeRowTemplate.CloneTree();
                    row.Q<VisualElement>("title-row");
                    descriptionLabel = row.Q<Label>("description-label");
                    xpLabel = row.Q<Label>("xp-label");
                    progressBar = row.Q<ProgressBar>("progress-bar");
                } else {
                    row = new VisualElement();
                    row.AddToClassList("challenge-row");
                    
                    var titleRow = new VisualElement();
                    titleRow.AddToClassList("challenge-title-row");
                    row.Add(titleRow);
                    
                    descriptionLabel = new Label();
                    descriptionLabel.AddToClassList("challenge-description");
                    titleRow.Add(descriptionLabel);
                    
                    xpLabel = new Label();
                    xpLabel.AddToClassList("challenge-xp");
                    titleRow.Add(xpLabel);
                    
                    progressBar = new ProgressBar { lowValue = 0, highValue = 100, value = 0 };
                    progressBar.AddToClassList("challenge-progress-bar");
                    row.Add(progressBar);
                }

                var progress = activeChallenge.currentProgress;
                var target = activeChallenge.targetProgress;
                if (progress > target) progress = target;
                    
                var descText = def.Description;
                try {
                    if (!string.IsNullOrEmpty(activeChallenge.filterID)) {
                        var displayFilter = pm.GetFilterDisplayName(activeChallenge.filterID);
                        descText = string.Format(def.Description, target, displayFilter);
                    } else {
                        descText = string.Format(def.Description, target);
                    }
                } catch {
                    // ignored
                }

                if(descriptionLabel != null) {
                    descriptionLabel.text = $"{descText} ({progress}/{target})";
                }

                if(xpLabel != null) {
                    xpLabel.text = $"+{activeChallenge.xpReward}";
                }

                if(progressBar != null) {
                    progressBar.lowValue = 0;
                    progressBar.highValue = target;
                    progressBar.value = progress;
                }

                list.Add(row);
            }
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
            if (hasLobby) {
                 _pauseJoinCodeLabel.text = "Lobby Active";
            } else {
                 _pauseJoinCodeLabel.text = "Single Player";
            }

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

            if(_quitConfirmationModal != null) {
                // Hide pause menu and challenges when showing quit confirmation modal
                if(_pauseMenuPanel != null) {
                    _pauseMenuPanel.AddToClassList("hidden");
                }
                if(_pauseChallengesContainer != null) {
                    _pauseChallengesContainer.AddToClassList("hidden");
                }
                _modalHost.ShowExistingModal(_quitConfirmationModal, "quit-confirmation");
            }
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
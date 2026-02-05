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
        private VisualElement _weeklyChallengesCard;
        private readonly Color _progressBarColor = new(1f, 0.392f, 0.392f); // #ff6464
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
            if(Root == null) return;
            _modalHost = new UIModalHost(this, Root);
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
            RegisterUIEvents();
            
            SetupOptionsMenuManager();
            SetupKillFeedManager();
            SetupScoreboardManager();
            SetupSniperOverlayManager();
            SetupSocialUI();
            
            // Clear chat history when initializing (new match)
            if(chatUIManager != null) {
                chatUIManager.ClearChatHistory();
            }
        }

        protected override Dictionary<string, System.Type> GetRequiredElements() {
            return new Dictionary<string, System.Type> {
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
            
            // hide invite/code UI
            if(_pauseJoinCodeLabel != null) _pauseJoinCodeLabel.style.display = DisplayStyle.None;
            if(_pauseCopyCodeButton != null) _pauseCopyCodeButton.style.display = DisplayStyle.None;
        }

        private void Update() {
            if (IsPaused && _pauseChallengesContainer != null) {
                UpdatePauseChallenges();
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
            
            _dailyChallengesCard = CreateChallengeCard("Daily Challenges");
            if(_dailyChallengesCard != null) {
                _dailyChallengesCard.style.marginRight = 50;
            }
            
            var spacer = new VisualElement { style = { width = 420, height = 10 } };
            
            _weeklyChallengesCard = CreateChallengeCard("Weekly Challenges");
            if(_weeklyChallengesCard != null) {
                _weeklyChallengesCard.style.marginLeft = 50;
            }

            if(_dailyChallengesCard != null) _pauseChallengesContainer.Add(_dailyChallengesCard);
            _pauseChallengesContainer.Add(spacer);
            if(_weeklyChallengesCard != null) _pauseChallengesContainer.Add(_weeklyChallengesCard);
            
            _pauseMenuPanel.parent.Insert(0, _pauseChallengesContainer);
            _pauseChallengesContainer.AddToClassList("hidden");
        }

        private VisualElement CreateChallengeCard(string title) {
            VisualElement card;
            Label titleLabel;
            VisualElement separator;
            VisualElement listContainer;

            if(challengeCardTemplate != null) {
                card = challengeCardTemplate.CloneTree();
                titleLabel = card.Q<Label>("title-label");
                separator = card.Q<VisualElement>("separator");
                listContainer = card.Q<VisualElement>("challenge-list");
            } else {
                card = new VisualElement {
                    style = {
                        width = 280, minHeight = 180,
                        backgroundColor = new Color(12f/255f, 12f/255f, 18f/255f, 0.85f),
                        borderTopWidth = 1, borderBottomWidth = 1, borderLeftWidth = 1, borderRightWidth = 1,
                        borderTopColor = new Color(200f/255f, 60f/255f, 60f/255f, 0.4f),
                        borderBottomColor = new Color(200f/255f, 60f/255f, 60f/255f, 0.4f),
                        borderLeftColor = new Color(200f/255f, 60f/255f, 60f/255f, 0.4f),
                        borderRightColor = new Color(200f/255f, 60f/255f, 60f/255f, 0.4f),
                        paddingTop = 16, paddingBottom = 16, paddingLeft = 18, paddingRight = 18,
                        borderTopLeftRadius = 4, borderTopRightRadius = 4, 
                        borderBottomLeftRadius = 4, borderBottomRightRadius = 4
                    }
                };
                titleLabel = new Label {
                    style = {
                        fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold,
                        color = Color.white, marginBottom = 12, unityTextAlign = TextAnchor.MiddleCenter
                    }
                };
                card.Add(titleLabel);
                separator = new VisualElement {
                    style = { height = 1, backgroundColor = new Color(200f/255f, 60f/255f, 60f/255f, 0.6f), marginBottom = 12 }
                };
                card.Add(separator);
                listContainer = new VisualElement { name = "challenge-list" };
                card.Add(listContainer);
            }

            if(titleLabel != null) {
                titleLabel.text = title;
            }

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

                VisualElement row;
                VisualElement titleRow;
                Label descriptionLabel;
                Label xpLabel;
                ProgressBar progressBar;

                if(challengeRowTemplate != null) {
                    row = challengeRowTemplate.CloneTree();
                    titleRow = row.Q<VisualElement>("title-row");
                    descriptionLabel = row.Q<Label>("description-label");
                    xpLabel = row.Q<Label>("xp-label");
                    progressBar = row.Q<ProgressBar>("progress-bar");
                } else {
                    row = new VisualElement {
                        style = { marginBottom = 12, paddingBottom = 8, borderBottomWidth = 1, borderBottomColor = new Color(200f/255f, 60f/255f, 60f/255f, 0.15f) }
                    };
                    titleRow = new VisualElement {
                        style = { flexDirection = FlexDirection.Row, justifyContent = Justify.SpaceBetween, alignItems = Align.FlexStart, marginBottom = 4 }
                    };
                    row.Add(titleRow);
                    descriptionLabel = new Label {
                        style = { fontSize = 11, color = new Color(0.9f, 0.9f, 0.9f), whiteSpace = WhiteSpace.Normal, flexShrink = 1 }
                    };
                    titleRow.Add(descriptionLabel);
                    xpLabel = new Label {
                        style = { fontSize = 10, color = new Color(0.5f, 0.8f, 0.5f), flexShrink = 0, marginLeft = 8 }
                    };
                    titleRow.Add(xpLabel);
                    progressBar = new ProgressBar { lowValue = 0, highValue = 100, value = 0, style = { height = 5 } };
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
                } catch { }

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
                    StyleProgressBar(progressBar);
                }

                list.Add(row);
            }
        }

        private void StyleProgressBar(ProgressBar bar) {
            bar.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f);
            var fill = bar.Q<VisualElement>(className: "unity-progress-bar__progress");
            if (fill != null) fill.style.backgroundColor = _progressBarColor;
        }

        private void RegisterUIEvents() {
            System.Action resumeHandler = () => { UISoundService.PlayButtonClick(); ResumeGame(); };
            _resumeButton.clicked += resumeHandler;
            RegisterCleanup(() => _resumeButton.clicked -= resumeHandler);
            UISoundService.RegisterButtonHover(_resumeButton);

            System.Action optionsHandler = () => { UISoundService.PlayButtonClick(); ShowOptions(); };
            _optionsButton.clicked += optionsHandler;
            RegisterCleanup(() => _optionsButton.clicked -= optionsHandler);
            UISoundService.RegisterButtonHover(_optionsButton);

            System.Action quitHandler = () => { UISoundService.PlayButtonClick(isBack: true); ShowQuitConfirmation(); };
            _quitButton.clicked += quitHandler;
            RegisterCleanup(() => _quitButton.clicked -= quitHandler);
            UISoundService.RegisterButtonHover(_quitButton);

            if(_quitConfirmationYes != null) {
                System.Action yesHandler = () => { 
                    UISoundService.PlayButtonClick(isBack: true); 
                    _modalHost.HideModal("quit-confirmation"); 
                    QuitToMenu(); 
                };
                _quitConfirmationYes.clicked += yesHandler;
                RegisterCleanup(() => _quitConfirmationYes.clicked -= yesHandler);
                UISoundService.RegisterButtonHover(_quitConfirmationYes);
            }

            if(_quitConfirmationNo == null) return;
            System.Action noHandler = () => { 
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
            killFeedManager?.Initialize(_killFeedContainer);
        }

        private void SetupScoreboardManager() {
            scoreboardManager?.Initialize(Root);
        }

        private void SetupSniperOverlayManager() {
            sniperOverlayManager?.Initialize(Root);
        }

        private void SetupSocialUI() {
            if(voiceOverlayManager == null) voiceOverlayManager = GetComponentInChildren<VoiceOverlayManager>();
            chatUIManager?.Initialize(Root);
            voiceOverlayManager?.Initialize(Root);
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
             if (SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                 SteamManager.Instance.OpenInviteOverlay(SessionManager.Instance.CurrentLobby.Value.Id);
             }
        }

        private void UpdatePauseJoinCodeDisplay() {
            if(_pauseJoinCodeLabel == null) return;
            
            // For Steam, we don't prefer showing raw Lobby IDs (too long).
            // Just show connected state.
            if (SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                 _pauseJoinCodeLabel.text = "Lobby Active";
            } else {
                 _pauseJoinCodeLabel.text = "Single Player"; // Or offline
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
            optionsMenuManager?.LoadSettings();
            optionsMenuManager?.OnOptionsPanelShown();
            _pauseMenuPanel.AddToClassList("hidden");
            // Hide challenges when showing options
            if(_pauseChallengesContainer != null) {
                _pauseChallengesContainer.AddToClassList("hidden");
            }
            _optionsPanel.RemoveFromClassList("hidden");
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
                ProgressionManager.Instance?.SaveData();
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
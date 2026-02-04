using System;
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

        // Quit confirmation modal
        private VisualElement _quitConfirmationModal;
        private Button _quitConfirmationYes;
        private Button _quitConfirmationNo;

        // Pause menu join code
        private Label _pauseJoinCodeLabel;
        private Button _pauseCopyCodeButton; // Will be "Invite" button

        private VisualElement _root;
        private string _cachedSceneName;
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

        private void Start() {
            if(DiscordManager.Instance == null) return;
            var mode = "Deathmatch";
            if(MatchSettingsManager.Instance != null) {
                mode = MatchSettingsManager.Instance.selectedGameModeId;
                if(string.IsNullOrEmpty(mode)) mode = "Deathmatch";
            }
            DiscordManager.Instance.SetStatus("Playing " + mode, "In Match", 
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        private void OnEnable() {
            if(uiDocument == null) return;
            _root = uiDocument.rootVisualElement;
            UpdateCachedSceneName();
            SceneManager.sceneLoaded += OnSceneLoaded;
            
            FindUIElements();
            RegisterUIEvents();
            
            SetupOptionsMenuManager();
            SetupKillFeedManager();
            SetupScoreboardManager();
            SetupSniperOverlayManager();
        }

        private void OnDisable() {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }
        
        #endregion

        private void UpdateCachedSceneName() {
            var activeScene = SceneManager.GetActiveScene();
            if(activeScene.IsValid()) _cachedSceneName = activeScene.name;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            UpdateCachedSceneName();
        }

        private void FindUIElements() {
            _pauseMenuPanel = _root.Q<VisualElement>("pause-menu-panel");
            _optionsPanel = _root.Q<VisualElement>("options-panel");
            _resumeButton = _root.Q<Button>("resume-button");
            _optionsButton = _root.Q<Button>("options-button");
            _quitButton = _root.Q<Button>("quit-button");
            _pauseJoinCodeLabel = _root.Q<Label>("pause-join-code-label");
            _pauseCopyCodeButton = _root.Q<Button>("pause-copy-code-button");
            _quitConfirmationModal = _root.Q<VisualElement>("quit-confirmation-modal");
            _quitConfirmationYes = _root.Q<Button>("quit-confirmation-yes");
            _quitConfirmationNo = _root.Q<Button>("quit-confirmation-no");
            _killFeedContainer = _root.Q<VisualElement>("kill-feed-container");
            
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
            _dailyChallengesCard.style.marginRight = 50;
            
            var spacer = new VisualElement { style = { width = 420, height = 10 } };
            
            _weeklyChallengesCard = CreateChallengeCard("Weekly Challenges");
            _weeklyChallengesCard.style.marginLeft = 50;

            _pauseChallengesContainer.Add(_dailyChallengesCard);
            _pauseChallengesContainer.Add(spacer);
            _pauseChallengesContainer.Add(_weeklyChallengesCard);
            
            _pauseMenuPanel.parent.Insert(0, _pauseChallengesContainer);
            _pauseChallengesContainer.AddToClassList("hidden");
        }

        private static VisualElement CreateChallengeCard(string title) {
            var card = new VisualElement {
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
            
            var titleLabel = new Label(title) {
                style = {
                    fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold,
                    color = Color.white, marginBottom = 12, unityTextAlign = TextAnchor.MiddleCenter
                }
            };
            card.Add(titleLabel);
            
            var separator = new VisualElement {
                style = { height = 1, backgroundColor = new Color(200f/255f, 60f/255f, 60f/255f, 0.6f), marginBottom = 12 }
            };
            card.Add(separator);
            
            var listContainer = new VisualElement { name = "challenge-list" };
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
                    style = { marginBottom = 12, paddingBottom = 8, borderBottomWidth = 1, borderBottomColor = new Color(200f/255f, 60f/255f, 60f/255f, 0.15f) }
                };

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

                var titleRow = new VisualElement {
                    style = { flexDirection = FlexDirection.Row, justifyContent = Justify.SpaceBetween, alignItems = Align.FlexStart, marginBottom = 4 }
                };
                
                var titleLabel = new Label($"{descText} ({progress}/{target})") {
                    style = { fontSize = 11, color = new Color(0.9f, 0.9f, 0.9f), whiteSpace = WhiteSpace.Normal, flexShrink = 1 }
                };
                titleRow.Add(titleLabel);
                
                var xpLabel = new Label($"+{activeChallenge.xpReward}") {
                    style = { fontSize = 10, color = new Color(0.5f, 0.8f, 0.5f), flexShrink = 0, marginLeft = 8 }
                };
                titleRow.Add(xpLabel);
                row.Add(titleRow);
                    
                var progressBar = new ProgressBar { lowValue = 0, highValue = target, value = progress, style = { height = 5 } };
                StyleProgressBar(progressBar);
                row.Add(progressBar);
                list.Add(row);
            }
        }

        private void StyleProgressBar(ProgressBar bar) {
            bar.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f);
            var fill = bar.Q<VisualElement>(className: "unity-progress-bar__progress");
            if (fill != null) fill.style.backgroundColor = _progressBarColor;
        }

        private void RegisterUIEvents() {
            _resumeButton.clicked += () => { UISoundService.PlayButtonClick(); ResumeGame(); };
            UISoundService.RegisterButtonHover(_resumeButton);

            _optionsButton.clicked += () => { UISoundService.PlayButtonClick(); ShowOptions(); };
            UISoundService.RegisterButtonHover(_optionsButton);

            _quitButton.clicked += () => { UISoundService.PlayButtonClick(isBack: true); ShowQuitConfirmation(); };
            UISoundService.RegisterButtonHover(_quitButton);

            if(_quitConfirmationYes != null) {
                _quitConfirmationYes.clicked += () => { UISoundService.PlayButtonClick(isBack: true); HideQuitConfirmation(); QuitToMenu(); };
                UISoundService.RegisterButtonHover(_quitConfirmationYes);
            }

            if(_quitConfirmationNo == null) return;
            _quitConfirmationNo.clicked += () => { UISoundService.PlayButtonClick(); HideQuitConfirmation(); };
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
            scoreboardManager?.Initialize(_root);
        }

        private void SetupSniperOverlayManager() {
            sniperOverlayManager?.Initialize(_root);
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
            HideQuitConfirmation();
            if (_pauseChallengesContainer != null) _pauseChallengesContainer.AddToClassList("hidden");
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;
            UnityEngine.Cursor.visible = false;
        }

        private void ShowOptions() {
            if(_cachedSceneName != "Game") return;
            optionsMenuManager?.LoadSettings();
            optionsMenuManager?.OnOptionsPanelShown();
            _pauseMenuPanel.AddToClassList("hidden");
            _optionsPanel.RemoveFromClassList("hidden");
        }

        private void HideOptions() {
            if(_cachedSceneName != "Game") return;
            _optionsPanel.AddToClassList("hidden");
            _pauseMenuPanel.RemoveFromClassList("hidden");
        }

        private void ShowQuitConfirmation() {
            _quitConfirmationModal?.RemoveFromClassList("hidden");
        }

        private void HideQuitConfirmation() {
            _quitConfirmationModal?.AddToClassList("hidden");
        }

        private async void QuitToMenu() {
            try {
                ProgressionManager.Instance?.SaveData();
                await SessionManager.Instance.LeaveToMainMenuAsync();
                
                var root = uiDocument.rootVisualElement;
                var rootContainer = root.Q<VisualElement>("root-container");
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
using System;
using System.Collections.Generic;
using Diagnostics;
using Events;
using Game.Social;
using Game.Progression;
using Game.Settings;
using Game.Menu.Options;
using Game.Match;
using Game.Player.Core;
using Game.UI.Core;
using Game.UI.HUD;
using Game.UI.Misc;
using Game.UI.Screens;
using Game.UI.Screens.Scoreboard;
using Game.Weapon.Core;
using Game.Weapon.Manager;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using SessionManager = Network.Session.SessionManager;

namespace Game.Menu.Game {
    public class GameMenuManager : UIElementBase {
        #region Serialized Fields

        [Header("Kill Feed")]
        [SerializeField] private KillFeedManager killFeedManager;

        [Header("Scoreboard")]
        [SerializeField] private ScoreboardManager scoreboardManager;

        [Header("Options")]
        [SerializeField] private OptionsMenuManager optionsMenuManager;
        [SerializeField] private MenuBlurVolumeController menuBlurController;
        [SerializeField] private Volume optionsBlurVolume;

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
        private bool _progressionEventsBound;
        private const float ChallengeTimerUpdateIntervalSeconds = 1f;

        // Pause loadout UI
        private VisualElement _pauseLoadoutContainer;
        private VisualElement _pausePrimarySlot;
        private VisualElement _pauseSecondarySlot;
        private VisualElement _pauseTertiarySlot;
        private VisualElement _pausePrimaryDropdown;
        private VisualElement _pauseSecondaryDropdown;
        private VisualElement _pauseTertiaryDropdown;
        private ScrollView _pausePrimaryScroll;
        private ScrollView _pauseSecondaryScroll;
        private ScrollView _pauseTertiaryScroll;
        private Image _pausePrimaryImage;
        private Image _pauseSecondaryImage;
        private Image _pauseTertiaryImage;
        private Label _pausePrimaryName;
        private Label _pauseSecondaryName;
        private Label _pauseTertiaryName;
        private VisualElement _pauseCurrentOpenDropdown;
        private bool _pauseLoadoutOutsideClickRegistered;
        private bool _pauseLoadoutPendingApply;
        private bool _gameplayEventsBound;
        private int _pauseSelectedPrimaryIndex;
        private int _pauseSelectedSecondaryIndex;
        private int _pauseSelectedTertiaryIndex;

        #endregion

        #region Properties

        private bool IsPaused { get; set; }
        private bool IsPostMatch { get; set; }

        #endregion

        #region Unity Lifecycle

        protected override void Awake() {
            base.Awake();
            EventBus.Publish(new GameMenuReadyEvent());
        }

        protected override void Start() {
            base.Start();
            InitializeMenuBlurController();
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
            BindGameplayEvents();
        }

        protected override void OnDisable() {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            UnbindProgressionEvents();
            UnbindGameplayEvents();
            ClosePauseLoadoutDropdowns();
            SetOptionsOpenState(false);
            base.OnDisable();
        }

        protected override void OnInitialize() {
            FindUIElements();
            if(_modalHost == null && Root != null) {
                _modalHost = new UIModalHost(Root);
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
            if(IsGameplaySceneContext()) {
                RestoreHudForMatchStart();
            }

            PublishGameplayUiDocumentReady();

            SetPauseChallengesDirty();
            UpdateChallengeTimers(force: true);
            _nextChallengeTimerUpdateAt = Time.unscaledTime + ChallengeTimerUpdateIntervalSeconds;
            SyncPauseLoadoutFromSettings();
            RefreshPauseLoadoutVisuals();
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
            if(!IsGameplaySceneContext()) return;
            // Reset pause state
            IsPaused = false;
            EventBus.Publish(new PauseMenuStateChangedEvent(false));
                
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
            SetOptionsOpenState(false);

            // Restore timer visibility for each fresh game scene load.
            if(_matchTimerContainer != null) {
                _matchTimerContainer.style.display = DisplayStyle.Flex;
            }

            EnsureGameplayRootVisible();

            PublishGameplayUiDocumentReady();

            // Reset cursor state
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;
            UnityEngine.Cursor.visible = false;
                
            // Clear chat history for new match
            if(chatUIManager != null) {
                chatUIManager.ClearChatHistory();
            }

            _pauseLoadoutPendingApply = false;
            SyncPauseLoadoutFromSettings();
            ClosePauseLoadoutDropdowns();
        }

        private void PublishGameplayUiDocumentReady() {
            if(!IsGameplaySceneContext()) return;
            if(uiDocument == null || uiDocument.rootVisualElement == null) return;
            EventBus.Publish(new GameplayUiDocumentReadyEvent(uiDocument));
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
            _quitConfirmationModal = QOptional<VisualElement>("quit-confirmation-modal");
            _quitConfirmationYes = QOptional<Button>("quit-confirmation-yes");
            _quitConfirmationNo = QOptional<Button>("quit-confirmation-no");
            _killFeedContainer = QOptional<VisualElement>("kill-feed-container");
            _pauseLoadoutContainer = QOptional<VisualElement>("pause-loadout-container");
            _pausePrimarySlot = QOptional<VisualElement>("pause-primary-weapon-slot");
            _pauseSecondarySlot = QOptional<VisualElement>("pause-secondary-weapon-slot");
            _pauseTertiarySlot = QOptional<VisualElement>("pause-tertiary-weapon-slot");
            _pausePrimaryDropdown = QOptional<VisualElement>("pause-primary-dropdown");
            _pauseSecondaryDropdown = QOptional<VisualElement>("pause-secondary-dropdown");
            _pauseTertiaryDropdown = QOptional<VisualElement>("pause-tertiary-dropdown");
            _pausePrimaryScroll = _pausePrimaryDropdown != null ? _pausePrimaryDropdown.Q<ScrollView>("pause-primary-scroll") : null;
            _pauseSecondaryScroll = _pauseSecondaryDropdown != null ? _pauseSecondaryDropdown.Q<ScrollView>("pause-secondary-scroll") : null;
            _pauseTertiaryScroll = _pauseTertiaryDropdown != null ? _pauseTertiaryDropdown.Q<ScrollView>("pause-tertiary-scroll") : null;
            _pausePrimaryImage = QOptional<Image>("pause-primary-weapon-image");
            _pauseSecondaryImage = QOptional<Image>("pause-secondary-weapon-image");
            _pauseTertiaryImage = QOptional<Image>("pause-tertiary-weapon-image");
            _pausePrimaryName = QOptional<Label>("pause-primary-weapon-name");
            _pauseSecondaryName = QOptional<Label>("pause-secondary-weapon-name");
            _pauseTertiaryName = QOptional<Label>("pause-tertiary-weapon-name");
            
            BuildPauseChallengeCards();
        }

        private void Update() {
            TryApplyPendingLoadout();

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
            Action resumeHandler = () => { UISound.PlayButtonClick(); ResumeGame(); };
            _resumeButton.clicked += resumeHandler;
            RegisterCleanup(() => _resumeButton.clicked -= resumeHandler);
            UISound.RegisterButtonHover(_resumeButton);

            Action optionsHandler = () => { UISound.PlayButtonClick(); ShowOptions(); };
            _optionsButton.clicked += optionsHandler;
            RegisterCleanup(() => _optionsButton.clicked -= optionsHandler);
            UISound.RegisterButtonHover(_optionsButton);

            Action quitHandler = () => { UISound.PlayButtonClick(isBack: true); ShowQuitConfirmation(); };
            _quitButton.clicked += quitHandler;
            RegisterCleanup(() => _quitButton.clicked -= quitHandler);
            UISound.RegisterButtonHover(_quitButton);

            if(_quitConfirmationYes != null) {
                Action yesHandler = () => { 
                    UISound.PlayButtonClick(isBack: true); 
                    _modalHost.HideModal("quit-confirmation"); 
                    QuitToMenu(); 
                };
                _quitConfirmationYes.clicked += yesHandler;
                RegisterCleanup(() => _quitConfirmationYes.clicked -= yesHandler);
                UISound.RegisterButtonHover(_quitConfirmationYes);
            }

            if(_quitConfirmationNo != null) {
                Action noHandler = () => {
                    UISound.PlayButtonClick();
                    _modalHost.HideModal("quit-confirmation");
                    // Show pause menu and challenges again when canceling quit
                    if(_pauseMenuPanel != null) {
                        _pauseMenuPanel.RemoveFromClassList("hidden");
                    }
                    if(_pauseChallengesContainer != null) {
                        _pauseChallengesContainer.RemoveFromClassList("hidden");
                    }
                    RefreshPauseLoadoutVisuals();
                };
                _quitConfirmationNo.clicked += noHandler;
                RegisterCleanup(() => _quitConfirmationNo.clicked -= noHandler);
                UISound.RegisterButtonHover(_quitConfirmationNo);
            }

            RegisterPauseLoadoutEvents();
        }

        private void BindProgressionEvents() {
            if(_progressionEventsBound) return;
            EventBus.Unsubscribe<ChallengesUpdatedEvent>(OnChallengesUpdatedEvent);
            EventBus.Subscribe<ChallengesUpdatedEvent>(OnChallengesUpdatedEvent);
            _progressionEventsBound = true;
        }

        private void UnbindProgressionEvents() {
            if(!_progressionEventsBound) return;
            EventBus.Unsubscribe<ChallengesUpdatedEvent>(OnChallengesUpdatedEvent);
            _progressionEventsBound = false;
        }

        private void BindGameplayEvents() {
            if(_gameplayEventsBound) return;
            EventBus.Subscribe<PlayerDiedEvent>(OnPlayerDied);
            EventBus.Subscribe<PreMatchWaitingForPlayersEvent>(OnPreMatchWaitingForPlayers);
            EventBus.Subscribe<PreMatchCountdownEvent>(OnPreMatchCountdown);
            EventBus.Subscribe<MatchStartedEvent>(OnMatchStartedEvent);
            EventBus.Subscribe<RestoreGameplayMenuPresentationEvent>(OnRestoreGameplayMenuPresentation);
            EventBus.Subscribe<TogglePauseMenuRequestedEvent>(OnTogglePauseMenuRequested);
            EventBus.Subscribe<SetPostMatchMenuStateEvent>(OnSetPostMatchMenuState);
            _gameplayEventsBound = true;
        }

        private void UnbindGameplayEvents() {
            if(!_gameplayEventsBound) return;
            EventBus.Unsubscribe<PlayerDiedEvent>(OnPlayerDied);
            EventBus.Unsubscribe<PreMatchWaitingForPlayersEvent>(OnPreMatchWaitingForPlayers);
            EventBus.Unsubscribe<PreMatchCountdownEvent>(OnPreMatchCountdown);
            EventBus.Unsubscribe<MatchStartedEvent>(OnMatchStartedEvent);
            EventBus.Unsubscribe<RestoreGameplayMenuPresentationEvent>(OnRestoreGameplayMenuPresentation);
            EventBus.Unsubscribe<TogglePauseMenuRequestedEvent>(OnTogglePauseMenuRequested);
            EventBus.Unsubscribe<SetPostMatchMenuStateEvent>(OnSetPostMatchMenuState);
            _gameplayEventsBound = false;
        }

        private void OnPlayerDied(PlayerDiedEvent gameEvent) {
            var controller = ResolveLocalController();
            if(controller == null) return;
            if(gameEvent.PlayerId != controller.OwnerClientId) return;
            ApplyPendingLoadoutNow();
        }

        private void OnPreMatchWaitingForPlayers(PreMatchWaitingForPlayersEvent evt) {
            if(evt is not { IsWaiting: true }) return;
            if(!IsPostMatch) return;
            RestoreHudForMatchStart();
        }

        private void OnPreMatchCountdown(PreMatchCountdownEvent _) {
            if(!IsPostMatch) return;
            RestoreHudForMatchStart();
        }

        private void OnMatchStartedEvent(MatchStartedEvent _) {
            RestoreHudForMatchStart();
        }

        private void OnRestoreGameplayMenuPresentation(RestoreGameplayMenuPresentationEvent _) {
            EnsureGameplayRootVisible();

            if(IsPaused) {
                TogglePause();
            }
        }

        private void OnTogglePauseMenuRequested(TogglePauseMenuRequestedEvent _) {
            TogglePause();
        }

        private void OnSetPostMatchMenuState(SetPostMatchMenuStateEvent evt) {
            if(evt == null) return;
            IsPostMatch = evt.IsPostMatch;
        }

        private void RegisterPauseLoadoutEvents() {
            if(_pauseLoadoutContainer == null) return;

            RegisterPauseLoadoutEvents(_pausePrimarySlot, _pausePrimaryDropdown);
            RegisterPauseLoadoutEvents(_pauseSecondarySlot, _pauseSecondaryDropdown);
            RegisterPauseLoadoutEvents(_pauseTertiarySlot, _pauseTertiaryDropdown);

            if(_pauseLoadoutOutsideClickRegistered || Root == null) return;
            Root.RegisterCallback<PointerDownEvent>(OnPauseLoadoutRootPointerDown, TrickleDown.TrickleDown);
            RegisterCleanup(() => {
                if(Root != null) {
                    Root.UnregisterCallback<PointerDownEvent>(OnPauseLoadoutRootPointerDown, TrickleDown.TrickleDown);
                }
            });
            _pauseLoadoutOutsideClickRegistered = true;
        }

        /// <summary>Registers click/hover for a pause loadout slot and its dropdown.</summary>
        private void RegisterPauseLoadoutEvents(VisualElement slot, VisualElement dropdown) {
            if(slot == null) return;

            EventCallback<ClickEvent> clickHandler = evt => {
                UISound.PlayButtonClick();
                TogglePauseLoadoutDropdown(dropdown);
                if(evt == null) return;
                evt.StopPropagation();
                evt.StopImmediatePropagation();
            };
            slot.RegisterCallback(clickHandler);
            RegisterCleanup(() => slot.UnregisterCallback(clickHandler));

            EventCallback<MouseEnterEvent> hoverHandler = _ => UISound.PlayButtonHover();
            slot.RegisterCallback(hoverHandler);
            RegisterCleanup(() => slot.UnregisterCallback(hoverHandler));
        }

        private void OnPauseLoadoutRootPointerDown(PointerDownEvent evt) {
            if(_pauseCurrentOpenDropdown == null || evt == null) return;
            if(IsPointerInPauseUi(evt.position)) return;
            ClosePauseLoadoutDropdowns();
        }

        private bool IsPointerInPauseUi(Vector2 pointerPosition) {
            if(_pauseCurrentOpenDropdown == null) return false;

            if(_pauseCurrentOpenDropdown.worldBound.Contains(pointerPosition)) {
                return true;
            }

            var slot = GetPauseSlotForDropdown(_pauseCurrentOpenDropdown);
            return slot != null && slot.worldBound.Contains(pointerPosition);
        }

        private VisualElement GetPauseSlotForDropdown(VisualElement dropdown) {
            if(dropdown == _pausePrimaryDropdown) return _pausePrimarySlot;
            if(dropdown == _pauseSecondaryDropdown) return _pauseSecondarySlot;
            return dropdown == _pauseTertiaryDropdown ? _pauseTertiarySlot : null;
        }

        private void TogglePauseLoadoutDropdown(VisualElement dropdown) {
            if(dropdown == null || !IsPaused) {
                ClosePauseLoadoutDropdowns();
                return;
            }

            var isCurrentlyOpen = _pauseCurrentOpenDropdown == dropdown && !dropdown.ClassListContains("hidden");
            ClosePauseLoadoutDropdowns();
            if(isCurrentlyOpen) return;

            RefreshPauseLoadoutDropdown(dropdown);
            dropdown.RemoveFromClassList("hidden");
            _pauseCurrentOpenDropdown = dropdown;
        }

        private void RefreshPauseLoadoutDropdown(VisualElement dropdown) {
            if(!TryGetLocalWeaponManager(out var weaponManager)) return;

            if(dropdown == _pausePrimaryDropdown) {
                PopulatePauseWeaponDropdown(_pausePrimaryScroll, weaponManager.PrimaryWeaponOptions,
                    _pauseSelectedPrimaryIndex, SelectPausePrimaryWeapon);
            } else if(dropdown == _pauseSecondaryDropdown) {
                PopulatePauseWeaponDropdown(_pauseSecondaryScroll, weaponManager.SecondaryWeaponOptions,
                    _pauseSelectedSecondaryIndex, SelectPauseSecondaryWeapon);
            } else if(dropdown == _pauseTertiaryDropdown) {
                PopulatePauseWeaponDropdown(_pauseTertiaryScroll, GetPauseTertiaryOptions(weaponManager),
                    _pauseSelectedTertiaryIndex, SelectPauseTertiaryWeapon);
            }
        }

        private static IReadOnlyList<WeaponData> GetPauseTertiaryOptions(WeaponManager _) {
            return Array.Empty<WeaponData>();
        }

        private void PopulatePauseWeaponDropdown(ScrollView scroll, IReadOnlyList<WeaponData> options, int selectedIndex,
            Action<int> onSelect) {
            if(scroll == null) return;

            var container = scroll.contentContainer;
            container.Clear();
            container.style.flexDirection = FlexDirection.Row;

            if(options is not { Count: > 1 }) {
                return;
            }

            var clampedSelected = Mathf.Clamp(selectedIndex, 0, options.Count - 1);
            for(var i = 0; i < options.Count; i++) {
                if(i == clampedSelected) continue;

                var index = i;
                var option = CreatePauseLoadoutOption(options[i]);
                option.RegisterCallback<MouseEnterEvent>(_ => UISound.PlayButtonHover());
                option.RegisterCallback<ClickEvent>(evt => {
                    UISound.PlayButtonClick();
                    onSelect(index);
                    ClosePauseLoadoutDropdowns();
                    if(evt == null) return;
                    evt.StopPropagation();
                    evt.StopImmediatePropagation();
                });

                container.Add(option);
            }
        }

        private static VisualElement CreatePauseLoadoutOption(WeaponData weaponData) {
            var option = new VisualElement();
            option.AddToClassList("pause-loadout-option");

            var image = new Image();
            image.AddToClassList("pause-loadout-option-image");
            if(weaponData != null && weaponData.loadoutIcon != null) {
                image.sprite = weaponData.loadoutIcon;
                image.style.visibility = Visibility.Visible;
            } else {
                image.sprite = null;
                image.style.visibility = Visibility.Hidden;
            }
            option.Add(image);

            var label = new Label(GetSafeWeaponName(weaponData, "WIP"));
            label.AddToClassList("pause-loadout-option-name");
            option.Add(label);
            return option;
        }

        private void ClosePauseLoadoutDropdowns() {
            if(_pausePrimaryDropdown != null) _pausePrimaryDropdown.AddToClassList("hidden");
            if(_pauseSecondaryDropdown != null) _pauseSecondaryDropdown.AddToClassList("hidden");
            if(_pauseTertiaryDropdown != null) _pauseTertiaryDropdown.AddToClassList("hidden");
            _pauseCurrentOpenDropdown = null;
        }

        private void SelectPausePrimaryWeapon(int index) {
            _pauseSelectedPrimaryIndex = index;
            SavePauseLoadoutSelections();
            QueueLoadoutApply();
            RefreshPauseLoadoutVisuals();
        }

        private void SelectPauseSecondaryWeapon(int index) {
            _pauseSelectedSecondaryIndex = index;
            SavePauseLoadoutSelections();
            QueueLoadoutApply();
            RefreshPauseLoadoutVisuals();
        }

        private void SelectPauseTertiaryWeapon(int index) {
            _pauseSelectedTertiaryIndex = index;
            SavePauseLoadoutSelections();
            RefreshPauseLoadoutVisuals();
        }

        private void SavePauseLoadoutSelections() {
            var player = GameSettings.Data.player;
            player.primaryWeaponIndex = _pauseSelectedPrimaryIndex;
            player.secondaryWeaponIndex = _pauseSelectedSecondaryIndex;
            player.tertiaryWeaponIndex = _pauseSelectedTertiaryIndex;
            GameSettings.Save();
        }

        /// <summary>Queues primary/secondary loadout apply (or applies now if not swapping on death).</summary>
        private void QueueLoadoutApply() {
            if(!ShouldSwapWeaponsOnDeath()) {
                ApplyLoadoutNow(deferTpRevealUntilRespawn: false);
                return;
            }

            _pauseLoadoutPendingApply = true;
            if(IsLocalPlayerDead()) {
                ApplyPendingLoadoutNow();
            }
        }

        /// <summary>Applies pending loadout if still dead (e.g. after respawn timer).</summary>
        private void TryApplyPendingLoadout() {
            if(!_pauseLoadoutPendingApply) return;
            if(!IsLocalPlayerDead()) return;
            ApplyPendingLoadoutNow();
        }

        private void ApplyPendingLoadoutNow() {
            ApplyLoadoutNow(deferTpRevealUntilRespawn: true);
        }

        private void ApplyLoadoutNow(bool deferTpRevealUntilRespawn) {
            if(!TryGetLocalWeaponManager(out var weaponManager)) return;

            _pauseLoadoutPendingApply = false;
            _ = weaponManager.ApplyOwnerLoadoutSelection(_pauseSelectedPrimaryIndex, _pauseSelectedSecondaryIndex,
                deferTpRevealUntilRespawn: deferTpRevealUntilRespawn);
        }

        private bool IsLocalPlayerDead() {
            var controller = ResolveLocalController();
            return controller != null && controller.IsDead;
        }

        private static bool ShouldSwapWeaponsOnDeath() {
            var matchSettings = MatchSettingsManager.Instance;
            return matchSettings == null || matchSettings.ShouldSwapWeaponsOnDeath();
        }

        private void SyncPauseLoadoutFromSettings() {
            var player = GameSettings.Data.player;
            _pauseSelectedPrimaryIndex = Mathf.Max(0, player.primaryWeaponIndex);
            _pauseSelectedSecondaryIndex = Mathf.Max(0, player.secondaryWeaponIndex);
            _pauseSelectedTertiaryIndex = Mathf.Max(0, player.tertiaryWeaponIndex);
        }

        private void RefreshPauseLoadoutVisuals() {
            if(_pauseLoadoutContainer == null) return;
            if(!TryGetLocalWeaponManager(out var weaponManager)) return;

            var primaryOptions = weaponManager.PrimaryWeaponOptions;
            var secondaryOptions = weaponManager.SecondaryWeaponOptions;
            var tertiaryOptions = GetPauseTertiaryOptions(weaponManager);

            _pauseSelectedPrimaryIndex = ClampPauseSelectionIndex(_pauseSelectedPrimaryIndex, primaryOptions);
            _pauseSelectedSecondaryIndex = ClampPauseSelectionIndex(_pauseSelectedSecondaryIndex, secondaryOptions);
            _pauseSelectedTertiaryIndex = ClampPauseSelectionIndex(_pauseSelectedTertiaryIndex, tertiaryOptions);

            UpdatePauseSlotVisual(_pausePrimaryImage, _pausePrimaryName, primaryOptions, _pauseSelectedPrimaryIndex,
                "PRIMARY");
            UpdatePauseSlotVisual(_pauseSecondaryImage, _pauseSecondaryName, secondaryOptions,
                _pauseSelectedSecondaryIndex, "SECONDARY");
            UpdatePauseSlotVisual(_pauseTertiaryImage, _pauseTertiaryName, tertiaryOptions, _pauseSelectedTertiaryIndex,
                string.Empty);

            if(_pauseCurrentOpenDropdown != null && _pauseCurrentOpenDropdown.ClassListContains("hidden") == false) {
                RefreshPauseLoadoutDropdown(_pauseCurrentOpenDropdown);
            }
        }

        private static int ClampPauseSelectionIndex(int selectedIndex, IReadOnlyList<WeaponData> options) {
            if(options == null || options.Count == 0) return 0;
            return Mathf.Clamp(selectedIndex, 0, options.Count - 1);
        }

        private static void UpdatePauseSlotVisual(Image slotImage, Label slotName, IReadOnlyList<WeaponData> options,
            int selectedIndex, string emptyLabel) {
            if(options == null || options.Count == 0) {
                if(slotImage != null) {
                    slotImage.sprite = null;
                    slotImage.style.visibility = Visibility.Hidden;
                }

                if(slotName != null) {
                    slotName.text = emptyLabel;
                }

                return;
            }

            var clampedIndex = Mathf.Clamp(selectedIndex, 0, options.Count - 1);
            var weaponData = options[clampedIndex];
            var icon = weaponData != null ? weaponData.loadoutIcon : null;

            if(slotImage != null) {
                slotImage.sprite = icon;
                slotImage.style.visibility = icon != null ? Visibility.Visible : Visibility.Hidden;
            }

            if(slotName != null) {
                slotName.text = GetSafeWeaponName(weaponData, emptyLabel);
            }
        }

        private static string GetSafeWeaponName(WeaponData weaponData, string fallback) {
            if(weaponData == null) return fallback;
            if(!string.IsNullOrEmpty(weaponData.weaponName)) {
                return weaponData.weaponName.ToUpperInvariant();
            }

            return weaponData.weaponPrefab != null ? weaponData.weaponPrefab.name.ToUpperInvariant() : fallback;
        }

        private PlayerController ResolveLocalController() {
            if(_localController != null && _localController.IsOwner) {
                return _localController;
            }

            _localController = PlayerController.LocalPlayer;
            return _localController;
        }

        private bool TryGetLocalWeaponManager(out WeaponManager weaponManager) {
            weaponManager = null;
            var controller = ResolveLocalController();
            if(controller == null) return false;
            weaponManager = controller.WeaponManager;
            return weaponManager != null;
        }

        private void OnChallengesUpdated() {
            SetPauseChallengesDirty();
            if(!IsPaused) return;
            UpdatePauseChallengesIfDirty();
        }

        private void OnChallengesUpdatedEvent(ChallengesUpdatedEvent _) {
            OnChallengesUpdated();
        }

        private static bool IsOfflineMode() {
            return Application.internetReachability == NetworkReachability.NotReachable;
        }

        private void RestoreHudForMatchStart() {
            EnsureGameplayRootVisible();
            if(_matchTimerContainer == null) return;
            IsPostMatch = false;
            _matchTimerContainer.style.display = DisplayStyle.Flex;
            if(PostMatchManager.Instance != null) {
                PostMatchManager.Instance.ShowInGameHudAfterPostMatch();
            }
        }

        private void EnsureGameplayRootVisible() {
            if(Root == null) return;
            var rootContainer = Root.Q<VisualElement>("root-container");
            if(rootContainer != null) {
                rootContainer.style.display = DisplayStyle.Flex;
            }
        }

        private void TogglePause() {
            if(!IsGameplaySceneContext()) return;

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
            optionsMenuManager.OnButtonClickedCallback = UISound.PlayButtonClick;
            optionsMenuManager.MouseEnterCallback = _ => UISound.PlayButtonHover();
            optionsMenuManager.MouseHoverCallback = _ => UISound.PlayButtonHover();
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
            EventBus.Publish(new PauseMenuStateChangedEvent(true));
            _pauseMenuPanel.RemoveFromClassList("hidden");
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            ClosePauseLoadoutDropdowns();
            SyncPauseLoadoutFromSettings();
            RefreshPauseLoadoutVisuals();
            UpdateChallengeTimers(force: true);
            SetPauseChallengesDirty();
            UpdatePauseChallengesIfDirty();
            _nextChallengeTimerUpdateAt = Time.unscaledTime + ChallengeTimerUpdateIntervalSeconds;
            
            if (_pauseChallengesContainer != null) _pauseChallengesContainer.RemoveFromClassList("hidden");
            if(_localController) _localController.moveInput = Vector2.zero;
        }
        
        private void ResumeGame() {
            IsPaused = false;
            EventBus.Publish(new PauseMenuStateChangedEvent(false));
            ClosePauseLoadoutDropdowns();
            _pauseMenuPanel.AddToClassList("hidden");
            _optionsPanel.AddToClassList("hidden");
            SetOptionsOpenState(false);
            _modalHost.HideModal("quit-confirmation");
            if (_pauseChallengesContainer != null) _pauseChallengesContainer.AddToClassList("hidden");
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;
            UnityEngine.Cursor.visible = false;
        }

        private void ShowOptions() {
            if(!IsGameplaySceneContext()) return;
            
            if (optionsMenuManager != null) {
                optionsMenuManager.LoadSettings();
            }
            
            ClosePauseLoadoutDropdowns();
            _pauseMenuPanel.AddToClassList("hidden");
            // Hide challenges when showing options
            if(_pauseChallengesContainer != null) {
                _pauseChallengesContainer.AddToClassList("hidden");
            }
            _optionsPanel.RemoveFromClassList("hidden");
            SetOptionsOpenState(true);
            
            // Call after panel is visible to ensure repaint works
            if (optionsMenuManager != null) {
                optionsMenuManager.OnOptionsPanelShown();
            }
        }

        private void HideOptions() {
            if(!IsGameplaySceneContext()) return;
            _optionsPanel.AddToClassList("hidden");
            SetOptionsOpenState(false);
            _pauseMenuPanel.RemoveFromClassList("hidden");
            RefreshPauseLoadoutVisuals();
            // Show challenges again when returning to pause menu
            if(_pauseChallengesContainer != null) {
                _pauseChallengesContainer.RemoveFromClassList("hidden");
            }
        }

        private void ShowQuitConfirmation() {
            if(_modalHost == null) {
                DevLog.LogWarning("[GameMenuManager] Quit modal host is not initialized yet.");
                return;
            }

            if(_quitConfirmationModal == null) return;
            // Hide pause menu and challenges when showing quit confirmation modal
            ClosePauseLoadoutDropdowns();
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
                SetOptionsOpenState(false);
                IsPaused = false;
                EventBus.Publish(new PauseMenuStateChangedEvent(false));
                UnityEngine.Cursor.lockState = CursorLockMode.None;
                UnityEngine.Cursor.visible = true;
            } catch(Exception e) {
                Debug.LogException(e);
            }
        }

        private void SetOptionsOpenState(bool isOptionsOpen) {
            if(Root == null) return;

            if(isOptionsOpen) {
                Root.AddToClassList("options-open");
            } else {
                Root.RemoveFromClassList("options-open");
            }

            if(menuBlurController != null) {
                menuBlurController.SetBlurActive(isOptionsOpen);
            }
        }

        private void InitializeMenuBlurController() {
            if(optionsBlurVolume == null) {
                return;
            }

            if(menuBlurController == null) {
                menuBlurController = GetComponent<MenuBlurVolumeController>();
            }

            if(menuBlurController == null) {
                menuBlurController = gameObject.AddComponent<MenuBlurVolumeController>();
            }

            menuBlurController.SetBlurVolume(optionsBlurVolume);
        }

        private bool IsGameplaySceneContext() {
            return SessionManager.IsGameplaySceneName(_cachedSceneName);
        }
        #endregion
    }
}


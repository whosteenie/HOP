using System.Collections;
using System.Collections.Generic;
using Game.Progression;
using Game.UI;
using Game.Settings;
using Network.Services;
using UnityEngine;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace Game.Menu {
    /// <summary>
    /// Manages UI panel switching, button events, and modal dialogs for the main menu.
    /// Handles visual updates and user interactions.
    /// </summary>
    public class MainMenuUIManager : UIElementBase {
        [Header("References")]
        public LoadoutManager LoadoutManager; // Added for Profile View access

        private UIModalHost _modalHost;
        private VisualElement MainMenuPanel { get; set; }
        private VisualElement _gamemodePanel;
        private VisualElement _playGamemodePanel;
        private VisualElement _lobbyPanel;
        private VisualElement _loadoutPanel;
        private VisualElement _optionsPanel;
        private VisualElement _creditsPanel;
        private Button _cardGunTag;

        // HUD / Global Containers
        [Header("Templates")]
        [SerializeField] private VisualTreeAsset partyMemberTemplate;
        [SerializeField] private VisualTreeAsset challengeCardTemplate;
        [SerializeField] private VisualTreeAsset challengeRowTemplate;
        public VisualTreeAsset PartyMemberTemplate => partyMemberTemplate;

        public VisualElement PartyContainer { get; private set; }
        public VisualElement StatusContainer { get; private set; }
        public Label MatchmakingStatusLabel { get; private set; }
        public Label QueueGamemodeLabel { get; private set; }
        public Label QueueTimerLabel { get; private set; }
        private Button CancelMatchmakingButton { get; set; }

        // Challenges (Main Menu - Bottom Right)
        private VisualElement _mainMenuChallengesContainer;
        private VisualElement _dailyChallengesCard;
        private VisualElement _weeklyChallengesCard;
        private readonly Color _progressBarColor = new(1f, 0.392f, 0.392f); // #ff6464

        // Buttons
        private Button _playButtonMatchmaking;
        private Button _playButtonPrivate;
        private Button _loadoutButton;
        private Button _optionsButton;
        private Button _creditsButton;
        private Button _quitButton;
        private Button _backGamemodeButton;
        private Button _backGamemodesButton;
        private Button _backCreditsButton;

        // Gamemode Cards
        private Button _cardDeathmatch;
        private Button _cardTeamDeathmatch;
        private Button _cardHopball;
        private Button _cardKoth;

        // Private Lobby Dropdown
        private VisualElement GamemodeDropdownContainer { get; set; }
        public Label GamemodeDisplayLabel { get; private set; }
        public VisualElement GamemodeDropdownMenu { get; private set; }
        private List<Button> _gamemodeOptions;

        private List<Button> _buttons;
        private List<Button> _backButtons;

        // First-time setup modal
        private VisualElement _firstTimeModal;
        private TextField _firstTimeNameInput;
        private Button _firstTimeContinueButton;

        // Quit confirmation modal
        private VisualElement _quitConfirmationModal;
        private Button _quitConfirmationYes;
        private Button _quitConfirmationNo;

        // Lobby leave modal
        private VisualElement _lobbyLeaveModal;
        private Button _lobbyLeaveYes;
        private Button _lobbyLeaveNo;

        // Misc
        private TextField _nameInput;
        private Image _logoGithub;
        private VisualElement _toastContainer;
        private Label _versionLabel;

        // Context Menu
        public VisualElement PartyContextMenu { get; private set; }
        public VisualElement ContextMenuBackdrop { get; private set; }
        public Button CtxProfile { get; private set; }
        public Button CtxSteamProfile { get; private set; }
        public Button CtxMuteChat { get; private set; }
        public Button CtxMuteVoice { get; private set; }
        public Button CtxBlock { get; private set; }
        public Button CtxMakeHost { get; private set; }
        public Button CtxKick { get; private set; }
        public Button CtxLeave { get; private set; }
        public VisualElement CtxSeparatorManagement { get; private set; }
        public VisualElement CtxSeparatorMute { get; private set; }

        // Events
        public System.Action OnPlayMatchmakingClicked;
        public System.Action OnPlayPrivateClicked;
        public System.Action<string> OnGamemodeSelected;
        public System.Action OnCancelMatchmakingClicked;
        public System.Action OnLoadoutClicked;
        public System.Action OnOptionsClicked;
        public System.Action OnCreditsClicked;
        public System.Action OnQuitConfirmed;
        public System.Action OnQuitCancelled;
        public System.Action OnLobbyLeaveConfirmed;
        public System.Action OnLobbyLeaveCancelled;
        public System.Action OnFirstTimeContinue;
        public System.Action<string> OnNameInputChanged;
        public System.Action<VisualElement> OnShowPanel;
        public System.Action OnGamemodeDropdownClicked;

        protected override void Awake() {
            base.Awake();
            if(Root == null) return;
            _modalHost = new UIModalHost(this, Root);
        }

        protected override void Start() {
            base.Start();
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        protected override void OnInitialize() {
            FindUIElements();
            SetupFirstTimeModal();
            RegisterUIEvents();
            SetupMainMenuChallenges();
        }

        protected override Dictionary<string, System.Type> GetRequiredElements() {
            return new Dictionary<string, System.Type> {
                { "main-menu-panel", typeof(VisualElement) },
                { "play-button-matchmaking", typeof(Button) },
                { "play-button-private", typeof(Button) },
                { "loadout-button", typeof(Button) },
                { "options-button", typeof(Button) },
                { "credits-button", typeof(Button) },
                { "quit-button", typeof(Button) }
            };
        }

        /// <summary>
        /// Initializes the UI manager and ensures proper visibility of global containers.
        /// Called externally to hide game menu UI when in main menu.
        /// </summary>
        public void InitializeGameMenuVisibility() {
            var gameMenu = GameMenuManager.Instance;
            if(gameMenu != null && gameMenu.TryGetComponent(out UIDocument doc) && doc != null) {
                var gameRoot = doc.rootVisualElement;
                var rootContainer = gameRoot?.Q<VisualElement>("root-container");
                if(rootContainer != null) {
                    rootContainer.style.display = DisplayStyle.None;
                }
            }
        }

        /// <summary>
        /// Queries and caches all necessary UI elements from the UXML root.
        /// </summary>
        private void FindUIElements() {
            // Panels (required)
            MainMenuPanel = QRequired<VisualElement>("main-menu-panel");
            _playGamemodePanel = QOptional<VisualElement>("play-gamemode-panel");
            _lobbyPanel = QOptional<VisualElement>("lobby-panel");
            _loadoutPanel = QOptional<VisualElement>("loadout-panel");
            _nameInput = QOptional<TextField>("player-name-input");
            _optionsPanel = QOptional<VisualElement>("options-panel");
            _creditsPanel = QOptional<VisualElement>("credits-panel");

            // Buttons (required)
            _playButtonMatchmaking = QRequired<Button>("play-button-matchmaking");
            _playButtonPrivate = QRequired<Button>("play-button-private");
            _loadoutButton = QRequired<Button>("loadout-button");
            _optionsButton = QRequired<Button>("options-button");
            _creditsButton = QRequired<Button>("credits-button");
            _quitButton = QRequired<Button>("quit-button");
            _backGamemodeButton = QOptional<Button>("back-to-main");
            _backGamemodesButton = QOptional<Button>("back-gamemodes-to-main");
            _backCreditsButton = QOptional<Button>("back-to-lobby");

            // Gamemode Cards
            _cardDeathmatch = QOptional<Button>("card-deathmatch");
            _cardTeamDeathmatch = QOptional<Button>("card-team-deathmatch");
            _cardHopball = QOptional<Button>("card-hopball");
            _cardKoth = QOptional<Button>("card-koth");
            _cardGunTag = QOptional<Button>("card-gun-tag");

            QOptional<VisualElement>("loading-overlay");
            GamemodeDropdownContainer = QOptional<VisualElement>("gamemode-dropdown-container");
            GamemodeDisplayLabel = QOptional<Label>("gamemode-display-label");
            GamemodeDropdownMenu = QOptional<VisualElement>("gamemode-dropdown-menu");
            
            // Party and Status containers
            PartyContainer = QOptional<VisualElement>("party-container");
            StatusContainer = QOptional<VisualElement>("status-container");
            
            // Challenges container (will be created if not exists)
            _mainMenuChallengesContainer = QOptional<VisualElement>("main-menu-challenges-container");
            
            _gamemodeOptions = new List<Button>();

            // First-time setup modal
            _firstTimeModal = QOptional<VisualElement>("first-time-setup-modal");
            _firstTimeNameInput = QOptional<TextField>("first-time-name-input");
            _firstTimeContinueButton = QOptional<Button>("first-time-continue-button");

            // Quit confirmation modal
            _quitConfirmationModal = QOptional<VisualElement>("quit-confirmation-modal");
            _quitConfirmationYes = QOptional<Button>("quit-confirmation-yes");
            _quitConfirmationNo = QOptional<Button>("quit-confirmation-no");

            // Lobby leave modal
            _lobbyLeaveModal = QOptional<VisualElement>("lobby-leave-modal");
            _lobbyLeaveYes = QOptional<Button>("lobby-leave-yes");
            _lobbyLeaveNo = QOptional<Button>("lobby-leave-no");

            // Misc
            _logoGithub = QOptional<Image>("credits-logo");
            _toastContainer = QOptional<VisualElement>("toast-container");
            _versionLabel = QOptional<Label>("version-text");
            
            if (StatusContainer != null) {
                MatchmakingStatusLabel = QOptional<Label>("matchmaking-status-label");
                QueueGamemodeLabel = QOptional<Label>("queue-gamemode-label");
                QueueTimerLabel = QOptional<Label>("queue-timer-label");
                CancelMatchmakingButton = QOptional<Button>("cancel-matchmaking-button");
                
                StatusContainer.pickingMode = PickingMode.Ignore;
                if (MatchmakingStatusLabel != null) MatchmakingStatusLabel.pickingMode = PickingMode.Ignore;
                if (CancelMatchmakingButton != null) CancelMatchmakingButton.pickingMode = PickingMode.Position;
            }
            
            if (PartyContainer != null) {
                PartyContainer.pickingMode = PickingMode.Ignore;
            }

            if (_versionLabel != null) {
                _versionLabel.text = $"v{Application.version}";
            }

            _buttons = new List<Button> {
                _playButtonMatchmaking,
                _playButtonPrivate,
                _loadoutButton,
                _optionsButton,
                _creditsButton,
                _cardDeathmatch,
                _cardTeamDeathmatch,
                _cardHopball,
                _cardKoth,
                _cardGunTag
                // CancelMatchmakingButton removed from generic list to prevent double sound registration
            };

            _backButtons = new List<Button> {
                _backGamemodeButton,
                _backGamemodesButton,
                _backCreditsButton
            };
            
            _buttons.AddRange(_gamemodeOptions);

            // Context Menu
            PartyContextMenu = QOptional<VisualElement>("party-context-menu");
            ContextMenuBackdrop = QOptional<VisualElement>("context-menu-backdrop");
            CtxProfile = QOptional<Button>("ctx-profile");
            CtxSteamProfile = QOptional<Button>("ctx-steam-profile");
            CtxMuteChat = QOptional<Button>("ctx-mute-chat");
            CtxMuteVoice = QOptional<Button>("ctx-mute-voice");
            CtxBlock = QOptional<Button>("ctx-block");
            CtxMakeHost = QOptional<Button>("ctx-make-host");
            CtxKick = QOptional<Button>("ctx-kick");
            CtxLeave = QOptional<Button>("ctx-leave");
            CtxSeparatorManagement = QOptional<VisualElement>("ctx-separator-management");
            CtxSeparatorMute = QOptional<VisualElement>("ctx-separator-mute");
            
            // Register hover for Context buttons
            var ctxButtons = new[] { CtxProfile, CtxSteamProfile, CtxMuteChat, CtxMuteVoice, CtxBlock, CtxMakeHost, CtxKick, CtxLeave };
            foreach(var b in ctxButtons) {
                if(b != null) UISoundService.RegisterButtonHover(b);
            }
        }

        /// <summary>
        /// Registers click and hover events for UI buttons and interactive elements.
        /// </summary>
        private void RegisterUIEvents() {
            // Generic button handlers
            foreach(var b in _buttons) {
                if(b == null) continue;
                UISoundService.RegisterButtonHover(b);
                System.Action clickHandler = () => UISoundService.PlayButtonClick();
                b.clicked += clickHandler;
                RegisterCleanup(() => b.clicked -= clickHandler);
            }

            foreach(var b in _backButtons) {
                if(b == null) continue;
                UISoundService.RegisterButtonHover(b);
                // No generic click here, handled in specialized ones below
            }

            // Cancel matchmaking button
            if (CancelMatchmakingButton != null) {
                System.Action cancelHandler = () => OnCancelMatchmakingClicked?.Invoke();
                CancelMatchmakingButton.clicked += cancelHandler;
                RegisterCleanup(() => CancelMatchmakingButton.clicked -= cancelHandler);
            }

            // Main menu navigation
            if (_playButtonMatchmaking != null) {
                System.Action playMatchHandler = () => OnPlayMatchmakingClicked?.Invoke();
                _playButtonMatchmaking.clicked += playMatchHandler;
                RegisterCleanup(() => _playButtonMatchmaking.clicked -= playMatchHandler);
            }
            if (_playButtonPrivate != null) {
                System.Action playPrivateHandler = () => OnPlayPrivateClicked?.Invoke();
                _playButtonPrivate.clicked += playPrivateHandler;
                RegisterCleanup(() => _playButtonPrivate.clicked -= playPrivateHandler);
            }

            System.Action loadoutHandler = () => {
                if(_nameInput != null) {
                    _nameInput.value = GameSettings.Data.player.playerName;
                }
                OnLoadoutClicked?.Invoke();
            };
            _loadoutButton.clicked += loadoutHandler;
            RegisterCleanup(() => _loadoutButton.clicked -= loadoutHandler);

            System.Action optionsHandler = () => {
                OnOptionsClicked?.Invoke();
            };
            _optionsButton.clicked += optionsHandler;
            RegisterCleanup(() => _optionsButton.clicked -= optionsHandler);

            System.Action creditsHandler = () => {
                OnCreditsClicked?.Invoke();
            };
            _creditsButton.clicked += creditsHandler;
            RegisterCleanup(() => _creditsButton.clicked -= creditsHandler);

            _quitButton.clicked += ShowQuitConfirmation;
            RegisterCleanup(() => _quitButton.clicked -= ShowQuitConfirmation);
            UISoundService.RegisterButtonHover(_quitButton);

            // Gamemode Card Clicks
            if (_cardDeathmatch != null) {
                System.Action cardHandler = () => OnGamemodeSelected?.Invoke("Deathmatch");
                _cardDeathmatch.clicked += cardHandler;
                RegisterCleanup(() => _cardDeathmatch.clicked -= cardHandler);
            }
            if (_cardTeamDeathmatch != null) {
                System.Action cardHandler = () => OnGamemodeSelected?.Invoke("Team Deathmatch");
                _cardTeamDeathmatch.clicked += cardHandler;
                RegisterCleanup(() => _cardTeamDeathmatch.clicked -= cardHandler);
            }
            if (_cardHopball != null) {
                System.Action cardHandler = () => OnGamemodeSelected?.Invoke("Hopball");
                _cardHopball.clicked += cardHandler;
                RegisterCleanup(() => _cardHopball.clicked -= cardHandler);
            }
            if (_cardKoth != null) {
                System.Action cardHandler = () => OnGamemodeSelected?.Invoke("King of the Hill");
                _cardKoth.clicked += cardHandler;
                RegisterCleanup(() => _cardKoth.clicked -= cardHandler);
            }
            if (_cardGunTag != null) {
                System.Action cardHandler = () => OnGamemodeSelected?.Invoke("Gun Tag");
                _cardGunTag.clicked += cardHandler;
                RegisterCleanup(() => _cardGunTag.clicked -= cardHandler);
            }

            // Back buttons
            if (_backGamemodeButton != null) {
                System.Action backHandler = () => OnShowPanel?.Invoke(MainMenuPanel);
                _backGamemodeButton.clicked += backHandler;
                RegisterCleanup(() => _backGamemodeButton.clicked -= backHandler);
            }
            if (_backGamemodesButton != null) {
                System.Action backHandler = () => {
                    UISoundService.PlayButtonClick(isBack: true);
                    OnShowPanel?.Invoke(MainMenuPanel);
                };
                _backGamemodesButton.clicked += backHandler;
                RegisterCleanup(() => _backGamemodesButton.clicked -= backHandler);
            }
            if (_backCreditsButton != null) {
                System.Action backHandler = () => {
                    UISoundService.PlayButtonClick(isBack: true);
                    OnShowPanel?.Invoke(MainMenuPanel);
                };
                _backCreditsButton.clicked += backHandler;
                RegisterCleanup(() => _backCreditsButton.clicked -= backHandler);
            }

            // Private Lobby Dropdown
            if (GamemodeDropdownContainer != null) {
                EventCallback<ClickEvent> dropdownHandler = _ => OnGamemodeDropdownClicked?.Invoke();
                GamemodeDropdownContainer.RegisterCallback(dropdownHandler);
                RegisterCleanup(() => GamemodeDropdownContainer.UnregisterCallback(dropdownHandler));
            }

            foreach (var opt in _gamemodeOptions) {
                if (opt == null) continue;
                System.Action optHandler = () => {
                    UISoundService.PlayButtonClick();
                    OnGamemodeSelected?.Invoke(opt.text);
                };
                opt.clicked += optHandler;
                RegisterCleanup(() => opt.clicked -= optHandler);
            }

            // Quit confirmation modal
            if(_quitConfirmationYes != null) {
                System.Action yesHandler = () => {
                    UISoundService.PlayButtonClick();
                    _modalHost.HideModal("quit-confirmation");
                    OnQuitConfirmed?.Invoke();
                };
                _quitConfirmationYes.clicked += yesHandler;
                RegisterCleanup(() => _quitConfirmationYes.clicked -= yesHandler);
                UISoundService.RegisterButtonHover(_quitConfirmationYes);
            }

            if(_quitConfirmationNo != null) {
                System.Action noHandler = () => {
                    UISoundService.PlayButtonClick();
                    _modalHost.HideModal("quit-confirmation");
                    OnQuitCancelled?.Invoke();
                };
                _quitConfirmationNo.clicked += noHandler;
                RegisterCleanup(() => _quitConfirmationNo.clicked -= noHandler);
                UISoundService.RegisterButtonHover(_quitConfirmationNo);
            }

            // Lobby leave modal
            if(_lobbyLeaveYes != null) {
                System.Action yesHandler = () => {
                    UISoundService.PlayButtonClick(isBack: true);
                    _modalHost.HideModal("lobby-leave");
                    OnLobbyLeaveConfirmed?.Invoke();
                };
                _lobbyLeaveYes.clicked += yesHandler;
                RegisterCleanup(() => _lobbyLeaveYes.clicked -= yesHandler);
                UISoundService.RegisterButtonHover(_lobbyLeaveYes);
            }

            if(_lobbyLeaveNo != null) {
                System.Action noHandler = () => {
                    UISoundService.PlayButtonClick();
                    _modalHost.HideModal("lobby-leave");
                    OnLobbyLeaveCancelled?.Invoke();
                };
                _lobbyLeaveNo.clicked += noHandler;
                RegisterCleanup(() => _lobbyLeaveNo.clicked -= noHandler);
                UISoundService.RegisterButtonHover(_lobbyLeaveNo);
            }

            // Credits
            if(_logoGithub != null) {
                EventCallback<ClickEvent> clickHandler = _ => Application.OpenURL("https://github.com/whosteenie/HOP");
                EventCallback<MouseEnterEvent> hoverHandler = _ => UISoundService.PlayButtonHover();
                _logoGithub.RegisterCallback(clickHandler);
                _logoGithub.RegisterCallback(hoverHandler);
                RegisterCleanup(() => {
                    _logoGithub.UnregisterCallback(clickHandler);
                    _logoGithub.UnregisterCallback(hoverHandler);
                });
            }
        }

        private void SetupFirstTimeModal() {
            if(_firstTimeContinueButton == null) return;
            System.Action continueHandler = () => {
                UISoundService.PlayButtonClick();
                OnFirstTimeContinue?.Invoke();
            };
            _firstTimeContinueButton.clicked += continueHandler;
            RegisterCleanup(() => _firstTimeContinueButton.clicked -= continueHandler);
            UISoundService.RegisterButtonHover(_firstTimeContinueButton);
        }

        /// <summary>
        /// Logic for handling first-time setup or name entry (Deprecated/Unused).
        /// </summary>
        public void CheckFirstTimeSetup() {
            HideFirstTimeSetup();
        }

        public void HideFirstTimeSetup() {
            if(_firstTimeModal != null) {
                _firstTimeModal.AddToClassList("hidden");
            }
        }

        /// <summary>
        /// Returns the text from the first-time name input field.
        /// </summary>
        public string GetFirstTimeNameInput() {
            return _firstTimeNameInput != null ? _firstTimeNameInput.value : string.Empty;
        }

        // Panel references (for external access - panel management stays in MainMenuManager for now)

        /// <summary>
        /// Enables a specific button and registers its hover events.
        /// </summary>
        public void EnableButton(Button button) {
            SetButtonEnabled(button, true);
        }

        /// <summary>
        /// Disables a specific button and unregisters its hover events.
        /// </summary>
        public void DisableButton(Button button) {
            SetButtonEnabled(button, false);
        }

        private static void SetButtonEnabled(Button button, bool enabled) {
            if(button == null) return;
            
            button.SetEnabled(enabled);

            // Handle different styles
            bool isTextButton = button.ClassListContains("text-button");

            if (enabled) {
                if (!isTextButton) button.AddToClassList("menu-chip-enabled");
                UISoundService.RegisterButtonHover(button);
            } else {
                button.RemoveFromClassList("menu-chip-enabled");
                UISoundService.UnregisterButtonHover(button);
            }
        }

        public VisualElement PlayGamemodePanel => _playGamemodePanel;
        public Button GetPlayButtonMatchmaking() => _playButtonMatchmaking;
        public Button GetPlayButtonPrivate() => _playButtonPrivate;

        /// <summary>
        /// Enables or disables the primary matchmaking and private game buttons.
        /// </summary>
        public void SetMenuButtonsEnabled(bool enabled) {
            if (enabled) {
                EnableButton(_playButtonMatchmaking);
                EnableButton(_playButtonPrivate);
            } else {
                DisableButton(_playButtonMatchmaking);
                DisableButton(_playButtonPrivate);
            }
        }

        /// <summary>
        /// Shows the quit confirmation modal.
        /// </summary>
        private void ShowQuitConfirmation() {
            UISoundService.PlayButtonClick(isBack: true);
            if(_quitConfirmationModal != null) {
                _modalHost.ShowExistingModal(_quitConfirmationModal, "quit-confirmation");
            }
        }

        /// <summary>
        /// Shows the confirmation modal for leaving a lobby.
        /// </summary>
        public void ShowLobbyLeaveConfirmation() {
            UISoundService.PlayButtonClick(isBack: true);
            if(_lobbyLeaveModal != null) {
                _modalHost.ShowExistingModal(_lobbyLeaveModal, "lobby-leave");
            }
        }

        /// <summary>
        /// Coroutine to display a temporary toast notification.
        /// </summary>
        /// <param name="message">The message to display.</param>
        public IEnumerator CopyToast(string message) {
            if(_toastContainer == null) yield break;

            var toast = new Label(message) {
                name = "toast"
            };
            toast.AddToClassList("toast");
            _toastContainer.Add(toast);
            toast.AddToClassList("show");

            yield return new WaitForSeconds(1.2f);

            toast.RemoveFromClassList("show");
            toast.AddToClassList("hide");

            yield return new WaitForSeconds(0.3f);
            _toastContainer.Remove(toast);
        }

        private void SetupMainMenuChallenges() {
            // Create challenges container if it doesn't exist
            if(_mainMenuChallengesContainer == null) {
                _mainMenuChallengesContainer = new VisualElement {
                    name = "main-menu-challenges-container",
                    style = {
                        position = Position.Absolute,
                        right = 20,
                        bottom = 20,
                        width = 300,
                        flexDirection = FlexDirection.Column,
                        alignItems = Align.FlexEnd
                    }
                };
                MainMenuPanel.Add(_mainMenuChallengesContainer);
            }

            // Create Daily Challenges Card
            _dailyChallengesCard = CreateChallengeCard("Daily Challenges");
            if(_dailyChallengesCard != null) {
                _dailyChallengesCard.style.marginBottom = 10;
                _mainMenuChallengesContainer.Add(_dailyChallengesCard);
            }

            // Create Weekly Challenges Card
            _weeklyChallengesCard = CreateChallengeCard("Weekly Challenges");
            if(_weeklyChallengesCard != null) {
                _mainMenuChallengesContainer.Add(_weeklyChallengesCard);
            }

            // Populate challenges after cards are created
            UpdateMainMenuChallenges();
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
                // Ensure list container exists
                if(listContainer == null) {
                    listContainer = new VisualElement { name = "challenge-list" };
                    card.Add(listContainer);
                }
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

        private void UpdateMainMenuChallenges() {
            var pm = ProgressionManager.Instance;
            if(pm == null || pm.Data == null) {
                Debug.LogWarning("[MainMenuUIManager] ProgressionManager or Data is null, cannot update challenges");
                return;
            }
            RenderChallengeList(_dailyChallengesCard, pm.Data.dailyChallenges);
            RenderChallengeList(_weeklyChallengesCard, pm.Data.weeklyChallenges);
        }

        private void RenderChallengeList(VisualElement card, System.Collections.Generic.List<ActiveChallengeData> challenges) {
            if(card == null) {
                Debug.LogWarning("[MainMenuUIManager] Challenge card is null");
                return;
            }
            var list = card.Q<VisualElement>("challenge-list");
            if(list == null) {
                Debug.LogWarning("[MainMenuUIManager] Challenge list container not found in card");
                return;
            }
            
            list.Clear();
            if(challenges == null || challenges.Count == 0) {
                // Show empty state
                var emptyLabel = new Label("No challenges available") {
                    style = {
                        fontSize = 12,
                        color = new Color(0.7f, 0.7f, 0.7f),
                        unityTextAlign = TextAnchor.MiddleCenter,
                        marginTop = 20
                    }
                };
                list.Add(emptyLabel);
                return;
            }
            var pm = ProgressionManager.Instance;
            if(pm == null) {
                Debug.LogWarning("[MainMenuUIManager] ProgressionManager is null");
                return;
            }

            foreach(var activeChallenge in challenges) {
                var def = pm.GetChallengeDefinition(activeChallenge.challengeID);
                if(def == null) continue;

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
                if(progress > target) progress = target;
                    
                var descText = def.Description;
                try {
                    if(!string.IsNullOrEmpty(activeChallenge.filterID)) {
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
            if(bar == null) return;
            bar.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f);
            bar.schedule.Execute(() => {
                var fill = bar.Q<VisualElement>(className: "unity-progress-bar__progress");
                if(fill != null) fill.style.backgroundColor = _progressBarColor;
            });
        }

        /// <summary>
        /// Refreshes the challenges displayed on the main menu.
        /// Can be called externally to update challenges when needed.
        /// </summary>
        public void RefreshMainMenuChallenges() {
            UpdateMainMenuChallenges();
        }

        // Getters for external access
        public VisualElement GetGamemodePanel() => _gamemodePanel;
        public VisualElement GetLobbyPanel() => _lobbyPanel;
        public VisualElement GetLoadoutPanel() => _loadoutPanel;
        public VisualElement GetOptionsPanel() => _optionsPanel;
        public VisualElement GetCreditsPanel() => _creditsPanel;
        public Button GetBackGamemodeButton() => _backGamemodeButton;
    }
}

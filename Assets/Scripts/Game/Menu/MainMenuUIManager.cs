using System.Collections;
using System.Collections.Generic;
using Game.Progression;
using Game.UI;
using Network.Events;
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
        public LoadoutManager loadoutManager; // Added for Profile View access

        private UIModalHost _modalHost;
        private VisualElement MainMenuPanel { get; set; }
        private VisualElement _gamemodePanel;
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
        private Label _dailyTimerLabel;
        private VisualElement _weeklyChallengesCard;
        private Label _weeklyTimerLabel;
        private bool _mainMenuChallengesContainerErrorLogged;
        private bool _challengeCardTemplateErrorLogged;
        private bool _challengeRowTemplateErrorLogged;

        // Buttons
        private Button _playButtonMatchmaking;
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
        private Button _gamemodePrivateMatchButton;
        private Button _gamemodeWipButton;

        // Private Lobby Dropdown
        private VisualElement GamemodeDropdownContainer { get; set; }
        public Label GamemodeDisplayLabel { get; private set; }
        public VisualElement GamemodeDropdownMenu { get; private set; }
        private List<Button> _gamemodeOptions;

        private List<Button> _buttons;
        private List<Button> _backButtons;

        // Quit confirmation modal
        private VisualElement _quitConfirmationModal;
        private Button _quitConfirmationYes;
        private Button _quitConfirmationNo;

        // Lobby leave modal
        private VisualElement _lobbyLeaveModal;
        private Button _lobbyLeaveYes;
        private Button _lobbyLeaveNo;

        // Misc
        private Label _playerNameLabel;
        private Image _logoGithub;
        private VisualElement _toastContainer;
        private Label _versionLabel;
        private Label _toastLabel;
        private Coroutine _toastRoutine;
        private string _toastMessage;
        private bool _toastLabelErrorLogged;

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
        public Button CtxSwitchTeam { get; private set; }
        public VisualElement CtxSeparatorManagement { get; private set; }
        public VisualElement CtxSeparatorMute { get; private set; }

        // Events
        public System.Action OnPlayMatchmakingClicked;
        public System.Action OnGamemodePrivateMatchClicked;
        public System.Action OnGamemodeWipClicked;
        public System.Action<string> OnGamemodeSelected;
        public System.Action OnCancelMatchmakingClicked;
        public System.Action OnLoadoutClicked;
        public System.Action OnOptionsClicked;
        public System.Action OnCreditsClicked;
        public System.Action OnQuitConfirmed;
        public System.Action OnQuitCancelled;
        public System.Action OnLobbyLeaveConfirmed;
        public System.Action OnLobbyLeaveCancelled;
        public System.Action<VisualElement> OnShowPanel;
        public System.Action OnGamemodeDropdownClicked;

        public MainMenuUIManager(List<Button> gamemodeOptions) {
            _gamemodeOptions = gamemodeOptions;
        }

        protected override void Start() {
            base.Start();
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        protected override void OnInitialize() {
            FindUIElements();
            if(_modalHost == null && Root != null) {
                _modalHost = new UIModalHost(this, Root);
            }
            RegisterUIEvents();
            SetupMainMenuChallenges();
        }

        protected override Dictionary<string, System.Type> GetRequiredElements() {
            return new Dictionary<string, System.Type> {
                { "main-menu-panel", typeof(VisualElement) },
                { "play-button-matchmaking", typeof(Button) },
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
        public static void InitializeGameMenuVisibility() {
            var gameMenu = GameMenuManager.Instance;
            if(gameMenu == null || !gameMenu.TryGetComponent(out UIDocument doc) || doc == null) return;
            var gameRoot = doc.rootVisualElement;
            var rootContainer = gameRoot?.Q<VisualElement>("root-container");
            if(rootContainer != null) {
                rootContainer.style.display = DisplayStyle.None;
            }
        }

        /// <summary>
        /// Queries and caches all necessary UI elements from the UXML root.
        /// </summary>
        private void FindUIElements() {
            // Panels (required)
            MainMenuPanel = QRequired<VisualElement>("main-menu-panel");
            PlayGamemodePanel = QOptional<VisualElement>("play-gamemode-panel");
            _lobbyPanel = QOptional<VisualElement>("lobby-panel");
            _loadoutPanel = QOptional<VisualElement>("loadout-panel");
            _playerNameLabel = QOptional<Label>("player-name-label");
            _optionsPanel = QOptional<VisualElement>("options-panel");
            _creditsPanel = QOptional<VisualElement>("credits-panel");

            // Buttons (required)
            _playButtonMatchmaking = QRequired<Button>("play-button-matchmaking");
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
            _gamemodePrivateMatchButton = QOptional<Button>("gamemode-private-match-button");
            _gamemodeWipButton = QOptional<Button>("gamemode-wip-button");

            QOptional<VisualElement>("loading-overlay");
            GamemodeDropdownContainer = QOptional<VisualElement>("gamemode-dropdown-container");
            GamemodeDisplayLabel = QOptional<Label>("gamemode-display-label");
            GamemodeDropdownMenu = QOptional<VisualElement>("gamemode-dropdown-menu");
            
            // Party and Status containers
            PartyContainer = QOptional<VisualElement>("party-container");
            StatusContainer = QOptional<VisualElement>("status-container");
            
            // Challenges container
            _mainMenuChallengesContainer = QOptional<VisualElement>("main-menu-challenges-container");
            
            _gamemodeOptions = new List<Button>();

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
            _toastLabel = QOptional<Label>("toast");
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
                _loadoutButton,
                _optionsButton,
                _creditsButton,
                _cardDeathmatch,
                _cardTeamDeathmatch,
                _cardHopball,
                _cardKoth,
                _cardGunTag,
                _gamemodePrivateMatchButton,
                _gamemodeWipButton
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
            CtxSwitchTeam = QOptional<Button>("ctx-switch-team");
            CtxSeparatorManagement = QOptional<VisualElement>("ctx-separator-management");
            CtxSeparatorMute = QOptional<VisualElement>("ctx-separator-mute");
            
            // Register hover for Context buttons
            var ctxButtons = new[] { CtxProfile, CtxSteamProfile, CtxMuteChat, CtxMuteVoice, CtxBlock, CtxMakeHost, CtxKick, CtxLeave, CtxSwitchTeam };
            foreach(var b in ctxButtons) {
                if(b != null) UISoundService.RegisterButtonHover(b);
            }
        }

        /// <summary>
        /// Registers click and hover events for UI buttons and interactive elements.
        /// </summary>
        private void RegisterUIEvents() {
            if(_buttons == null || _backButtons == null) {
                Debug.LogError("[MainMenuUIManager] Button collections were not initialized before event registration.", this);
                return;
            }

            // Generic button handlers
            foreach(var b in _buttons) {
                if(b == null) continue;
                try {
                    UISoundService.RegisterButtonHover(b);
                    System.Action clickHandler = () => UISoundService.PlayButtonClick();
                    b.clicked += clickHandler;
                    RegisterCleanup(() => b.clicked -= clickHandler);
                } catch(System.Exception ex) {
                    Debug.LogError($"[MainMenuUIManager] Failed to bind click/hover for button `{b.name}`: {ex}", this);
                }
            }

            foreach(var b in _backButtons) {
                if(b == null) continue;
                try {
                    UISoundService.RegisterButtonHover(b);
                    // No generic click here, handled in specialized ones below
                } catch(System.Exception ex) {
                    Debug.LogError($"[MainMenuUIManager] Failed to bind hover for back button `{b.name}`: {ex}", this);
                }
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
            System.Action loadoutHandler = () => {
                if(_playerNameLabel != null) _playerNameLabel.text = Social.StreamerMode.GetLocalDisplayName();
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
                System.Action cardHandler = () => OnGamemodeSelected?.Invoke("KOTH");
                _cardKoth.clicked += cardHandler;
                RegisterCleanup(() => _cardKoth.clicked -= cardHandler);
            }
            if (_cardGunTag != null) {
                System.Action cardHandler = () => OnGamemodeSelected?.Invoke("Gun Tag");
                _cardGunTag.clicked += cardHandler;
                RegisterCleanup(() => _cardGunTag.clicked -= cardHandler);
            }
            if(_gamemodePrivateMatchButton != null) {
                System.Action privateHandler = () => OnGamemodePrivateMatchClicked?.Invoke();
                _gamemodePrivateMatchButton.clicked += privateHandler;
                RegisterCleanup(() => _gamemodePrivateMatchButton.clicked -= privateHandler);
            }
            if(_gamemodeWipButton != null) {
                System.Action wipHandler = () => OnGamemodeWipClicked?.Invoke();
                _gamemodeWipButton.clicked += wipHandler;
                RegisterCleanup(() => _gamemodeWipButton.clicked -= wipHandler);
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

            // Challenge updates
            EventBus.Unsubscribe<ChallengesUpdatedEvent>(OnChallengesUpdatedEvent);
            EventBus.Subscribe<ChallengesUpdatedEvent>(OnChallengesUpdatedEvent);
            RegisterCleanup(() => EventBus.Unsubscribe<ChallengesUpdatedEvent>(OnChallengesUpdatedEvent));
        }

        // Panel references (for external access - panel management stays in MainMenuManager for now)

        /// <summary>
        /// Enables a specific button and registers its hover events.
        /// </summary>
        private static void EnableButton(Button button) {
            SetButtonEnabled(button, true);
        }

        /// <summary>
        /// Disables a specific button and unregisters its hover events.
        /// </summary>
        public static void DisableButton(Button button) {
            SetButtonEnabled(button, false);
        }

        private static void SetButtonEnabled(Button button, bool enabled) {
            if(button == null) return;
            
            button.SetEnabled(enabled);

            // Handle different styles
            var isTextButton = button.ClassListContains("text-button");

            if (enabled) {
                if (!isTextButton) button.AddToClassList("menu-chip-enabled");
                UISoundService.RegisterButtonHover(button);
            } else {
                button.RemoveFromClassList("menu-chip-enabled");
                UISoundService.UnregisterButtonHover(button);
            }
        }

        public VisualElement PlayGamemodePanel { get; private set; }

        public Button GetPlayButtonMatchmaking() => _playButtonMatchmaking;

        /// <summary>
        /// Enables or disables the primary Play (matchmaking) button. Private match is reached via Play -> Gamemode Select -> Private Match.
        /// </summary>
        public void SetMenuButtonsEnabled(bool shouldEnable) {
            if(shouldEnable)
                EnableButton(_playButtonMatchmaking);
            else
                DisableButton(_playButtonMatchmaking);
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
        /// Shows a temporary toast notification. Reuses a single toast (no stacking).
        /// </summary>
        /// <param name="message">The message to display.</param>
        /// <param name="anchor">Optional anchor element to position near.</param>
        public void ShowToast(string message, VisualElement anchor = null) {
            if(_toastContainer == null) return;
            if(string.IsNullOrEmpty(message)) return;

            if(_toastLabel == null) {
                if(_toastLabelErrorLogged) return;
                Debug.LogError(
                    "[MainMenuUIManager] Missing required `toast` label inside `toast-container` in MainMenu.uxml.",
                    this);
                _toastLabelErrorLogged = true;
                return;
            }

            _toastMessage = message;

            if(_toastRoutine != null) {
                StopCoroutine(_toastRoutine);
                _toastRoutine = null;
            }

            _toastLabel.text = message;

            if(anchor != null) {
                // Position toast near the anchor so it's noticed at point-of-click.
                // Try left-of-anchor first; if it would likely be off-screen, flip to right; otherwise center.
                var wb = anchor.worldBound;
                var rb = Root.worldBound;

                var x = wb.xMin - 12f;
                var useLeft = true;
                var useRight = false;

                // Heuristic: short toasts are ~220px wide; keep a little margin.
                if(x - 220f < rb.xMin + 8f) {
                    useLeft = false;
                    useRight = true;
                    x = wb.xMax + 12f;
                }

                if(useRight && x + 220f > rb.xMax - 8f) {
                    useRight = false;
                    x = wb.center.x;
                }

                _toastContainer.style.position = Position.Absolute;
                _toastContainer.style.left = x;
                _toastContainer.style.top = wb.center.y;
                _toastContainer.style.bottom = StyleKeyword.Null;

                if(useLeft) {
                    _toastContainer.style.translate = new Translate(
                        new Length(-100, LengthUnit.Percent),
                        new Length(-50, LengthUnit.Percent),
                        0
                    );
                } else if(useRight) {
                    _toastContainer.style.translate = new Translate(
                        new Length(0, LengthUnit.Percent),
                        new Length(-50, LengthUnit.Percent),
                        0
                    );
                } else {
                    _toastContainer.style.translate = new Translate(
                        new Length(-50, LengthUnit.Percent),
                        new Length(-50, LengthUnit.Percent),
                        0
                    );
                }
            } else {
                // Reset to default (bottom center) styling from USS.
                _toastContainer.style.position = StyleKeyword.Null;
                _toastContainer.style.left = StyleKeyword.Null;
                _toastContainer.style.top = StyleKeyword.Null;
                _toastContainer.style.bottom = StyleKeyword.Null;
                _toastContainer.style.translate = StyleKeyword.Null;
            }

            _toastRoutine = StartCoroutine(ToastRoutine());
        }

        /// <summary>
        /// Back-compat coroutine wrapper. Prefer <see cref="ShowToast"/>.
        /// </summary>
        public IEnumerator CopyToast(string message) {
            ShowToast(message);
            yield return null;
        }

        private IEnumerator ToastRoutine() {
            if(_toastLabel == null) yield break;

            _toastLabel.RemoveFromClassList("hide");
            _toastLabel.AddToClassList("show");

            yield return new WaitForSeconds(1.2f);

            // If another toast replaced our message, don't hide the new one.
            if(_toastLabel == null) yield break;
            if(_toastLabel.text != _toastMessage) yield break;

            _toastLabel.RemoveFromClassList("show");
            _toastLabel.AddToClassList("hide");

            yield return new WaitForSeconds(0.3f);

            if(_toastLabel == null) yield break;
            if(_toastLabel.text != _toastMessage) yield break;

            _toastRoutine = null;
        }

        private void SetupMainMenuChallenges() {
            if(_mainMenuChallengesContainer == null) {
                if(_mainMenuChallengesContainerErrorLogged) return;
                Debug.LogError(
                    "[MainMenuUIManager] Missing required `main-menu-challenges-container` in MainMenu.uxml.",
                    this);
                _mainMenuChallengesContainerErrorLogged = true;
                return;
            }

            _mainMenuChallengesContainer.Clear();

            // Create Daily Challenges Card
            _dailyChallengesCard = ChallengeUiRenderer.CreateChallengeCard(
                challengeCardTemplate,
                "D A I L Y",
                ref _challengeCardTemplateErrorLogged,
                this,
                out _dailyTimerLabel
            );
            if(_dailyChallengesCard != null) {
                _dailyChallengesCard.AddToClassList("main-menu-challenges-card--daily");
                _mainMenuChallengesContainer.Add(_dailyChallengesCard);
            }

            // Create Weekly Challenges Card
            _weeklyChallengesCard = ChallengeUiRenderer.CreateChallengeCard(
                challengeCardTemplate,
                "W E E K L Y",
                ref _challengeCardTemplateErrorLogged,
                this,
                out _weeklyTimerLabel
            );
            if(_weeklyChallengesCard != null) {
                _mainMenuChallengesContainer.Add(_weeklyChallengesCard);
            }

            // Populate challenges after cards are created
            UpdateMainMenuChallenges();
        }

        private void Update() {
            var isOffline = IsOfflineMode();
            ChallengeUiRenderer.SetOfflineState(_dailyChallengesCard, isOffline);
            ChallengeUiRenderer.SetOfflineState(_weeklyChallengesCard, isOffline);

            if(isOffline) {
                ChallengeUiRenderer.SetOfflineTimer(_dailyTimerLabel);
                ChallengeUiRenderer.SetOfflineTimer(_weeklyTimerLabel);
                return;
            }

            if (ProgressionManager.Instance == null) return;

            if (_dailyTimerLabel != null) {
                var time = ProgressionManager.Instance.GetTimeUntilDailyReset();
                ChallengeUiRenderer.SetDailyResetTimer(_dailyTimerLabel, time);
            }

            if(_weeklyTimerLabel == null) return;
            {
                var time = ProgressionManager.Instance.GetTimeUntilWeeklyReset();
                ChallengeUiRenderer.SetWeeklyResetTimer(_weeklyTimerLabel, time);
            }
        }

        private void UpdateMainMenuChallenges() {
            if(IsOfflineMode()) {
                ChallengeUiRenderer.SetOfflineState(_dailyChallengesCard, true);
                ChallengeUiRenderer.SetOfflineState(_weeklyChallengesCard, true);
                return;
            }

            var pm = ProgressionManager.Instance;
            if(pm == null || pm.Data == null) {
                Debug.LogWarning("[MainMenuUIManager] ProgressionManager or Data is null, cannot update challenges");
                return;
            }

            ChallengeUiRenderer.SetOfflineState(_dailyChallengesCard, false);
            ChallengeUiRenderer.SetOfflineState(_weeklyChallengesCard, false);
            RenderChallengeList(_dailyChallengesCard, pm.Data.dailyChallenges);
            RenderChallengeList(_weeklyChallengesCard, pm.Data.weeklyChallenges);
        }

        private void OnChallengesUpdatedEvent(ChallengesUpdatedEvent _) {
            UpdateMainMenuChallenges();
        }

        private void RenderChallengeList(VisualElement card, List<ActiveChallengeData> challenges) {
            if(card == null) {
                Debug.LogWarning("[MainMenuUIManager] Challenge card is null");
                return;
            }
            var list = card.Q<VisualElement>("challenge-list");
            if(list == null) {
                Debug.LogWarning("[MainMenuUIManager] Challenge list container not found in card");
                return;
            }

            var pm = ProgressionManager.Instance;
            if(pm == null) {
                Debug.LogWarning("[MainMenuUIManager] ProgressionManager is null");
                return;
            }

            ChallengeUiRenderer.RenderChallengeList(
                list,
                challenges,
                challengeRowTemplate,
                pm,
                ref _challengeRowTemplateErrorLogged,
                this,
                showEmptyLabel: true,
                includeXpSuffix: false
            );
        }

        /// <summary>
        /// Refreshes the challenges displayed on the main menu.
        /// Can be called externally to update challenges when needed.
        /// </summary>
        public void RefreshMainMenuChallenges() {
            UpdateMainMenuChallenges();
        }

        private static bool IsOfflineMode() {
            return Application.internetReachability == NetworkReachability.NotReachable;
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

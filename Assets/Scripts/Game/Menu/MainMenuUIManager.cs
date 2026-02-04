using System.Collections;
using System.Collections.Generic;
using Network.Services;
using UnityEngine;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace Game.Menu {
    /// <summary>
    /// Manages UI panel switching, button events, and modal dialogs for the main menu.
    /// Handles visual updates and user interactions.
    /// </summary>
    public class MainMenuUIManager : MonoBehaviour {
        [Header("References")]
        public UIDocument uiDocument;
        public LoadoutManager LoadoutManager; // Added for Profile View access

        private VisualElement _root;
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
        public VisualTreeAsset PartyMemberTemplate => partyMemberTemplate;

        public VisualElement PartyContainer { get; }
        public VisualElement StatusContainer { get; }
        public Label MatchmakingStatusLabel { get; private set; }
        public Label QueueGamemodeLabel { get; private set; }
        public Label QueueTimerLabel { get; private set; }
        private Button CancelMatchmakingButton { get; set; }

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

        public MainMenuUIManager(VisualElement partyContainer, VisualElement statusContainer, List<Button> gamemodeOptions) {
            PartyContainer = partyContainer;
            StatusContainer = statusContainer;
            _gamemodeOptions = gamemodeOptions;
        }


        private void Awake() {
            if(uiDocument == null) {
                Debug.LogError("[MainMenuUIManager] UIDocument is not assigned!");
                return;
            }

            _root = uiDocument.rootVisualElement;
            FindUIElements();
            SetupFirstTimeModal();
        }

        private void Start() {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            RegisterUIEvents();
        }

        /// <summary>
        /// Initializes the UI manager and ensures proper visibility of global containers.
        /// </summary>
        public void Initialize() {
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
            // Panels
            MainMenuPanel = _root.Q<VisualElement>("main-menu-panel");
            _playGamemodePanel = _root.Q<VisualElement>("play-gamemode-panel");
            _lobbyPanel = _root.Q<VisualElement>("lobby-panel");
            _loadoutPanel = _root.Q<VisualElement>("loadout-panel");
            _nameInput = _root.Q<TextField>("player-name-input");
            _optionsPanel = _root.Q<VisualElement>("options-panel");
            _creditsPanel = _root.Q<VisualElement>("credits-panel");

            // Buttons
            _playButtonMatchmaking = _root.Q<Button>("play-button-matchmaking");
            _playButtonPrivate = _root.Q<Button>("play-button-private");
            _loadoutButton = _root.Q<Button>("loadout-button");
            _optionsButton = _root.Q<Button>("options-button");
            _creditsButton = _root.Q<Button>("credits-button");
            _quitButton = _root.Q<Button>("quit-button");
            _backGamemodeButton = _root.Q<Button>("back-to-main");
            _backGamemodesButton = _root.Q<Button>("back-gamemodes-to-main");
            _backCreditsButton = _root.Q<Button>("back-to-lobby");

            // Gamemode Cards
            _cardDeathmatch = _root.Q<Button>("card-deathmatch");
            _cardTeamDeathmatch = _root.Q<Button>("card-team-deathmatch");
            _cardHopball = _root.Q<Button>("card-hopball");
            _cardKoth = _root.Q<Button>("card-koth");
            _cardGunTag = _root.Q<Button>("card-gun-tag");

            _root.Q<VisualElement>("loading-overlay");
            GamemodeDropdownContainer = _root.Q<VisualElement>("gamemode-dropdown-container");
            GamemodeDisplayLabel = _root.Q<Label>("gamemode-display-label");
            GamemodeDropdownMenu = _root.Q<VisualElement>("gamemode-dropdown-menu");
            
            _gamemodeOptions = new List<Button>();

            // First-time setup modal
            _firstTimeModal = _root.Q<VisualElement>("first-time-setup-modal");
            _firstTimeNameInput = _root.Q<TextField>("first-time-name-input");
            _firstTimeContinueButton = _root.Q<Button>("first-time-continue-button");

            // Quit confirmation modal
            _quitConfirmationModal = _root.Q<VisualElement>("quit-confirmation-modal");
            _quitConfirmationYes = _root.Q<Button>("quit-confirmation-yes");
            _quitConfirmationNo = _root.Q<Button>("quit-confirmation-no");

            // Lobby leave modal
            _lobbyLeaveModal = _root.Q<VisualElement>("lobby-leave-modal");
            _lobbyLeaveYes = _root.Q<Button>("lobby-leave-yes");
            _lobbyLeaveNo = _root.Q<Button>("lobby-leave-no");

            // Misc
            _logoGithub = _root.Q<Image>("credits-logo");
            _toastContainer = _root.Q<VisualElement>("toast-container");
            _versionLabel = _root.Q<Label>("version-text");
            
            if (StatusContainer != null) {
                MatchmakingStatusLabel = _root.Q<Label>("matchmaking-status-label");
                QueueGamemodeLabel = _root.Q<Label>("queue-gamemode-label");
                QueueTimerLabel = _root.Q<Label>("queue-timer-label");
                CancelMatchmakingButton = _root.Q<Button>("cancel-matchmaking-button");
                
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
                _quitButton,
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
            PartyContextMenu = _root.Q<VisualElement>("party-context-menu");
            ContextMenuBackdrop = _root.Q<VisualElement>("context-menu-backdrop");
            CtxProfile = _root.Q<Button>("ctx-profile");
            CtxSteamProfile = _root.Q<Button>("ctx-steam-profile");
            CtxMuteChat = _root.Q<Button>("ctx-mute-chat");
            CtxMuteVoice = _root.Q<Button>("ctx-mute-voice");
            CtxBlock = _root.Q<Button>("ctx-block");
            CtxMakeHost = _root.Q<Button>("ctx-make-host");
            CtxKick = _root.Q<Button>("ctx-kick");
            CtxLeave = _root.Q<Button>("ctx-leave");
            CtxSeparatorManagement = _root.Q<VisualElement>("ctx-separator-management");
            CtxSeparatorMute = _root.Q<VisualElement>("ctx-separator-mute");
            
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
            foreach(var b in _buttons) {
                if(b == null) continue;
                UISoundService.RegisterButtonHover(b);
                b.clicked += () => UISoundService.PlayButtonClick();
            }

            foreach(var b in _backButtons) {
                if(b == null) continue;
                UISoundService.RegisterButtonHover(b);
                // No generic click here, handled in specialized ones below
            }

            if (CancelMatchmakingButton != null) {
                CancelMatchmakingButton.clicked += () => OnCancelMatchmakingClicked?.Invoke();
            }

            // Main menu navigation
            if (_playButtonMatchmaking != null) {
                _playButtonMatchmaking.clicked += () => OnPlayMatchmakingClicked?.Invoke();
            }
            if (_playButtonPrivate != null) {
                _playButtonPrivate.clicked += () => OnPlayPrivateClicked?.Invoke();
            }

            _loadoutButton.clicked += () => {
                if(_nameInput != null) {
                    _nameInput.value = PlayerPrefs.GetString("PlayerName");
                }
                OnLoadoutClicked?.Invoke();
            };

            _optionsButton.clicked += () => {
                UISoundService.PlayButtonClick();
                OnOptionsClicked?.Invoke();
            };

            _creditsButton.clicked += () => {
                UISoundService.PlayButtonClick();
                OnCreditsClicked?.Invoke();
            };

            _quitButton.clicked += ShowQuitConfirmation;
            UISoundService.RegisterButtonHover(_quitButton);

            // Gamemode Card Clicks
            if (_cardDeathmatch != null) _cardDeathmatch.clicked += () => OnGamemodeSelected?.Invoke("Deathmatch");
            if (_cardTeamDeathmatch != null) _cardTeamDeathmatch.clicked += () => OnGamemodeSelected?.Invoke("Team Deathmatch");
            if (_cardHopball != null) _cardHopball.clicked += () => OnGamemodeSelected?.Invoke("Hopball");
            if (_cardKoth != null) _cardKoth.clicked += () => OnGamemodeSelected?.Invoke("King of the Hill");
            if (_cardGunTag != null) _cardGunTag.clicked += () => OnGamemodeSelected?.Invoke("Gun Tag");

            // Back buttons
            if (_backGamemodeButton != null) _backGamemodeButton.clicked += () => OnShowPanel?.Invoke(MainMenuPanel);
            if (_backGamemodesButton != null) {
                _backGamemodesButton.clicked += () => {
                    UISoundService.PlayButtonClick(isBack: true);
                    OnShowPanel?.Invoke(MainMenuPanel);
                };
            }
            if (_backCreditsButton != null) {
                _backCreditsButton.clicked += () => {
                    UISoundService.PlayButtonClick(isBack: true);
                    OnShowPanel?.Invoke(MainMenuPanel);
                };
            }

            // Private Lobby Dropdown
            if (GamemodeDropdownContainer != null) {
                GamemodeDropdownContainer.RegisterCallback<ClickEvent>(_ => OnGamemodeDropdownClicked?.Invoke());
            }

            foreach (var opt in _gamemodeOptions) {
                if (opt == null) continue;
                opt.clicked += () => {
                    UISoundService.PlayButtonClick();
                    OnGamemodeSelected?.Invoke(opt.text);
                };
            }

            // Quit confirmation modal
            if(_quitConfirmationYes != null) {
                _quitConfirmationYes.clicked += () => {
                    UISoundService.PlayButtonClick();
                    OnQuitConfirmed?.Invoke();
                };
                UISoundService.RegisterButtonHover(_quitConfirmationYes);
            }

            if(_quitConfirmationNo != null) {
                _quitConfirmationNo.clicked += () => {
                    UISoundService.PlayButtonClick();
                    OnQuitCancelled?.Invoke();
                    HideQuitConfirmation();
                };
                UISoundService.RegisterButtonHover(_quitConfirmationNo);
            }

            // Lobby leave modal
            if(_lobbyLeaveYes != null) {
                _lobbyLeaveYes.clicked += () => {
                    UISoundService.PlayButtonClick(isBack: true);
                    OnLobbyLeaveConfirmed?.Invoke();
                    HideLobbyLeaveConfirmation();
                };
                UISoundService.RegisterButtonHover(_lobbyLeaveYes);
            }

            if(_lobbyLeaveNo != null) {
                _lobbyLeaveNo.clicked += () => {
                    UISoundService.PlayButtonClick();
                    OnLobbyLeaveCancelled?.Invoke();
                    HideLobbyLeaveConfirmation();
                };
                UISoundService.RegisterButtonHover(_lobbyLeaveNo);
            }

            // Credits
            if(_logoGithub != null) {
                _logoGithub.RegisterCallback<ClickEvent>(_ => {
                    Application.OpenURL("https://github.com/whosteenie/HOP");
                });
                _logoGithub.RegisterCallback<MouseEnterEvent>(_ => {
                    UISoundService.PlayButtonHover();
                });
            }
        }

        private void SetupFirstTimeModal() {
            if(_firstTimeContinueButton == null) return;
            _firstTimeContinueButton.clicked += () => {
                UISoundService.PlayButtonClick();
                OnFirstTimeContinue?.Invoke();
            };
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
            if(_quitConfirmationModal == null) return;
            _quitConfirmationModal.RemoveFromClassList("hidden");
            _quitConfirmationModal.style.display = DisplayStyle.Flex;
            _quitConfirmationModal.BringToFront();
        }

        private void HideQuitConfirmation() {
            if(_quitConfirmationModal == null) return;
            _quitConfirmationModal.AddToClassList("hidden");
            _quitConfirmationModal.style.display = StyleKeyword.Null;
        }

        /// <summary>
        /// Shows the confirmation modal for leaving a lobby.
        /// </summary>
        public void ShowLobbyLeaveConfirmation() {
            UISoundService.PlayButtonClick(isBack: true);
            if(_lobbyLeaveModal == null) return;
            _lobbyLeaveModal.RemoveFromClassList("hidden");
            _lobbyLeaveModal.style.display = DisplayStyle.Flex;
            _lobbyLeaveModal.BringToFront();
        }

        private void HideLobbyLeaveConfirmation() {
            if(_lobbyLeaveModal == null) return;
            _lobbyLeaveModal.AddToClassList("hidden");
            _lobbyLeaveModal.style.display = StyleKeyword.Null;
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

        // Getters for external access
        public VisualElement GetGamemodePanel() => _gamemodePanel;
        public VisualElement GetLobbyPanel() => _lobbyPanel;
        public VisualElement GetLoadoutPanel() => _loadoutPanel;
        public VisualElement GetOptionsPanel() => _optionsPanel;
        public VisualElement GetCreditsPanel() => _creditsPanel;
        public Button GetBackGamemodeButton() => _backGamemodeButton;
    }
}

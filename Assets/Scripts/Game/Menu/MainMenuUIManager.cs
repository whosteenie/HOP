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

        private VisualElement _root;
        private VisualElement MainMenuPanel { get; set; }
        private VisualElement _gamemodePanel;
        private VisualElement _lobbyPanel;
        private VisualElement _loadoutPanel;
        private VisualElement _optionsPanel;
        private VisualElement _creditsPanel;

        // Buttons
        private Button _playButton;
        private Button _loadoutButton;
        private Button _optionsButton;
        private Button _creditsButton;
        private Button _quitButton;
        private Button _backGamemodeButton;
        private Button _backCreditsButton;
        private List<Button> _buttons;

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

        // Events
        public System.Action OnPlayClicked;
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

        private bool _isInitializing = true;

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

            // Register events (callbacks should be wired by MainMenuManager in Awake)
            RegisterUIEvents();

            // Mark initialization as complete after a frame
            StartCoroutine(FinishInitialization());
        }

        private IEnumerator FinishInitialization() {
            yield return null;
            _isInitializing = false;
        }

        public void Initialize() {
            // Hide game menu root container if it exists
            var gameMenu = GameMenuManager.Instance;
            if(gameMenu != null && gameMenu.TryGetComponent(out UIDocument doc) && doc != null) {
                var gameRoot = doc.rootVisualElement;
                var rootContainer = gameRoot?.Q<VisualElement>("root-container");
                if(rootContainer != null) {
                    rootContainer.style.display = DisplayStyle.None;
                }
            }
        }

        private void FindUIElements() {
            // Panels
            MainMenuPanel = _root.Q<VisualElement>("main-menu-panel");
            _gamemodePanel = _root.Q<VisualElement>("gamemode-panel");
            _lobbyPanel = _root.Q<VisualElement>("lobby-panel");
            _loadoutPanel = _root.Q<VisualElement>("loadout-panel");
            _nameInput = _root.Q<TextField>("player-name-input");
            _optionsPanel = _root.Q<VisualElement>("options-panel");
            _creditsPanel = _root.Q<VisualElement>("credits-panel");

            // Buttons
            _playButton = _root.Q<Button>("play-button");
            _loadoutButton = _root.Q<Button>("loadout-button");
            _optionsButton = _root.Q<Button>("options-button");
            _creditsButton = _root.Q<Button>("credits-button");
            _quitButton = _root.Q<Button>("quit-button");
            _backGamemodeButton = _root.Q<Button>("back-to-main");
            _backCreditsButton = _root.Q<Button>("back-to-lobby");

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


            _buttons = new List<Button> {
                _playButton,
                _loadoutButton,
                _optionsButton,
                _creditsButton,
                _backGamemodeButton,
                _backCreditsButton
            };
        }

        private void RegisterUIEvents() {
            // Generic button events
            foreach(var b in _buttons) {
                if(b == null) continue;
                b.clicked += () => {
                    if(!_isInitializing) {
                        UISoundService.PlayButtonClick(b.ClassListContains("back-button"));
                    }
                };
                UISoundService.RegisterButtonHover(b);
            }

            // Main menu navigation
            _playButton.clicked += () => {
                if(!_isInitializing) UISoundService.PlayButtonClick();
                OnPlayClicked?.Invoke();
            };

            _loadoutButton.clicked += () => {
                if(_nameInput != null) {
                    _nameInput.value = PlayerPrefs.GetString("PlayerName");
                }
                if(!_isInitializing) UISoundService.PlayButtonClick();
                OnLoadoutClicked?.Invoke();
            };

            _optionsButton.clicked += () => {
                if(!_isInitializing) UISoundService.PlayButtonClick();
                OnOptionsClicked?.Invoke();
            };

            _creditsButton.clicked += () => {
                if(!_isInitializing) UISoundService.PlayButtonClick();
                OnCreditsClicked?.Invoke();
            };

            _quitButton.clicked += ShowQuitConfirmation;
            UISoundService.RegisterButtonHover(_quitButton);

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

            if(_backCreditsButton != null) {
                _backCreditsButton.clicked += () => {
                    if(!_isInitializing) UISoundService.PlayButtonClick(isBack: true);
                    OnShowPanel?.Invoke(MainMenuPanel);
                };
            }
        }

        private void SetupFirstTimeModal() {
            if(_firstTimeContinueButton == null) return;
            _firstTimeContinueButton.clicked += () => {
                if(!_isInitializing) UISoundService.PlayButtonClick();
                OnFirstTimeContinue?.Invoke();
            };
            UISoundService.RegisterButtonHover(_firstTimeContinueButton);
        }

        public void CheckFirstTimeSetup() {
            var hasName = PlayerPrefs.HasKey("PlayerName");
            if(!hasName) {
                ShowFirstTimeSetup();
            }
        }

        private void ShowFirstTimeSetup() {
            if(_firstTimeModal != null) {
                _firstTimeModal.RemoveFromClassList("hidden");
            }
        }

        public void HideFirstTimeSetup() {
            if(_firstTimeModal != null) {
                _firstTimeModal.AddToClassList("hidden");
            }
        }

        public string GetFirstTimeNameInput() {
            return _firstTimeNameInput != null ? _firstTimeNameInput.value : string.Empty;
        }

        // Panel references (for external access - panel management stays in MainMenuManager for now)

        // Button Enable/Disable
        public static void EnableButton(Button button) {
            if(button == null) return;
            button.AddToClassList("menu-chip-enabled");
            button.SetEnabled(true);
            UISoundService.RegisterButtonHover(button);
        }

        public static void DisableButton(Button button) {
            if(button == null) return;
            button.RemoveFromClassList("menu-chip-enabled");
            button.SetEnabled(false);
            UISoundService.UnregisterButtonHover(button);
        }

        // Quit Confirmation
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

        // Lobby Leave Confirmation
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

        // Toast notifications
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

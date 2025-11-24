using System;
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace Network.Singletons {
    public class MainMenuManager : MonoBehaviour {
        #region Serialized Fields

        public UIDocument uiDocument;

        [Header("Managers")]
        // Note: SessionManager and GameMenuManager are now DDOL in Init scene
        // Access via Instance properties instead of inspector references
        [Header("Audio")]
        [SerializeField]
        private AudioMixer audioMixer;

        [Header("Options")]
        [SerializeField] private OptionsMenuManager optionsMenuManager;

        [Header("Player Customization")]
        [SerializeField] private Color[] playerColors;

        [SerializeField] private Material[] playerMaterials;

        [Header("References")]
        [SerializeField] private Camera mainCamera;

        #endregion

        #region UI Elements - Panels

        private VisualElement _root;
        public VisualElement MainMenuPanel;
        private VisualElement _gamemodePanel;
        private VisualElement _lobbyPanel;
        private VisualElement _loadoutPanel;
        private VisualElement _optionsPanel;
        private VisualElement _creditsPanel;
        private List<VisualElement> _panels;

        #endregion

        #region UI Elements - Buttons

        private Button _hostButton;
        private Button _startButton;
        private Button _playButton;
        private Button _loadoutButton;
        private Button _optionsButton;
        private Button _creditsButton;
        private Button _quitButton;
        private Button _modeOneButton;
        private Button _modeTwoButton;
        private Button _modeThreeButton;
        private Button _backGamemodeButton;
        private Button _backLobbyButton;

        private Button _backCreditsButton;

        // Apply and back buttons are now handled by OptionsMenuManager
        private Button _joinButton;
        private Button _copyButton;
        private List<Button> _buttons;

        #endregion

        #region UI Elements - Options

        // Options functionality is now handled by OptionsMenuManager component

        #endregion

        #region UI Elements - Lobby

        private Label _joinCodeLabel;
        private Label _waitingLabel;
        private TextField _joinCodeInput;
        private VisualElement _playerList;
        private VisualElement _toastContainer;

        // Gamemode dropdown elements
        private VisualElement _gamemodeDropdownContainer;
        private Label _gamemodeDisplayLabel;
        private VisualElement _gamemodeArrow;
        private VisualElement _gamemodeDropdownMenu;
        private bool _isGamemodeDropdownOpen;

        #endregion

        #region UI Elements - First Time Setup

        private VisualElement _firstTimeModal;
        private VisualElement _firstTimeColorOptions;
        private TextField _firstTimeNameInput;
        private Button _firstTimeContinueButton;
        private int _firstTimeSelectedColorIndex;

        #endregion

        #region UI Elements - Unsaved Changes Dialog

        private VisualElement _unsavedChangesModal;
        private Button _unsavedChangesYes;
        private Button _unsavedChangesNo;
        private Button _unsavedChangesCancel;

        // Original settings values are now tracked by OptionsMenuManager

        #endregion

        #region UI Elements - Quit Confirmation Dialog

        private VisualElement _quitConfirmationModal;
        private Button _quitConfirmationYes;
        private Button _quitConfirmationNo;

        #endregion

        #region UI Elements - Lobby Leave Dialog

        private VisualElement _lobbyLeaveModal;
        private Button _lobbyLeaveYes;
        private Button _lobbyLeaveNo;

        #endregion

        #region UI Elements - Misc

        private TextField _nameInput;
        private Image _logoGithub;

        #endregion

        private string _selectedGameMode;
        private bool _isHost;
        private bool _justCreatedSessionAsHost; // Track if we just created a session as host
        private bool _isStartingGame; // Track if start button was clicked and game is starting
        private string _cachedPlayerName; // Temporary cache for our own player name (cleared on session leave)

        #region Unity Lifecycle

        private void Start() {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            if(uiDocument == null) {
                Debug.LogError("[MainMenuManager] UIDocument is not assigned!");
                return;
            }

            _root = uiDocument.rootVisualElement;

            FindUIElements();
            RegisterUIEvents();

            SetupFirstTimeModal();
            CheckFirstTimeSetup();

            _joinCodeInput.maxLength = 6;
            _joinCodeInput.isDelayed = false;

            SetupOptionsMenuManager();
            LoadSettings();

            var gameMenu = GameMenuManager.Instance;
            if(gameMenu != null && gameMenu.TryGetComponent(out UIDocument doc) && doc != null) {
                var gameRoot = doc.rootVisualElement;
                var rootContainer = gameRoot?.Q<VisualElement>("root-container");
                if(rootContainer != null)
                    rootContainer.style.display = DisplayStyle.None;
            }

            // Mark initialization as complete after a frame to allow all setup to finish
            StartCoroutine(FinishInitialization());
        }

        private IEnumerator FinishInitialization() {
            yield return null; // Wait one frame
            _isInitializing = false;
        }

        private void OnEnable() {
            if(SessionManager.Instance != null) {
                SessionManager.Instance.PlayersChanged += OnPlayersChanged;
                SessionManager.Instance.RelayCodeAvailable += OnRelayCodeAvailable;
                SessionManager.Instance.FrontStatusChanged += UpdateStatusText;
                SessionManager.Instance.SessionJoined += OnSessionJoined;
                SessionManager.Instance.HostDisconnected += OnHostDisconnected;
                SessionManager.Instance.LobbyReset += ResetLobbyUI;
            }

            // Hook into session property changes directly
            HookSessionPropertyChanges();
        }

        private void OnDisable() {
            if(SessionManager.Instance != null) {
                SessionManager.Instance.PlayersChanged -= OnPlayersChanged;
                SessionManager.Instance.RelayCodeAvailable -= OnRelayCodeAvailable;
                SessionManager.Instance.FrontStatusChanged -= UpdateStatusText;
                SessionManager.Instance.SessionJoined -= OnSessionJoined;
                SessionManager.Instance.HostDisconnected -= OnHostDisconnected;
                SessionManager.Instance.LobbyReset -= ResetLobbyUI;
            }

            UnhookSessionPropertyChanges();
        }

        private void HookSessionPropertyChanges() {
            var session = SessionManager.Instance?.ActiveSession;
            if(session != null) {
                session.SessionPropertiesChanged += OnSessionPropertiesChanged;
            }
        }

        private void UnhookSessionPropertyChanges() {
            var session = SessionManager.Instance?.ActiveSession;
            if(session != null) {
                session.SessionPropertiesChanged -= OnSessionPropertiesChanged;
            }
        }

        private void OnSessionPropertiesChanged() {
            // Update gamemode when session properties change
            UpdateGamemodeFromSession();
        }

        private void UpdateStatusText(string msg) {
            _waitingLabel.text = msg;
        }

        private void OnSessionJoined(string sessionCode) {
            _joinCodeLabel.text = $"Join Code: {sessionCode}";
            EnableButton(_copyButton);
            HookSessionPropertyChanges(); // Hook into property changes when joining
            UpdateHostStatus();
            UpdateGamemodeFromSession(); // Try to get gamemode when joining
        }

        private void OnHostDisconnected() {
            _waitingLabel.text = "Host disconnected. Create or join a new game.";
            EnableButton(_hostButton);
            EnableButton(_joinButton);
            DisableButton(_startButton);
            DisableButton(_copyButton);
        }

        private void ResetLobbyUI() {
            _joinCodeLabel.text = "Join Code: - - - - - -";
            _playerList.Clear();
            _joinCodeInput.value = "";
            _isHost = false;
            _justCreatedSessionAsHost = false; // Reset host creation flag
            _isStartingGame = false; // Reset game starting flag
            _cachedPlayerName = null; // Clear cached player name on session leave
            _isGamemodeDropdownOpen = false;

            // Reset gamemode dropdown UI
            if(_gamemodeDropdownMenu != null) _gamemodeDropdownMenu.AddToClassList("hidden");
            if(_gamemodeArrow != null) {
                _gamemodeArrow.AddToClassList("hidden");
                _gamemodeArrow.RemoveFromClassList("arrow-down");
            }

            // Reset gamemode display label to "Lobby" (default state)
            if(_gamemodeDisplayLabel != null) {
                _gamemodeDisplayLabel.text = "Lobby";
                _gamemodeDisplayLabel.RemoveFromClassList("gamemode-hover");
                _gamemodeDisplayLabel.RemoveFromClassList("gamemode-clicked");
            }

            UnsubscribeFromGamemodeEvents();

            // Re-enable host and join buttons when resetting lobby
            EnableButton(_hostButton);
            EnableButton(_joinButton);

            // Disable copy button (no join code available yet)
            DisableButton(_copyButton);

            // Disable start button (only enabled for hosts)
            DisableButton(_startButton);
        }

        private void ShowArrowWithAnimation() {
            if(_gamemodeArrow == null) return;

            // Remove hidden class first
            _gamemodeArrow.RemoveFromClassList("hidden");

            // Force a frame to ensure base styles are applied (opacity: 0, translate: -20px)
            StartCoroutine(AnimateArrowIn());
        }

        private IEnumerator AnimateArrowIn() {
            // Wait one frame to ensure base styles are applied
            yield return null;

            // Set initial arrow direction (pointing down when closed - default state, no class needed)
            _gamemodeArrow?.RemoveFromClassList("arrow-down");

            // Now add the slide-in class to trigger animation
            _gamemodeArrow?.AddToClassList("arrow-slide-in");

            // Keep the class to maintain final position (don't remove it)
        }

        #endregion

        #region Setup

        private void FindUIElements() {
            // Panels
            MainMenuPanel = _root.Q<VisualElement>("main-menu-panel");
            _gamemodePanel = _root.Q<VisualElement>("gamemode-panel");
            _lobbyPanel = _root.Q<VisualElement>("lobby-panel");
            _loadoutPanel = _root.Q<VisualElement>("loadout-panel");
            _nameInput = _root.Q<TextField>("player-name-input");
            _optionsPanel = _root.Q<VisualElement>("options-panel");
            _creditsPanel = _root.Q<VisualElement>("credits-panel");

            _playButton = _root.Q<Button>("play-button");
            _loadoutButton = _root.Q<Button>("loadout-button");
            _optionsButton = _root.Q<Button>("options-button");
            _creditsButton = _root.Q<Button>("credits-button");
            _quitButton = _root.Q<Button>("quit-button");
            _modeOneButton = _root.Q<Button>("mode-one-button");
            _modeTwoButton = _root.Q<Button>("mode-two-button");
            _modeThreeButton = _root.Q<Button>("mode-three-button");
            _backGamemodeButton = _root.Q<Button>("back-to-main");
            _backLobbyButton = _root.Q<Button>("back-to-gamemode");
            _backCreditsButton = _root.Q<Button>("back-to-lobby");
            // Apply and back buttons are now handled by OptionsMenuManager
            // Unsaved changes dialog is now handled by OptionsMenuManager
            _hostButton = _root.Q<Button>("host-button");
            _startButton = _root.Q<Button>("start-button");
            _joinButton = _root.Q<Button>("join-button");
            _copyButton = _root.Q<Button>("copy-code-button");

            // Options UI elements are now found by OptionsMenuManager

            // Lobby
            _joinCodeInput = _root.Q<TextField>("join-input");
            _joinCodeLabel = _root.Q<Label>("host-label");
            _toastContainer = _root.Q<VisualElement>("toast-container");
            _playerList = _root.Q<VisualElement>("player-list");
            _waitingLabel = _root.Q<Label>("waiting-label");

            // Gamemode dropdown
            _gamemodeDropdownContainer = _root.Q<VisualElement>("gamemode-dropdown-container");
            _gamemodeDisplayLabel = _root.Q<Label>("gamemode-display-label");
            _gamemodeArrow = _root.Q<VisualElement>("gamemode-arrow");
            _gamemodeDropdownMenu = _root.Q<VisualElement>("gamemode-dropdown-menu");

            _logoGithub = _root.Q<Image>("credits-logo");

            // Quit confirmation modal
            _quitConfirmationModal = _root.Q<VisualElement>("quit-confirmation-modal");
            _quitConfirmationYes = _root.Q<Button>("quit-confirmation-yes");
            _quitConfirmationNo = _root.Q<Button>("quit-confirmation-no");

            // Lobby leave modal
            _lobbyLeaveModal = _root.Q<VisualElement>("lobby-leave-modal");
            _lobbyLeaveYes = _root.Q<Button>("lobby-leave-yes");
            _lobbyLeaveNo = _root.Q<Button>("lobby-leave-no");

            _panels = new List<VisualElement> {
                MainMenuPanel,
                _gamemodePanel,
                _lobbyPanel,
                _loadoutPanel,
                _optionsPanel,
                _creditsPanel
            };

            _buttons = new List<Button> {
                _playButton,
                _loadoutButton,
                _optionsButton,
                _creditsButton,
                // _quitButton - registered separately to handle quit confirmation

                _modeOneButton,
                _modeTwoButton,
                _modeThreeButton,
                _backGamemodeButton,

                _hostButton,
                _joinButton,
                // _copyButton - excluded from generic registration; hover sound registered only when enabled (after hosting/joining)
                _backLobbyButton,
                _startButton,

                _backCreditsButton
            };
        }

        private void RegisterUIEvents() {
            // === Generic Button Events ===
            foreach(var b in _buttons) {
                b.clicked += () => OnButtonClicked(b.ClassListContains("back-button"));
                // Use MouseEnterEvent instead of MouseOverEvent to prevent multiple triggers from child elements
                b.RegisterCallback<MouseEnterEvent>(MouseEnter);
            }

            // === Main Menu Navigation ===
            _playButton.clicked += OnPlayClicked;
            _loadoutButton.clicked += () => {
                _nameInput.value = PlayerPrefs.GetString("PlayerName");
                var loadoutManager = FindFirstObjectByType<LoadoutManager>();
                loadoutManager.ShowLoadout();
                ShowPanel(_loadoutPanel);
            };
            _optionsButton.clicked += () => {
                Debug.Log("[MainMenuManager] Entering options menu");
                if(optionsMenuManager != null) {
                    optionsMenuManager.LoadSettings();
                    optionsMenuManager.OnOptionsPanelShown();
                }

                ShowPanel(_optionsPanel);
            };
            _creditsButton.clicked += () => ShowPanel(_creditsPanel);
            // Quit button - registered separately to handle quit confirmation
            _quitButton.clicked += ShowQuitConfirmation;
            _quitButton.RegisterCallback<MouseEnterEvent>(MouseEnter);
            
            // Quit confirmation modal
            if(_quitConfirmationYes != null) {
                _quitConfirmationYes.clicked += OnQuitConfirmed;
                _quitConfirmationYes.RegisterCallback<MouseEnterEvent>(MouseEnter);
            }
            if(_quitConfirmationNo != null) {
                _quitConfirmationNo.clicked += OnQuitCancelled;
                _quitConfirmationNo.RegisterCallback<MouseEnterEvent>(MouseEnter);
            }

            // Lobby leave modal
            if(_lobbyLeaveYes != null) {
                _lobbyLeaveYes.clicked += OnLobbyLeaveConfirmed;
                _lobbyLeaveYes.RegisterCallback<MouseEnterEvent>(MouseEnter);
            }
            if(_lobbyLeaveNo != null) {
                _lobbyLeaveNo.clicked += OnLobbyLeaveCancelled;
                _lobbyLeaveNo.RegisterCallback<MouseEnterEvent>(MouseEnter);
            }

            // === Lobby Actions ===
            _hostButton.clicked += OnHostClicked;
            _joinCodeInput.RegisterValueChangedCallback(OnJoinCodeInputValueChanged);
            _joinCodeInput.RegisterCallback<FocusInEvent>(OnJoinCodeInputFocusIn);
            _joinCodeInput.RegisterCallback<FocusOutEvent>(OnJoinCodeInputFocusOut);
            UpdateJoinCodePlaceholderVisibility();
            _joinButton.clicked += () => OnJoinGameClicked(_joinCodeInput.value.ToUpper());
            _copyButton.clicked += CopyJoinCodeToClipboard;
            _startButton.clicked += OnStartGameClicked;
            _backLobbyButton.clicked += OnBackFromLobbyClicked;

            // === Gamemode Dropdown ===
            SetupGamemodeDropdown();

            // === Credits ===
            _logoGithub.RegisterCallback<ClickEvent>(_ => {
                Application.OpenURL("https://github.com/whosteenie/HOP");
            });
            _logoGithub.RegisterCallback<MouseEnterEvent>(MouseEnter);
            _backCreditsButton.clicked += () => ShowPanel(MainMenuPanel);
        }

        #endregion

        #region First Time Setup

        private void CheckFirstTimeSetup() {
            var hasName = PlayerPrefs.HasKey("PlayerName");
            var hasColor = PlayerPrefs.HasKey("PlayerColorIndex");

            if(!hasName || !hasColor) {
                ShowFirstTimeSetup();
            }
        }

        private void SetupFirstTimeModal() {
            _firstTimeModal = _root.Q<VisualElement>("first-time-setup-modal");
            _firstTimeColorOptions = _root.Q<VisualElement>("first-time-color-options");
            _firstTimeNameInput = _root.Q<TextField>("first-time-name-input");
            _firstTimeContinueButton = _root.Q<Button>("first-time-continue-button");

            // Setup current color display clicks
            for(var i = 0; i < 7; i++) {
                var currentCircle = _root.Q<VisualElement>($"first-time-current-color-{i}");
                if(currentCircle != null) {
                    currentCircle.RegisterCallback<ClickEvent>(_ => {
                        OnButtonClicked();
                        _firstTimeColorOptions.ToggleInClassList("hidden");
                    });
                    currentCircle.RegisterCallback<MouseEnterEvent>(MouseEnter);
                }
            }

            // Show first color by default
            UpdateFirstTimeColorDisplay(0);

            // Setup color option clicks
            for(var i = 0; i < 7; i++) {
                var optionCircle = _root.Q<VisualElement>($"first-time-option-color-{i}");
                if(optionCircle != null) {
                    var index = i;
                    optionCircle.RegisterCallback<ClickEvent>(_ => {
                        OnButtonClicked();
                        _firstTimeSelectedColorIndex = index;
                        UpdateFirstTimeColorDisplay(index);
                        _firstTimeColorOptions.AddToClassList("hidden");
                    });
                    optionCircle.RegisterCallback<MouseEnterEvent>(MouseEnter);
                }
            }

            // Continue button
            _firstTimeContinueButton.clicked += () => {
                OnButtonClicked();
                OnFirstTimeSetupContinue();
            };
            _firstTimeContinueButton.RegisterCallback<MouseEnterEvent>(MouseEnter);
        }

        private void UpdateFirstTimeColorDisplay(int colorIndex) {
            // Hide all current color circles
            for(var i = 0; i < 7; i++) {
                var currentCircle = _root.Q<VisualElement>($"first-time-current-color-{i}");
                currentCircle?.AddToClassList("hidden");
            }

            // Show only the selected color
            var selectedCircle = _root.Q<VisualElement>($"first-time-current-color-{colorIndex}");
            selectedCircle?.RemoveFromClassList("hidden");

            // Update options - hide selected, show others
            for(var i = 0; i < 7; i++) {
                var optionCircle = _root.Q<VisualElement>($"first-time-option-color-{i}");
                if(optionCircle != null) {
                    if(i == colorIndex) {
                        optionCircle.AddToClassList("hidden");
                    } else {
                        optionCircle.RemoveFromClassList("hidden");
                    }
                }
            }
        }

        private void ShowFirstTimeSetup() {
            _firstTimeModal?.RemoveFromClassList("hidden");
        }

        private void OnFirstTimeSetupContinue() {
            var playerName = _firstTimeNameInput.value;

            // Validate name
            if(string.IsNullOrWhiteSpace(playerName)) {
                playerName = "Player";
            }

            // Save to PlayerPrefs
            PlayerPrefs.SetString("PlayerName", playerName);
            PlayerPrefs.SetInt("PlayerColorIndex", _firstTimeSelectedColorIndex);
            PlayerPrefs.Save();

            // Hide modal
            _firstTimeModal.AddToClassList("hidden");

            LoadSettings();
        }

        #endregion

        #region Navigation

        public void ShowPanel(VisualElement panel) {
            foreach(var p in _panels) {
                p.AddToClassList("hidden");
                // Also clear any inline display styles that might override CSS
                p.style.display = StyleKeyword.Null;
            }

            panel.RemoveFromClassList("hidden");
            // Ensure display is set to flex (or null to use CSS)
            panel.style.display = DisplayStyle.Flex;
            // Bring panel to front to ensure it renders above other panels
            panel.BringToFront();
        }

        private void OnPlayClicked() {
            // Initialize gamemode from MatchSettings or default to Deathmatch
            if(MatchSettingsManager.Instance != null) {
                _selectedGameMode = MatchSettingsManager.Instance.selectedGameModeId;
                if(string.IsNullOrEmpty(_selectedGameMode)) {
                    _selectedGameMode = "Deathmatch";
                    MatchSettingsManager.Instance.selectedGameModeId = _selectedGameMode;
                }
            } else {
                _selectedGameMode = "Deathmatch";
            }

            // Show "Lobby" initially (will change to gamemode when host is clicked)
            if(_gamemodeDisplayLabel != null) {
                _gamemodeDisplayLabel.text = "Lobby";
            }

            ResetLobbyUI();
            ShowPanel(_lobbyPanel);
        }

        private void SetupGamemodeDropdown() {
            // Don't subscribe to hover/click events yet - wait until host becomes host
            // Setup gamemode option buttons
            var deathmatchOption = _root.Q<Button>("gamemode-option-deathmatch");
            var teamDeathmatchOption = _root.Q<Button>("gamemode-option-team-deathmatch");
            var tagOption = _root.Q<Button>("gamemode-option-tag");
            var hopballOption = _root.Q<Button>("gamemode-option-hopball");
            var privateMatchOption = _root.Q<Button>("gamemode-option-private-match");

            if(deathmatchOption != null) {
                deathmatchOption.clicked += () => {
                    if(_isHost) {
                        OnButtonClicked();
                        OnGameModeSelected("Deathmatch");
                    }
                };
                deathmatchOption.RegisterCallback<MouseEnterEvent>(evt => {
                    if(_isHost) {
                        MouseEnter(evt);
                    }
                });
            }

            if(teamDeathmatchOption != null) {
                teamDeathmatchOption.clicked += () => {
                    if(_isHost) {
                        OnButtonClicked();
                        OnGameModeSelected("Team Deathmatch");
                    }
                };
                teamDeathmatchOption.RegisterCallback<MouseEnterEvent>(evt => {
                    if(_isHost) {
                        MouseEnter(evt);
                    }
                });
            }

            if(tagOption != null) {
                tagOption.clicked += () => {
                    if(_isHost) {
                        OnButtonClicked();
                        OnGameModeSelected("Gun Tag");
                    }
                };
                tagOption.RegisterCallback<MouseEnterEvent>(evt => {
                    if(_isHost) {
                        MouseEnter(evt);
                    }
                });
            }

            if(hopballOption != null) {
                hopballOption.clicked += () => {
                    if(_isHost) {
                        OnButtonClicked();
                        OnGameModeSelected("Hopball");
                    }
                };
                hopballOption.RegisterCallback<MouseEnterEvent>(evt => {
                    if(_isHost) {
                        MouseEnter(evt);
                    }
                });
            }

            if(privateMatchOption != null) {
                privateMatchOption.clicked += () => {
                    if(_isHost) {
                        OnButtonClicked();
                        OnGameModeSelected("Private Match");
                    }
                };
                privateMatchOption.RegisterCallback<MouseEnterEvent>(evt => {
                    if(_isHost) {
                        MouseEnter(evt);
                    }
                });
            }
        }

        private void SubscribeToGamemodeEvents() {
            if(_gamemodeDisplayLabel != null) {
                _gamemodeDisplayLabel.RegisterCallback<ClickEvent>(OnGamemodeLabelClicked);
                _gamemodeDisplayLabel.RegisterCallback<MouseEnterEvent>(OnGamemodeMouseEnter);
                _gamemodeDisplayLabel.RegisterCallback<MouseLeaveEvent>(OnGamemodeMouseLeave);
            }
        }

        private void UnsubscribeFromGamemodeEvents() {
            if(_gamemodeDisplayLabel != null) {
                _gamemodeDisplayLabel.UnregisterCallback<ClickEvent>(OnGamemodeLabelClicked);
                _gamemodeDisplayLabel.UnregisterCallback<MouseEnterEvent>(OnGamemodeMouseEnter);
                _gamemodeDisplayLabel.UnregisterCallback<MouseLeaveEvent>(OnGamemodeMouseLeave);
            }
        }

        private void OnGamemodeMouseEnter(MouseEnterEvent evt) {
            if(_isHost) {
                _gamemodeDisplayLabel?.AddToClassList("gamemode-hover");
                // Play hover sound when entering the gamemode title (only for host)
                MouseEnter(evt);
            }
        }

        private void OnGamemodeMouseLeave(MouseLeaveEvent evt) {
            if(_isHost) {
                _gamemodeDisplayLabel?.RemoveFromClassList("gamemode-hover");
                // Don't remove clicked class here - it's handled by coroutine
            }
        }

        private void OnGamemodeLabelClicked(ClickEvent evt) {
            if(!_isHost) return; // Only host can interact

            // Play click sound
            OnButtonClicked();

            // Add click feedback (brief press effect)
            _gamemodeDisplayLabel?.AddToClassList("gamemode-clicked");

            // Remove click feedback after brief moment, returning to hover or normal state
            StartCoroutine(RemoveClickFeedback());

            // If clicking the same gamemode that's already selected, just toggle dropdown
            ToggleGamemodeDropdown();
        }

        private IEnumerator RemoveClickFeedback() {
            yield return new WaitForSeconds(0.15f); // Brief press feedback
            _gamemodeDisplayLabel?.RemoveFromClassList("gamemode-clicked");
        }

        private void ToggleGamemodeDropdown() {
            if(!_isHost) return;

            _isGamemodeDropdownOpen = !_isGamemodeDropdownOpen;

            if(_isGamemodeDropdownOpen) {
                _gamemodeDropdownMenu?.RemoveFromClassList("hidden");
                _gamemodeArrow?.RemoveFromClassList("hidden");
                _gamemodeArrow?.AddToClassList("arrow-down"); // Points up when open

                // Position dropdown below container
                if(_gamemodeDropdownMenu != null && _gamemodeDropdownContainer != null) {
                    var containerWorldPos = _gamemodeDropdownContainer.worldBound.position;
                    var containerLocalPos = _gamemodeDropdownContainer.parent.WorldToLocal(containerWorldPos);
                    _gamemodeDropdownMenu.style.left = containerLocalPos.x;
                    _gamemodeDropdownMenu.style.top =
                        containerLocalPos.y + _gamemodeDropdownContainer.resolvedStyle.height + 8f;
                }

                _gamemodeDropdownMenu?.BringToFront();
            } else {
                _gamemodeDropdownMenu?.AddToClassList("hidden");
                _gamemodeArrow?.RemoveFromClassList("arrow-down"); // Points down when closed (default)
            }
        }

        private int GetMatchDurationForMode(string modeName) {
            return modeName switch {
                "Deathmatch" => 600, // 10 min
                "Team Deathmatch" => 900, // 15 min
                "Gun Tag" => 300, // 5 min (shorter for Gun Tag mode)
                "Hopball" => 1200, // 20 min
                "Private Match" => 30, // 20 min or whatever
                _ => MatchSettingsManager.Instance != null
                    ? MatchSettingsManager.Instance.defaultMatchDurationSeconds
                    : 600
            };
        }

        private void OnGameModeSelected(string modeName) {
            // If clicking the same gamemode, just close the dropdown
            if(_selectedGameMode == modeName && _isGamemodeDropdownOpen) {
                ToggleGamemodeDropdown();
                // Click feedback is handled by coroutine
                return;
            }

            // Update local state immediately for instant feedback
            _selectedGameMode = modeName;

            if(MatchSettingsManager.Instance != null) {
                var settings = MatchSettingsManager.Instance;
                settings.selectedGameModeId = modeName;
                settings.matchDurationSeconds = GetMatchDurationForMode(modeName);
            }

            // Update display immediately (no delay)
            UpdateGamemodeDisplay();
            ToggleGamemodeDropdown(); // Close dropdown after selection

            // Sync gamemode to session properties in background (fire and forget)
            // This allows the host to see the change immediately while it syncs to clients
            SyncGamemodeToSessionAsync(modeName).Forget();

            // Click feedback is handled by coroutine, don't remove here

            // Start button is always enabled for hosts (regardless of gamemode)
            // It's disabled for non-hosts in UpdateHostStatus
        }

        private async UniTask SyncGamemodeToSessionAsync(string gamemode) {
            var session = SessionManager.Instance?.ActiveSession;
            if(session == null || !_isHost) return;

            try {
                var host = session.AsHost();
                if(host != null) {
                    host.SetProperty("gamemode", new SessionProperty(gamemode, VisibilityPropertyOptions.Member));
                    await host.SavePropertiesAsync();
                }
            } catch(Exception e) {
                Debug.LogWarning($"[MainMenuManager] Failed to sync gamemode to session: {e.Message}");
            }
        }

        private void UpdateGamemodeDisplay() {
            if(_gamemodeDisplayLabel != null) {
                // Show gamemode to host, "Lobby" to clients
                if(_isHost) {
                    _gamemodeDisplayLabel.text = _selectedGameMode ?? "Deathmatch";
                } else {
                    _gamemodeDisplayLabel.text = "Lobby";
                }
            }

            // Update host status and enable/disable dropdown interaction
            UpdateHostStatus();
        }

        private void UpdateGamemodeFromSession() {
            var session = SessionManager.Instance?.ActiveSession;
            if(session == null || _isHost) return; // Only update for clients

            // Try to get gamemode from session properties
            if(session.Properties.TryGetValue("gamemode", out var prop) && !string.IsNullOrEmpty(prop.Value)) {
                _selectedGameMode = prop.Value;
                if(_gamemodeDisplayLabel != null) {
                    _gamemodeDisplayLabel.text = _selectedGameMode;
                }

                // Also update MatchSettings
                if(MatchSettingsManager.Instance != null) {
                    MatchSettingsManager.Instance.selectedGameModeId = _selectedGameMode;
                }
            } else {
                // No gamemode set yet, show "Lobby"
                if(_gamemodeDisplayLabel != null) {
                    _gamemodeDisplayLabel.text = "Lobby";
                }
            }
        }

        private void UpdateHostStatus() {
            var session = SessionManager.Instance?.ActiveSession;
            var wasHost = _isHost;

            if(session == null) {
                _isHost = false;
                Debug.Log("[MainMenuManager] UpdateHostStatus: No active session, setting _isHost = false");
            } else {
                // Check if current player is host
                var hostId = session.Host;
                bool detectedAsHost = false;

                if(!string.IsNullOrEmpty(hostId)) {
                    // Primary check: Use session.IsHost property (most reliable)
                    if(session.IsHost) {
                        detectedAsHost = true;
                        Debug.Log($"[MainMenuManager] UpdateHostStatus: session.IsHost = true, hostId = {hostId}");
                    }
                    // Fallback: If we just created this session as host, we're the host
                    else if(_justCreatedSessionAsHost) {
                        detectedAsHost = true;
                        Debug.Log(
                            $"[MainMenuManager] UpdateHostStatus: Just created session as host (fallback), hostId = {hostId}");
                    }
                    // Fallback: If we're the only player, we must be the host
                    else if(session.Players.Count == 1) {
                        detectedAsHost = true;
                        Debug.Log(
                            $"[MainMenuManager] UpdateHostStatus: Only player in session (count={session.Players.Count}), assuming host");
                    }
                    // Additional fallback: Check if we created this session (tracked via session creation)
                    else {
                        Debug.Log(
                            $"[MainMenuManager] UpdateHostStatus: session.IsHost = {session.IsHost}, Players.Count = {session.Players.Count}, hostId = {hostId}, _justCreatedSessionAsHost = {_justCreatedSessionAsHost}");
                    }
                } else {
                    // Even if no hostId, if we just created the session, we're likely the host
                    if(_justCreatedSessionAsHost) {
                        detectedAsHost = true;
                        Debug.Log("[MainMenuManager] UpdateHostStatus: Just created session as host (no hostId yet)");
                    } else {
                        Debug.Log("[MainMenuManager] UpdateHostStatus: No host ID in session");
                    }
                }

                _isHost = detectedAsHost;
            }

            Debug.Log($"[MainMenuManager] UpdateHostStatus: Final _isHost = {_isHost} (was {wasHost})");

            // Subscribe/unsubscribe to events based on host status
            if(_isHost && !wasHost) {
                // Just became host - subscribe to events and show arrow with animation
                Debug.Log("[MainMenuManager] Just became host - subscribing to events and showing arrow");
                SubscribeToGamemodeEvents();
                ShowArrowWithAnimation();
            } else if(!_isHost && wasHost) {
                // No longer host - unsubscribe from events and hide arrow
                Debug.Log("[MainMenuManager] No longer host - unsubscribing from events and hiding arrow");
                UnsubscribeFromGamemodeEvents();
                _gamemodeArrow?.AddToClassList("hidden");
                _gamemodeDropdownMenu?.AddToClassList("hidden");
                _isGamemodeDropdownOpen = false;
                _gamemodeDisplayLabel?.RemoveFromClassList("gamemode-hover");
                _gamemodeDisplayLabel?.RemoveFromClassList("gamemode-clicked");
            } else if(!_isHost) {
                // Client - try to get gamemode from session
                UpdateGamemodeFromSession();
            }

            // Enable/disable dropdown interaction based on host status
            if(_gamemodeDisplayLabel != null) {
                _gamemodeDisplayLabel.SetEnabled(_isHost);
            }

            // Start button: enabled for hosts (only if not in gameplay and not already starting), disabled for non-hosts or if already in gameplay or starting
            if(_isHost && !(SessionManager.Instance?.IsInGameplay ?? false) && !_isStartingGame) {
                Debug.Log("[MainMenuManager] Enabling start button (host detected, not in gameplay, not starting)");
                EnableButton(_startButton);
            } else {
                Debug.Log(
                    $"[MainMenuManager] Disabling start button (not host or in gameplay or starting: isHost={_isHost}, isInGameplay={SessionManager.Instance?.IsInGameplay ?? false}, isStartingGame={_isStartingGame})");
                DisableButton(_startButton);
            }

            // Hide dropdown if not host
            if(!_isHost) {
                _gamemodeDropdownMenu?.AddToClassList("hidden");
                _isGamemodeDropdownOpen = false;
                _gamemodeArrow?.RemoveFromClassList("arrow-down");
                _gamemodeArrow?.RemoveFromClassList("arrow-slide-in");
            }
        }

        #endregion

        #region Lobby

        private async void OnHostClicked() {
            try {
                _isStartingGame = false; // Reset flag when hosting a new game
                DisableButton(_hostButton);
                DisableButton(_joinButton);
                // _waitingLabel.text = "Waiting for connection...";

                // Clear player list immediately
                if(_playerList != null) {
                    _playerList.Clear();
                }

                // Cache our own player name for immediate display
                _cachedPlayerName = PlayerPrefs.GetString("PlayerName", "Player");
                if(string.IsNullOrWhiteSpace(_cachedPlayerName)) {
                    _cachedPlayerName = "Player";
                }

                Debug.Log($"[MainMenuManager] Cached player name for host: {_cachedPlayerName}");

                var joinCode = await SessionManager.Instance.StartSessionAsHost();

                if(string.IsNullOrEmpty(joinCode)) {
                    // _waitingLabel.text = "Failed to create session";
                    EnableButton(_hostButton);
                    EnableButton(_joinButton);
                    _cachedPlayerName = null; // Clear cache on failure
                    return;
                }

                // Mark that we just created a session as host (fallback for host detection)
                _justCreatedSessionAsHost = true;
                Debug.Log("[MainMenuManager] Marked as just created session as host");

                _joinCodeLabel.text = $"Join Code: {joinCode}";
                // _waitingLabel.text = "Lobby ready";

                EnableButton(_copyButton);

                // Hook into property changes when hosting
                HookSessionPropertyChanges();

                // Set default gamemode to "Deathmatch" when hosting
                _selectedGameMode = "Deathmatch";
                if(MatchSettingsManager.Instance != null) {
                    var settings = MatchSettingsManager.Instance;
                    settings.selectedGameModeId = "Deathmatch";
                    settings.matchDurationSeconds = GetMatchDurationForMode("Deathmatch");
                }

                // Update display to show "Deathmatch" instead of "Lobby"
                if(_gamemodeDisplayLabel != null) {
                    _gamemodeDisplayLabel.text = "Deathmatch";
                }

                // Sync initial gamemode to session
                await SyncGamemodeToSessionAsync("Deathmatch");

                // Refresh session to ensure IsHost property is set correctly
                // Note: This is non-critical - we have fallback logic (_justCreatedSessionAsHost flag)
                var session = SessionManager.Instance?.ActiveSession;
                if(session != null) {
                    try {
                        // Small delay to avoid rate limiting if multiple operations happened quickly
                        await UniTask.Delay(100);
                        await session.RefreshAsync();
                        Debug.Log(
                            $"[MainMenuManager] Session refreshed after hosting. IsHost = {session.IsHost}, Host = {session.Host}, Players = {session.Players.Count}");
                    } catch(Exception e) {
                        // Handle rate limiting gracefully - this is non-critical since we have fallback logic
                        if(e.Message.Contains("Too Many Requests") || e.Message.Contains("429")) {
                            Debug.Log(
                                $"[MainMenuManager] Rate limited on session refresh (non-critical, using fallback host detection)");
                        } else {
                            Debug.LogWarning($"[MainMenuManager] Failed to refresh session after hosting: {e.Message}");
                        }
                    }
                }

                // Immediately refresh player list with current session players (or just ourselves if session hasn't updated)
                RefreshPlayerList();

                // Update host status after becoming host (this will enable start button and show arrow)
                // Also called by PlayersChanged event, but call here to ensure immediate update
                // Force update host status - the _justCreatedSessionAsHost flag should ensure we're detected as host
                UpdateHostStatus();

                // If still not detected as host after refresh, force it (fallback for second+ sessions)
                if(!_isHost && _justCreatedSessionAsHost) {
                    Debug.LogWarning(
                        "[MainMenuManager] Host not detected after refresh, forcing host status (fallback)");
                    _isHost = true;
                    EnableButton(_startButton);
                    SubscribeToGamemodeEvents();
                    ShowArrowWithAnimation();
                    if(_gamemodeDisplayLabel != null) {
                        _gamemodeDisplayLabel.SetEnabled(true);
                    }
                }
                // Player list will be filled by PlayersChanged event (which may have cached players, so we refresh manually above).
            } catch(Exception e) {
                Debug.LogException(e);
                // _waitingLabel.text = "Error creating session: " + e.Message;
                EnableButton(_hostButton);
                EnableButton(_joinButton);
            }
        }

        private async void OnJoinGameClicked(string code) {
            try {
                var regexAlphaNum = new System.Text.RegularExpressions.Regex("^[a-zA-Z0-9]*$");
                if(string.IsNullOrWhiteSpace(code) || code.Length != 6 || !regexAlphaNum.IsMatch(code)) {
                    _waitingLabel.text = "Invalid join code";
                    return;
                }

                // Clear player list immediately
                if(_playerList != null) {
                    _playerList.Clear();
                }

                // Cache our own player name for immediate display
                _cachedPlayerName = PlayerPrefs.GetString("PlayerName", "Player");
                if(string.IsNullOrWhiteSpace(_cachedPlayerName)) {
                    _cachedPlayerName = "Player";
                }

                Debug.Log($"[MainMenuManager] Cached player name for join: {_cachedPlayerName}");

                // _waitingLabel.text = "Joining lobby...";
                DisableButton(_hostButton);
                DisableButton(_joinButton);

                var result = await SessionManager.Instance.JoinSessionByCodeAsync(code);
                _waitingLabel.text = result;
                if(result.Contains("Lobby joined")) {
                    _joinCodeLabel.text = $"Join Code: {code}";
                    EnableButton(_copyButton);

                    // Immediately refresh player list with current session players
                    // This ensures we see the host and any other players even if session state is cached
                    RefreshPlayerList();
                } else {
                    EnableButton(_joinButton);
                    EnableButton(_hostButton);
                    _cachedPlayerName = null; // Clear cache on failure
                }
                // Player list will be filled by PlayersChanged event (which may have cached players, so we refresh manually above).
            } catch(Exception e) {
                Debug.LogException(e);
                _waitingLabel.text = "Error joining session: " + e.Message;
                EnableButton(_hostButton);
                EnableButton(_joinButton);
            }
        }

        private async void OnStartGameClicked() {
            try {
                _isStartingGame = true; // Mark that we're starting the game
                DisableButton(_startButton);
                // _waitingLabel.text = "Starting game...";

                // Close gamemode dropdown when starting game (as if a gamemode was selected)
                if(_isGamemodeDropdownOpen) {
                    ToggleGamemodeDropdown();
                }

                await SessionManager.Instance.BeginGameplayAsHostAsync();
                // Once gameplay actually starts, IsInGameplay will be true, so the flag will be checked but IsInGameplay takes precedence
            } catch(Exception e) {
                Debug.LogException(e);
                _waitingLabel.text = "Failed to start game: " + e.Message;
                _isStartingGame = false; // Reset flag on error so button can be clicked again
                EnableButton(_startButton);
            }
        }

        private void OnRelayCodeAvailable(string code) {
            // Cosmetic: keep label in sync when host publishes/upgrades relay code
            // if(_joinCodeLabel != null)
            // _joinCodeLabel.text = $"Join Code: {code}";
            Debug.Log("Relay code available: " + code);
        }

        private void OnPlayersChanged(IReadOnlyList<IReadOnlyPlayer> players) {
            RefreshPlayerList(players);
        }

        /// <summary>
        /// Manually refresh the player list with the given players, or current session players if null.
        /// This ensures the player list updates immediately even if session state is cached.
        /// </summary>
        private void RefreshPlayerList(IReadOnlyList<IReadOnlyPlayer> players = null) {
            if(_playerList == null) return;

            // Clear the list first
            _playerList.Clear();

            // Get players from parameter or current session
            if(players == null) {
                var session = SessionManager.Instance?.ActiveSession;
                if(session == null) return;
                players = session.Players;
            }

            if(players == null || players.Count == 0) {
                Debug.Log("[MainMenuManager] No players to display in player list");
                return;
            }

            // Identify host
            var hostId = SessionManager.Instance.ActiveSession?.Host;

            Debug.Log($"[MainMenuManager] Refreshing player list with {players.Count} players (hostId: {hostId})");

            foreach(var p in players) {
                string display;

                // Try to get player name from properties
                if(p.Properties != null &&
                   p.Properties.TryGetValue("playerName", out var prop) &&
                   !string.IsNullOrEmpty(prop.Value)) {
                    display = prop.Value;
                }
                // Fallback to cached name if available (for ourselves when session hasn't updated yet)
                else if(!string.IsNullOrEmpty(_cachedPlayerName) && p.Id == AuthenticationService.Instance.PlayerId) {
                    display = _cachedPlayerName;
                    Debug.Log($"[MainMenuManager] Using cached player name for self: {display}");
                }
                // Final fallback to player ID
                else {
                    display = p.Id;
                }

                bool isHost = !string.IsNullOrEmpty(hostId) && p.Id == hostId;
                AddPlayerEntry(display, isHost);
                Debug.Log($"[MainMenuManager] Added player to list: {display} (isHost: {isHost}, id: {p.Id})");
            }

            // Update host status after players change
            UpdateHostStatus();
        }

        private void AddPlayerEntry(string playerName, bool isHost) {
            foreach(var child in _playerList.Children())
                child.style.borderBottomWidth = 1f;

            var entry = new VisualElement();
            entry.AddToClassList("player-entry");
            if(isHost) entry.AddToClassList("host");

            var label = new Label(playerName);
            entry.Add(label);
            _playerList.Add(entry);

            entry.style.borderBottomWidth = 0f;
        }

        private void CopyJoinCodeToClipboard() {
            // Play click sound
            OnButtonClicked();

            // Extract code from "Join Code: ABC123" → "ABC123"
            var fullText = _joinCodeLabel.text;
            var code = fullText.Replace("Join Code: ", "").Trim();

            if(code.Length == 0) return;

            GUIUtility.systemCopyBuffer = code;

            // StartCoroutine(CopyToast("Copied!"));
        }

        private void OnJoinCodeInputValueChanged(ChangeEvent<string> evt) {
            // Force uppercase
            if(evt.newValue != evt.newValue.ToUpper()) {
                _joinCodeInput.value = evt.newValue.ToUpper();
            }

            UpdateJoinCodePlaceholderVisibility();
        }

        private void OnJoinCodeInputFocusIn(FocusInEvent evt) {
            UpdateJoinCodePlaceholderVisibility();
        }

        private void OnJoinCodeInputFocusOut(FocusOutEvent evt) {
            UpdateJoinCodePlaceholderVisibility();
        }

        private void UpdateJoinCodePlaceholderVisibility() {
            if(_joinCodeInput == null) return;

            // Hide placeholder if field has value or is focused
            bool hasValue = !string.IsNullOrEmpty(_joinCodeInput.value);

            // Unity's hide-placeholder-on-focus handles focus, but we need to handle value
            // We'll use a class to hide the placeholder when there's a value
            if(hasValue) {
                _joinCodeInput.AddToClassList("has-value");
            } else {
                _joinCodeInput.RemoveFromClassList("has-value");
            }
        }

        #endregion

        #region Settings

        private void SetupOptionsMenuManager() {
            if(optionsMenuManager == null) {
                Debug.LogError("[MainMenuManager] OptionsMenuManager not assigned!");
                return;
            }

            // Set up callbacks
            optionsMenuManager.OnButtonClickedCallback = OnButtonClicked;
            optionsMenuManager.MouseEnterCallback = MouseEnter;
            optionsMenuManager.OnBackFromOptionsCallback = () => ShowPanel(MainMenuPanel);

            // Initialize the options menu manager
            optionsMenuManager.Initialize();
        }

        private void LoadSettings() {
            if(optionsMenuManager != null) {
                optionsMenuManager.LoadSettings();
            }
        }

        #endregion

        #region UI Utilities

        private bool _isInitializing = true;

        public void OnButtonClicked(bool isBack = false) {
            // Don't play sounds during initialization to prevent startup sound
            if(_isInitializing) return;

            if(SoundFXManager.Instance != null) {
                var soundKey = !isBack ? SfxKey.ButtonClick : SfxKey.BackButton;
                SoundFXManager.Instance.PlayUISound(soundKey);
            }
        }

        public void MouseEnter(MouseEnterEvent evt) {
            // MouseEnterEvent only fires when entering the element, not when moving over children
            // This prevents multiple triggers when moving mouse over child elements (images, etc.)
            if(SoundFXManager.Instance != null) {
                SoundFXManager.Instance.PlayUISound(SfxKey.ButtonHover);
            }
        }

        private void EnableButton(Button button) {
            button.AddToClassList("menu-chip-enabled");
            button.SetEnabled(true);
            button.UnregisterCallback<MouseEnterEvent>(MouseEnter);
            button.RegisterCallback<MouseEnterEvent>(MouseEnter);
        }

        private void DisableButton(Button button) {
            button.RemoveFromClassList("menu-chip-enabled");
            button.SetEnabled(false);
            button.UnregisterCallback<MouseEnterEvent>(MouseEnter);
        }

        private IEnumerator CopyToast(string message) {
            // Create toast element
            var toast = new Label(message) {
                name = "toast"
            };
            toast.AddToClassList("toast");

            _toastContainer.Add(toast);

            // Trigger enter animation
            toast.AddToClassList("show");

            // Wait for display
            yield return new WaitForSeconds(1.2f);

            // Trigger exit animation
            toast.RemoveFromClassList("show");
            toast.AddToClassList("hide");

            // Wait for fade out
            yield return new WaitForSeconds(0.3f);

            // Remove from hierarchy
            _toastContainer.Remove(toast);
        }

        #endregion
        
        #region Quit Confirmation

        private void ShowQuitConfirmation() {
            OnButtonClicked(isBack: true); // Play back button click sound
            if(_quitConfirmationModal != null) {
                _quitConfirmationModal.RemoveFromClassList("hidden");
                _quitConfirmationModal.style.display = DisplayStyle.Flex; // Ensure it's visible
                // Ensure modal appears on top of all other UI elements
                _quitConfirmationModal.BringToFront();
            }
        }

        private void HideQuitConfirmation() {
            if(_quitConfirmationModal != null) {
                _quitConfirmationModal.AddToClassList("hidden");
                _quitConfirmationModal.style.display = StyleKeyword.Null; // Reset display style
            }
        }

        private void OnQuitConfirmed() {
            // No sound needed - app is quitting
            Debug.Log("Quitting game...");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void OnQuitCancelled() {
            OnButtonClicked(); // Play normal button click sound (not back button sound)
            HideQuitConfirmation();
        }

        #endregion

        #region Lobby Leave Confirmation

        private void OnBackFromLobbyClicked() {
            // Check if we should show the confirmation modal
            var session = SessionManager.Instance?.ActiveSession;
            bool shouldShowModal = false;

            if(session != null) {
                // Show modal if hosting OR if we joined with a join code (join code input has value)
                shouldShowModal = _isHost || (!string.IsNullOrWhiteSpace(_joinCodeInput.value) && !_isHost);
            }

            if(shouldShowModal) {
                ShowLobbyLeaveConfirmation();
            } else {
                // No modal needed, leave directly without fade
                OnButtonClicked(isBack: true);
                SessionManager.Instance?.LeaveToMainMenuAsync(skipFade: true).Forget();
                ShowPanel(MainMenuPanel);
            }
        }

        private void ShowLobbyLeaveConfirmation() {
            OnButtonClicked(isBack: true); // Play back button click sound
            if(_lobbyLeaveModal != null) {
                _lobbyLeaveModal.RemoveFromClassList("hidden");
                _lobbyLeaveModal.style.display = DisplayStyle.Flex; // Ensure it's visible
                // Ensure modal appears on top of all other UI elements
                _lobbyLeaveModal.BringToFront();
            }
        }

        private void HideLobbyLeaveConfirmation() {
            if(_lobbyLeaveModal != null) {
                _lobbyLeaveModal.AddToClassList("hidden");
                _lobbyLeaveModal.style.display = StyleKeyword.Null; // Reset display style
            }
        }

        private void OnLobbyLeaveConfirmed() {
            OnButtonClicked(isBack: true); // Play back button click sound
            HideLobbyLeaveConfirmation();
            // Leave without fade since this is a voluntary leave
            SessionManager.Instance.LeaveToMainMenuAsync(skipFade: true).Forget();
            ShowPanel(MainMenuPanel);
        }

        private void OnLobbyLeaveCancelled() {
            OnButtonClicked(); // Play normal button click sound
            HideLobbyLeaveConfirmation();
        }

        #endregion
    }
}
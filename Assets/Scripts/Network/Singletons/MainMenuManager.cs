using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Serialization;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace Network.Singletons {
    public class MainMenuManager : MonoBehaviour {
        #region Debug Logging

        private const bool DebugLogs = true;

        private static void D(string msg, bool isServer = false) {
            if(!DebugLogs) return;
            var prefix = isServer ? "[HOST]" : "[CLIENT]";
            Debug.Log($"{prefix} {msg} | Frame: {Time.frameCount} | Time: {Time.time:F2}");
        }

        #endregion

        #region Serialized

        public UIDocument uiDocument;
        [SerializeField] private AudioMixer audioMixer;
        [SerializeField] private SessionManager sessionManager;
        [SerializeField] private GameMenuManager gameMenuManager;

        [SerializeField] private AudioClip buttonClickSound;
        [SerializeField] private AudioClip buttonHoverSound;
        [SerializeField] private AudioClip backClickSound;
        [SerializeField] private Color[] playerColors;
        [SerializeField] private Material[] playerMaterials;

        [SerializeField] private Camera mainCamera;

        #endregion

        #region Private UI refs

        private VisualElement _mainMenuPanel;
        private VisualElement _gamemodePanel;
        private VisualElement _lobbyPanel;
        private VisualElement _loadoutPanel;
        private VisualElement _optionsPanel;
        private VisualElement _creditsPanel;

        private List<VisualElement> _panels;

        private string _selectedGameMode;

        // Audio
        private Slider _masterVolumeSlider;
        private Slider _musicVolumeSlider;
        private Slider _sfxVolumeSlider;
        private Label _masterVolumeValue;
        private Label _musicVolumeValue;
        private Label _sfxVolumeValue;

        // Controls
        private Slider _sensitivityXSlider;
        private Slider _sensitivityYSlider;
        private Label _sensitivityXValue;
        private Label _sensitivityYValue;
        private Toggle _invertYToggle;

        // Graphics
        private DropdownField _qualityDropdown;
        private Toggle _vsyncToggle;
        private DropdownField _fpsDropdown;

        // Lobby
        private Label _joinCodeLabel;
        private Label _waitingLabel;
        private TextField _joinCodeInput;
        private VisualElement _playerList;
        private VisualElement _toastContainer;

        // Buttons
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
        private Button _applySettingsButton;
        private Button _backOptionsButton;
        private Button _joinButton;
        private Button _copyButton;
        private List<Button> _buttons;

        private VisualElement _root;

        #endregion

        private VisualElement _firstTimeModal;
        private VisualElement _firstTimeCurrentColor;
        private VisualElement _firstTimeColorOptions;
        private TextField _firstTimeNameInput;
        private Button _firstTimeContinueButton;
        private int _firstTimeSelectedColorIndex = 0;
        private TextField _nameInput;

        private void CheckFirstTimeSetup() {
            // Check if player has set up their profile
            bool hasName = PlayerPrefs.HasKey("PlayerName");
            bool hasColor = PlayerPrefs.HasKey("PlayerColorIndex");

            if(!hasName || !hasColor) {
                ShowFirstTimeSetup();
            }
        }

        private void SetupFirstTimeModal() {
            _firstTimeModal = _root.Q<VisualElement>("first-time-setup-modal");
            _firstTimeCurrentColor = _root.Q<VisualElement>("first-time-current-color");
            _firstTimeColorOptions = _root.Q<VisualElement>("first-time-color-options");
            _firstTimeNameInput = _root.Q<TextField>("first-time-name-input");
            _firstTimeContinueButton = _root.Q<Button>("first-time-continue-button");

            // Setup current color display clicks
            for (int i = 0; i < 7; i++) {
                var currentCircle = _root.Q<VisualElement>($"first-time-current-color-{i}");
                if (currentCircle != null) {
                    currentCircle.RegisterCallback<ClickEvent>(evt => {
                        _firstTimeColorOptions.ToggleInClassList("hidden");
                    });
                }
            }

            // Show first color by default
            UpdateFirstTimeColorDisplay(0);

            // Setup color option clicks
            for(int i = 0; i < 7; i++) {
                var optionCircle = _root.Q<VisualElement>($"first-time-option-color-{i}");
                if(optionCircle != null) {
                    var index = i;
                    optionCircle.RegisterCallback<ClickEvent>(evt => {
                        _firstTimeSelectedColorIndex = index;
                        UpdateFirstTimeColorDisplay(index);
                        _firstTimeColorOptions.AddToClassList("hidden");
                    });
                }
            }

            // Continue button
            _firstTimeContinueButton.clicked += OnFirstTimeSetupContinue;
        }
        
        private void UpdateFirstTimeColorDisplay(int colorIndex) {
            // Hide all current color circles
            for (int i = 0; i < 7; i++) {
                var currentCircle = _root.Q<VisualElement>($"first-time-current-color-{i}");
                currentCircle?.AddToClassList("hidden");
            }
    
            // Show only the selected color
            var selectedCircle = _root.Q<VisualElement>($"first-time-current-color-{colorIndex}");
            selectedCircle?.RemoveFromClassList("hidden");
    
            // Update options - hide selected, show others
            for (int i = 0; i < 7; i++) {
                var optionCircle = _root.Q<VisualElement>($"first-time-option-color-{i}");
                if (optionCircle != null) {
                    if (i == colorIndex) {
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

            Debug.Log($"First time setup complete: {playerName}, Color: {_firstTimeSelectedColorIndex}");
        }

        private void OnEnable() {
            _root = uiDocument.rootVisualElement;

            SetupFirstTimeModal();
            CheckFirstTimeSetup();

            // Panels
            _mainMenuPanel = _root.Q<VisualElement>("main-menu-panel");
            _gamemodePanel = _root.Q<VisualElement>("gamemode-panel");
            _lobbyPanel = _root.Q<VisualElement>("lobby-panel");
            _loadoutPanel = _root.Q<VisualElement>("loadout-panel");
            _nameInput = _root.Q<TextField>("player-name-input");
            _optionsPanel = _root.Q<VisualElement>("options-panel");
            _creditsPanel = _root.Q<VisualElement>("credits-panel");

            _panels = new List<VisualElement> {
                _mainMenuPanel,
                _gamemodePanel,
                _lobbyPanel,
                _loadoutPanel,
                _optionsPanel,
                _creditsPanel
            };

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
            _applySettingsButton = _root.Q<Button>("apply-button");
            _backOptionsButton = _root.Q<Button>("back-button");
            _hostButton = _root.Q<Button>("host-button");
            _startButton = _root.Q<Button>("start-button");
            _joinButton = _root.Q<Button>("join-button");
            _copyButton = _root.Q<Button>("copy-code-button");

            _buttons = new List<Button> {
                _playButton,
                _loadoutButton,
                _optionsButton,
                _creditsButton,
                _quitButton,
                _modeOneButton,
                _modeTwoButton,
                _modeThreeButton,
                _backGamemodeButton,
                _backCreditsButton,
                _backLobbyButton,
                _backLobbyButton,
                _applySettingsButton,
                _backOptionsButton,
                _hostButton,
                _startButton,
                _joinButton,
                _copyButton
            };

            foreach(var b in _buttons) {
                b.clicked += () => OnButtonClicked(b.ClassListContains("back-button"));
                b.RegisterCallback<MouseOverEvent>(MouseHover);
            }

            // Options controls
            _masterVolumeSlider = _root.Q<Slider>("master-volume");
            _musicVolumeSlider = _root.Q<Slider>("music-volume");
            _sfxVolumeSlider = _root.Q<Slider>("sfx-volume");
            _masterVolumeValue = _root.Q<Label>("master-volume-value");
            _musicVolumeValue = _root.Q<Label>("music-volume-value");
            _sfxVolumeValue = _root.Q<Label>("sfx-volume-value");

            _sensitivityXSlider = _root.Q<Slider>("sensitivity-x");
            _sensitivityYSlider = _root.Q<Slider>("sensitivity-y");
            _sensitivityXValue = _root.Q<Label>("sensitivity-x-value");
            _sensitivityYValue = _root.Q<Label>("sensitivity-y-value");
            _invertYToggle = _root.Q<Toggle>("invert-y");

            _qualityDropdown = _root.Q<DropdownField>("quality-level");
            _vsyncToggle = _root.Q<Toggle>("vsync");
            _fpsDropdown = _root.Q<DropdownField>("target-fps");

            // Lobby
            _joinCodeInput = _root.Q<TextField>("join-input");
            _joinCodeLabel = _root.Q<Label>("host-label");
            _toastContainer = _root.Q<VisualElement>("toast-container");
            _playerList = _root.Q<VisualElement>("player-list");
            _waitingLabel = _root.Q<Label>("waiting-label");

            // Main actions
            _playButton.clicked += () => ShowPanel(_gamemodePanel);

            _loadoutButton.clicked += () => {
                _nameInput.value = PlayerPrefs.GetString("PlayerName");
                var loadoutManager = FindFirstObjectByType<LoadoutManager>();
                loadoutManager.ShowLoadout();
                ShowPanel(_loadoutPanel);
            };

            _optionsButton.clicked += () => {
                LoadSettings();
                ShowPanel(_optionsPanel);
            };

            _creditsButton.clicked += () => ShowPanel(_creditsPanel);

            var logo = _root.Q<Image>("credits-logo");
            logo.RegisterCallback<ClickEvent>(evt => { Application.OpenURL("https://github.com/whosteenie/HOP"); });

            _backCreditsButton.clicked += () => ShowPanel(_mainMenuPanel);

            _quitButton.clicked += OnQuitClicked;

            // Gamemodes
            _modeOneButton.clicked += () => OnGameModeSelected("Deathmatch");

            _modeTwoButton.clicked += () => OnGameModeSelected("Team Deathmatch");

            _modeThreeButton.clicked += () => OnGameModeSelected("Private Match");

            _backGamemodeButton.clicked += () => { ShowPanel(_mainMenuPanel); };

            _backLobbyButton.clicked += () => {
                SessionManager.Instance.LeaveToMainMenuAsync();
                ShowPanel(_gamemodePanel);
            };

            // Options
            _applySettingsButton.clicked += ApplySettings;

            _backOptionsButton.clicked += () => ShowPanel(_mainMenuPanel);

            // Lobby actions

            _startButton.clicked += OnStartGameClicked;

            _joinCodeInput.RegisterValueChangedCallback(OnJoinCodeInputValueChanged);
            _joinCodeInput.maxLength = 6;
            _joinCodeInput.isDelayed = false;
            _joinButton.clicked += () => OnJoinGameClicked(_joinCodeInput.value.ToUpper());

            _hostButton.clicked += OnHostClicked;

            _copyButton.clicked += CopyJoinCodeToClipboard;

            SetupAudioCallbacks();
            SetupControlsCallbacks();
            SetupGraphicsCallbacks();
            LoadSettings();

            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            // Subscribe to session events for lobby UI
            if(sessionManager != null) {
                sessionManager.PlayersChanged += OnPlayersChanged;
                sessionManager.RelayCodeAvailable += OnRelayCodeAvailable;
            }

            var gameMenu = GameMenuManager.Instance;
            if(gameMenu != null && gameMenu.TryGetComponent(out UIDocument doc)) {
                var gameRoot = doc.rootVisualElement;
                var rootContainer = gameRoot?.Q<VisualElement>("root-container");
                if(rootContainer != null)
                    rootContainer.style.display = DisplayStyle.None;
            }
        }

        private void CopyJoinCodeToClipboard() {
            // Extract code from "Join Code: ABC123" → "ABC123"
            var fullText = _joinCodeLabel.text;
            var code = fullText.Replace("Join Code: ", "").Trim();

            if(code.Length == 0) return;

            GUIUtility.systemCopyBuffer = code;

            // StartCoroutine(CopyToast("Copied!"));
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

        private void OnButtonClicked(bool isBack = false) {
            SoundFXManager.Instance.PlayUISound(!isBack ? buttonClickSound : backClickSound);
        }

        private void EnableButton(Button button) {
            button.AddToClassList("menu-chip-enabled");
            button.SetEnabled(true);
            button.RegisterCallback<MouseOverEvent>(MouseHover);
        }

        private void DisableButton(Button button) {
            button.RemoveFromClassList("menu-chip-enabled");
            button.SetEnabled(false);
            button.UnregisterCallback<MouseOverEvent>(MouseHover);
        }

        private void OnJoinCodeInputValueChanged(ChangeEvent<string> evt) {
            // Force uppercase
            if(evt.newValue != evt.newValue.ToUpper()) {
                _joinCodeInput.value = evt.newValue.ToUpper();
            }
        }

        private void OnDisable() {
            if(sessionManager != null) {
                sessionManager.PlayersChanged -= OnPlayersChanged;
                sessionManager.RelayCodeAvailable -= OnRelayCodeAvailable;
            }
        }

        #region Navigation

        private void OnGameModeSelected(string modeName) {
            _selectedGameMode = modeName;

            if(modeName != "Private Match") {
                DisableButton(_startButton);
            } else {
                EnableButton(_startButton);
            }

            _joinCodeLabel.text = "Join Code: - - - - - - -";
            _waitingLabel.text = "Join or host";

            EnableButton(_hostButton);
            EnableButton(_joinButton);
            DisableButton(_copyButton);

            _joinCodeInput.value = "";
            _playerList.Clear();
            ShowPanel(_lobbyPanel);
        }

        private void ShowPanel(VisualElement panel) {
            foreach(var p in _panels)
                p.AddToClassList("hidden");

            panel.RemoveFromClassList("hidden");
        }

        private void MouseHover(MouseOverEvent evt) {
            SoundFXManager.Instance.PlayUISound(buttonHoverSound);
        }

        private async void OnHostClicked() {
            try {
                DisableButton(_hostButton);
                DisableButton(_joinButton);
                _waitingLabel.text = "Waiting for connection...";

                var joinCode = await sessionManager.StartSessionAsHost();
                D($"UI: Host created – Code: {joinCode}");

                if(string.IsNullOrEmpty(joinCode)) {
                    _waitingLabel.text = "Failed to create session";
                    EnableButton(_hostButton);
                    EnableButton(_joinButton);
                    return;
                }

                _joinCodeLabel.text = $"Join Code: {joinCode}";
                _waitingLabel.text = "Lobby ready";

                EnableButton(_startButton);
                EnableButton(_copyButton);
                // Player list will be filled by PlayersChanged event.
            } catch(Exception e) {
                Debug.LogException(e);
                _waitingLabel.text = "Error creating session: " + e.Message;
                EnableButton(_hostButton);
                EnableButton(_joinButton);
            }
        }

        private async void OnStartGameClicked() {
            try {
                DisableButton(_startButton);
                _waitingLabel.text = "Starting game...";

                D("UI: Start Game button clicked");
                await sessionManager.BeginGameplayAsHostAsync();
            } catch(Exception e) {
                Debug.LogException(e);
                _waitingLabel.text = "Failed to start game: " + e.Message;
                EnableButton(_startButton);
            }
        }

        private async void OnJoinGameClicked(string code) {
            try {
                var regexAlphaNum = new System.Text.RegularExpressions.Regex("^[a-zA-Z0-9]*$");
                if(string.IsNullOrWhiteSpace(code) || code.Length != 6 || !regexAlphaNum.IsMatch(code)) {
                    _waitingLabel.text = "Invalid join code";
                    return;
                }

                D($"UI: Join button clicked – Code: {code}");

                _waitingLabel.text = "Joining lobby...";
                DisableButton(_hostButton);
                DisableButton(_joinButton);

                var result = await sessionManager.JoinSessionByCodeAsync(code);
                D($"UI: Join result: {result}");
                _waitingLabel.text = result;
                if(result is "Lobby joined" or "Connected to Local Host (Editor)") {
                    _joinCodeLabel.text = $"Join Code: {code}";
                    EnableButton(_copyButton);
                } else {
                    EnableButton(_joinButton);
                    EnableButton(_hostButton);
                }
                // Player list will be filled by PlayersChanged event.
            } catch(Exception e) {
                Debug.LogException(e);
                _waitingLabel.text = "Error joining session: " + e.Message;
                EnableButton(_hostButton);
                EnableButton(_joinButton);
            }
        }

        #endregion

        #region Lobby UI Updates

        private void OnRelayCodeAvailable(string code) {
            // Cosmetic: keep label in sync when host publishes/upgrades relay code
            // if(_joinCodeLabel != null)
            // _joinCodeLabel.text = $"Join Code: {code}";
            Debug.Log("Relay code available: " + code);
        }

        private void OnPlayersChanged(IReadOnlyList<IReadOnlyPlayer> players) {
            if(_playerList == null || players == null) return;

            _playerList.Clear();

            // Identify host
            string hostId = SessionManager.Instance.ActiveSession?.Host;

            foreach(var p in players) {
                string display = (p.Properties != null &&
                                  p.Properties.TryGetValue("playerName", out var prop) &&
                                  !string.IsNullOrEmpty(prop.Value))
                    ? prop.Value
                    : p.Id;

                AddPlayerEntry(display, p.Id == hostId);
            }
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

        #endregion

        #region Settings

        private void SetupAudioCallbacks() {
            _masterVolumeSlider.RegisterValueChangedCallback(evt => {
                _masterVolumeValue.text = $"{Mathf.RoundToInt(evt.newValue * 100)}%";
            });
            _musicVolumeSlider.RegisterValueChangedCallback(evt => {
                _musicVolumeValue.text = $"{Mathf.RoundToInt(evt.newValue * 100)}%";
            });
            _sfxVolumeSlider.RegisterValueChangedCallback(evt => {
                _sfxVolumeValue.text = $"{Mathf.RoundToInt(evt.newValue * 100)}%";
            });
        }

        private static float LinearToDb(float linear) => linear <= 0f ? -80f : 20f * Mathf.Log10(linear);
        private static float DbToLinear(float db) => db <= -80f ? 0f : Mathf.Pow(10f, db / 20);

        private void SetupControlsCallbacks() {
            _sensitivityXSlider.RegisterValueChangedCallback(evt => {
                _sensitivityXValue.text = evt.newValue.ToString("F2");
            });

            _sensitivityYSlider.RegisterValueChangedCallback(evt => {
                _sensitivityYValue.text = evt.newValue.ToString("F2");
            });
        }

        private void SetupGraphicsCallbacks() {
            _qualityDropdown.choices = new List<string>(QualitySettings.names);
            _qualityDropdown.index = QualitySettings.GetQualityLevel();

            _fpsDropdown.choices = new List<string> { "30", "60", "120", "144", "Unlimited" };
        }

        private void LoadSettings() {
            var masterDb = PlayerPrefs.GetFloat("MasterVolume", 0f);
            var musicDb = PlayerPrefs.GetFloat("MusicVolume", 0f);
            var sfxDb = PlayerPrefs.GetFloat("SFXVolume", 0f);
            _masterVolumeSlider.value = DbToLinear(masterDb);
            _musicVolumeSlider.value = DbToLinear(musicDb);
            _sfxVolumeSlider.value = DbToLinear(sfxDb);

            _sensitivityXSlider.value = PlayerPrefs.GetFloat("SensitivityX", 0.1f);
            _sensitivityYSlider.value = PlayerPrefs.GetFloat("SensitivityY", 0.1f);
            _invertYToggle.value = PlayerPrefs.GetInt("InvertY", 0) == 1;

            _qualityDropdown.index = PlayerPrefs.GetInt("QualityLevel", QualitySettings.GetQualityLevel());
            _vsyncToggle.value = PlayerPrefs.GetInt("VSync", 0) == 1;
            _fpsDropdown.index = PlayerPrefs.GetInt("TargetFPS", 1);

            ApplySettingsInternal();
        }

        private void ApplySettings() {
            var masterDb = LinearToDb(_masterVolumeSlider.value);
            var musicDb = LinearToDb(_musicVolumeSlider.value);
            var sfxDb = LinearToDb(_sfxVolumeSlider.value);
            PlayerPrefs.SetFloat("MasterVolume", masterDb);
            PlayerPrefs.SetFloat("MusicVolume", musicDb);
            PlayerPrefs.SetFloat("SFXVolume", sfxDb);

            PlayerPrefs.SetFloat("SensitivityX", _sensitivityXSlider.value);
            PlayerPrefs.SetFloat("SensitivityY", _sensitivityYSlider.value);
            PlayerPrefs.SetInt("InvertY", _invertYToggle.value ? 1 : 0);

            PlayerPrefs.SetInt("QualityLevel", _qualityDropdown.index);
            PlayerPrefs.SetInt("VSync", _vsyncToggle.value ? 1 : 0);
            PlayerPrefs.SetInt("TargetFPS", _fpsDropdown.index);

            PlayerPrefs.Save();

            ApplySettingsInternal();

            Debug.Log("Settings applied and saved!");
        }

        private void ApplySettingsInternal() {
            if(audioMixer != null) {
                audioMixer.SetFloat("masterVolume", LinearToDb(_masterVolumeSlider.value));
                audioMixer.SetFloat("musicVolume", LinearToDb(_musicVolumeSlider.value));
                audioMixer.SetFloat("soundFXVolume", LinearToDb(_sfxVolumeSlider.value));
            }

            QualitySettings.SetQualityLevel(_qualityDropdown.index);
            QualitySettings.vSyncCount = _vsyncToggle.value ? 1 : 0;

            switch(_fpsDropdown.index) {
                case 0:
                    Application.targetFrameRate = 30;
                    break;
                case 1:
                    Application.targetFrameRate = 60;
                    break;
                case 2:
                    Application.targetFrameRate = 120;
                    break;
                case 3:
                    Application.targetFrameRate = 144;
                    break;
                case 4:
                    Application.targetFrameRate = -1;
                    break; // Unlimited
            }
        }

        #endregion

        // ===== Misc =====

        private void OnQuitClicked() {
            Debug.Log("Quitting game...");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
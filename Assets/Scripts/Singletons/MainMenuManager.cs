using System;
using System.Collections.Generic;
using Network;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace Singletons {
    public class MainMenuManager : MonoBehaviour {
        #region Serialized
        [SerializeField] private UIDocument uiDocument;
        [SerializeField] private AudioMixer audioMixer;
        [SerializeField] private SessionManager sessionManager;
        [SerializeField] private GameMenuManager gameMenuManager;
    
        [SerializeField] private AudioClip buttonClickSound;
        [SerializeField] private AudioClip buttonHoverSound;
        [SerializeField] private AudioClip backClickSound;

        [SerializeField] private Camera mainCamera;
        #endregion

        #region Private UI refs
        private VisualElement _mainMenuPanel;
        private VisualElement _gamemodePanel;
        private VisualElement _lobbyPanel;
        private VisualElement _optionsPanel;

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
        private Button _hostButton;
        private Button _startButton;
        private Button _joinSessionButton;
        private TextField _joinCodeInput;
        private VisualElement _playerList;
        private Label _waitingLabel;
    
        private Button _playButton;
        private Button _loadoutButton;
        private Button _optionsButton;
        private Button _creditsButton;
        private Button _quitButton;
        private Button _modeOneButton;
        private Button _modeTwoButton;
        private Button _modeThreeButton;
        private Button _backToMainButton;
        private Button _backToGamemodeButton;
        private Button _applySettingsButton;
        private Button _backFromOptionsButton;
        private Button _joinButton;
    
        #endregion

        private void OnEnable() {
            var root = uiDocument.rootVisualElement;

            // Panels
            _mainMenuPanel = root.Q<VisualElement>("main-menu-panel");
            _gamemodePanel = root.Q<VisualElement>("gamemode-panel");
            _lobbyPanel = root.Q<VisualElement>("lobby-panel");
            _optionsPanel = root.Q<VisualElement>("options-panel");
        
            _playButton = root.Q<Button>("play-button");
            _loadoutButton = root.Q<Button>("loadout-button");
            _optionsButton = root.Q<Button>("options-button");
            _creditsButton = root.Q<Button>("credits-button");
            _quitButton = root.Q<Button>("quit-button");

            // Main actions
            _playButton.clicked += () => ShowPanel(_gamemodePanel);
            _playButton.RegisterCallback<MouseOverEvent>(MouseHover);
        
            _loadoutButton.clicked += OnLoadoutClicked;
            _loadoutButton.RegisterCallback<MouseOverEvent>(MouseHover);
        
            _optionsButton.clicked += () => {
                LoadSettings();
                ShowPanel(_optionsPanel);
            };
            _optionsButton.RegisterCallback<MouseOverEvent>(MouseHover);
        
            _creditsButton.clicked += OnCreditsClicked;
            _creditsButton.RegisterCallback<MouseOverEvent>(MouseHover);
        
            _quitButton.clicked += OnQuitClicked;
            _quitButton.RegisterCallback<MouseOverEvent>(MouseHover);
        
            _modeOneButton = root.Q<Button>("mode-one-button");
            _modeTwoButton = root.Q<Button>("mode-two-button");
            _modeThreeButton = root.Q<Button>("mode-three-button");
            _backToMainButton = root.Q<Button>("back-to-main");
            _backToGamemodeButton = root.Q<Button>("back-to-gamemode");

            // Gamemodes
            _modeOneButton.clicked += () => OnGameModeSelected("Deathmatch");
            _modeOneButton.RegisterCallback<MouseOverEvent>(MouseHover);
        
            _modeTwoButton.clicked += () => OnGameModeSelected("Team Deathmatch");
            _modeTwoButton.RegisterCallback<MouseOverEvent>(MouseHover);
        
            _modeThreeButton.clicked += () => OnGameModeSelected("Private Match");
            _modeThreeButton.RegisterCallback<MouseOverEvent>(MouseHover);
        
            _backToMainButton.clicked += () => ShowPanel(_mainMenuPanel, true);
            _backToMainButton.RegisterCallback<MouseOverEvent>(MouseHover);
        
            _backToGamemodeButton.clicked += () => {
                SessionManager.Instance.LeaveToMainMenuAsync();
                ShowPanel(_gamemodePanel, true);
            };
            _backToGamemodeButton.RegisterCallback<MouseOverEvent>(MouseHover);

            _applySettingsButton = root.Q<Button>("apply-button");
            _backFromOptionsButton = root.Q<Button>("back-button");
        
            // Options
            _applySettingsButton.clicked += ApplySettings;
            _applySettingsButton.RegisterCallback<MouseOverEvent>(MouseHover);
        
            _backFromOptionsButton.clicked += () => ShowPanel(_mainMenuPanel, true);
            _backFromOptionsButton.RegisterCallback<MouseOverEvent>(MouseHover);

            // Options controls
            _masterVolumeSlider = root.Q<Slider>("master-volume");
            _musicVolumeSlider = root.Q<Slider>("music-volume");
            _sfxVolumeSlider = root.Q<Slider>("sfx-volume");
            _masterVolumeValue = root.Q<Label>("master-volume-value");
            _musicVolumeValue = root.Q<Label>("music-volume-value");
            _sfxVolumeValue = root.Q<Label>("sfx-volume-value");

            _sensitivityXSlider = root.Q<Slider>("sensitivity-x");
            _sensitivityYSlider = root.Q<Slider>("sensitivity-y");
            _sensitivityXValue = root.Q<Label>("sensitivity-x-value");
            _sensitivityYValue = root.Q<Label>("sensitivity-y-value");
            _invertYToggle = root.Q<Toggle>("invert-y");

            _qualityDropdown = root.Q<DropdownField>("quality-level");
            _vsyncToggle = root.Q<Toggle>("vsync");
            _fpsDropdown = root.Q<DropdownField>("target-fps");

            // Lobby
            _joinCodeInput = root.Q<TextField>("join-input");
            _hostButton = root.Q<Button>("host-button");
            _joinCodeLabel = root.Q<Label>("host-label");
            _playerList = root.Q<VisualElement>("player-list");
            _startButton = root.Q<Button>("start-button");
            _joinSessionButton = root.Q<Button>("join-button");
            _waitingLabel = root.Q<Label>("waiting-label");
            _joinButton = root.Q<Button>("join-button");

            // Lobby actions

            _startButton.clicked += OnStartGameClicked;

            _joinCodeInput.RegisterValueChangedCallback(OnJoinCodeInputValueChanged);
            _joinCodeInput.maxLength = 6;
            _joinCodeInput.isDelayed = false;
            _joinSessionButton.clicked += () => OnJoinGameClicked(_joinCodeInput.value.ToUpper());
            _joinSessionButton.RegisterCallback<MouseOverEvent>(MouseHover);
        
            _hostButton.clicked += OnHostClicked;
            _hostButton.RegisterCallback<MouseOverEvent>(MouseHover);

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
            SoundFXManager.Instance.PlayUISound(buttonClickSound);

            _selectedGameMode = modeName;

            if(modeName != "Private Match") {
                _startButton.RemoveFromClassList("menu-chip-enabled");
                _startButton.SetEnabled(false);
                _startButton.UnregisterCallback<MouseOverEvent>(MouseHover);
            } else {
                if(!_startButton.ClassListContains("menu-chip-enabled"))
                    _startButton.AddToClassList("menu-chip-enabled");
                _startButton.SetEnabled(true);
                _startButton.RegisterCallback<MouseOverEvent>(MouseHover);
            }
        
            Debug.LogWarning("Gamemode: " + modeName);
            _joinCodeLabel.text = "Join Code: ------";
            _waitingLabel.text = "Join or host";

            if(!_hostButton.ClassListContains("menu-chip-enabled")) {
                _hostButton.AddToClassList("menu-chip-enabled");
            }
            _hostButton.SetEnabled(true);
            _hostButton.RegisterCallback<MouseOverEvent>(MouseHover);

            _startButton.SetEnabled(false);
            _startButton.RemoveFromClassList("menu-chip-enabled");
            _startButton.UnregisterCallback<MouseOverEvent>(MouseHover);
        
            _joinButton.AddToClassList("menu-chip-enabled");
            _joinButton.SetEnabled(true);
            _joinButton.RegisterCallback<MouseOverEvent>(MouseHover);

            _joinCodeInput.value = "";
            _playerList.Clear();
            ShowPanel(_lobbyPanel);
        }

        private void ShowPanel(VisualElement panel, bool playBack = false) {
            var clip = playBack ? backClickSound : buttonClickSound;
            SoundFXManager.Instance.PlayUISound(clip);
        
            _mainMenuPanel.AddToClassList("hidden");
            _optionsPanel.AddToClassList("hidden");
            _gamemodePanel.AddToClassList("hidden");
            _lobbyPanel.AddToClassList("hidden");

            panel.RemoveFromClassList("hidden");
        }

        private void MouseHover(MouseOverEvent evt) {
            SoundFXManager.Instance.PlayUISound(buttonHoverSound);
        }

        private async void OnHostClicked() {
            try {
                SoundFXManager.Instance.PlayUISound(buttonClickSound);
                _hostButton.RemoveFromClassList("menu-chip-enabled");
                _hostButton.SetEnabled(false);
                _hostButton.UnregisterCallback<MouseOverEvent>(MouseHover);
                _joinButton.RemoveFromClassList("menu-chip-enabled");
                _joinButton.SetEnabled(false);
                _joinButton.UnregisterCallback<MouseOverEvent>(MouseHover);
                _waitingLabel.text = "Waiting for connection...";

                var joinCode = await sessionManager.StartSessionAsHost();
                _joinCodeLabel.text = $"Join Code: {joinCode}";

                _waitingLabel.text = "Lobby ready";
                _startButton.SetEnabled(true);
                _startButton.AddToClassList("menu-chip-enabled");
                _startButton.RegisterCallback<MouseOverEvent>(MouseHover);
                // Player list will be filled by PlayersChanged event.
            } catch(Exception e) {
                Debug.LogException(e);
            }
        }

        private async void OnStartGameClicked() {
            try {
                SoundFXManager.Instance.PlayUISound(buttonClickSound);

                await sessionManager.BeginGameplayAsHostAsync();
            } catch (Exception e) {
                Debug.LogException(e);
            }
        }

        private async void OnJoinGameClicked(string code) {
            try {
                var regexAlphaNum = new System.Text.RegularExpressions.Regex("^[a-zA-Z0-9]*$");
                if(string.IsNullOrWhiteSpace(code) || code.Length != 6 || !regexAlphaNum.IsMatch(code)) {
                    SoundFXManager.Instance.PlayUISound(backClickSound);

                    _waitingLabel.text = "Invalid join code";
                    return;
                }
            
                _hostButton.RemoveFromClassList("menu-chip-enabled");
                _hostButton.SetEnabled(false);
                _hostButton.UnregisterCallback<MouseOverEvent>(MouseHover);
                _waitingLabel.text = "Joining lobby...";
                _joinButton.RemoveFromClassList("menu-chip-enabled");
                _joinButton.SetEnabled(false);
                _joinButton.UnregisterCallback<MouseOverEvent>(MouseHover);
            
                SoundFXManager.Instance.PlayUISound(buttonClickSound);

                var result = await sessionManager.JoinSessionByCodeAsync(code);
                _waitingLabel.text = result;
                if(result is "Lobby joined" or "Connected to Local Host (Editor)") {
                    _joinCodeLabel.text = $"Join Code: {code}";
                } else {
                    _joinButton.AddToClassList("menu-chip-enabled");
                    _joinButton.SetEnabled(true);
                    _joinButton.RegisterCallback<MouseOverEvent>(MouseHover);
                    _hostButton.AddToClassList("menu-chip-enabled");
                    _hostButton.SetEnabled(true);
                    _hostButton.RegisterCallback<MouseOverEvent>(MouseHover);
                }
                // Player list will be filled by PlayersChanged event.
            } catch (Exception e) {
                Debug.LogException(e);
            }
        }
    
        #endregion
    
        #region Lobby UI Updates
    
        private void OnRelayCodeAvailable(string code) {
            // Cosmetic: keep label in sync when host publishes/upgrades relay code
            // if(_joinCodeLabel != null)
            // _joinCodeLabel.text = $"Join Code: {code}";
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
            _masterVolumeSlider.RegisterValueChangedCallback(evt =>
            {
                _masterVolumeValue.text = $"{Mathf.RoundToInt(evt.newValue * 100)}%";
            });
            _musicVolumeSlider.RegisterValueChangedCallback(evt =>
            {
                _musicVolumeValue.text = $"{Mathf.RoundToInt(evt.newValue * 100)}%";
            });
            _sfxVolumeSlider.RegisterValueChangedCallback(evt =>
            {
                _sfxVolumeValue.text = $"{Mathf.RoundToInt(evt.newValue * 100)}%";
            });
        }

        private static float LinearToDb(float linear) => linear <= 0f ? -80f : 20f * Mathf.Log10(linear);
        private static float DbToLinear(float db) => db <= -80f ? 0f : Mathf.Pow(10f, db / 20);

        private void SetupControlsCallbacks() {
            _sensitivityXSlider.RegisterValueChangedCallback(evt =>
            {
                _sensitivityXValue.text = evt.newValue.ToString("F2");
            });

            _sensitivityYSlider.RegisterValueChangedCallback(evt =>
            {
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
        
            SoundFXManager.Instance.PlayUISound(buttonClickSound);
        
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
                case 0: Application.targetFrameRate = 30; break;
                case 1: Application.targetFrameRate = 60; break;
                case 2: Application.targetFrameRate = 120; break;
                case 3: Application.targetFrameRate = 144; break;
                case 4: Application.targetFrameRate = -1; break; // Unlimited
            }
        }
    
        #endregion

        // ===== Misc =====

        private void OnLoadoutClicked() {
            SoundFXManager.Instance.PlayUISound(buttonClickSound);

            Debug.LogWarning("Loadout menu - not yet implemented");
        }

        private void OnCreditsClicked() {
            SoundFXManager.Instance.PlayUISound(buttonClickSound);
        
            Debug.LogWarning("Credits - not yet implemented");
        }

        private void OnQuitClicked() {
            SoundFXManager.Instance.PlayUISound(buttonClickSound);

            Debug.Log("Quitting game...");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
        }
    }
}
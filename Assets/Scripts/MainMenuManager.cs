using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

public class MainMenuManager : MonoBehaviour {
    #region Serialized Fields
    [SerializeField] private UIDocument uiDocument;
    [SerializeField] private AudioMixer audioMixer;
    [SerializeField] private SessionManager sessionManager;
    [SerializeField] private PauseMenuManager pauseMenuManager;
    #endregion

    #region Private Fields
    private VisualElement _mainMenuPanel;
    private VisualElement _gamemodePanel;
    private VisualElement _lobbyPanel;
    private VisualElement _optionsPanel;

    private string _selectedGameMode;

    // Audio sliders
    private Slider _masterVolumeSlider;
    private Slider _musicVolumeSlider;
    private Slider _sfxVolumeSlider;
    private Label _masterVolumeValue;
    private Label _musicVolumeValue;
    private Label _sfxVolumeValue;

    // Sensitivity sliders
    private Slider _sensitivityXSlider;
    private Slider _sensitivityYSlider;
    private Label _sensitivityXValue;
    private Label _sensitivityYValue;
    private Toggle _invertYToggle;

    // Graphics controls
    private DropdownField _qualityDropdown;
    private Toggle _vsyncToggle;
    private DropdownField _fpsDropdown;
    #endregion

    private Label _joinCodeLabel;
    private Button _hostButton;
    private Button _startButton;
    private Button _joinSessionButton;
    private TextField _joinCodeInput;
    private VisualElement _playerList;

    #region Unity Lifecycle
    private void OnEnable() {
        var root = uiDocument.rootVisualElement;

        _joinCodeInput = root.Q<TextField>("join-code-input");
        _hostButton = root.Q<Button>("host-button");
        _joinCodeLabel = root.Q<Label>("join-code-label");
        _playerList = root.Q<VisualElement>("player-list");
        _startButton = root.Q<Button>("start-button");
        _joinSessionButton = root.Q<Button>("join-session-button");

        _mainMenuPanel = root.Q<VisualElement>("main-menu-panel");
        _gamemodePanel = root.Q<VisualElement>("gamemode-panel");
        _lobbyPanel = root.Q<VisualElement>("lobby-panel");
        _optionsPanel = root.Q<VisualElement>("options-panel");

        root.Q<Button>("play-button").clicked += () => ShowPanel(_gamemodePanel);
        root.Q<Button>("select-mode1").clicked += () => OnGameModeSelected("Deathmatch");
        root.Q<Button>("select-mode2").clicked += () => OnGameModeSelected("Team Deathmatch");
        root.Q<Button>("select-mode3").clicked += () => OnGameModeSelected("Capture the Flag");
        root.Q<Button>("back-to-main").clicked += () => ShowPanel(_mainMenuPanel);
        root.Q<Button>("back-to-gamemode").clicked += () => ShowPanel(_gamemodePanel);
        _startButton.clicked += OnStartGameClicked;
        root.Q<Button>("loadout-button").clicked += OnLoadoutClicked;
        root.Q<Button>("options-button").clicked += ShowOptions;
        root.Q<Button>("credits-button").clicked += OnCreditsClicked;
        root.Q<Button>("quit-button").clicked += OnQuitClicked;

        root.Q<Button>("apply-button").clicked += ApplySettings;
        root.Q<Button>("back-button").clicked += HideOptions;

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

        SetupAudioCallbacks();
        SetupControlsCallbacks();
        SetupGraphicsCallbacks();
        LoadSettings();

        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;

        _joinSessionButton.clicked += () => OnJoinGameClicked(_joinCodeInput.value);
        _hostButton.clicked += OnHostClicked;
        _startButton.SetEnabled(false);
    }
    #endregion

    #region Setup Methods
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
    #endregion

    #region Menu Navigation
    private void OnGameModeSelected(string modeName) {
        _selectedGameMode = modeName;
        ShowPanel(_lobbyPanel);
    }

    private void ShowPanel(VisualElement panel) {
        _mainMenuPanel.AddToClassList("hidden");
        _optionsPanel.AddToClassList("hidden");
        _gamemodePanel.AddToClassList("hidden");
        _lobbyPanel.AddToClassList("hidden");

        panel.RemoveFromClassList("hidden");
    }

    private async void OnHostClicked() {
        try {
            Debug.Log("Hosting game (session only)...");
            var joinCode = await sessionManager.StartSessionAsHost();
            _joinCodeLabel.text = $"Join Code: {joinCode}";

            _hostButton.SetEnabled(false);
            _startButton.SetEnabled(true);

            AddPlayer("You (Host)", true);
        } catch(Exception e) {
            Debug.LogException(e);
        }
    }

    private async void OnStartGameClicked() {
        try {
            Debug.Log("Starting game as host...");
            await sessionManager.BeginGameplayAsHostAsync();
        } catch(Exception e) {
            Debug.LogException(e);
        }
    }

    private async void OnJoinGameClicked(string code) {
        try {
            if(string.IsNullOrWhiteSpace(code)) {
                Debug.LogWarning("Invalid code");
                return;
            }

            Debug.Log($"Joining game with code: {code}");
            await sessionManager.JoinSessionByCodeAsync(code);
            // The host scene load + spawn is automatic; we don’t load scenes here.
        } catch(Exception e) {
            Debug.LogException(e);
        }
    }

    public void AddPlayer(string playerName, bool isHost) {
        foreach(var child in _playerList.Children())
            child.style.borderBottomWidth = 1f;

        var entry = new VisualElement();
        entry.AddToClassList("player-entry");
        if (isHost) entry.AddToClassList("host");

        var label = new Label(playerName);
        entry.Add(label);
        _playerList.Add(entry);

        entry.style.borderBottomWidth = 0f;
    }

    public void RemovePlayer(string playerName) {
        foreach(var child in _playerList.Children()) {
            var label = child.Q<Label>();
            if(label != null && label.text == playerName) {
                _playerList.Remove(child);
                break;
            }
        }

        var children = _playerList.Children().ToList();
        if(children.Count > 0) {
            foreach(var child in children)
                child.style.borderBottomWidth = 1f;

            children.Last().style.borderBottomWidth = 0f;
        }
    }

    private void OnLoadoutClicked() => Debug.Log("Loadout menu - not yet implemented");
    private void ShowOptions() {
        _mainMenuPanel.AddToClassList("hidden");
        _optionsPanel.RemoveFromClassList("hidden");
    }
    private void HideOptions() {
        _optionsPanel.AddToClassList("hidden");
        _mainMenuPanel.RemoveFromClassList("hidden");
    }
    private void OnCreditsClicked() => Debug.Log("Credits - not yet implemented");

    private void OnQuitClicked() {
        Debug.Log("Quitting game...");
        #if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
        #else
        Application.Quit();
        #endif
    }
    #endregion

    #region Settings Management
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
            case 0: Application.targetFrameRate = 30; break;
            case 1: Application.targetFrameRate = 60; break;
            case 2: Application.targetFrameRate = 120; break;
            case 3: Application.targetFrameRate = 144; break;
            case 4: Application.targetFrameRate = -1; break; // Unlimited
        }
    }
    #endregion
}
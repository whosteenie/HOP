using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using UnityEngine.UIElements;

public class MainMenuManager : MonoBehaviour
{
    #region Serialized Fields
    
    [SerializeField] private UIDocument uiDocument;
    [SerializeField] private AudioMixer audioMixer;
    [SerializeField] private string gameSceneName = "Game";
    [SerializeField] private SessionManager sessionManager;
    
    #endregion
    
    #region Private Fields
    
    private VisualElement _mainMenuPanel;
    private VisualElement _gamemodePanel;
    private VisualElement _lobbyPanel;
    private VisualElement _optionsPanel;
    private VisualElement _hudPanel;
    
    private new string _selectedGameMode;
    
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
    
    #region Unity Lifecycle
    
    private void OnEnable()
    {
        DontDestroyOnLoad(gameObject);
        
        var root = uiDocument.rootVisualElement;
        
        // Get panels
        _mainMenuPanel = root.Q<VisualElement>("main-menu-panel");
        _gamemodePanel = root.Q<VisualElement>("gamemode-panel");
        _lobbyPanel = root.Q<VisualElement>("lobby-panel");
        _optionsPanel = root.Q<VisualElement>("options-panel");
        _hudPanel = root.Q<VisualElement>("hud-panel");
        
        // Setup main menu buttons
        root.Q<Button>("play-button").clicked += () => ShowPanel(_gamemodePanel);
        root.Q<Button>("select-mode1").clicked += () => OnGameModeSelected("Deathmatch");
        root.Q<Button>("select-mode2").clicked += () => OnGameModeSelected("Team Deathmatch");
        root.Q<Button>("select-mode3").clicked += () => OnGameModeSelected("Capture the Flag");
        root.Q<Button>("back-to-main").clicked += () => ShowPanel(_mainMenuPanel);
        root.Q<Button>("back-to-gamemode").clicked += () => ShowPanel(_gamemodePanel);
        root.Q<Button>("start-game").clicked += OnStartGameClicked;
        root.Q<Button>("loadout-button").clicked += OnLoadoutClicked;
        root.Q<Button>("options-button").clicked += ShowOptions;
        root.Q<Button>("credits-button").clicked += OnCreditsClicked;
        root.Q<Button>("quit-button").clicked += OnQuitClicked;
        
        // Setup options buttons
        root.Q<Button>("apply-button").clicked += ApplySettings;
        root.Q<Button>("back-button").clicked += HideOptions;
        
        // Get audio controls
        _masterVolumeSlider = root.Q<Slider>("master-volume");
        _musicVolumeSlider = root.Q<Slider>("music-volume");
        _sfxVolumeSlider = root.Q<Slider>("sfx-volume");
        _masterVolumeValue = root.Q<Label>("master-volume-value");
        _musicVolumeValue = root.Q<Label>("music-volume-value");
        _sfxVolumeValue = root.Q<Label>("sfx-volume-value");
        
        // Get sensitivity controls
        _sensitivityXSlider = root.Q<Slider>("sensitivity-x");
        _sensitivityYSlider = root.Q<Slider>("sensitivity-y");
        _sensitivityXValue = root.Q<Label>("sensitivity-x-value");
        _sensitivityYValue = root.Q<Label>("sensitivity-y-value");
        _invertYToggle = root.Q<Toggle>("invert-y");
        
        // Get graphics controls
        _qualityDropdown = root.Q<DropdownField>("quality-level");
        _vsyncToggle = root.Q<Toggle>("vsync");
        _fpsDropdown = root.Q<DropdownField>("target-fps");
        
        SetupAudioCallbacks();
        SetupControlsCallbacks();
        SetupGraphicsCallbacks();
        
        LoadSettings();
        
        // Show cursor
        UnityEngine.Cursor.visible = true;
        UnityEngine.Cursor.lockState = CursorLockMode.None;
    }
    
    #endregion
    
    #region Setup Methods
    
    private void SetupAudioCallbacks()
    {
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

    private static float LinearToDb(float linear)
    {
        if(linear <= 0f) return -80f;
        return 20f * Mathf.Log10(linear);
    }
    
    private static float DbToLinear(float db)
    {
        return db <= -80f ? 0f : Mathf.Pow(10f, db / 20f);
    }
    
    private void SetupControlsCallbacks()
    {
        _sensitivityXSlider.RegisterValueChangedCallback(evt => {
            _sensitivityXValue.text = evt.newValue.ToString("F2");
        });
        
        _sensitivityYSlider.RegisterValueChangedCallback(evt => {
            _sensitivityYValue.text = evt.newValue.ToString("F2");
        });
    }
    
    private void SetupGraphicsCallbacks()
    {
        // Setup quality dropdown
        _qualityDropdown.choices = new List<string>(QualitySettings.names);
        _qualityDropdown.index = QualitySettings.GetQualityLevel();
        
        // Setup FPS dropdown
        _fpsDropdown.choices = new List<string> { "30", "60", "120", "144", "Unlimited" };
    }
    
    #endregion
    
    #region Menu Navigation
    
    private void OnGameModeSelected(string modeName)
    {
        _selectedGameMode = modeName;
        ShowPanel(_lobbyPanel);
    }
    
    private void ShowPanel(VisualElement panel)
    {
        _mainMenuPanel.AddToClassList("hidden");
        _optionsPanel.AddToClassList("hidden");
        _gamemodePanel.AddToClassList("hidden");
        _lobbyPanel.AddToClassList("hidden");

        panel.RemoveFromClassList("hidden");
    }
    
    private void OnStartGameClicked()
    {
        Debug.Log("Starting game...");
        SessionManager.Instance.StartSessionAsHost();
        ShowPanel(_hudPanel);
    }
    
    private void OnLoadoutClicked()
    {
        Debug.Log("Loadout menu - not yet implemented");
        // TODO: Implement loadout menu
    }
    
    private void ShowOptions()
    {
        _mainMenuPanel.AddToClassList("hidden");
        _optionsPanel.RemoveFromClassList("hidden");
    }
    
    private void HideOptions()
    {
        _optionsPanel.AddToClassList("hidden");
        _mainMenuPanel.RemoveFromClassList("hidden");
    }
    
    private void OnCreditsClicked()
    {
        Debug.Log("Credits - not yet implemented");
        // TODO: Implement credits screen
    }
    
    private void OnQuitClicked()
    {
        Debug.Log("Quitting game...");
        #if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
        #else
            Application.Quit();
        #endif
    }
    
    #endregion
    
    #region Settings Management
    
    private void LoadSettings()
    {
        // Load audio settings
        var masterDb = PlayerPrefs.GetFloat("MasterVolume", 0f);
        var musicDb = PlayerPrefs.GetFloat("MusicVolume", 0f);
        var sfxDb = PlayerPrefs.GetFloat("SFXVolume", 0f);
        _masterVolumeSlider.value = DbToLinear(masterDb);
        _musicVolumeSlider.value = DbToLinear(musicDb);
        _sfxVolumeSlider.value = DbToLinear(sfxDb);
        
        // Load control settings
        _sensitivityXSlider.value = PlayerPrefs.GetFloat("SensitivityX", 0.1f);
        _sensitivityYSlider.value = PlayerPrefs.GetFloat("SensitivityY", 0.1f);
        _invertYToggle.value = PlayerPrefs.GetInt("InvertY", 0) == 1;
        
        // Load graphics settings
        _qualityDropdown.index = PlayerPrefs.GetInt("QualityLevel", QualitySettings.GetQualityLevel());
        _vsyncToggle.value = PlayerPrefs.GetInt("VSync", 0) == 1;
        _fpsDropdown.index = PlayerPrefs.GetInt("TargetFPS", 1);
        
        // Apply loaded settings
        ApplySettingsInternal();
    }
    
    private void ApplySettings()
    {
        // Save audio settings
        var masterDb = LinearToDb(_masterVolumeSlider.value);
        var musicDb = LinearToDb(_musicVolumeSlider.value);
        var sfxDb = LinearToDb(_sfxVolumeSlider.value);
        PlayerPrefs.SetFloat("MasterVolume", masterDb);
        PlayerPrefs.SetFloat("MusicVolume", musicDb);
        PlayerPrefs.SetFloat("SFXVolume", sfxDb);
        
        // Save control settings
        PlayerPrefs.SetFloat("SensitivityX", _sensitivityXSlider.value);
        PlayerPrefs.SetFloat("SensitivityY", _sensitivityYSlider.value);
        PlayerPrefs.SetInt("InvertY", _invertYToggle.value ? 1 : 0);
        
        // Save graphics settings
        PlayerPrefs.SetInt("QualityLevel", _qualityDropdown.index);
        PlayerPrefs.SetInt("VSync", _vsyncToggle.value ? 1 : 0);
        PlayerPrefs.SetInt("TargetFPS", _fpsDropdown.index);
        
        PlayerPrefs.Save();
        
        ApplySettingsInternal();
        
        Debug.Log("Settings applied and saved!");
    }
    
    private void ApplySettingsInternal()
    {
        // Apply audio
        if(audioMixer != null)
        {
            audioMixer.SetFloat("masterVolume", LinearToDb(_masterVolumeSlider.value));
            audioMixer.SetFloat("musicVolume", LinearToDb(_musicVolumeSlider.value));
            audioMixer.SetFloat("soundFXVolume", LinearToDb(_sfxVolumeSlider.value));
        }
        
        // Apply graphics
        QualitySettings.SetQualityLevel(_qualityDropdown.index);
        QualitySettings.vSyncCount = _vsyncToggle.value ? 1 : 0;
        
        // Apply target FPS
        switch (_fpsDropdown.index)
        {
            case 0: Application.targetFrameRate = 30; break;
            case 1: Application.targetFrameRate = 60; break;
            case 2: Application.targetFrameRate = 120; break;
            case 3: Application.targetFrameRate = 144; break;
            case 4: Application.targetFrameRate = -1; break; // Unlimited
        }
    }
    
    #endregion
}
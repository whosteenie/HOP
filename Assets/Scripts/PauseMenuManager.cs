using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UIElements;

public class PauseMenuManager : MonoBehaviour {
    #region Serialized Fields
    
    [SerializeField] private UIDocument uiDocument;
    [SerializeField] private FpController fpController;
    [SerializeField] private WeaponManager weaponManager;
    [SerializeField] private AudioMixer audioMixer;
    
    #endregion
    
    #region Private Fields
    
    private VisualElement _pauseMenuPanel;
    private VisualElement _optionsPanel;
    
    // HUD elements
    private ProgressBar _healthBar;
    private Label _healthValue;
    private ProgressBar _velocityBar;
    private Label _velocityValue;

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
    
    #region Properties
    
    public bool IsPaused { get; private set; }
    public float MasterVolume => _masterVolumeSlider.value;
    public float MusicVolume => _musicVolumeSlider.value;
    public float SfxVolume => _sfxVolumeSlider.value;

    #endregion
    
    private const float MinVelocityThreshold = FpController.SprintSpeed;
    private const float MaxVelocityThreshold = 18f;
    
    #region Unity Lifecycle
    
    private void OnEnable() {
        var root = uiDocument.rootVisualElement;
        
        // Get panels
        _pauseMenuPanel = root.Q<VisualElement>("pause-menu-panel");
        _optionsPanel = root.Q<VisualElement>("options-panel");
        
        // Get HUD elements
        _healthBar = root.Q<ProgressBar>("health-bar");
        _healthValue = root.Q<Label>("health-value");
        _velocityBar = root.Q<ProgressBar>("velocity-bar");
        _velocityValue = root.Q<Label>("velocity-value");
        
        // Setup main menu buttons
        root.Q<Button>("resume-button").clicked += ResumeGame;
        root.Q<Button>("options-button").clicked += ShowOptions;
        root.Q<Button>("quit-button").clicked += QuitToMenu;
        
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
        
        // Hide menu initially
        _pauseMenuPanel.AddToClassList("hidden");
        _optionsPanel.AddToClassList("hidden");
    }

    private void Update() {
        UpdateVelocityBar();
    }

    private void UpdateVelocityBar() {
        var weapon = weaponManager.CurrentWeapon;
        
        var progress = Mathf.InverseLerp(1f, weapon.maxDamageMultiplier, weapon.CurrentDamageMultiplier);
        _velocityBar.value = progress * 100f;
        _velocityValue.text = $"{weapon.CurrentDamageMultiplier:F2}x";
    }

    public void UpdateHealthBar() {
        var healthPercent = (fpController.CurrentHealth / 100f) * 100f;
        _healthBar.value = healthPercent;
        _healthValue.text = fpController.CurrentHealth.ToString();
    }
    
    public void TogglePause() {
        if (IsPaused) {
            if (!_optionsPanel.ClassListContains("hidden")) {
                HideOptions();
            } else {
                ResumeGame();
            }
        } else {
            PauseGame();
        }
    }
    
    #endregion
    
    #region Setup Methods
    
    private void SetupAudioCallbacks() {
        _masterVolumeSlider.RegisterValueChangedCallback(evt => {
            var linear = evt.newValue;
            _masterVolumeValue.text = $"{Mathf.RoundToInt(linear * 100)}%";
        });

        _musicVolumeSlider.RegisterValueChangedCallback(evt => {
            var linear = evt.newValue;
            _musicVolumeValue.text = $"{Mathf.RoundToInt(linear * 100)}%";
        });
        
        _sfxVolumeSlider.RegisterValueChangedCallback(evt => {
            var linear = evt.newValue;
            _sfxVolumeValue.text = $"{Mathf.RoundToInt(linear * 100)}%";
        });
    }

    private static float LinearToDb(float linear) {
        if(linear <= 0f) return -80f;
        return 20f * Mathf.Log10(linear);
    }
    
    private static float DbToLinear(float db) {
        return db <= -80f ? 0f : Mathf.Pow(10f, db / 20f);
    }
    
    private void SetupControlsCallbacks() {
        _sensitivityXSlider.RegisterValueChangedCallback(evt => {
            _sensitivityXValue.text = evt.newValue.ToString("F2");
        });
        
        _sensitivityYSlider.RegisterValueChangedCallback(evt => {
            _sensitivityYValue.text = evt.newValue.ToString("F2");
        });
    }
    
    private void SetupGraphicsCallbacks() {
        // Setup quality dropdown
        _qualityDropdown.choices = new List<string>(QualitySettings.names);
        _qualityDropdown.index = QualitySettings.GetQualityLevel();
        
        // Setup FPS dropdown
        _fpsDropdown.choices = new List<string> { "30", "60", "120", "144", "Unlimited" };
    }
    
    #endregion
    
    #region Menu Navigation
    
    private void PauseGame() {
        IsPaused = true;
        _pauseMenuPanel.RemoveFromClassList("hidden");
        UnityEngine.Cursor.lockState = CursorLockMode.None;
        UnityEngine.Cursor.visible = true;
        
        fpController.moveInput = Vector2.zero;
    }
    
    private void ResumeGame() {
        IsPaused = false;
        _pauseMenuPanel.AddToClassList("hidden");
        _optionsPanel.AddToClassList("hidden");
        UnityEngine.Cursor.lockState = CursorLockMode.Locked;
        UnityEngine.Cursor.visible = false;
    }
    
    private void ShowOptions() {
        _pauseMenuPanel.AddToClassList("hidden");
        _optionsPanel.RemoveFromClassList("hidden");
    }
    
    private void HideOptions() {
        _optionsPanel.AddToClassList("hidden");
        _pauseMenuPanel.RemoveFromClassList("hidden");
    }
    
    private void QuitToMenu() {
        UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
        Debug.Log("Quit to Main Menu");
    }
    
    #endregion
    
    #region Settings Management
    
    private void LoadSettings() {
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
    
    private void ApplySettings() {
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
    
    private void ApplySettingsInternal() {
        // Apply audio
        // AudioListener.volume = _masterVolumeSlider.value;
        audioMixer.SetFloat("masterVolume", LinearToDb(_masterVolumeSlider.value));
        audioMixer.SetFloat("musicVolume", LinearToDb(_musicVolumeSlider.value));
        audioMixer.SetFloat("soundFXVolume", LinearToDb(_sfxVolumeSlider.value));
        
        // Apply sensitivity to player controller
        if (fpController != null) {
            var invertMultiplier = _invertYToggle.value ? -1f : 1f;
            fpController.lookSensitivity = new Vector2(
                _sensitivityXSlider.value,
                _sensitivityYSlider.value * invertMultiplier
            );
        }
        
        // Apply graphics
        QualitySettings.SetQualityLevel(_qualityDropdown.index);
        QualitySettings.vSyncCount = _vsyncToggle.value ? 1 : 0;
        
        // Apply target FPS
        switch (_fpsDropdown.index) {
            case 0: Application.targetFrameRate = 30; break;
            case 1: Application.targetFrameRate = 60; break;
            case 2: Application.targetFrameRate = 120; break;
            case 3: Application.targetFrameRate = 144; break;
            case 4: Application.targetFrameRate = -1; break; // Unlimited
        }
    }
    
    #endregion
}
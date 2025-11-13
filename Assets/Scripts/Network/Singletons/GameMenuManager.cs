using System;
using System.Collections;
using System.Collections.Generic;
using Game.Player;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Network.Singletons {
    public class GameMenuManager : MonoBehaviour {
        #region Serialized Fields

        [SerializeField] private UIDocument uiDocument;
        [SerializeField] private AudioMixer audioMixer;

        [SerializeField] private AudioClip buttonClickSound;
        [SerializeField] private AudioClip buttonHoverSound;
        [SerializeField] private AudioClip backClickSound;

        [Header("Kill Feed")] 
        [SerializeField] private Sprite killIconSprite;
        [SerializeField] private float killFeedDisplayTime = 5f;
        [SerializeField] private int maxKillFeedEntries = 5; // NEW: Max entries in kill feed

        #endregion

        #region UI Elements - Kill Feed

        private VisualElement _killFeedContainer;
        private List<VisualElement> _activeKillEntries = new List<VisualElement>();
        private Dictionary<VisualElement, Coroutine> _fadeCoroutines; // NEW: Track coroutines

        #endregion

        #region UI Elements - Scoreboard

        private VisualElement _scoreboardPanel;
        private VisualElement _playerRows;

        #endregion

        #region Private Fields

        private VisualElement _pauseMenuPanel;
        private VisualElement _optionsPanel;
        private PlayerController _localController;

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

        private Button _resumeButton;
        private Button _optionsButton;
        private Button _quitButton;
        private Button _applyButton;
        private Button _backButton;

        private VisualElement _root;

        #endregion

        #region Properties

        public bool IsPaused { get; private set; }
        public bool IsScoreboardVisible { get; private set; }

        #endregion

        public static GameMenuManager Instance { get; private set; }

        #region Unity Lifecycle

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnEnable() {
            _root = uiDocument.rootVisualElement;
            
            _fadeCoroutines = new Dictionary<VisualElement, Coroutine>();

            FindUIElements();
            RegisterUIEvents();

            SetupAudioCallbacks();
            SetupControlsCallbacks();
            SetupGraphicsCallbacks();

            LoadSettings();
        }

        private void Update() {
            if(_localController == null && SceneManager.GetActiveScene().name == "Game") {
                FindLocalController();
            }
        }

        private void FindLocalController() {
            var allControllers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
            foreach(var controller in allControllers) {
                if(controller.IsOwner) {
                    _localController = controller.GetComponent<PlayerController>();
                    break;
                }
            }
        }

        private void FindUIElements() {
            // Get panels
            _pauseMenuPanel = _root.Q<VisualElement>("pause-menu-panel");
            _optionsPanel = _root.Q<VisualElement>("options-panel");

            _resumeButton = _root.Q<Button>("resume-button");
            _optionsButton = _root.Q<Button>("options-button");
            _quitButton = _root.Q<Button>("quit-button");

            _applyButton = _root.Q<Button>("apply-button");
            _backButton = _root.Q<Button>("back-button");

            // Get audio controls
            _masterVolumeSlider = _root.Q<Slider>("master-volume");
            _musicVolumeSlider = _root.Q<Slider>("music-volume");
            _sfxVolumeSlider = _root.Q<Slider>("sfx-volume");
            _masterVolumeValue = _root.Q<Label>("master-volume-value");
            _musicVolumeValue = _root.Q<Label>("music-volume-value");
            _sfxVolumeValue = _root.Q<Label>("sfx-volume-value");

            // Get sensitivity controls
            _sensitivityXSlider = _root.Q<Slider>("sensitivity-x");
            _sensitivityYSlider = _root.Q<Slider>("sensitivity-y");
            _sensitivityXValue = _root.Q<Label>("sensitivity-x-value");
            _sensitivityYValue = _root.Q<Label>("sensitivity-y-value");
            _invertYToggle = _root.Q<Toggle>("invert-y");

            // Get graphics controls
            _qualityDropdown = _root.Q<DropdownField>("quality-level");
            _vsyncToggle = _root.Q<Toggle>("vsync");
            _fpsDropdown = _root.Q<DropdownField>("target-fps");

            // Scoreboard
            _scoreboardPanel = _root.Q<VisualElement>("scoreboard-panel");
            _playerRows = _root.Q<VisualElement>("player-rows");

            // Kill Feed
            _killFeedContainer = _root.Q<VisualElement>("kill-feed-container");
        }

        private void RegisterUIEvents() {
            // Setup main menu buttons
            _resumeButton.clicked += () => {
                OnButtonClicked();
                ResumeGame();
            };
            _resumeButton.RegisterCallback<MouseOverEvent>(MouseHover);

            _optionsButton.clicked += () => {
                OnButtonClicked();
                ShowOptions();
            };
            _optionsButton.RegisterCallback<MouseOverEvent>(MouseHover);

            _quitButton.clicked += () => {
                OnButtonClicked(true);
                QuitToMenu();
            };
            _quitButton.RegisterCallback<MouseOverEvent>(MouseHover);

            // Setup options buttons
            _applyButton.clicked += () => {
                OnButtonClicked();
                ApplySettings();
            };
            _applyButton.RegisterCallback<MouseOverEvent>(MouseHover);

            _backButton.clicked += () => {
                OnButtonClicked(true);
                HideOptions();
            };
            _backButton.RegisterCallback<MouseOverEvent>(MouseHover);
        }

        private void OnButtonClicked(bool isBack = false) {
            SoundFXManager.Instance.PlayUISound(!isBack ? buttonClickSound : backClickSound);
        }

        private void MouseHover(MouseOverEvent evt) {
            SoundFXManager.Instance.PlayUISound(buttonHoverSound);
        }

        public void TogglePause() {
            if(SceneManager.GetActiveScene().name != "Game") return;

            if(IsPaused) {
                if(!_optionsPanel.ClassListContains("hidden")) {
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

            if(_localController) {
                _localController.moveInput = Vector2.zero;
            }
        }

        private void ResumeGame() {
            IsPaused = false;
            _pauseMenuPanel.AddToClassList("hidden");
            _optionsPanel.AddToClassList("hidden");
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;
            UnityEngine.Cursor.visible = false;
        }

        private void ShowOptions() {
            if(SceneManager.GetActiveScene().name != "Game") return;
            LoadSettings();
            _pauseMenuPanel.AddToClassList("hidden");
            _optionsPanel.RemoveFromClassList("hidden");
        }

        private void HideOptions() {
            if(SceneManager.GetActiveScene().name != "Game") return;
            _optionsPanel.AddToClassList("hidden");
            _pauseMenuPanel.RemoveFromClassList("hidden");
        }

        private async void QuitToMenu() {
            try {
                await SessionManager.Instance.LeaveToMainMenuAsync();

                var root = uiDocument.rootVisualElement;
                var rootContainer = root.Q<VisualElement>("root-container");
                rootContainer.style.display = DisplayStyle.None;
                _pauseMenuPanel.AddToClassList("hidden");
                _optionsPanel.AddToClassList("hidden");
                IsPaused = false;
                UnityEngine.Cursor.lockState = CursorLockMode.None;
                UnityEngine.Cursor.visible = true;
            } catch(Exception e) {
                Debug.LogException(e);
            }
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
            audioMixer.SetFloat("masterVolume", LinearToDb(_masterVolumeSlider.value));
            audioMixer.SetFloat("musicVolume", LinearToDb(_musicVolumeSlider.value));
            audioMixer.SetFloat("soundFXVolume", LinearToDb(_sfxVolumeSlider.value));

            var invertMultiplier = _invertYToggle.value ? -1f : 1f;

            if(_localController) {
                _localController.lookSensitivity = new Vector2(_sensitivityXSlider.value,
                    _sensitivityYSlider.value * invertMultiplier);
            }

            // Apply graphics
            QualitySettings.SetQualityLevel(_qualityDropdown.index);
            QualitySettings.vSyncCount = _vsyncToggle.value ? 1 : 0;

            // Apply target FPS
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

        #region Scoreboard Management

        public void ShowScoreboard() {
            if(SceneManager.GetActiveScene().name != "Game") return;

            IsScoreboardVisible = true;
            _scoreboardPanel.RemoveFromClassList("hidden");
            UpdateScoreboard();
        }

        public void HideScoreboard() {
            if(SceneManager.GetActiveScene().name != "Game") return;

            IsScoreboardVisible = false;
            _scoreboardPanel.AddToClassList("hidden");
        }

        private void UpdateScoreboard() {
            _playerRows.Clear();

            // Get all player controllers
            var allControllers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);

            // Sort by kills (descending)
            var sortedPlayers = new List<PlayerController>(allControllers);
            sortedPlayers.Sort((a, b) => b.kills.Value.CompareTo(a.kills.Value));

            foreach(var player in sortedPlayers) {
                CreatePlayerRow(player);
            }
        }

        private void CreatePlayerRow(PlayerController player) {
            var row = new VisualElement();
            row.AddToClassList("player-row");

            // Highlight local player
            if(player.IsOwner) {
                row.AddToClassList("player-row-local");
            }

            // Ping
            var ping = new Label(GetPingText(player));
            ping.AddToClassList("player-ping");
            ping.AddToClassList(GetPingColorClass(player));
            row.Add(ping);

            // Avatar (placeholder)
            var avatar = new VisualElement();
            avatar.AddToClassList("player-avatar");
            // TODO: Add player color/image here
            row.Add(avatar);

            // Name
            var playerName = new Label(player.playerName.Value.ToString());
            playerName.AddToClassList("player-name");
            row.Add(playerName);

            // Kills
            var kills = new Label(player.kills.Value.ToString());
            kills.AddToClassList("player-stat");
            row.Add(kills);

            // Deaths
            var deaths = new Label(player.deaths.Value.ToString());
            deaths.AddToClassList("player-stat");
            row.Add(deaths);

            // Assists (placeholder)
            var assists = new Label("0");
            assists.AddToClassList("player-stat");
            row.Add(assists);

            // KDA
            var kda = CalculateKda(player.kills.Value, player.deaths.Value, 0);
            var kdaLabel = new Label(kda.ToString("F2"));
            kdaLabel.AddToClassList("player-stat");
            if(kda >= 2.0f) {
                kdaLabel.AddToClassList("player-stat-highlight");
            }

            row.Add(kdaLabel);

            // Damage (placeholder)
            var damage = Mathf.RoundToInt(player.damageDealt.Value);
            var damageLabel = new Label($"{damage:N0}");
            damageLabel.AddToClassList("player-stat");
            row.Add(damageLabel);

            // Headshot % (placeholder)
            var headshotPct = new Label("0%");
            headshotPct.AddToClassList("player-stat");
            row.Add(headshotPct);

            // Average Velocity (after headshot %)
            var avgVelocity = player.averageVelocity.Value;
            var avgVelocityLabel = new Label($"{avgVelocity:F1} u/s");
            avgVelocityLabel.AddToClassList("player-stat");
            row.Add(avgVelocityLabel);

            _playerRows.Add(row);
        }

        private string GetPingText(PlayerController player) {
            var ping = player.pingMs.Value;
            return $"{ping}ms";
        }

        private string GetPingColorClass(PlayerController player) {
            var ping = player.pingMs.Value;

            return ping switch {
                > 100 => "player-ping-critical",
                > 50 => "player-ping-high",
                _ => ""
            };
        }

        private float CalculateKda(int kills, int deaths, int assists) {
            if(deaths == 0) return kills + assists;
            return (kills + assists) / (float)deaths;
        }

        #endregion

        #region Kill Feed Management

        /// <summary>
        /// Call this when a kill happens. Pass killer and victim PlayerControllers.
        /// </summary>
        public void AddKillToFeed(string killerName, string victimName, bool isLocalKiller) {
            if(_killFeedContainer == null) return;
            Debug.LogWarning("AddKillToFeed called: " + killerName + " killed " + victimName);

            // NEW: Check if we're at capacity
            if(_activeKillEntries.Count >= maxKillFeedEntries) {
                // Force remove the oldest entry (last in the list)
                var oldestEntry = _activeKillEntries[^1];
                RemoveKillEntry(oldestEntry, immediate: true);
            }

            var killEntry = CreateKillEntry(killerName, victimName, isLocalKiller);

            // Add to top of feed
            _killFeedContainer.Add(killEntry);
            _activeKillEntries.Add(killEntry); // Insert at beginning so oldest is at end

            // Start fade-out timer
            var fadeCoroutine = StartCoroutine(FadeOutKillEntry(killEntry));
            _fadeCoroutines[killEntry] = fadeCoroutine;
        }

        private VisualElement CreateKillEntry(string killerName, string victimName, bool isLocalKiller) {
            var entry = new VisualElement();
            entry.AddToClassList("kill-entry");

            if(isLocalKiller) {
                entry.AddToClassList("kill-entry-local");
            }

            // Killer name
            var killer = new Label(killerName);
            killer.AddToClassList("killer-name");
            if(isLocalKiller) {
                killer.AddToClassList("killer-name-local");
            }

            entry.Add(killer);

            // Kill icon (skull)
            var icon = new VisualElement();
            icon.AddToClassList("kill-icon");
            if(killIconSprite != null) {
                icon.style.backgroundImage = new StyleBackground(killIconSprite);
            }

            entry.Add(icon);

            // Victim name
            var victim = new Label(victimName);
            victim.AddToClassList("victim-name");
            entry.Add(victim);

            return entry;
        }

        private IEnumerator FadeOutKillEntry(VisualElement entry) {
            // Wait for display time
            yield return new WaitForSeconds(killFeedDisplayTime);

            // Remove the entry
            RemoveKillEntry(entry, immediate: false);
        }

        /// <summary>
        /// Removes a kill entry from the feed.
        /// </summary>
        /// <param name="entry">The entry to remove</param>
        /// <param name="immediate">If true, removes instantly. If false, fades out first.</param>
        private void RemoveKillEntry(VisualElement entry, bool immediate) {
            if(entry == null || !_activeKillEntries.Contains(entry)) return;

            // Cancel existing fade coroutine if any
            if(_fadeCoroutines.TryGetValue(entry, out var coroutine)) {
                if(coroutine != null) {
                    StopCoroutine(coroutine);
                }

                _fadeCoroutines.Remove(entry);
            }

            if(immediate) {
                // Immediate removal (when at capacity)
                entry.AddToClassList("kill-entry-fade");

                // Remove from lists immediately
                _activeKillEntries.Remove(entry);

                // Wait one frame for fade class to apply, then remove from DOM
                StartCoroutine(RemoveAfterFrame(entry));
            } else {
                // Normal fade out
                StartCoroutine(FadeAndRemove(entry));
            }
        }

        private IEnumerator FadeAndRemove(VisualElement entry) {
            // Fade out
            entry.AddToClassList("kill-entry-fade");

            // Wait for fade animation
            yield return new WaitForSeconds(0.3f);

            // Remove from feed
            if(_killFeedContainer.Contains(entry)) {
                _killFeedContainer.Remove(entry);
                _activeKillEntries.Remove(entry);
            }
        }

        private IEnumerator RemoveAfterFrame(VisualElement entry) {
            // Wait briefly for fade animation to start
            yield return new WaitForSeconds(0.15f);

            // Remove from DOM
            if(_killFeedContainer.Contains(entry)) {
                _killFeedContainer.Remove(entry);
            }
        }

        #endregion
    }
}
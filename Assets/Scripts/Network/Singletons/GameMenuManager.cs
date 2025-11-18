using System;
using System.Collections;
using System.Collections.Generic;
using Game.Player;
using Unity.Netcode;
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

        [Header("Kill Feed")] [SerializeField] private Sprite killIconSprite;
        [SerializeField] private Sprite youreItIconSprite; // Icon for tag transfers in Tag mode
        [SerializeField] private float killFeedDisplayTime = 5f;
        [SerializeField] private int maxKillFeedEntries = 5; // NEW: Max entries in kill feed

        [Header("Player Icons")] [SerializeField] private Sprite[] playerIconSprites; // Order: white, red, orange, yellow, green, blue, purple

        #endregion

        #region UI Elements - Kill Feed

        private VisualElement _killFeedContainer;
        private List<VisualElement> _activeKillEntries = new List<VisualElement>();
        private Dictionary<VisualElement, Coroutine> _fadeCoroutines; // NEW: Track coroutines

        #endregion

        #region UI Elements - Scoreboard

        private VisualElement _scoreboardPanel;
        private VisualElement _playerRows;

        // FFA Scoreboard
        private VisualElement _scoreboardContainer;

        // TDM Scoreboard
        private VisualElement _tdmScoreboardContainer;
        private VisualElement _enemyTeamSection;
        private VisualElement _enemyTeamRows;
        private VisualElement _yourTeamSection;
        private VisualElement _yourTeamRows;
        private Label _enemyScoreValue;
        private Label _yourScoreValue;

        // NEW: match timer
        private Label _matchTimerLabel;

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

        // Sensitivity slider
        private Slider _sensitivitySlider;
        private Label _sensitivityValue;
        private Button _invertYButton;

        // Graphics controls
        private DropdownField _qualityDropdown;
        private Button _vsyncButton;
        private DropdownField _fpsDropdown;

        // Options tabs
        private Button _tabVideo;
        private Button _tabAudio;
        private Button _tabGame;
        private Button _tabControls;
        private VisualElement _videoContent;
        private VisualElement _audioContent;
        private VisualElement _gameContent;
        private VisualElement _controlsContent;

        // Keybind buttons - stored as Dictionary for easier access
        private Dictionary<string, Button[]> _keybindButtons = new Dictionary<string, Button[]>();

        private Button _resumeButton;
        private Button _optionsButton;
        private Button _quitButton;
        private Button _applyButton;
        private Button _backButton;

        // Pause menu join code
        private Label _pauseJoinCodeLabel;
        private Button _pauseCopyCodeButton;

        // Unsaved changes dialog
        private VisualElement _unsavedChangesModal;
        private Button _unsavedChangesYes;
        private Button _unsavedChangesNo;
        private Button _unsavedChangesCancel;

        // Track original settings values to detect changes
        private float _originalMasterVolume;
        private float _originalMusicVolume;
        private float _originalSfxVolume;
        private float _originalSensitivity;
        private bool _originalInvertY;
        private int _originalQualityLevel;
        private bool _originalVsync;
        private int _originalTargetFPS;

        private VisualElement _root;

        // HUD / Match UI
        private VisualElement _hudPanel;
        private VisualElement _matchTimerContainer;

        // Post-match state

        // Podium UI
        private VisualElement _podiumContainer;
        private VisualElement _podiumFirstSlot;
        private VisualElement _podiumSecondSlot;
        private VisualElement _podiumThirdSlot;

        private Label _podiumFirstName;
        private Label _podiumSecondName;
        private Label _podiumThirdName;

        private Label _podiumFirstKills;
        private Label _podiumSecondKills;
        private Label _podiumThirdKills;

        #endregion

        #region Properties

        public bool IsPaused { get; private set; }
        public bool IsScoreboardVisible { get; private set; }

        public bool IsPostMatch { get; set; }

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
            SetupOptionsTabs();
            SetupKeybinds();

            LoadSettings();

            // Subscribe to join code updates
            if(SessionManager.Instance != null) {
                SessionManager.Instance.RelayCodeAvailable += OnRelayCodeAvailable;
            }
        }

        private void OnDisable() {
            // Unsubscribe from join code updates
            if(SessionManager.Instance != null) {
                SessionManager.Instance.RelayCodeAvailable -= OnRelayCodeAvailable;
            }
        }

        private void OnRelayCodeAvailable(string joinCode) {
            // Update join code display if pause menu is visible
            if(IsPaused && _pauseJoinCodeLabel != null) {
                _pauseJoinCodeLabel.text = $"Join Code: {joinCode}";
                if(_pauseCopyCodeButton != null) {
                    _pauseCopyCodeButton.SetEnabled(true);
                }
            }
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

            // Pause menu join code
            _pauseJoinCodeLabel = _root.Q<Label>("pause-join-code-label");
            _pauseCopyCodeButton = _root.Q<Button>("pause-copy-code-button");

            _applyButton = _root.Q<Button>("apply-button");
            _backButton = _root.Q<Button>("back-button");

            // Unsaved changes dialog
            _unsavedChangesModal = _root.Q<VisualElement>("unsaved-changes-modal");
            _unsavedChangesYes = _root.Q<Button>("unsaved-changes-yes");
            _unsavedChangesNo = _root.Q<Button>("unsaved-changes-no");
            _unsavedChangesCancel = _root.Q<Button>("unsaved-changes-cancel");

            // Get audio controls
            _masterVolumeSlider = _root.Q<Slider>("master-volume");
            _musicVolumeSlider = _root.Q<Slider>("music-volume");
            _sfxVolumeSlider = _root.Q<Slider>("sfx-volume");
            _masterVolumeValue = _root.Q<Label>("master-volume-value");
            _musicVolumeValue = _root.Q<Label>("music-volume-value");
            _sfxVolumeValue = _root.Q<Label>("sfx-volume-value");

            // Get sensitivity controls
            _sensitivitySlider = _root.Q<Slider>("sensitivity");
            _sensitivityValue = _root.Q<Label>("sensitivity-value");
            _invertYButton = _root.Q<Button>("invert-y");

            // Get graphics controls
            _qualityDropdown = _root.Q<DropdownField>("quality-level");
            _vsyncButton = _root.Q<Button>("vsync");

            // Setup button checkbox click handlers
            _invertYButton?.RegisterCallback<ClickEvent>(_ => ToggleCheckbox(_invertYButton));
            _vsyncButton?.RegisterCallback<ClickEvent>(_ => ToggleCheckbox(_vsyncButton));
            _fpsDropdown = _root.Q<DropdownField>("target-fps");

            // Options tabs
            _tabVideo = _root.Q<Button>("tab-video");
            _tabAudio = _root.Q<Button>("tab-audio");
            _tabGame = _root.Q<Button>("tab-game");
            _tabControls = _root.Q<Button>("tab-controls");
            _videoContent = _root.Q<VisualElement>("video-content");
            _audioContent = _root.Q<VisualElement>("audio-content");
            _gameContent = _root.Q<VisualElement>("game-content");
            _controlsContent = _root.Q<VisualElement>("controls-content");

            // Scoreboard
            _scoreboardPanel = _root.Q<VisualElement>("scoreboard-panel");
            _playerRows = _root.Q<VisualElement>("player-rows");
            _scoreboardContainer = _root.Q<VisualElement>("scoreboard-container");

            // TDM Scoreboard
            _tdmScoreboardContainer = _root.Q<VisualElement>("tdm-scoreboard-container");
            _enemyTeamSection = _root.Q<VisualElement>("enemy-team-section");
            _enemyTeamRows = _root.Q<VisualElement>("enemy-team-rows");
            _yourTeamSection = _root.Q<VisualElement>("your-team-section");
            _yourTeamRows = _root.Q<VisualElement>("your-team-rows");
            _enemyScoreValue = _root.Q<Label>("enemy-score-value");
            _yourScoreValue = _root.Q<Label>("your-score-value");

            // Kill Feed
            _killFeedContainer = _root.Q<VisualElement>("kill-feed-container");

            // NEW: Match timer
            _matchTimerLabel = _root.Q<Label>("match-timer-label");

            // HUD root + containers
            _hudPanel = _root.Q<VisualElement>("hud-panel");
            _matchTimerContainer = _root.Q<VisualElement>("match-timer-container");

            // Podium nameplates
            _podiumContainer = _root.Q<VisualElement>("podium-nameplates-container");
            _podiumFirstSlot = _root.Q<VisualElement>("podium-first-slot");
            _podiumSecondSlot = _root.Q<VisualElement>("podium-second-slot");
            _podiumThirdSlot = _root.Q<VisualElement>("podium-third-slot");

            _podiumFirstName = _root.Q<Label>("podium-first-name");
            _podiumSecondName = _root.Q<Label>("podium-second-name");
            _podiumThirdName = _root.Q<Label>("podium-third-name");

            _podiumFirstKills = _root.Q<Label>("podium-first-kills");
            _podiumSecondKills = _root.Q<Label>("podium-second-kills");
            _podiumThirdKills = _root.Q<Label>("podium-third-kills");
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

            // Setup pause menu copy button
            if(_pauseCopyCodeButton != null) {
                _pauseCopyCodeButton.clicked += CopyPauseJoinCodeToClipboard;
                _pauseCopyCodeButton.RegisterCallback<MouseEnterEvent>(MouseHover);
            }

            // Setup options buttons
            _applyButton.clicked += () => {
                OnButtonClicked();
                ApplySettings();
            };
            _applyButton.RegisterCallback<MouseOverEvent>(MouseHover);

            _backButton.clicked += () => {
                OnButtonClicked(true);
                OnBackFromOptions();
            };
            _backButton.RegisterCallback<MouseOverEvent>(MouseHover);

            // Setup unsaved changes dialog buttons
            if(_unsavedChangesYes != null) {
                _unsavedChangesYes.clicked += OnUnsavedChangesYes;
                _unsavedChangesYes.RegisterCallback<MouseEnterEvent>(MouseHover);
            }

            if(_unsavedChangesNo != null) {
                _unsavedChangesNo.clicked += OnUnsavedChangesNo;
                _unsavedChangesNo.RegisterCallback<MouseEnterEvent>(MouseHover);
                // Note: back-button class is already in UXML, so OnButtonClicked will detect it
            }

            if(_unsavedChangesCancel != null) {
                _unsavedChangesCancel.clicked += OnUnsavedChangesCancel;
                _unsavedChangesCancel.RegisterCallback<MouseEnterEvent>(MouseHover);
            }
        }

        private void OnButtonClicked(bool isBack = false) {
            if(SoundFXManager.Instance != null) {
                var sound = !isBack ? buttonClickSound : backClickSound;
                if(sound != null) {
                    SoundFXManager.Instance.PlayUISound(sound);
                }
            }
        }

        private void MouseHover(MouseOverEvent evt) {
            SoundFXManager.Instance.PlayUISound(buttonHoverSound);
        }

        private void MouseHover(MouseEnterEvent evt) {
            SoundFXManager.Instance.PlayUISound(buttonHoverSound);
        }

        public void TogglePause() {
            // Only allow pausing in Game scene
            if(!SceneManager.GetActiveScene().name.Contains("Game")) return;

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

        // Helper methods for button checkbox state
        private static bool GetCheckboxValue(Button button) {
            return button != null && button.ClassListContains("checked");
        }

        private static void SetCheckboxValue(Button button, bool value) {
            if(button == null) return;
            if(value) {
                button.AddToClassList("checked");
            } else {
                button.RemoveFromClassList("checked");
            }
        }

        private void ToggleCheckbox(Button button) {
            if(button == null) return;
            bool currentValue = GetCheckboxValue(button);
            SetCheckboxValue(button, !currentValue);
        }

        private void SetupControlsCallbacks() {
            _sensitivitySlider.RegisterValueChangedCallback(evt => {
                _sensitivityValue.text = evt.newValue.ToString("F2");
            });
        }

        private void SetupGraphicsCallbacks() {
            // Setup quality dropdown
            _qualityDropdown.choices = new List<string>(QualitySettings.names);
            _qualityDropdown.index = QualitySettings.GetQualityLevel();

            // Setup FPS dropdown
            _fpsDropdown.choices = new List<string> { "30", "60", "120", "144", "Unlimited" };
        }

        private void SetupKeybinds() {
            if(KeybindManager.Instance == null) {
                Debug.LogWarning("[GameMenuManager] KeybindManager not found, keybinds will not work");
                return;
            }

            var keybindNames = new[] {
                "forward", "back", "left", "right", "jump", "shoot", "reload", "grapple", "primary", "secondary",
                "nextweapon", "previousweapon"
            };

            foreach(var keybindName in keybindNames) {
                var buttons = new Button[2];
                buttons[0] = _root.Q<Button>($"keybind-{keybindName}-0");
                buttons[1] = _root.Q<Button>($"keybind-{keybindName}-1");

                if(buttons[0] != null && buttons[1] != null) {
                    _keybindButtons[keybindName] = buttons;

                    // Setup click handlers
                    for(int i = 0; i < 2; i++) {
                        var index = i;
                        buttons[i].clicked += () => OnKeybindButtonClicked(keybindName, index);
                    }
                }
            }

            LoadKeybindDisplayStrings();
        }

        private void OnKeybindButtonClicked(string keybindName, int bindingIndex) {
            if(KeybindManager.Instance == null) return;

            var button = _keybindButtons[keybindName][bindingIndex];
            button.text = "Press key...";
            button.SetEnabled(false);

            KeybindManager.Instance.StartRebinding(keybindName, bindingIndex, (displayString) => {
                button.SetEnabled(true);
                if(!string.IsNullOrEmpty(displayString)) {
                    button.text = displayString;
                } else {
                    // Reload original binding if cancelled
                    LoadKeybindDisplayString(keybindName, bindingIndex);
                }
            });
        }

        private void LoadKeybindDisplayStrings() {
            if(KeybindManager.Instance == null) return;

            foreach(var kvp in _keybindButtons) {
                var keybindName = kvp.Key;
                var buttons = kvp.Value;

                for(int i = 0; i < buttons.Length; i++) {
                    // Ensure button is enabled (in case it was disabled during a cancelled rebind)
                    if(buttons[i] != null) {
                        buttons[i].SetEnabled(true);
                    }

                    LoadKeybindDisplayString(keybindName, i);
                }
            }
        }

        private void LoadKeybindDisplayString(string keybindName, int bindingIndex) {
            if(KeybindManager.Instance == null ||
               !_keybindButtons.TryGetValue(keybindName, out var keybindButton)) return;

            var button = keybindButton[bindingIndex];
            if(button != null) {
                var displayString = KeybindManager.Instance.GetBindingDisplayString(keybindName, bindingIndex);
                button.text = displayString;
            }
        }

        private void SetupOptionsTabs() {
            // Configure scrollbar visibility for options content scroll
            var optionsScrollView = _root.Q<ScrollView>("options-content-scroll");
            if(optionsScrollView != null) {
                optionsScrollView.verticalScrollerVisibility = ScrollerVisibility.Auto;
                optionsScrollView.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            }

            if(_tabVideo != null) _tabVideo.clicked += () => SwitchOptionsTab("video");
            if(_tabAudio != null) _tabAudio.clicked += () => SwitchOptionsTab("audio");
            if(_tabGame != null) _tabGame.clicked += () => SwitchOptionsTab("game");
            if(_tabControls != null) _tabControls.clicked += () => SwitchOptionsTab("controls");

            // Register hover sounds and add hover class for tabs
            // Use MouseEnterEvent for sound (only once) and MouseOverEvent for continuous repaint
            _tabVideo?.RegisterCallback<MouseEnterEvent>(evt => {
                MouseHover(evt);
                if(!_tabVideo.ClassListContains("options-tab-active")) {
                    _tabVideo.AddToClassList("options-tab-hover");
                    // Force immediate repaint when adding hover class
                    _tabVideo.schedule.Execute(() => _tabVideo.MarkDirtyRepaint());
                }
            });
            _tabVideo?.RegisterCallback<MouseOverEvent>(_ => {
                if(!_tabVideo.ClassListContains("options-tab-active") &&
                   _tabVideo.ClassListContains("options-tab-hover")) {
                    _tabVideo.MarkDirtyRepaint();
                }
            });
            _tabVideo?.RegisterCallback<MouseLeaveEvent>(_ => {
                _tabVideo.RemoveFromClassList("options-tab-hover");
                _tabVideo.MarkDirtyRepaint();
            });

            _tabAudio?.RegisterCallback<MouseEnterEvent>(evt => {
                MouseHover(evt);
                if(!_tabAudio.ClassListContains("options-tab-active")) {
                    _tabAudio.AddToClassList("options-tab-hover");
                    _tabAudio.schedule.Execute(() => _tabAudio.MarkDirtyRepaint());
                }
            });
            _tabAudio?.RegisterCallback<MouseOverEvent>(_ => {
                if(!_tabAudio.ClassListContains("options-tab-active") &&
                   _tabAudio.ClassListContains("options-tab-hover")) {
                    _tabAudio.MarkDirtyRepaint();
                }
            });
            _tabAudio?.RegisterCallback<MouseLeaveEvent>(_ => {
                _tabAudio.RemoveFromClassList("options-tab-hover");
                _tabAudio.MarkDirtyRepaint();
            });

            _tabGame?.RegisterCallback<MouseEnterEvent>(evt => {
                MouseHover(evt);
                if(!_tabGame.ClassListContains("options-tab-active")) {
                    _tabGame.AddToClassList("options-tab-hover");
                    _tabGame.schedule.Execute(() => _tabGame.MarkDirtyRepaint());
                }
            });
            _tabGame?.RegisterCallback<MouseOverEvent>(_ => {
                if(!_tabGame.ClassListContains("options-tab-active") &&
                   _tabGame.ClassListContains("options-tab-hover")) {
                    _tabGame.MarkDirtyRepaint();
                }
            });
            _tabGame?.RegisterCallback<MouseLeaveEvent>(_ => {
                _tabGame.RemoveFromClassList("options-tab-hover");
                _tabGame.MarkDirtyRepaint();
            });

            _tabControls?.RegisterCallback<MouseEnterEvent>(evt => {
                MouseHover(evt);
                if(!_tabControls.ClassListContains("options-tab-active")) {
                    _tabControls.AddToClassList("options-tab-hover");
                    _tabControls.schedule.Execute(() => _tabControls.MarkDirtyRepaint());
                }
            });
            _tabControls?.RegisterCallback<MouseOverEvent>(_ => {
                if(!_tabControls.ClassListContains("options-tab-active") &&
                   _tabControls.ClassListContains("options-tab-hover")) {
                    _tabControls.MarkDirtyRepaint();
                }
            });
            _tabControls?.RegisterCallback<MouseLeaveEvent>(_ => {
                _tabControls.RemoveFromClassList("options-tab-hover");
                _tabControls.MarkDirtyRepaint();
            });

            // Start with Video tab active
            SwitchOptionsTab("video");
        }

        private void SwitchOptionsTab(string tabName) {
            // Remove active and hover classes from all tabs
            _tabVideo?.RemoveFromClassList("options-tab-active");
            _tabVideo?.RemoveFromClassList("options-tab-hover");
            _tabAudio?.RemoveFromClassList("options-tab-active");
            _tabAudio?.RemoveFromClassList("options-tab-hover");
            _tabGame?.RemoveFromClassList("options-tab-active");
            _tabGame?.RemoveFromClassList("options-tab-hover");
            _tabControls?.RemoveFromClassList("options-tab-active");
            _tabControls?.RemoveFromClassList("options-tab-hover");

            // Hide all content
            _videoContent?.AddToClassList("hidden");
            _audioContent?.AddToClassList("hidden");
            _gameContent?.AddToClassList("hidden");
            _controlsContent?.AddToClassList("hidden");

            // Show selected tab and content
            switch(tabName.ToLower()) {
                case "video":
                    _tabVideo?.AddToClassList("options-tab-active");
                    _videoContent?.RemoveFromClassList("hidden");
                    break;
                case "audio":
                    _tabAudio?.AddToClassList("options-tab-active");
                    _audioContent?.RemoveFromClassList("hidden");
                    break;
                case "game":
                    _tabGame?.AddToClassList("options-tab-active");
                    _gameContent?.RemoveFromClassList("hidden");
                    break;
                case "controls":
                    _tabControls?.AddToClassList("options-tab-active");
                    _controlsContent?.RemoveFromClassList("hidden");
                    break;
            }

            // Force style refresh after tab switch to ensure borders are visible
            _tabVideo?.MarkDirtyRepaint();
            _tabAudio?.MarkDirtyRepaint();
            _tabGame?.MarkDirtyRepaint();
            _tabControls?.MarkDirtyRepaint();

            // Play click sound
            OnButtonClicked();
        }

        #endregion

        #region Menu Navigation

        private void PauseGame() {
            IsPaused = true;
            _pauseMenuPanel.RemoveFromClassList("hidden");
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            // Update join code display when pausing
            UpdatePauseJoinCodeDisplay();

            if(_localController) {
                _localController.moveInput = Vector2.zero;
            }
        }

        private void UpdatePauseJoinCodeDisplay() {
            if(_pauseJoinCodeLabel == null) return;

            // Try to get join code from SessionManager
            var sessionManager = SessionManager.Instance;
            if(sessionManager?.ActiveSession != null) {
                // Try to get relay code from session properties first
                string joinCode = null;
                if(sessionManager.ActiveSession.Properties.TryGetValue("relayCode", out var prop) &&
                   !string.IsNullOrEmpty(prop.Value)) {
                    joinCode = prop.Value;
                } else if(!string.IsNullOrEmpty(sessionManager.ActiveSession.Code)) {
                    // Fallback to UGS session code
                    joinCode = sessionManager.ActiveSession.Code;
                }

                if(!string.IsNullOrEmpty(joinCode)) {
                    _pauseJoinCodeLabel.text = $"Join Code: {joinCode}";
                    if(_pauseCopyCodeButton != null) {
                        _pauseCopyCodeButton.SetEnabled(true);
                    }
                } else {
                    _pauseJoinCodeLabel.text = "Join Code: - - - - - -";
                    if(_pauseCopyCodeButton != null) {
                        _pauseCopyCodeButton.SetEnabled(false);
                    }
                }
            } else {
                _pauseJoinCodeLabel.text = "Join Code: - - - - - -";
                if(_pauseCopyCodeButton != null) {
                    _pauseCopyCodeButton.SetEnabled(false);
                }
            }
        }

        private void CopyPauseJoinCodeToClipboard() {
            OnButtonClicked();
            if(_pauseJoinCodeLabel == null) return;

            // Extract code from "Join Code: ABC123" → "ABC123"
            var fullText = _pauseJoinCodeLabel.text;
            var code = fullText.Replace("Join Code: ", "").Trim();

            if(string.IsNullOrEmpty(code) || code == "- - - - - -") {
                return; // No code to copy
            }

            GUIUtility.systemCopyBuffer = code;
            Debug.Log($"[GameMenuManager] Copied join code to clipboard: {code}");
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

            // Force style recalculation after panel becomes visible
            // Use multiple schedule calls to ensure styles are applied
            _optionsPanel.schedule.Execute(() => {
                // First pass: ensure all buttons are enabled and visible
                _tabVideo?.SetEnabled(true);
                _tabAudio?.SetEnabled(true);
                _tabGame?.SetEnabled(true);
                _tabControls?.SetEnabled(true);

                // Second pass: force repaint
                _optionsPanel.schedule.Execute(() => {
                    _tabVideo?.MarkDirtyRepaint();
                    _tabAudio?.MarkDirtyRepaint();
                    _tabGame?.MarkDirtyRepaint();
                    _tabControls?.MarkDirtyRepaint();
                });
            });
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
            // Load single sensitivity value, defaulting to 0.1 if not set
            // If old separate X/Y values exist, use X as the new unified value
            float sensitivityValue;
            if(PlayerPrefs.HasKey("Sensitivity")) {
                sensitivityValue = PlayerPrefs.GetFloat("Sensitivity", 0.1f);
            } else if(PlayerPrefs.HasKey("SensitivityX")) {
                sensitivityValue = PlayerPrefs.GetFloat("SensitivityX", 0.1f);
                // Migrate to new unified key
                PlayerPrefs.SetFloat("Sensitivity", sensitivityValue);
            } else {
                sensitivityValue = 0.1f;
            }

            _sensitivitySlider.value = sensitivityValue;
            SetCheckboxValue(_invertYButton, PlayerPrefs.GetInt("InvertY", 0) == 1);

            // Load graphics settings
            _qualityDropdown.index = PlayerPrefs.GetInt("QualityLevel", QualitySettings.GetQualityLevel());
            SetCheckboxValue(_vsyncButton, PlayerPrefs.GetInt("VSync", 0) == 1);
            _fpsDropdown.index = PlayerPrefs.GetInt("TargetFPS", 1);

            // Store original values to detect changes
            _originalMasterVolume = _masterVolumeSlider.value;
            _originalMusicVolume = _musicVolumeSlider.value;
            _originalSfxVolume = _sfxVolumeSlider.value;
            _originalSensitivity = _sensitivitySlider.value;
            _originalInvertY = GetCheckboxValue(_invertYButton);
            _originalQualityLevel = _qualityDropdown.index;
            _originalVsync = GetCheckboxValue(_vsyncButton);
            _originalTargetFPS = _fpsDropdown.index;

            // Apply loaded settings
            ApplySettingsInternal();

            // Load keybind display strings
            LoadKeybindDisplayStrings();
        }

        private bool HasUnsavedChanges() {
            bool hasKeybindChanges = KeybindManager.Instance != null && KeybindManager.Instance.HasPendingBindings();
            return !Mathf.Approximately(_masterVolumeSlider.value, _originalMasterVolume) ||
                   !Mathf.Approximately(_musicVolumeSlider.value, _originalMusicVolume) ||
                   !Mathf.Approximately(_sfxVolumeSlider.value, _originalSfxVolume) ||
                   !Mathf.Approximately(_sensitivitySlider.value, _originalSensitivity) ||
                   GetCheckboxValue(_invertYButton) != _originalInvertY ||
                   _qualityDropdown.index != _originalQualityLevel ||
                   GetCheckboxValue(_vsyncButton) != _originalVsync ||
                   _fpsDropdown.index != _originalTargetFPS ||
                   hasKeybindChanges;
        }

        private void OnBackFromOptions() {
            // Cancel any active rebinding operations FIRST (before checking for unsaved changes)
            // This stops listening for inputs immediately
            if(KeybindManager.Instance != null) {
                KeybindManager.Instance.CancelActiveRebinding();
            }

            // Reset all keybind buttons to enabled state and revert to saved PlayerPrefs values
            LoadKeybindDisplayStrings();

            // Check for unsaved changes AFTER canceling rebinding (so pending bindings are still checked)
            bool hasUnsaved = HasUnsavedChanges();

            // Clear pending bindings (revert to saved PlayerPrefs)
            if(KeybindManager.Instance != null) {
                KeybindManager.Instance.CancelBindings();
            }

            if(hasUnsaved) {
                ShowUnsavedChangesDialog();
            } else {
                HideOptions();
            }
        }

        private void ShowUnsavedChangesDialog() {
            if(_unsavedChangesModal != null) {
                _unsavedChangesModal.RemoveFromClassList("hidden");
                _unsavedChangesModal.BringToFront(); // Ensure it appears above options menu
            }
        }

        private void HideUnsavedChangesDialog() {
            if(_unsavedChangesModal != null) {
                _unsavedChangesModal.AddToClassList("hidden");
            }
        }

        private void OnUnsavedChangesYes() {
            OnButtonClicked();
            ApplySettings(); // Apply and save changes
            HideUnsavedChangesDialog();
            HideOptions();
        }

        private void OnUnsavedChangesNo() {
            OnButtonClicked(true); // Use back button sound
            // Cancel keybind changes
            if(KeybindManager.Instance != null) {
                KeybindManager.Instance.CancelBindings();
            }

            LoadSettings(); // Reload original settings (discard changes)
            HideUnsavedChangesDialog();
            HideOptions();
        }

        private void OnUnsavedChangesCancel() {
            OnButtonClicked(true);
            HideUnsavedChangesDialog();
            // Stay in options menu
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
            PlayerPrefs.SetFloat("Sensitivity", _sensitivitySlider.value);
            PlayerPrefs.SetInt("InvertY", GetCheckboxValue(_invertYButton) ? 1 : 0);

            // Save graphics settings
            PlayerPrefs.SetInt("QualityLevel", _qualityDropdown.index);
            PlayerPrefs.SetInt("VSync", GetCheckboxValue(_vsyncButton) ? 1 : 0);
            PlayerPrefs.SetInt("TargetFPS", _fpsDropdown.index);

            // Save keybinds
            if(KeybindManager.Instance != null) {
                KeybindManager.Instance.SaveBindings();
            }

            PlayerPrefs.Save();

            ApplySettingsInternal();

            // Update original values after applying (no longer unsaved)
            _originalMasterVolume = _masterVolumeSlider.value;
            _originalMusicVolume = _musicVolumeSlider.value;
            _originalSfxVolume = _sfxVolumeSlider.value;
            _originalSensitivity = _sensitivitySlider.value;
            _originalInvertY = GetCheckboxValue(_invertYButton);
            _originalQualityLevel = _qualityDropdown.index;
            _originalVsync = GetCheckboxValue(_vsyncButton);
            _originalTargetFPS = _fpsDropdown.index;

            // Reload keybind display strings after saving
            LoadKeybindDisplayStrings();

            Debug.Log("Settings applied and saved!");
        }

        private void ApplySettingsInternal() {
            // Apply audio
            audioMixer.SetFloat("masterVolume", LinearToDb(_masterVolumeSlider.value));
            audioMixer.SetFloat("musicVolume", LinearToDb(_musicVolumeSlider.value));
            audioMixer.SetFloat("soundFXVolume", LinearToDb(_sfxVolumeSlider.value));

            var invertMultiplier = GetCheckboxValue(_invertYButton) ? -1f : 1f;

            if(_localController) {
                float sensitivity = _sensitivitySlider.value;
                _localController.lookSensitivity = new Vector2(sensitivity, sensitivity * invertMultiplier);
            }

            // Apply graphics
            QualitySettings.SetQualityLevel(_qualityDropdown.index);
            QualitySettings.vSyncCount = GetCheckboxValue(_vsyncButton) ? 1 : 0;

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

        public void SetMatchTime(int secondsRemaining) {
            if(_matchTimerLabel == null) return;

            if(secondsRemaining < 0)
                secondsRemaining = 0;

            int minutes = secondsRemaining / 60;
            int seconds = secondsRemaining % 60;

            _matchTimerLabel.text = $"{minutes:00}:{seconds:00}";
        }

        public void ShowScoreboard() {
            if(SceneManager.GetActiveScene().name != "Game") return;

            IsScoreboardVisible = true;
            // Ensure root-container is visible (in case it was hidden)
            var rootContainer = _root.Q<VisualElement>("root-container");
            if(rootContainer != null) {
                rootContainer.style.display = DisplayStyle.Flex;
            }

            // Show scoreboard panel
            _scoreboardPanel.style.display = DisplayStyle.Flex;
            _scoreboardPanel.RemoveFromClassList("hidden");
            UpdateScoreboardHeaders();
            UpdateScoreboard();
        }

        private void UpdateScoreboardHeaders() {
            var matchSettings = MatchSettings.Instance;
            bool isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Tag";

            // Get header elements
            var scoreboardHeader = _root.Q<VisualElement>("scoreboard-header");
            if(scoreboardHeader == null) return;

            // Find header labels by their text content (they don't have names)
            var headerLabels = scoreboardHeader.Query<Label>().ToList();

            if(isTagMode) {
                // Tag mode headers: PING, AVATAR, NAME, TT, Tags, Tagged, TTR, AV
                // Order: TT (first/main score), Tags (replaces K), Tagged (replaces D), TTR (replaces KDR)
                // Reuse existing columns: K -> TT, D -> Tags, A -> Tagged, KDR -> TTR
                // Hide HS% and DMG columns
                foreach(var label in headerLabels) {
                    var text = label.text;
                    if(text == "K") {
                        label.text = "TT"; // TT is the main score, placed first
                    } else if(text == "D") {
                        label.text = "Tags";
                    } else if(text == "A") {
                        label.text = "Tagged";
                    } else if(text == "KDR") {
                        label.text = "TTR";
                    } else if(text == "HS%") {
                        label.style.display = DisplayStyle.None;
                    } else if(text == "DMG") {
                        label.style.display = DisplayStyle.None;
                    }
                }
            } else {
                // Normal mode headers: PING, AVATAR, NAME, K, D, A, KDR, DMG, HS%, AV
                // Restore all columns
                foreach(var label in headerLabels) {
                    var text = label.text;
                    if(text == "TT") {
                        label.text = "K";
                    } else if(text == "Tags") {
                        label.text = "D";
                    } else if(text == "Tagged") {
                        label.text = "A";
                    } else if(text == "TTR") {
                        label.text = "KDR";
                    }

                    label.style.display = DisplayStyle.Flex;
                }
            }
        }

        public void HideScoreboard() {
            if(SceneManager.GetActiveScene().name != "Game") return;

            IsScoreboardVisible = false;
            // Remove inline display style so the hidden class can take effect
            _scoreboardPanel.style.display = StyleKeyword.Null;
            _scoreboardPanel.AddToClassList("hidden");
        }

        private void UpdateScoreboard() {
            // Check if current game mode is team-based
            var matchSettings = MatchSettings.Instance;
            bool isTeamBased = matchSettings != null && IsTeamBasedMode(matchSettings.selectedGameModeId);

            if(isTeamBased) {
                UpdateTdmScoreboard();
            } else {
                UpdateFfaScoreboard();
            }
        }

        private bool IsTeamBasedMode(string modeId) => modeId switch {
            "Team Deathmatch" => true,
            "CTF" => true,
            "Oddball" => true,
            "KOTH" => true,
            _ => false
        };

        private void UpdateFfaScoreboard() {
            // Null checks for UI elements
            if(_scoreboardContainer == null || _tdmScoreboardContainer == null || _playerRows == null) {
                Debug.LogWarning("[GameMenuManager] FFA scoreboard UI elements not initialized");
                return;
            }

            // Show FFA scoreboard, hide TDM
            _scoreboardContainer.RemoveFromClassList("hidden");
            _tdmScoreboardContainer.AddToClassList("hidden");

            _playerRows.Clear();

            // Get all player controllers
            var allControllers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);

            // Check if we're in Tag mode
            var matchSettings = MatchSettings.Instance;
            bool isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Tag";

            // Sort by appropriate stat
            var sortedPlayers = new List<PlayerController>(allControllers);
            if(isTagMode) {
                // Tag mode: sort by time tagged (lowest first)
                sortedPlayers.Sort((a, b) => a.timeTagged.Value.CompareTo(b.timeTagged.Value));
            } else {
                // Normal mode: sort by kills (descending)
                sortedPlayers.Sort((a, b) => b.kills.Value.CompareTo(a.kills.Value));
            }

            foreach(var player in sortedPlayers) {
                CreatePlayerRow(player, _playerRows, isTagMode: isTagMode);
            }
        }

        private void UpdateTdmScoreboard() {
            // Null checks for UI elements
            if(_scoreboardContainer == null || _tdmScoreboardContainer == null ||
               _enemyTeamRows == null || _yourTeamRows == null) {
                Debug.LogWarning("[GameMenuManager] TDM scoreboard UI elements not initialized, falling back to FFA");
                UpdateFfaScoreboard();
                return;
            }

            // Show TDM scoreboard, hide FFA
            _scoreboardContainer.AddToClassList("hidden");
            _tdmScoreboardContainer.RemoveFromClassList("hidden");

            _enemyTeamRows.Clear();
            _yourTeamRows.Clear();

            // Get local player's team
            var localPlayer = NetworkManager.Singleton?.LocalClient?.PlayerObject;
            if(localPlayer == null) {
                // Fallback to FFA if no local player
                UpdateFfaScoreboard();
                return;
            }

            var localTeamMgr = localPlayer.GetComponent<PlayerTeamManager>();
            if(localTeamMgr == null) {
                UpdateFfaScoreboard();
                return;
            }

            var localTeam = localTeamMgr.netTeam.Value;

            // Get all players and split by team
            var allControllers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
            var enemyPlayers = new List<PlayerController>();
            var yourTeamPlayers = new List<PlayerController>();

            foreach(var player in allControllers) {
                var teamMgr = player.GetComponent<PlayerTeamManager>();
                if(teamMgr == null) continue;

                if(teamMgr.netTeam.Value == localTeam) {
                    yourTeamPlayers.Add(player);
                } else {
                    enemyPlayers.Add(player);
                }
            }

            // Sort by kills (descending)
            enemyPlayers.Sort((a, b) => b.kills.Value.CompareTo(a.kills.Value));
            yourTeamPlayers.Sort((a, b) => b.kills.Value.CompareTo(a.kills.Value));

            // Create rows for each team (simplified stats for TDM)
            foreach(var player in enemyPlayers) {
                CreatePlayerRow(player, _enemyTeamRows, simplifiedStats: true, isYourTeam: false);
            }

            foreach(var player in yourTeamPlayers) {
                CreatePlayerRow(player, _yourTeamRows, simplifiedStats: true, isYourTeam: true);
            }

            // Update team scores (total kills per team)
            int enemyScore = CalculateTeamScore(enemyPlayers);
            int yourScore = CalculateTeamScore(yourTeamPlayers);

            if(_enemyScoreValue != null) {
                _enemyScoreValue.text = enemyScore.ToString();
            }

            if(_yourScoreValue != null) {
                _yourScoreValue.text = yourScore.ToString();
            }
        }

        private int CalculateTeamScore(List<PlayerController> players) {
            int totalKills = 0;
            foreach(var player in players) {
                totalKills += player.kills.Value;
            }

            return totalKills;
        }

        private void CreatePlayerRow(PlayerController player, VisualElement parentContainer, bool isTagMode = false) {
            var row = new VisualElement();
            row.AddToClassList("player-row");

            // Highlight local player
            if(player.IsOwner) {
                row.AddToClassList("player-row-local");
            }

            // Add to parent container
            parentContainer.Add(row);

            // Ping
            var ping = new Label(GetPingText(player));
            ping.AddToClassList("player-ping");
            ping.AddToClassList(GetPingColorClass(player));
            row.Add(ping);

            // Avatar (player icon based on color)
            var avatar = new VisualElement();
            avatar.AddToClassList("player-avatar");
            var playerIcon = GetPlayerIconSprite(player.playerMaterialIndex.Value);
            if(playerIcon != null) {
                avatar.style.backgroundImage = new StyleBackground(playerIcon);
            }
            row.Add(avatar);

            // Name
            var playerName = new Label(player.playerName.Value.ToString());
            playerName.AddToClassList("player-name");
            row.Add(playerName);

            if(isTagMode) {
                // Tag mode stats: TT, Tags, Tagged, TTR, DMG, AV
                // Order matches header: PING, AVATAR, NAME, TT, Tags, Tagged, TTR, DMG, AV
                // TT (Time Tagged) - main score, shown first (replaces K)
                var timeTagged = new Label(player.timeTagged.Value.ToString());
                timeTagged.AddToClassList("player-stat");
                row.Add(timeTagged);

                // Tags (replaces D)
                var tags = new Label(player.tags.Value.ToString());
                tags.AddToClassList("player-stat");
                row.Add(tags);

                // Tagged (replaces A)
                var tagged = new Label(player.tagged.Value.ToString());
                tagged.AddToClassList("player-stat");
                row.Add(tagged);

                // TTR (Tag-Tagged Ratio) instead of KDR
                var ttr = CalculateTtr(player.tags.Value, player.tagged.Value);
                var ttrLabel = new Label(ttr.ToString("F2"));
                ttrLabel.AddToClassList("player-stat");
                if(ttr >= 2.0f) {
                    ttrLabel.AddToClassList("player-stat-highlight");
                }

                row.Add(ttrLabel);

                // Skip Damage and HS% for Tag mode (no damage dealt in Tag mode)

                // Average Velocity (skip HS% and DMG for Tag mode)
                var avgVelocity = player.averageVelocity.Value;
                var avgVelocityLabel = new Label($"{avgVelocity:F1} u/s");
                avgVelocityLabel.AddToClassList("player-stat");
                row.Add(avgVelocityLabel);
            } else {
                // Normal mode stats
                // Kills
                var kills = new Label(player.kills.Value.ToString());
                kills.AddToClassList("player-stat");
                row.Add(kills);

                // Deaths
                var deaths = new Label(player.deaths.Value.ToString());
                deaths.AddToClassList("player-stat");
                row.Add(deaths);

                // Assists
                var assists = new Label(player.assists.Value.ToString());
                assists.AddToClassList("player-stat");
                row.Add(assists);

                // KDA
                var kda = CalculateKdr(player.kills.Value, player.deaths.Value, player.assists.Value);
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
            }
        }

        // Overload for TDM (includes K, D, A, KDR, DMG, HS%, AV)
        private void CreatePlayerRow(PlayerController player, VisualElement parentContainer, bool simplifiedStats,
            bool isYourTeam = false) {
            if(!simplifiedStats) {
                CreatePlayerRow(player, parentContainer);
                return;
            }

            var row = new VisualElement();
            row.AddToClassList("player-row");

            // Highlight local player
            if(player.IsOwner) {
                row.AddToClassList("player-row-local");
                if(isYourTeam) {
                    row.AddToClassList("player-row-local-your-team");
                }
            }

            parentContainer.Add(row);

            // Ping
            var ping = new Label(GetPingText(player));
            ping.AddToClassList("player-ping");
            ping.AddToClassList(GetPingColorClass(player));
            row.Add(ping);

            // Avatar (player icon based on color)
            var avatar = new VisualElement();
            avatar.AddToClassList("player-avatar");
            var playerIcon = GetPlayerIconSprite(player.playerMaterialIndex.Value);
            if(playerIcon != null) {
                avatar.style.backgroundImage = new StyleBackground(playerIcon);
            }
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

            // Assists
            var assists = new Label(player.assists.Value.ToString());
            assists.AddToClassList("player-stat");
            row.Add(assists);

            // KDR
            var kda = CalculateKdr(player.kills.Value, player.deaths.Value, player.assists.Value);
            var kdaLabel = new Label(kda.ToString("F2"));
            kdaLabel.AddToClassList("player-stat");
            if(kda >= 2.0f) {
                kdaLabel.AddToClassList("player-stat-highlight");
            }

            row.Add(kdaLabel);

            // Damage
            var damage = Mathf.RoundToInt(player.damageDealt.Value);
            var damageLabel = new Label($"{damage:N0}");
            damageLabel.AddToClassList("player-stat");
            row.Add(damageLabel);

            // Headshot % (placeholder)
            var headshotPct = new Label("0%");
            headshotPct.AddToClassList("player-stat");
            row.Add(headshotPct);

            // Average Velocity
            var avgVelocity = player.averageVelocity.Value;
            var avgVelocityLabel = new Label($"{avgVelocity:F1} u/s");
            avgVelocityLabel.AddToClassList("player-stat");
            row.Add(avgVelocityLabel);
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

        private float CalculateKdr(int kills, int deaths, int assists) {
            if(deaths == 0) return kills + assists;
            return (kills + assists) / (float)deaths;
        }

        private float CalculateTtr(int tags, int tagged) {
            if(tagged == 0) return tags;
            return tags / (float)tagged;
        }

        /// <summary>
        /// Gets the player icon sprite based on the player's material index.
        /// Material index order: 0=white, 1=red, 2=orange, 3=yellow, 4=green, 5=blue, 6=purple
        /// </summary>
        private Sprite GetPlayerIconSprite(int materialIndex) {
            if(playerIconSprites == null || playerIconSprites.Length == 0) {
                return null;
            }

            // Clamp index to valid range
            var clampedIndex = Mathf.Clamp(materialIndex, 0, playerIconSprites.Length - 1);
            return playerIconSprites[clampedIndex];
        }

        #endregion

        #region Kill Feed Management

        /// <summary>
        /// Call this when a kill happens. Pass killer and victim PlayerControllers.
        /// </summary>
        public void AddKillToFeed(string killerName, string victimName, bool isLocalKiller, ulong killerClientId,
            ulong victimClientId) {
            if(_killFeedContainer == null) return;

            // NEW: Check if we're at capacity
            if(_activeKillEntries.Count >= maxKillFeedEntries) {
                // Force remove the oldest entry (last in the list)
                var oldestEntry = _activeKillEntries[^1];
                RemoveKillEntry(oldestEntry, immediate: true);
            }

            var killEntry = CreateKillEntry(killerName, victimName, isLocalKiller, killerClientId, victimClientId);

            // Add to top of feed
            _killFeedContainer.Add(killEntry);
            _activeKillEntries.Add(killEntry); // Insert at beginning so oldest is at end

            // Start fade-out timer
            var fadeCoroutine = StartCoroutine(FadeOutKillEntry(killEntry));
            _fadeCoroutines[killEntry] = fadeCoroutine;
        }

        /// <summary>
        /// Call this when a tag is transferred in Tag mode. Pass tagger and tagged player names.
        /// </summary>
        public void AddTagTransferToFeed(string taggerName, string taggedName, bool isLocalTagger, ulong taggerClientId,
            ulong taggedClientId) {
            if(_killFeedContainer == null) return;

            // Check if we're at capacity
            if(_activeKillEntries.Count >= maxKillFeedEntries) {
                var oldestEntry = _activeKillEntries[^1];
                RemoveKillEntry(oldestEntry, immediate: true);
            }

            var tagEntry =
                CreateTagTransferEntry(taggerName, taggedName, isLocalTagger, taggerClientId, taggedClientId);

            // Add to top of feed
            _killFeedContainer.Add(tagEntry);
            _activeKillEntries.Add(tagEntry);

            // Start fade-out timer
            var fadeCoroutine = StartCoroutine(FadeOutKillEntry(tagEntry));
            _fadeCoroutines[tagEntry] = fadeCoroutine;
        }

        private VisualElement CreateKillEntry(string killerName, string victimName, bool isLocalKiller,
            ulong killerClientId, ulong victimClientId) {
            var entry = new VisualElement();
            entry.AddToClassList("kill-entry");

            if(isLocalKiller) {
                entry.AddToClassList("kill-entry-local");
            }

            // Get team colors for killer and victim
            Color killerColor = GetTeamColorForPlayer(killerClientId);
            Color victimColor = GetTeamColorForPlayer(victimClientId);

            // Killer name
            var killer = new Label(killerName);
            killer.AddToClassList("killer-name");
            if(isLocalKiller) {
                killer.AddToClassList("killer-name-local");
            }

            // Apply team color to killer name
            killer.style.color = new StyleColor(killerColor);
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
            // Apply team color to victim name
            victim.style.color = new StyleColor(victimColor);
            entry.Add(victim);

            return entry;
        }

        private VisualElement CreateTagTransferEntry(string taggerName, string taggedName, bool isLocalTagger,
            ulong taggerClientId, ulong taggedClientId) {
            var entry = new VisualElement();
            entry.AddToClassList("kill-entry");

            if(isLocalTagger) {
                entry.AddToClassList("kill-entry-local");
            }

            // Get team colors for tagger and tagged (Tag mode is FFA, so use white/default)
            Color taggerColor = Color.white;
            Color taggedColor = Color.white;

            // Tagger name
            var tagger = new Label(taggerName);
            tagger.AddToClassList("killer-name");
            if(isLocalTagger) {
                tagger.AddToClassList("killer-name-local");
            }

            tagger.style.color = new StyleColor(taggerColor);
            entry.Add(tagger);

            // Tag icon (use "you're it" icon for Tag mode)
            var icon = new VisualElement();
            icon.AddToClassList("kill-icon");
            if(youreItIconSprite != null) {
                icon.style.backgroundImage = new StyleBackground(youreItIconSprite);
            } else if(killIconSprite != null) {
                // Fallback to kill icon if you're it icon is not set
                icon.style.backgroundImage = new StyleBackground(killIconSprite);
            }

            entry.Add(icon);

            // Tagged name
            var tagged = new Label(taggedName);
            tagged.AddToClassList("victim-name");
            tagged.style.color = new StyleColor(taggedColor);
            entry.Add(tagged);

            return entry;
        }

        /// <summary>
        /// Gets the appropriate team color for a player based on their team and the local player's team.
        /// Returns a readable RGB color (not HDR) for text display.
        /// </summary>
        private Color GetTeamColorForPlayer(ulong clientId) {
            // Special case: HOP/fall damage (ulong.MaxValue) - use white
            if(clientId == ulong.MaxValue) {
                return Color.white;
            }

            // Check if this is a team-based mode
            var matchSettings = MatchSettings.Instance;
            bool isTeamBased = matchSettings != null && IsTeamBasedMode(matchSettings.selectedGameModeId);

            if(!isTeamBased) {
                // FFA mode: use default white color
                return Color.white;
            }

            // Get local player's team
            var localPlayer = NetworkManager.Singleton?.LocalClient?.PlayerObject;
            if(localPlayer == null) return Color.white;

            var localTeamMgr = localPlayer.GetComponent<PlayerTeamManager>();
            if(localTeamMgr == null) return Color.white;

            var localTeam = localTeamMgr.netTeam.Value;

            // Find the player by client ID
            if(!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client)) {
                return Color.white;
            }

            var playerObj = client.PlayerObject;
            if(playerObj == null) return Color.white;

            var playerTeamMgr = playerObj.GetComponent<PlayerTeamManager>();
            if(playerTeamMgr == null) return Color.white;

            var playerTeam = playerTeamMgr.netTeam.Value;

            // Determine if this player is teammate or enemy
            bool isTeammate = playerTeam == localTeam;

            // Try to get actual colors from a PlayerTeamManager instance
            // If we can't find one, use default colors that match the outline colors
            // Convert HDR colors to readable RGB for text (tone down brightness)
            if(isTeammate) {
                // Teammate: cyan-blue
                // HDR outline: (0, 1.5, 2.5) -> readable text: (0, 0.7, 1.0)
                return new Color(0f, 0.7f, 1f, 1f); // Bright cyan-blue
            } else {
                // Enemy: red
                // HDR outline: (2.5, 0.5, 0.5) -> readable text: (1.0, 0.3, 0.3)
                return new Color(1f, 0.3f, 0.3f, 1f); // Bright red
            }
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

        #region Match Flow / Post-Match

        /// <summary>
        /// Called when the server-authoritative match timer hits 0.
        /// Hides in-game HUD and uses SceneTransitionManager to fade to black.
        /// </summary>
        public void EnterPostMatch() {
            if(IsPostMatch)
                return;

            IsPostMatch = true;

            HUDManager.Instance.HideHUD();
            HideInGameHudForPostMatch();
        }

        /// <summary>
        /// Hides only the in-game HUD elements, but leaves pause/scoreboard usable.
        /// </summary>
        private void HideInGameHudForPostMatch() {
            // If for any reason the whole HUD panel is null, bail gracefully.
            if(_hudPanel == null)
                return;

            // Hide individual HUD elements
            if(_killFeedContainer != null)
                _killFeedContainer.style.display = DisplayStyle.None;
            if(_matchTimerContainer != null)
                _matchTimerContainer.style.display = DisplayStyle.None;

            // Pause menu + scoreboard panels are untouched,
            // so ESC / Tab (or whatever) still work.
        }

        public void ShowInGameHudAfterPostMatch() {
            // If for any reason the whole HUD panel is null, bail gracefully.
            if(_hudPanel == null)
                return;

            // Show individual HUD elements
            if(_killFeedContainer != null)
                _killFeedContainer.style.display = DisplayStyle.Flex;
            if(_matchTimerContainer != null)
                _matchTimerContainer.style.display = DisplayStyle.Flex;

            // TODO: this is only being pushed to host...
            _podiumContainer.style.display = DisplayStyle.None;
        }

        #endregion

        public void SetPodiumSlots(
            string firstName, int firstKills,
            string secondName, int secondKills,
            string thirdName, int thirdKills
        ) {
            if(_podiumContainer == null)
                return;

            // Show the container as soon as we have data
            _podiumContainer.style.display = DisplayStyle.Flex;

            // Allow pointer events to pass through the container so pause menu is clickable
            // Only the actual podium slots should capture pointer events
            _podiumContainer.pickingMode = PickingMode.Ignore;

            SetPodiumSlot(_podiumFirstSlot, _podiumFirstName, _podiumFirstKills, firstName, firstKills);
            SetPodiumSlot(_podiumSecondSlot, _podiumSecondName, _podiumSecondKills, secondName, secondKills);
            SetPodiumSlot(_podiumThirdSlot, _podiumThirdName, _podiumThirdKills, thirdName, thirdKills);
        }

        private void SetPodiumSlot(
            VisualElement slotRoot,
            Label nameLabel,
            Label killsLabel,
            string playerName,
            int kills
        ) {
            if(slotRoot == null || nameLabel == null || killsLabel == null)
                return;

            bool hasPlayer = !string.IsNullOrEmpty(playerName);

            slotRoot.style.display = hasPlayer ? DisplayStyle.Flex : DisplayStyle.None;
            nameLabel.text = hasPlayer ? playerName : "---";
            killsLabel.text = hasPlayer ? kills.ToString() : "0";
        }
    }
}
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UIElements;

namespace Network.Singletons {
    /// <summary>
    /// Shared options menu manager that handles all options functionality.
    /// Can be used by both MainMenuManager and GameMenuManager.
    /// </summary>
    public class OptionsMenuManager : MonoBehaviour {
        [Header("References")]
        [SerializeField] private UIDocument uiDocument;

        [SerializeField] private AudioMixer audioMixer;

        [Header("Callbacks")]
        [SerializeField] private bool useCallbacks = true;

        // Callbacks for button sounds and hover sounds (set by parent manager)
        public Action<bool> OnButtonClickedCallback;
        public Action<MouseEnterEvent> MouseEnterCallback;
        public Action<MouseOverEvent> MouseHoverCallback;

        // Callback for when back is pressed with no unsaved changes (set by parent manager)
        public Action OnBackFromOptionsCallback;

        #region UI Elements - Options

        private VisualElement _root;
        private Slider _masterVolumeSlider;
        private Slider _musicVolumeSlider;
        private Slider _sfxVolumeSlider;
        private TextField _masterVolumeValue;
        private TextField _musicVolumeValue;
        private TextField _sfxVolumeValue;
        private Slider _sensitivitySlider;
        private TextField _sensitivityValue;
        private Button _invertYButton;
        private Button _playerTrailsButton;
        private Button _holdMantleButton;
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

        // Keybind buttons
        private Dictionary<string, Button[]> _keybindButtons = new Dictionary<string, Button[]>();

        // Unsaved changes dialog
        private VisualElement _unsavedChangesModal;
        private Button _unsavedChangesYes;
        private Button _unsavedChangesNo;
        private Button _unsavedChangesCancel;

        // Apply and back buttons
        private Button _applyButton;
        private Button _backButton;

        #endregion

        #region Original Settings Values

        private float _originalMasterVolume;
        private float _originalMusicVolume;
        private float _originalSfxVolume;
        private float _originalSensitivity;
        private bool _originalInvertY;
        private bool _originalPlayerTrails;
        private bool _originalHoldMantle;
        private int _originalQualityLevel;
        private bool _originalVsync;
        private int _originalTargetFPS;

        #endregion

        #region Unity Lifecycle

        private void Awake() {
            if(uiDocument == null) {
                uiDocument = GetComponent<UIDocument>();
            }
        }

        public void Initialize() {
            if(uiDocument == null) {
                Debug.LogError("[OptionsMenuManager] UIDocument not assigned!");
                return;
            }

            _root = uiDocument.rootVisualElement;
            FindUIElements();
            SetupCallbacks();
            SetupOptionsTabs();
            SetupKeybinds();
        }

        #endregion

        #region Setup

        private void FindUIElements() {
            // Audio controls
            _masterVolumeSlider = _root.Q<Slider>("master-volume");
            _musicVolumeSlider = _root.Q<Slider>("music-volume");
            _sfxVolumeSlider = _root.Q<Slider>("sfx-volume");
            _masterVolumeValue = _root.Q<TextField>("master-volume-value");
            _musicVolumeValue = _root.Q<TextField>("music-volume-value");
            _sfxVolumeValue = _root.Q<TextField>("sfx-volume-value");

            // Sensitivity controls
            _sensitivitySlider = _root.Q<Slider>("sensitivity");
            _sensitivityValue = _root.Q<TextField>("sensitivity-value");
            _invertYButton = _root.Q<Button>("invert-y");
            _playerTrailsButton = _root.Q<Button>("player-trails");
            _holdMantleButton = _root.Q<Button>("hold-mantle");

            // Graphics controls
            _qualityDropdown = _root.Q<DropdownField>("quality-level");
            _vsyncButton = _root.Q<Button>("vsync");
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

            // Unsaved changes dialog
            _unsavedChangesModal = _root.Q<VisualElement>("unsaved-changes-modal");
            _unsavedChangesYes = _root.Q<Button>("unsaved-changes-yes");
            _unsavedChangesNo = _root.Q<Button>("unsaved-changes-no");
            _unsavedChangesCancel = _root.Q<Button>("unsaved-changes-cancel");

            // Apply and back buttons
            _applyButton = _root.Q<Button>("apply-button");
            _backButton = _root.Q<Button>("back-button");

            // Setup checkbox click handlers
            _invertYButton?.RegisterCallback<ClickEvent>(_ => ToggleCheckbox(_invertYButton));
            _playerTrailsButton?.RegisterCallback<ClickEvent>(_ => ToggleCheckbox(_playerTrailsButton));
            _holdMantleButton?.RegisterCallback<ClickEvent>(_ => ToggleCheckbox(_holdMantleButton));
            _vsyncButton?.RegisterCallback<ClickEvent>(_ => ToggleCheckbox(_vsyncButton));
        }

        private void SetupCallbacks() {
            SetupAudioCallbacks();
            SetupControlsCallbacks();
            SetupGraphicsCallbacks();

            // Setup apply and back buttons
            _applyButton?.RegisterCallback<ClickEvent>(_ => {
                OnButtonClicked();
                ApplySettings();
            });
            if(_applyButton != null && useCallbacks) {
                RegisterHoverCallback(_applyButton);
            }

            _backButton?.RegisterCallback<ClickEvent>(_ => {
                OnButtonClicked(true);
                OnBackFromOptions();
            });
            if(_backButton != null && useCallbacks) {
                RegisterHoverCallback(_backButton);
            }

            // Setup unsaved changes dialog buttons
            _unsavedChangesYes?.RegisterCallback<ClickEvent>(_ => OnUnsavedChangesYes());
            if(_unsavedChangesYes != null && useCallbacks) {
                RegisterHoverCallback(_unsavedChangesYes);
            }

            _unsavedChangesNo?.RegisterCallback<ClickEvent>(_ => OnUnsavedChangesNo());
            if(_unsavedChangesNo != null && useCallbacks) {
                RegisterHoverCallback(_unsavedChangesNo);
            }

            _unsavedChangesCancel?.RegisterCallback<ClickEvent>(_ => OnUnsavedChangesCancel());
            if(_unsavedChangesCancel != null && useCallbacks) {
                RegisterHoverCallback(_unsavedChangesCancel);
            }
        }

        private void RegisterHoverCallback(Button button) {
            if(MouseEnterCallback != null) {
                button.RegisterCallback<MouseEnterEvent>(evt => MouseEnterCallback(evt));
            }

            if(MouseHoverCallback != null) {
                button.RegisterCallback<MouseOverEvent>(evt => MouseHoverCallback(evt));
            }
        }

        private void SetupAudioCallbacks() {
            // Update text fields when sliders change (with % sign for volumes)
            _masterVolumeSlider?.RegisterValueChangedCallback(evt => {
                if(_masterVolumeValue != null) {
                    _masterVolumeValue.value = Mathf.RoundToInt(evt.newValue * 100).ToString() + "%";
                }
            });
            _musicVolumeSlider?.RegisterValueChangedCallback(evt => {
                if(_musicVolumeValue != null) {
                    _musicVolumeValue.value = Mathf.RoundToInt(evt.newValue * 100).ToString() + "%";
                }
            });
            _sfxVolumeSlider?.RegisterValueChangedCallback(evt => {
                if(_sfxVolumeValue != null) {
                    _sfxVolumeValue.value = Mathf.RoundToInt(evt.newValue * 100).ToString() + "%";
                }
            });

            // Setup text field input validation and callbacks
            SetupVolumeInputField(_masterVolumeSlider, _masterVolumeValue, 0f, 1f, true);
            SetupVolumeInputField(_musicVolumeSlider, _musicVolumeValue, 0f, 1f, true);
            SetupVolumeInputField(_sfxVolumeSlider, _sfxVolumeValue, 0f, 1f, true);
        }

        private void SetupControlsCallbacks() {
            // Update text field when slider changes
            _sensitivitySlider?.RegisterValueChangedCallback(evt => {
                if(_sensitivityValue != null) {
                    _sensitivityValue.value = evt.newValue.ToString("F2");
                }
            });

            // Setup sensitivity input field (with fixed width class)
            if(_sensitivityValue != null) {
                _sensitivityValue.AddToClassList("sensitivity-input");
            }

            SetupVolumeInputField(_sensitivitySlider, _sensitivityValue, 0.01f, 0.5f, false);
        }

        private void SetupVolumeInputField(Slider slider, TextField textField, float minValue, float maxValue,
            bool isPercentage) {
            if(slider == null || textField == null) return;

            // Set max length (3 for percentage to allow "100%", 5 for sensitivity to allow decimals)
            textField.maxLength = isPercentage ? 4 : 5; // 4 to allow "100%"
            textField.isDelayed = false;

            // Filter input in real-time using ValueChanged callback (like join code input)
            textField.RegisterValueChangedCallback(evt => {
                string newValue = evt.newValue;
                string filtered = "";

                // For percentage inputs, remove % sign before filtering, then add it back
                if(isPercentage && newValue.EndsWith("%")) {
                    newValue = newValue.Replace("%", "");
                }

                // Filter to only allow digits and decimal point (for sensitivity)
                foreach(char c in newValue) {
                    if(char.IsDigit(c)) {
                        filtered += c;
                    } else if(c == '.' && !isPercentage) {
                        // Only allow one decimal point for sensitivity
                        if(!filtered.Contains(".")) {
                            filtered += c;
                        }
                    }
                }

                // Apply length limit
                if(isPercentage && filtered.Length > 3) {
                    filtered = filtered.Substring(0, 3);
                } else if(!isPercentage && filtered.Length > 5) {
                    filtered = filtered.Substring(0, 5);
                }

                // Add % sign back for percentage inputs
                if(isPercentage && !string.IsNullOrEmpty(filtered)) {
                    filtered += "%";
                }

                // Only update if the value changed (to avoid infinite loops)
                if(filtered != newValue) {
                    textField.value = filtered;
                }
            });

            // Handle value change on Enter or focus loss
            textField.RegisterCallback<KeyDownEvent>(evt => {
                if(evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) {
                    ApplyTextFieldValue(slider, textField, minValue, maxValue, isPercentage);
                    textField.Blur(); // Remove focus
                }
            });

            textField.RegisterCallback<BlurEvent>(_ => {
                ApplyTextFieldValue(slider, textField, minValue, maxValue, isPercentage);
            });
        }

        private void ApplyTextFieldValue(Slider slider, TextField textField, float minValue, float maxValue,
            bool isPercentage) {
            if(slider == null || textField == null) return;

            string input = textField.value.Trim();

            // Remove % sign if present (for percentage inputs)
            if(isPercentage && input.EndsWith("%")) {
                input = input.Replace("%", "").Trim();
            }

            if(string.IsNullOrEmpty(input)) {
                // Restore current slider value
                if(isPercentage) {
                    textField.value = Mathf.RoundToInt(slider.value * 100).ToString() + "%";
                } else {
                    textField.value = slider.value.ToString("F2");
                }

                return;
            }

            // Parse the input
            if(float.TryParse(input, out float parsedValue)) {
                // Convert percentage to 0-1 range if needed
                if(isPercentage) {
                    parsedValue = parsedValue / 100f;
                }

                // Clamp to valid range
                float clampedValue = Mathf.Clamp(parsedValue, minValue, maxValue);
                slider.value = clampedValue;

                // Update text field with clamped value (add % for percentage)
                if(isPercentage) {
                    textField.value = Mathf.RoundToInt(clampedValue * 100).ToString() + "%";
                } else {
                    textField.value = clampedValue.ToString("F2");
                }
            } else {
                // Invalid input, restore current slider value
                if(isPercentage) {
                    textField.value = Mathf.RoundToInt(slider.value * 100).ToString() + "%";
                } else {
                    textField.value = slider.value.ToString("F2");
                }
            }
        }

        private void SetupGraphicsCallbacks() {
            if(_qualityDropdown != null) {
                _qualityDropdown.choices = new List<string>(QualitySettings.names);
                _qualityDropdown.index = QualitySettings.GetQualityLevel();
            }

            if(_fpsDropdown != null) {
                _fpsDropdown.choices = new List<string> { "30", "60", "120", "144", "Unlimited" };
            }
        }

        private void SetupOptionsTabs() {
            // Configure scrollbar visibility
            var optionsScrollView = _root.Q<ScrollView>("options-content-scroll");
            if(optionsScrollView != null) {
                optionsScrollView.verticalScrollerVisibility = ScrollerVisibility.Auto;
                optionsScrollView.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            }

            // Setup tab click handlers
            _tabVideo?.RegisterCallback<ClickEvent>(_ => SwitchOptionsTab("video"));
            _tabAudio?.RegisterCallback<ClickEvent>(_ => SwitchOptionsTab("audio"));
            _tabGame?.RegisterCallback<ClickEvent>(_ => SwitchOptionsTab("game"));
            _tabControls?.RegisterCallback<ClickEvent>(_ => SwitchOptionsTab("controls"));

            // Register hover callbacks for tabs
            SetupTabHoverCallbacks(_tabVideo);
            SetupTabHoverCallbacks(_tabAudio);
            SetupTabHoverCallbacks(_tabGame);
            SetupTabHoverCallbacks(_tabControls);

            // Start with Video tab active
            SwitchOptionsTab("video");
        }

        private void SetupTabHoverCallbacks(Button tab) {
            if(tab == null) return;

            tab.RegisterCallback<MouseEnterEvent>(evt => {
                if(MouseEnterCallback != null) {
                    MouseEnterCallback(evt);
                }

                if(!tab.ClassListContains("options-tab-active")) {
                    tab.AddToClassList("options-tab-hover");
                    tab.schedule.Execute(tab.MarkDirtyRepaint);
                }
            });

            tab.RegisterCallback<MouseOverEvent>(evt => {
                if(MouseHoverCallback != null && !tab.ClassListContains("options-tab-active")) {
                    MouseHoverCallback(evt);
                }

                if(!tab.ClassListContains("options-tab-active") && tab.ClassListContains("options-tab-hover")) {
                    tab.MarkDirtyRepaint();
                }
            });

            tab.RegisterCallback<MouseLeaveEvent>(_ => {
                tab.RemoveFromClassList("options-tab-hover");
                tab.MarkDirtyRepaint();
            });
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

            // Force style refresh
            _tabVideo?.MarkDirtyRepaint();
            _tabAudio?.MarkDirtyRepaint();
            _tabGame?.MarkDirtyRepaint();
            _tabControls?.MarkDirtyRepaint();

            // Play click sound
            OnButtonClicked();
        }

        private void SetupKeybinds() {
            if(KeybindManager.Instance == null) {
                Debug.LogWarning("[OptionsMenuManager] KeybindManager not found, keybinds will not work");
                return;
            }

            var keybindNames = new[] {
                "forward", "back", "left", "right", "jump", "interact", "shoot", "ads", "reload", "grapple", "primary", "secondary",
                "nextweapon", "previousweapon"
            };

            foreach(var keybindName in keybindNames) {
                var buttons = new Button[2];
                buttons[0] = _root.Q<Button>($"keybind-{keybindName}-0");
                buttons[1] = _root.Q<Button>($"keybind-{keybindName}-1");

                if(buttons[0] != null && buttons[1] != null) {
                    _keybindButtons[keybindName] = buttons;

                    for(int i = 0; i < 2; i++) {
                        var index = i;
                        buttons[i].RegisterCallback<ClickEvent>(_ => OnKeybindButtonClicked(keybindName, index));
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

        #endregion

        #region Settings Management

        public void LoadSettings() {
            // Load audio settings
            var masterDb = PlayerPrefs.GetFloat("MasterVolume", 0f);
            var musicDb = PlayerPrefs.GetFloat("MusicVolume", -20f);
            var sfxDb = PlayerPrefs.GetFloat("SFXVolume", -8f);
            if(_masterVolumeSlider != null) _masterVolumeSlider.value = DbToLinear(masterDb);
            if(_musicVolumeSlider != null) _musicVolumeSlider.value = DbToLinear(musicDb);
            if(_sfxVolumeSlider != null) _sfxVolumeSlider.value = DbToLinear(sfxDb);

            // Load sensitivity (with migration from old X/Y values)
            float sensitivityValue;
            if(PlayerPrefs.HasKey("Sensitivity")) {
                sensitivityValue = PlayerPrefs.GetFloat("Sensitivity", 0.1f);
            } else if(PlayerPrefs.HasKey("SensitivityX")) {
                sensitivityValue = PlayerPrefs.GetFloat("SensitivityX", 0.1f);
                PlayerPrefs.SetFloat("Sensitivity", sensitivityValue);
            } else {
                sensitivityValue = 0.1f;
            }

            if(_sensitivitySlider != null) _sensitivitySlider.value = sensitivityValue;
            if(_invertYButton != null) SetCheckboxValue(_invertYButton, PlayerPrefs.GetInt("InvertY", 0) == 1);
            if(_playerTrailsButton != null)
                SetCheckboxValue(_playerTrailsButton, PlayerPrefs.GetInt("PlayerTrails", 1) == 1);
            if(_holdMantleButton != null) SetCheckboxValue(_holdMantleButton, PlayerPrefs.GetInt("HoldMantle", 1) == 1);

            // Load graphics settings
            if(_qualityDropdown != null) {
                _qualityDropdown.index = PlayerPrefs.GetInt("QualityLevel", QualitySettings.GetQualityLevel());
            }

            if(_vsyncButton != null) SetCheckboxValue(_vsyncButton, PlayerPrefs.GetInt("VSync", 0) == 1);
            if(_fpsDropdown != null) _fpsDropdown.index = PlayerPrefs.GetInt("TargetFPS", 1);

            // Store original values
            if(_masterVolumeSlider != null) _originalMasterVolume = _masterVolumeSlider.value;
            if(_musicVolumeSlider != null) _originalMusicVolume = _musicVolumeSlider.value;
            if(_sfxVolumeSlider != null) _originalSfxVolume = _sfxVolumeSlider.value;
            if(_sensitivitySlider != null) _originalSensitivity = _sensitivitySlider.value;
            _originalInvertY = GetCheckboxValue(_invertYButton);
            _originalPlayerTrails = GetCheckboxValue(_playerTrailsButton);
            _originalHoldMantle = GetCheckboxValue(_holdMantleButton);
            if(_qualityDropdown != null) _originalQualityLevel = _qualityDropdown.index;
            _originalVsync = GetCheckboxValue(_vsyncButton);
            if(_fpsDropdown != null) _originalTargetFPS = _fpsDropdown.index;

            ApplySettingsInternal();

            // Update display text fields (with % for volumes)
            if(_masterVolumeValue != null && _masterVolumeSlider != null) {
                _masterVolumeValue.value = Mathf.RoundToInt(_masterVolumeSlider.value * 100).ToString() + "%";
            }

            if(_musicVolumeValue != null && _musicVolumeSlider != null) {
                _musicVolumeValue.value = Mathf.RoundToInt(_musicVolumeSlider.value * 100).ToString() + "%";
            }

            if(_sfxVolumeValue != null && _sfxVolumeSlider != null) {
                _sfxVolumeValue.value = Mathf.RoundToInt(_sfxVolumeSlider.value * 100).ToString() + "%";
            }

            if(_sensitivityValue != null && _sensitivitySlider != null) {
                _sensitivityValue.value = _sensitivitySlider.value.ToString("F2");
            }

            LoadKeybindDisplayStrings();
        }

        public bool HasUnsavedChanges() {
            bool hasKeybindChanges = KeybindManager.Instance != null && KeybindManager.Instance.HasPendingBindings();

            bool volumeChanged = false;
            if(_masterVolumeSlider != null)
                volumeChanged |= !Mathf.Approximately(_masterVolumeSlider.value, _originalMasterVolume);
            if(_musicVolumeSlider != null)
                volumeChanged |= !Mathf.Approximately(_musicVolumeSlider.value, _originalMusicVolume);
            if(_sfxVolumeSlider != null)
                volumeChanged |= !Mathf.Approximately(_sfxVolumeSlider.value, _originalSfxVolume);

            bool sensitivityChanged = false;
            if(_sensitivitySlider != null)
                sensitivityChanged = !Mathf.Approximately(_sensitivitySlider.value, _originalSensitivity);

            bool invertYChanged = GetCheckboxValue(_invertYButton) != _originalInvertY;
            bool playerTrailsChanged = GetCheckboxValue(_playerTrailsButton) != _originalPlayerTrails;
            bool holdMantleChanged = GetCheckboxValue(_holdMantleButton) != _originalHoldMantle;

            bool qualityChanged = false;
            if(_qualityDropdown != null) qualityChanged = _qualityDropdown.index != _originalQualityLevel;

            bool vsyncChanged = GetCheckboxValue(_vsyncButton) != _originalVsync;

            bool fpsChanged = false;
            if(_fpsDropdown != null) fpsChanged = _fpsDropdown.index != _originalTargetFPS;

            return volumeChanged || sensitivityChanged || invertYChanged || playerTrailsChanged || holdMantleChanged ||
                   qualityChanged || vsyncChanged || fpsChanged || hasKeybindChanges;
        }

        public void OnBackFromOptions() {
            // Cancel any active rebinding operations
            if(KeybindManager.Instance != null) {
                KeybindManager.Instance.CancelActiveRebinding();
            }

            // Reset keybind buttons
            LoadKeybindDisplayStrings();

            // Check for unsaved changes
            bool hasUnsaved = HasUnsavedChanges();

            // Clear pending bindings
            if(KeybindManager.Instance != null) {
                KeybindManager.Instance.CancelBindings();
            }

            if(hasUnsaved) {
                ShowUnsavedChangesDialog();
            } else {
                // No unsaved changes, call parent callback to handle navigation
                OnBackFromOptionsCallback?.Invoke();
            }
        }

        private void ShowUnsavedChangesDialog() {
            if(_unsavedChangesModal != null) {
                _unsavedChangesModal.RemoveFromClassList("hidden");
                _unsavedChangesModal.BringToFront();
            }
        }

        private void HideUnsavedChangesDialog() {
            if(_unsavedChangesModal != null) {
                _unsavedChangesModal.AddToClassList("hidden");
            }
        }

        private void OnUnsavedChangesYes() {
            OnButtonClicked();
            ApplySettings();
            HideUnsavedChangesDialog();
            OnBackFromOptionsCallback?.Invoke();
        }

        private void OnUnsavedChangesNo() {
            OnButtonClicked(true);
            if(KeybindManager.Instance != null) {
                KeybindManager.Instance.CancelBindings();
            }

            LoadSettings();
            HideUnsavedChangesDialog();
            OnBackFromOptionsCallback?.Invoke();
        }

        private void OnUnsavedChangesCancel() {
            OnButtonClicked(true);
            HideUnsavedChangesDialog();
        }

        public void ApplySettings() {
            // Save audio settings
            if(_masterVolumeSlider != null) {
                var masterDb = LinearToDb(_masterVolumeSlider.value);
                PlayerPrefs.SetFloat("MasterVolume", masterDb);
            }

            if(_musicVolumeSlider != null) {
                var musicDb = LinearToDb(_musicVolumeSlider.value);
                PlayerPrefs.SetFloat("MusicVolume", musicDb);
            }

            if(_sfxVolumeSlider != null) {
                var sfxDb = LinearToDb(_sfxVolumeSlider.value);
                PlayerPrefs.SetFloat("SFXVolume", sfxDb);
            }

            // Save control settings
            if(_sensitivitySlider != null) {
                PlayerPrefs.SetFloat("Sensitivity", _sensitivitySlider.value);
            }

            PlayerPrefs.SetInt("InvertY", GetCheckboxValue(_invertYButton) ? 1 : 0);
            PlayerPrefs.SetInt("PlayerTrails", GetCheckboxValue(_playerTrailsButton) ? 1 : 0);
            PlayerPrefs.SetInt("HoldMantle", GetCheckboxValue(_holdMantleButton) ? 1 : 0);

            // Save graphics settings
            if(_qualityDropdown != null) {
                PlayerPrefs.SetInt("QualityLevel", _qualityDropdown.index);
            }

            PlayerPrefs.SetInt("VSync", GetCheckboxValue(_vsyncButton) ? 1 : 0);
            if(_fpsDropdown != null) {
                PlayerPrefs.SetInt("TargetFPS", _fpsDropdown.index);
            }

            // Save keybinds
            if(KeybindManager.Instance != null) {
                KeybindManager.Instance.SaveBindings();
            }

            PlayerPrefs.Save();

            ApplySettingsInternal();

            // Update original values
            if(_masterVolumeSlider != null) _originalMasterVolume = _masterVolumeSlider.value;
            if(_musicVolumeSlider != null) _originalMusicVolume = _musicVolumeSlider.value;
            if(_sfxVolumeSlider != null) _originalSfxVolume = _sfxVolumeSlider.value;
            if(_sensitivitySlider != null) _originalSensitivity = _sensitivitySlider.value;
            _originalInvertY = GetCheckboxValue(_invertYButton);
            _originalPlayerTrails = GetCheckboxValue(_playerTrailsButton);
            _originalHoldMantle = GetCheckboxValue(_holdMantleButton);
            if(_qualityDropdown != null) _originalQualityLevel = _qualityDropdown.index;
            _originalVsync = GetCheckboxValue(_vsyncButton);
            if(_fpsDropdown != null) _originalTargetFPS = _fpsDropdown.index;

            LoadKeybindDisplayStrings();

            Debug.Log("[OptionsMenuManager] Settings applied and saved!");
        }

        private void ApplySettingsInternal() {
            if(audioMixer != null) {
                if(_masterVolumeSlider != null) {
                    audioMixer.SetFloat("masterVolume", LinearToDb(_masterVolumeSlider.value));
                }

                if(_musicVolumeSlider != null) {
                    audioMixer.SetFloat("musicVolume", LinearToDb(_musicVolumeSlider.value));
                }

                if(_sfxVolumeSlider != null) {
                    audioMixer.SetFloat("soundFXVolume", LinearToDb(_sfxVolumeSlider.value));
                }
            }

            if(_qualityDropdown != null) {
                QualitySettings.SetQualityLevel(_qualityDropdown.index);
            }

            QualitySettings.vSyncCount = GetCheckboxValue(_vsyncButton) ? 1 : 0;

            if(_fpsDropdown != null) {
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
                        break;
                }
            }

            // Sensitivity is now loaded from PlayerPrefs in PlayerLookController, no need to notify
        }

        #endregion

        #region Helper Methods

        private static float LinearToDb(float linear) => linear <= 0f ? -80f : 20f * Mathf.Log10(linear);
        private static float DbToLinear(float db) => db <= -80f ? 0f : Mathf.Pow(10f, db / 20f);

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

        private void OnButtonClicked(bool isBack = false) {
            OnButtonClickedCallback?.Invoke(isBack);
        }

        #endregion

        #region Public Methods for Parent Managers

        /// <summary>
        /// Call this when the options panel becomes visible to force style refresh.
        /// </summary>
        public void OnOptionsPanelShown() {
            var optionsPanel = _root?.Q<VisualElement>("options-panel");
            if(optionsPanel == null) return;

            // Force style recalculation
            optionsPanel.schedule.Execute(() => {
                _tabVideo?.SetEnabled(true);
                _tabAudio?.SetEnabled(true);
                _tabGame?.SetEnabled(true);
                _tabControls?.SetEnabled(true);

                optionsPanel.schedule.Execute(() => {
                    _tabVideo?.MarkDirtyRepaint();
                    _tabAudio?.MarkDirtyRepaint();
                    _tabGame?.MarkDirtyRepaint();
                    _tabControls?.MarkDirtyRepaint();
                });
            });
        }

        #endregion
    }
}
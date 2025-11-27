using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
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
        private DropdownField _windowModeDropdown;
        private DropdownField _aspectRatioDropdown;
        private DropdownField _resolutionDropdown;
        private DropdownField _msaaDropdown;
        private Slider _shadowDistanceSlider;
        private TextField _shadowDistanceValue;
        private DropdownField _shadowResolutionDropdown;
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
        private readonly Dictionary<string, Button[]> _keybindButtons = new();

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
        private int _originalWindowMode;
        private string _originalAspectRatio;
        private int _originalResolutionIndex;
        private int _originalMsaa;
        private float _originalShadowDistance;
        private int _originalShadowResolution;
        private bool _originalVsync;
        private int _originalTargetFPS;

        #endregion

        #region Unity Lifecycle

        private void Awake() {
            uiDocument = GetComponent<UIDocument>();
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
            _windowModeDropdown = _root.Q<DropdownField>("window-mode");
            _aspectRatioDropdown = _root.Q<DropdownField>("aspect-ratio");
            _resolutionDropdown = _root.Q<DropdownField>("resolution");
            _msaaDropdown = _root.Q<DropdownField>("msaa");
            _shadowDistanceSlider = _root.Q<Slider>("shadow-distance");
            _shadowDistanceValue = _root.Q<TextField>("shadow-distance-value");
            _shadowResolutionDropdown = _root.Q<DropdownField>("shadow-resolution");
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
                    _masterVolumeValue.value = Mathf.RoundToInt(evt.newValue * 100) + "%";
                }
            });
            _musicVolumeSlider?.RegisterValueChangedCallback(evt => {
                if(_musicVolumeValue != null) {
                    _musicVolumeValue.value = Mathf.RoundToInt(evt.newValue * 100) + "%";
                }
            });
            _sfxVolumeSlider?.RegisterValueChangedCallback(evt => {
                if(_sfxVolumeValue != null) {
                    _sfxVolumeValue.value = Mathf.RoundToInt(evt.newValue * 100) + "%";
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
            _sensitivityValue?.AddToClassList("sensitivity-input");

            SetupVolumeInputField(_sensitivitySlider, _sensitivityValue, 0.01f, 0.5f, false);
        }

        private static void SetupVolumeInputField(Slider slider, TextField textField, float minValue, float maxValue,
            bool isPercentage) {
            if(slider == null || textField == null) return;

            // Set max length (3 for percentage to allow "100%", 5 for sensitivity to allow decimals)
            textField.maxLength = isPercentage ? 4 : 5; // 4 to allow "100%"
            textField.isDelayed = false;

            // Filter input in real-time using ValueChanged callback (like join code input)
            textField.RegisterValueChangedCallback(evt => {
                var newValue = evt.newValue;
                var filtered = "";

                // For percentage inputs, remove % sign before filtering, then add it back
                if(isPercentage && newValue.EndsWith("%")) {
                    newValue = newValue.Replace("%", "");
                }

                // Filter to only allow digits and decimal point (for sensitivity)
                foreach(var c in newValue) {
                    if(char.IsDigit(c)) {
                        filtered += c;
                    } else if(c == '.' && !isPercentage) {
                        // Only allow one decimal point for sensitivity
                        if(!filtered.Contains(".")) {
                            filtered += c;
                        }
                    }
                }

                filtered = isPercentage switch {
                    // Apply length limit
                    true when filtered.Length > 3 => filtered[..3],
                    false when filtered.Length > 5 => filtered[..5],
                    _ => filtered
                };

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
                if(evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
                ApplyTextFieldValue(slider, textField, minValue, maxValue, isPercentage);
                textField.Blur(); // Remove focus
            });

            textField.RegisterCallback<BlurEvent>(_ => {
                ApplyTextFieldValue(slider, textField, minValue, maxValue, isPercentage);
            });
        }

        private static void ApplyTextFieldValue(Slider slider, TextField textField, float minValue, float maxValue,
            bool isPercentage) {
            if(slider == null || textField == null) return;

            var input = textField.value.Trim();

            // Remove % sign if present (for percentage inputs)
            if(isPercentage && input.EndsWith("%")) {
                input = input.Replace("%", "").Trim();
            }

            if(string.IsNullOrEmpty(input)) {
                // Restore current slider value
                if(isPercentage) {
                    textField.value = Mathf.RoundToInt(slider.value * 100) + "%";
                } else {
                    textField.value = slider.value.ToString("F2");
                }

                return;
            }

            // Parse the input
            if(float.TryParse(input, out var parsedValue)) {
                // Convert percentage to 0-1 range if needed
                if(isPercentage) {
                    parsedValue /= 100f;
                }

                // Clamp to valid range
                var clampedValue = Mathf.Clamp(parsedValue, minValue, maxValue);
                slider.value = clampedValue;

                // Update text field with clamped value (add % for percentage)
                if(isPercentage) {
                    textField.value = Mathf.RoundToInt(clampedValue * 100) + "%";
                } else {
                    textField.value = clampedValue.ToString("F2");
                }
            } else {
                // Invalid input, restore current slider value
                if(isPercentage) {
                    textField.value = Mathf.RoundToInt(slider.value * 100) + "%";
                } else {
                    textField.value = slider.value.ToString("F2");
                }
            }
        }

        // Resolution management
        private struct ResolutionData {
            public readonly int Width;
            public readonly int Height;
            public readonly string AspectRatio;
            public readonly string DisplayString;

            public ResolutionData(int w, int h) {
                Width = w;
                Height = h;
                AspectRatio = CalculateAspectRatio(w, h);
                DisplayString = $"{w} x {h}";
            }

            private static string CalculateAspectRatio(int width, int height) {
                // Calculate GCD to simplify aspect ratio
                var gcd = Gcd(width, height);
                var w = width / gcd;
                var h = height / gcd;

                // Detect supported aspect ratios: 16:9, 16:10, 4:3, and 21:9
                var ratio = (float)width / height;
                if(Mathf.Approximately(ratio, 16f / 9f)) return "16:9";
                if(Mathf.Approximately(ratio, 16f / 10f)) return "16:10";
                if(Mathf.Approximately(ratio, 21f / 9f)) return "21:9";
                return Mathf.Approximately(ratio, 4f / 3f) ? "4:3" :
                    // For other aspect ratios, return the simplified ratio
                    // These won't appear in the dropdown but will be stored correctly
                    $"{w}:{h}";
            }

            private static int Gcd(int a, int b) {
                while(b != 0) {
                    var temp = b;
                    b = a % b;
                    a = temp;
                }

                return a;
            }
        }

        private readonly List<ResolutionData> _allResolutions = new();
        private readonly List<ResolutionData> _filteredResolutions = new();
        private readonly HashSet<string> _availableAspectRatios = new();

        private void SetupGraphicsCallbacks() {
            SetupWindowModeDropdown();
            SetupResolutionDropdowns();
            SetupMsaaDropdown();
            SetupShadowDistanceSlider();
            SetupShadowResolutionDropdown();

            if(_fpsDropdown != null) {
                _fpsDropdown.choices = new List<string> { "30", "60", "120", "144", "Unlimited" };
            }
        }

        private void SetupMsaaDropdown() {
            if(_msaaDropdown == null) return;

            _msaaDropdown.choices = new List<string> {
                "Off",
                "2x",
                "4x",
                "8x"
            };
        }

        private void SetupShadowDistanceSlider() {
            if(_shadowDistanceSlider == null || _shadowDistanceValue == null) return;

            // Update text field when slider changes
            _shadowDistanceSlider.RegisterValueChangedCallback(evt => {
                if(_shadowDistanceValue != null) {
                    _shadowDistanceValue.value = Mathf.RoundToInt(evt.newValue).ToString();
                }
            });

            // Setup input field validation
            SetupShadowDistanceInputField();
        }

        private void SetupShadowDistanceInputField() {
            if(_shadowDistanceSlider == null || _shadowDistanceValue == null) return;

            _shadowDistanceValue.maxLength = 6;
            _shadowDistanceValue.isDelayed = false;

            // Filter input to only allow digits
            _shadowDistanceValue.RegisterValueChangedCallback(evt => {
                var newValue = evt.newValue;
                var filtered = "";

                foreach(var c in newValue) {
                    if(char.IsDigit(c)) {
                        filtered += c;
                    }
                }

                if(filtered.Length > 6) {
                    filtered = filtered[..6];
                }

                if(filtered != newValue) {
                    _shadowDistanceValue.value = filtered;
                }
            });

            // Handle value change on Enter or focus loss
            _shadowDistanceValue.RegisterCallback<KeyDownEvent>(evt => {
                if(evt.keyCode is not (KeyCode.Return or KeyCode.KeypadEnter)) return;
                ApplyShadowDistanceTextFieldValue();
                _shadowDistanceValue.Blur();
            });

            _shadowDistanceValue.RegisterCallback<BlurEvent>(_ => { ApplyShadowDistanceTextFieldValue(); });
        }

        private void ApplyShadowDistanceTextFieldValue() {
            if(_shadowDistanceSlider == null || _shadowDistanceValue == null) return;

            var input = _shadowDistanceValue.value.Trim();
            if(string.IsNullOrEmpty(input)) {
                _shadowDistanceValue.value = Mathf.RoundToInt(_shadowDistanceSlider.value).ToString();
                return;
            }

            if(int.TryParse(input, out var parsedValue)) {
                var clampedValue = Mathf.Clamp(parsedValue, 0f, 500f);
                _shadowDistanceSlider.value = clampedValue;
                _shadowDistanceValue.value = Mathf.RoundToInt(clampedValue).ToString();
            } else {
                _shadowDistanceValue.value = Mathf.RoundToInt(_shadowDistanceSlider.value).ToString();
            }
        }

        private void SetupShadowResolutionDropdown() {
            if(_shadowResolutionDropdown == null) return;

            _shadowResolutionDropdown.choices = new List<string> {
                "Low",
                "Medium",
                "High",
                "Ultra"
            };
        }

        private void SetupWindowModeDropdown() {
            if(_windowModeDropdown == null) return;

            _windowModeDropdown.choices = new List<string> {
                "Windowed",
                "Borderless Windowed",
                "Fullscreen"
            };
        }

        private void SetupResolutionDropdowns() {
            if(_aspectRatioDropdown == null || _resolutionDropdown == null) return;

            // Get all unique resolutions
            _allResolutions.Clear();
            var seenResolutions = new HashSet<string>();

            foreach(var res in Screen.resolutions) {
                var resData = new ResolutionData(res.width, res.height);
                var key = $"{resData.Width}x{resData.Height}";

                if(!seenResolutions.Add(key)) continue;
                _allResolutions.Add(resData);
                _availableAspectRatios.Add(resData.AspectRatio);
            }

            // Sort resolutions by width (descending), then height (descending)
            _allResolutions.Sort((a, b) => a.Width != b.Width ? b.Width.CompareTo(a.Width) : b.Height.CompareTo(a.Height));

            // Only show supported aspect ratios: 16:9, 16:10, 4:3, and 21:9
            // Check which ones are actually available in the resolutions
            var supportedAspectRatios = new List<string>();
            if(_availableAspectRatios.Contains("16:9")) {
                supportedAspectRatios.Add("16:9");
            }

            if(_availableAspectRatios.Contains("16:10")) {
                supportedAspectRatios.Add("16:10");
            }

            if(_availableAspectRatios.Contains("21:9")) {
                supportedAspectRatios.Add("21:9");
            }

            if(_availableAspectRatios.Contains("4:3")) {
                supportedAspectRatios.Add("4:3");
            }

            // If none found, default to 16:9 (most common)
            if(supportedAspectRatios.Count == 0) {
                supportedAspectRatios.Add("16:9");
            }

            _aspectRatioDropdown.choices = supportedAspectRatios;

            // Set default aspect ratio to 16:9 or first available
            var defaultAspectRatio = supportedAspectRatios.Contains("16:9") ? "16:9" : supportedAspectRatios[0];
            _aspectRatioDropdown.value = defaultAspectRatio;

            // Filter resolutions by default aspect ratio
            FilterResolutionsByAspectRatio(defaultAspectRatio);

            // Setup aspect ratio change callback
            _aspectRatioDropdown.RegisterValueChangedCallback(evt => { FilterResolutionsByAspectRatio(evt.newValue); });
        }

        private void FilterResolutionsByAspectRatio(string aspectRatio) {
            if(_resolutionDropdown == null) return;

            _filteredResolutions.Clear();
            foreach(var res in _allResolutions) {
                if(res.AspectRatio == aspectRatio) {
                    _filteredResolutions.Add(res);
                }
            }

            // Update resolution dropdown
            var resolutionChoices = new List<string>();
            foreach(var res in _filteredResolutions) {
                resolutionChoices.Add(res.DisplayString);
            }

            _resolutionDropdown.choices = resolutionChoices;

            // Try to find and set current resolution
            var currentIndex = FindCurrentResolutionIndex();
            if(currentIndex >= 0) {
                _resolutionDropdown.index = currentIndex;
            } else if(_filteredResolutions.Count > 0) {
                _resolutionDropdown.index = 0;
            }
        }

        private int FindCurrentResolutionIndex() {
            var currentWidth = Screen.width;
            var currentHeight = Screen.height;

            for(var i = 0; i < _filteredResolutions.Count; i++) {
                if(_filteredResolutions[i].Width == currentWidth &&
                   _filteredResolutions[i].Height == currentHeight) {
                    return i;
                }
            }

            return -1;
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
                MouseEnterCallback?.Invoke(evt);

                if(tab.ClassListContains("options-tab-active")) return;
                tab.AddToClassList("options-tab-hover");
                tab.schedule.Execute(tab.MarkDirtyRepaint);
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
                "forward", "back", "left", "right", "jump", "interact", "shoot", "ads", "reload", "grapple", "primary",
                "secondary",
                "nextweapon", "previousweapon"
            };

            foreach(var keybindName in keybindNames) {
                var buttons = new Button[2];
                buttons[0] = _root.Q<Button>($"keybind-{keybindName}-0");
                buttons[1] = _root.Q<Button>($"keybind-{keybindName}-1");

                if(buttons[0] == null || buttons[1] == null) continue;
                _keybindButtons[keybindName] = buttons;

                for(var i = 0; i < 2; i++) {
                    var index = i;
                    buttons[i].RegisterCallback<ClickEvent>(_ => OnKeybindButtonClicked(keybindName, index));
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

            foreach(var (keybindName, buttons) in _keybindButtons) {
                for(var i = 0; i < buttons.Length; i++) {
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
            if(button == null) return;
            var displayString = KeybindManager.GetBindingDisplayString(keybindName, bindingIndex);
            button.text = displayString;
        }

        #endregion

        #region Settings Management

        private void LoadGraphicsSettings() {
            // Get current URP asset values as defaults
            var urpAsset = GetUrpAsset();
            var currentMsaa = 1; // Off
            var currentShadowDistance = 300f;
            var currentShadowResolution = 2048;

            if(urpAsset != null) {
                currentMsaa = urpAsset.msaaSampleCount;
                currentShadowDistance = urpAsset.shadowDistance;
                currentShadowResolution = urpAsset.mainLightShadowmapResolution;
            }

            // Load MSAA
            if(_msaaDropdown != null) {
                var savedMsaa = PlayerPrefs.GetInt("MSAA", currentMsaa);
                // Map MSAA value to dropdown index: 1=Off, 2=2x, 4=4x, 8=8x
                var msaaIndex = savedMsaa switch {
                    1 => 0, // Off
                    2 => 1, // 2x
                    4 => 2, // 4x
                    8 => 3, // 8x
                    _ => 0 // Default to Off
                };
                _msaaDropdown.index = Mathf.Clamp(msaaIndex, 0, _msaaDropdown.choices.Count - 1);
            }

            // Load shadow distance
            if(_shadowDistanceSlider != null) {
                var savedShadowDistance = PlayerPrefs.GetFloat("ShadowDistance", currentShadowDistance);
                _shadowDistanceSlider.value = Mathf.Clamp(savedShadowDistance, 0f, 500f);
                if(_shadowDistanceValue != null) {
                    _shadowDistanceValue.value = Mathf.RoundToInt(_shadowDistanceSlider.value).ToString();
                }
            }

            // Load shadow resolution
            if(_shadowResolutionDropdown == null) return;
            var savedShadowResolution = PlayerPrefs.GetInt("ShadowResolution", currentShadowResolution);
            // Map resolution to preset index: Low=512, Medium=1024, High=2048, Ultra=4096
            var resolutionIndex = savedShadowResolution switch {
                512 => 0,
                1024 => 1,
                2048 => 2,
                4096 => 3,
                <= 512 => 0,
                <= 1024 => 1,
                <= 2048 => 2,
                _ => 3
            };

            _shadowResolutionDropdown.index =
                Mathf.Clamp(resolutionIndex, 0, _shadowResolutionDropdown.choices.Count - 1);
        }

        private static UniversalRenderPipelineAsset GetUrpAsset() {
            return GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
        }

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

            // Load window mode and resolution settings
            if(_windowModeDropdown != null) {
                // Default to current fullscreen mode
                var savedWindowMode = PlayerPrefs.GetInt("WindowMode", GetCurrentWindowModeIndex());
                _windowModeDropdown.index = Mathf.Clamp(savedWindowMode, 0, _windowModeDropdown.choices.Count - 1);
            }

            // Load aspect ratio (default to 16:9 or current resolution's aspect ratio)
            var savedAspectRatio = PlayerPrefs.GetString("AspectRatio", "");
            if(_aspectRatioDropdown != null && _aspectRatioDropdown.choices.Count > 0) {
                if(string.IsNullOrEmpty(savedAspectRatio)) {
                    // Try to detect current aspect ratio
                    var currentRes = new ResolutionData(Screen.width, Screen.height);
                    savedAspectRatio = currentRes.AspectRatio;
                }

                // Find index of saved aspect ratio
                var aspectRatioIndex = _aspectRatioDropdown.choices.IndexOf(savedAspectRatio);
                if(aspectRatioIndex >= 0) {
                    _aspectRatioDropdown.index = aspectRatioIndex;
                    FilterResolutionsByAspectRatio(savedAspectRatio);
                } else if(_aspectRatioDropdown.choices.Count > 0) {
                    // Fallback to first available aspect ratio
                    _aspectRatioDropdown.index = 0;
                    FilterResolutionsByAspectRatio(_aspectRatioDropdown.choices[0]);
                }
            }

            // Load resolution
            if(_resolutionDropdown != null && _filteredResolutions.Count > 0) {
                var savedWidth = PlayerPrefs.GetInt("ResolutionWidth", Screen.width);
                var savedHeight = PlayerPrefs.GetInt("ResolutionHeight", Screen.height);

                // Find matching resolution in filtered list
                var resolutionIndex = -1;
                for(var i = 0; i < _filteredResolutions.Count; i++) {
                    if(_filteredResolutions[i].Width != savedWidth ||
                       _filteredResolutions[i].Height != savedHeight) continue;
                    resolutionIndex = i;
                    break;
                }

                if(resolutionIndex >= 0) {
                    _resolutionDropdown.index = resolutionIndex;
                } else {
                    // Fallback to current resolution or first available
                    var currentIndex = FindCurrentResolutionIndex();
                    _resolutionDropdown.index = currentIndex >= 0 ? currentIndex : 0;
                }
            }

            // Load graphics settings
            LoadGraphicsSettings();

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
            if(_windowModeDropdown != null) _originalWindowMode = _windowModeDropdown.index;
            if(_aspectRatioDropdown != null) _originalAspectRatio = _aspectRatioDropdown.value;
            if(_resolutionDropdown != null) _originalResolutionIndex = _resolutionDropdown.index;
            if(_msaaDropdown != null) _originalMsaa = _msaaDropdown.index;
            if(_shadowDistanceSlider != null) _originalShadowDistance = _shadowDistanceSlider.value;
            if(_shadowResolutionDropdown != null) _originalShadowResolution = _shadowResolutionDropdown.index;
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

        private bool HasUnsavedChanges() {
            var hasKeybindChanges = KeybindManager.Instance != null && KeybindManager.Instance.HasPendingBindings();

            var volumeChanged = false;
            if(_masterVolumeSlider != null)
                volumeChanged |= !Mathf.Approximately(_masterVolumeSlider.value, _originalMasterVolume);
            if(_musicVolumeSlider != null)
                volumeChanged |= !Mathf.Approximately(_musicVolumeSlider.value, _originalMusicVolume);
            if(_sfxVolumeSlider != null)
                volumeChanged |= !Mathf.Approximately(_sfxVolumeSlider.value, _originalSfxVolume);

            var sensitivityChanged = false;
            if(_sensitivitySlider != null)
                sensitivityChanged = !Mathf.Approximately(_sensitivitySlider.value, _originalSensitivity);

            var invertYChanged = GetCheckboxValue(_invertYButton) != _originalInvertY;
            var playerTrailsChanged = GetCheckboxValue(_playerTrailsButton) != _originalPlayerTrails;
            var holdMantleChanged = GetCheckboxValue(_holdMantleButton) != _originalHoldMantle;

            var windowModeChanged = false;
            if(_windowModeDropdown != null) windowModeChanged = _windowModeDropdown.index != _originalWindowMode;

            var aspectRatioChanged = false;
            if(_aspectRatioDropdown != null) aspectRatioChanged = _aspectRatioDropdown.value != _originalAspectRatio;

            var resolutionChanged = false;
            if(_resolutionDropdown != null) resolutionChanged = _resolutionDropdown.index != _originalResolutionIndex;

            var msaaChanged = false;
            if(_msaaDropdown != null) msaaChanged = _msaaDropdown.index != _originalMsaa;

            var shadowDistanceChanged = false;
            if(_shadowDistanceSlider != null)
                shadowDistanceChanged = !Mathf.Approximately(_shadowDistanceSlider.value, _originalShadowDistance);

            var shadowResolutionChanged = false;
            if(_shadowResolutionDropdown != null)
                shadowResolutionChanged = _shadowResolutionDropdown.index != _originalShadowResolution;

            var vsyncChanged = GetCheckboxValue(_vsyncButton) != _originalVsync;

            var fpsChanged = false;
            if(_fpsDropdown != null) fpsChanged = _fpsDropdown.index != _originalTargetFPS;

            return volumeChanged || sensitivityChanged || invertYChanged || playerTrailsChanged || holdMantleChanged ||
                   windowModeChanged || aspectRatioChanged || resolutionChanged || msaaChanged ||
                   shadowDistanceChanged || shadowResolutionChanged || vsyncChanged || fpsChanged || hasKeybindChanges;
        }

        private void OnBackFromOptions() {
            // Cancel any active rebinding operations
            if(KeybindManager.Instance != null) {
                KeybindManager.Instance.CancelActiveRebinding();
            }

            // Reset keybind buttons
            LoadKeybindDisplayStrings();

            // Check for unsaved changes
            var hasUnsaved = HasUnsavedChanges();

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
            if(_unsavedChangesModal == null) return;
            _unsavedChangesModal.RemoveFromClassList("hidden");
            _unsavedChangesModal.BringToFront();
        }

        private void HideUnsavedChangesDialog() {
            _unsavedChangesModal?.AddToClassList("hidden");
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

        private void ApplySettings() {
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

            // Save window mode and resolution settings
            if(_windowModeDropdown != null) {
                PlayerPrefs.SetInt("WindowMode", _windowModeDropdown.index);
            }

            if(_aspectRatioDropdown != null) {
                PlayerPrefs.SetString("AspectRatio", _aspectRatioDropdown.value);
            }

            if(_resolutionDropdown is { index: >= 0 } &&
               _resolutionDropdown.index < _filteredResolutions.Count) {
                var selectedRes = _filteredResolutions[_resolutionDropdown.index];
                PlayerPrefs.SetInt("ResolutionWidth", selectedRes.Width);
                PlayerPrefs.SetInt("ResolutionHeight", selectedRes.Height);
            }

            // Save graphics settings
            if(_msaaDropdown != null) {
                // Map dropdown index to MSAA value: 0=1(Off), 1=2, 2=4, 3=8
                var msaaValue = _msaaDropdown.index switch {
                    0 => 1, // Off
                    1 => 2, // 2x
                    2 => 4, // 4x
                    3 => 8, // 8x
                    _ => 1 // Default to Off
                };
                PlayerPrefs.SetInt("MSAA", msaaValue);
            }

            if(_shadowDistanceSlider != null) {
                PlayerPrefs.SetFloat("ShadowDistance", _shadowDistanceSlider.value);
            }

            if(_shadowResolutionDropdown != null) {
                // Map preset index to resolution value: Low=512, Medium=1024, High=2048, Ultra=4096
                var resolutionValue = _shadowResolutionDropdown.index switch {
                    0 => 512, // Low
                    1 => 1024, // Medium
                    2 => 2048, // High
                    3 => 4096, // Ultra
                    _ => 2048 // Default to High (2048)
                };
                PlayerPrefs.SetInt("ShadowResolution", resolutionValue);
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
            if(_windowModeDropdown != null) _originalWindowMode = _windowModeDropdown.index;
            if(_aspectRatioDropdown != null) _originalAspectRatio = _aspectRatioDropdown.value;
            if(_resolutionDropdown != null) _originalResolutionIndex = _resolutionDropdown.index;
            if(_msaaDropdown != null) _originalMsaa = _msaaDropdown.index;
            if(_shadowDistanceSlider != null) _originalShadowDistance = _shadowDistanceSlider.value;
            if(_shadowResolutionDropdown != null) _originalShadowResolution = _shadowResolutionDropdown.index;
            _originalVsync = GetCheckboxValue(_vsyncButton);
            if(_fpsDropdown != null) _originalTargetFPS = _fpsDropdown.index;

            LoadKeybindDisplayStrings();
        }

        private void ApplySettingsInternal() {
            // Apply window mode and resolution
            ApplyWindowModeAndResolution();

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

            // Apply URP graphics settings
            ApplyUrpGraphicsSettings();

            QualitySettings.vSyncCount = GetCheckboxValue(_vsyncButton) ? 1 : 0;

            if(_fpsDropdown == null) return;
            
            Application.targetFrameRate = _fpsDropdown.index switch {
                0 => 30,
                1 => 60,
                2 => 120,
                3 => 144,
                4 => -1,
                _ => Application.targetFrameRate
            };
        }

        private void ApplyUrpGraphicsSettings() {
            var urpAsset = GetUrpAsset();
            if(urpAsset == null) {
                Debug.LogWarning("[OptionsMenuManager] URP Asset not found, cannot apply graphics settings");
                return;
            }

            // Apply MSAA
            if(_msaaDropdown != null) {
                var msaaValue = _msaaDropdown.index switch {
                    0 => 1, // Off
                    1 => 2, // 2x
                    2 => 4, // 4x
                    3 => 8, // 8x
                    _ => 1 // Default to Off
                };
                urpAsset.msaaSampleCount = msaaValue;
            }

            // Apply shadow distance
            if(_shadowDistanceSlider != null) {
                urpAsset.shadowDistance = _shadowDistanceSlider.value;
            }

            // Apply shadow resolution
            if(_shadowResolutionDropdown == null) return;
            // Map preset index to resolution value: Low=512, Medium=1024, High=2048, Ultra=4096
            var resolutionValue = _shadowResolutionDropdown.index switch {
                0 => 512, // Low
                1 => 1024, // Medium
                2 => 2048, // High
                3 => 4096, // Ultra
                _ => 2048 // Default to High (2048)
            };
            urpAsset.mainLightShadowmapResolution = resolutionValue;
        }

        private void ApplyWindowModeAndResolution() {
            if(_windowModeDropdown == null || _resolutionDropdown == null) return;
            if(_resolutionDropdown.index < 0 || _resolutionDropdown.index >= _filteredResolutions.Count) return;

            var selectedRes = _filteredResolutions[_resolutionDropdown.index];

            // Map dropdown index to FullScreenMode
            var fullScreenMode = _windowModeDropdown.index switch {
                0 => // Windowed
                    FullScreenMode.Windowed,
                1 => // Borderless Windowed
                    FullScreenMode.FullScreenWindow,
                2 => // Fullscreen
                    FullScreenMode.ExclusiveFullScreen,
                _ => FullScreenMode.FullScreenWindow
            };

            // Apply resolution and window mode
            Screen.SetResolution(selectedRes.Width, selectedRes.Height, fullScreenMode);
        }

        private static int GetCurrentWindowModeIndex() {
            return Screen.fullScreenMode switch {
                FullScreenMode.Windowed => 0,
                FullScreenMode.FullScreenWindow => 1,
                FullScreenMode.ExclusiveFullScreen => 2,
                _ => 1
            };
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

        private static void ToggleCheckbox(Button button) {
            if(button == null) return;
            var currentValue = GetCheckboxValue(button);
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

            // Force style recalculation
            optionsPanel?.schedule.Execute(() => {
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
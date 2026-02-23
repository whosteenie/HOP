using System;
using System.Collections.Generic;
using System.Linq;
using Game.Settings;
using Game.Social;
using Game.UI;
using Network.Singletons;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using UnityUtils;

namespace Game.Menu {
    /// <summary>
    /// Shared options menu manager that handles all options functionality.
    /// Can be used by both MainMenuManager and GameMenuManager.
    /// </summary>
    public class OptionsMenuManager : UIElementBase {
        [Header("References")]
        [SerializeField] private AudioMixer audioMixer;

        [Header("Callbacks")]
        [SerializeField] private bool useCallbacks = true;

        // Callbacks for button sounds and hover sounds (set by parent manager)
        public Action<bool> OnButtonClickedCallback;
        public Action<MouseEnterEvent> MouseEnterCallback;
        public Action<MouseOverEvent> MouseHoverCallback;

        // Callback for when back is pressed with no unsaved changes (set by parent manager)
        public Action OnBackFromOptionsCallback;

        // Static event for resolution changes (notifies all listeners when resolution is applied)
        public static event Action<int, int> OnResolutionChanged; // width, height

        #region UI Elements - Options
        private Slider _masterVolumeSlider;
        private Slider _musicVolumeSlider;
        private Slider _sfxVolumeSlider;
        private Slider _voiceVolumeSlider;
        private Slider _voiceInputVolumeSlider;
        private TextField _masterVolumeValue;
        private TextField _musicVolumeValue;
        private TextField _sfxVolumeValue;
        private TextField _voiceVolumeValue;
        private TextField _voiceInputVolumeValue;
        private Slider _sensitivitySlider;
        private TextField _sensitivityValue;
        private Button _invertYButton;
        private Button _playerTrailsButton;
        private Button _streamerModeButton;
        private Button _holdMantleButton;
        private Button _profanityFilterButton;
        private Button _autoWallRunButton;
        private DropdownField _mainMenuBackgroundDropdown;
        private DropdownField _grappleIndicatorDropdown;
        private DropdownField _voiceModeDropdown;
        private DropdownField _windowModeDropdown;
        private DropdownField _aspectRatioDropdown;
        private DropdownField _resolutionDropdown;
        private DropdownField _msaaDropdown;
        private Slider _shadowDistanceSlider;
        private TextField _shadowDistanceValue;
        private DropdownField _shadowResolutionDropdown;
        private Button _bloomButton;
        private Button _motionBlurButton;
        private Button _filmGrainButton;
        private Button _vignetteButton;
        private Button _vsyncButton;
        private DropdownField _fpsDropdown;
        private DropdownField _voiceDeviceDropdown;
        private MainMenuBackgroundRandomizer _mainMenuBackgroundRandomizer;

        // Options tabs
        private Button _tabVideo;
        private Button _tabAudio;
        private Button _tabGame;
        private Button _tabControls;
        private VisualElement _videoContent;
        private VisualElement _audioContent;
        private VisualElement _gameContent;
        private VisualElement _controlsContent;
        private ScrollView _optionsContentScroll;
        private Label _optionsDescriptionTitle;
        private Label _optionsDescriptionBody;
        private VisualElement _optionsDescriptionPanel;

        private static readonly Color OptionsTabHoverTextColor = new(240f / 255f, 240f / 255f, 240f / 255f, 1f);
        private static readonly Color OptionsTabHoverBorderColor = new(200f / 255f, 60f / 255f, 60f / 255f, 0.5f);
        private readonly Dictionary<string, (string Title, string Body)> _settingDescriptions = new() {
            ["window-mode-container"] = ("WINDOW MODE", "Choose how the game window is presented: windowed, borderless, or fullscreen."),
            ["aspect-ratio-container"] = ("ASPECT RATIO", "Sets the screen ratio. Match this to your monitor for the cleanest image framing."),
            ["resolution-container"] = ("RESOLUTION", "Higher resolution improves clarity but increases GPU load."),
            ["msaa-container"] = ("ANTI-ALIASING (MSAA)", "Smooths jagged edges. Higher values improve quality at a performance cost."),
            ["shadow-distance-container"] = ("SHADOW DISTANCE", "Controls how far dynamic shadows are rendered from the camera."),
            ["shadow-resolution-container"] = ("SHADOW RESOLUTION", "Increases shadow sharpness. Higher settings cost more GPU memory and performance."),
            ["bloom-container"] = ("BLOOM", "Adds glow around bright highlights. Disable for a cleaner image and lower post-processing cost."),
            ["motion-blur-container"] = ("MOTION BLUR", "Simulates camera/object motion streaking. Disable for a sharper image and reduced post-processing cost."),
            ["film-grain-container"] = ("FILM GRAIN", "Adds subtle film-like noise for texture. Disable for a cleaner image."),
            ["vignette-container"] = ("VIGNETTE", "Darkens screen edges to focus attention toward the center."),
            ["vsync-container"] = ("VSYNC", "Reduces tearing by syncing frame output with monitor refresh rate."),
            ["target-fps-container"] = ("TARGET FPS", "Caps framerate to reduce power use and stabilize frame pacing."),
            ["master-container"] = ("MASTER VOLUME", "Global volume level for all game audio."),
            ["music-container"] = ("MUSIC VOLUME", "Controls soundtrack and ambient music loudness."),
            ["sfx-container"] = ("SFX VOLUME", "Controls gameplay sound effects such as weapons, impacts, and movement."),
            ["voice-volume-container"] = ("VOICE CHAT VOLUME", "Adjusts how loud incoming voice chat sounds."),
            ["voice-input-volume-container"] = ("MICROPHONE VOLUME", "Adjusts outgoing microphone gain for voice chat."),
            ["voice-device-container"] = ("MICROPHONE", "Select which input device is used for voice chat."),
            ["player-trails-container"] = ("PLAYER SPEED TRAILS", "Toggles speed trail visual effects on players."),
            ["main-menu-background-container"] = ("MAIN MENU BACKGROUND", "Select which mannequin background scene is shown in the main menu. Random picks one each menu entry."),
            ["streamer-mode-container"] = ("STREAMER MODE", "Hides your display name in supported UI surfaces."),
            ["hold-mantle-container"] = ("HOLD TO MANTLE", "When enabled, mantling can trigger while jump is held."),
            ["auto-wall-run-container"] = ("AUTO WALL RUN", "Automatically starts a wall run when valid wall-run conditions are met."),
            ["grapple-indicator-container"] = ("GRAPPLE INDICATOR TYPE", "Choose where grapple readiness/aim feedback is shown."),
            ["profanity-filter-container"] = ("CHAT PROFANITY FILTER", "Locally filters text chat according to your preference."),
            ["voice-mode-container"] = ("VOICE INPUT MODE", "Select voice activation mode: push-to-talk or open mic."),
            ["sensitivity-container"] = ("MOUSE SENSITIVITY", "Controls horizontal and vertical look sensitivity."),
            ["invert-y-container"] = ("INVERT Y AXIS", "Inverts vertical look input for mouse movement.")
        };

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
        private float _originalVoiceVolume;
        private float _originalSensitivity;
        private bool _originalInvertY;
        private bool _originalPlayerTrails;
        private bool _originalStreamerMode;
        private bool _originalHoldMantle;
        private bool _originalProfanityFilter;
        private bool _originalAutoWallRun;
        private int _originalGrappleIndicator;
        private int _originalVoiceMode;
        private string _originalMainMenuBackgroundSelection = MainMenuBackgroundRandomizer.RandomSelectionOption;
        private int _originalWindowMode;
        private string _originalAspectRatio;
        private int _originalResolutionIndex;
        private int _originalMsaa;
        private float _originalShadowDistance;
        private int _originalShadowResolution;
        private bool _originalBloom;
        private bool _originalMotionBlur;
        private bool _originalFilmGrain;
        private bool _originalVignette;
        private bool _originalVsync;
        private int _originalTargetFPS;
        private string _originalVoiceDevice;

        #endregion

        #region Unity Lifecycle

        protected override void Awake() {
            base.Awake();
            if(uiDocument == null) {
                uiDocument = GetComponent<UIDocument>();
            }
        }

        public new void Initialize() {
            base.Initialize();
        }

        protected override void OnInitialize() {
            FindUIElements();
            BindDropdownOpenStateClasses();
            SetupDropdownTextFormatting();
            SetupCallbacks();
            SetupOptionsTabs();
            SetupKeybinds();
            SetupSettingDescriptions();
        }

        protected override Dictionary<string, Type> GetRequiredElements() {
            return new Dictionary<string, Type> {
                { "apply-button", typeof(Button) },
                { "back-button", typeof(Button) }
            };
        }

        #endregion

        #region Setup

        private void FindUIElements() {
            // Audio controls
            _masterVolumeSlider = QOptional<Slider>("master-volume");
            _musicVolumeSlider = QOptional<Slider>("music-volume");
            _sfxVolumeSlider = QOptional<Slider>("sfx-volume");
            _masterVolumeValue = QOptional<TextField>("master-volume-value");
            _musicVolumeValue = QOptional<TextField>("music-volume-value");
            _sfxVolumeValue = QOptional<TextField>("sfx-volume-value");
            _voiceVolumeSlider = QOptional<Slider>("voice-volume");
            _voiceVolumeValue = QOptional<TextField>("voice-volume-value");
            _voiceInputVolumeSlider = QOptional<Slider>("voice-input-volume");
            _voiceInputVolumeValue = QOptional<TextField>("voice-input-volume-value");

            // Sensitivity controls
            _sensitivitySlider = QOptional<Slider>("sensitivity");
            _sensitivityValue = QOptional<TextField>("sensitivity-value");
            _invertYButton = QOptional<Button>("invert-y");
            _playerTrailsButton = QOptional<Button>("player-trails");
            _streamerModeButton = QOptional<Button>("streamer-mode");
            _holdMantleButton = QOptional<Button>("hold-mantle");
            _profanityFilterButton = QOptional<Button>("profanity-filter");
            _autoWallRunButton = QOptional<Button>("auto-wall-run");
            _mainMenuBackgroundDropdown = QOptional<DropdownField>("main-menu-background");
            _grappleIndicatorDropdown = QOptional<DropdownField>("grapple-indicator");
            _voiceModeDropdown = QOptional<DropdownField>("voice-mode");
            _voiceDeviceDropdown = QOptional<DropdownField>("voice-device");

            // Graphics controls
            _windowModeDropdown = QOptional<DropdownField>("window-mode");
            _aspectRatioDropdown = QOptional<DropdownField>("aspect-ratio");
            _resolutionDropdown = QOptional<DropdownField>("resolution");
            _msaaDropdown = QOptional<DropdownField>("msaa");
            _shadowDistanceSlider = QOptional<Slider>("shadow-distance");
            _shadowDistanceValue = QOptional<TextField>("shadow-distance-value");
            _shadowResolutionDropdown = QOptional<DropdownField>("shadow-resolution");
            _bloomButton = QOptional<Button>("bloom");
            _motionBlurButton = QOptional<Button>("motion-blur");
            _filmGrainButton = QOptional<Button>("film-grain");
            _vignetteButton = QOptional<Button>("vignette");
            _vsyncButton = QOptional<Button>("vsync");
            _fpsDropdown = QOptional<DropdownField>("target-fps");

            // Options tabs
            _tabVideo = QOptional<Button>("tab-video");
            _tabAudio = QOptional<Button>("tab-audio");
            _tabGame = QOptional<Button>("tab-game");
            _tabControls = QOptional<Button>("tab-controls");
            _optionsContentScroll = QOptional<ScrollView>("options-content-scroll");
            _videoContent = QOptional<VisualElement>("video-content");
            _audioContent = QOptional<VisualElement>("audio-content");
            _gameContent = QOptional<VisualElement>("game-content");
            _controlsContent = QOptional<VisualElement>("controls-content");
            _optionsDescriptionPanel = QOptional<VisualElement>("options-description-panel");
            _optionsDescriptionTitle = QOptional<Label>("options-description-title");
            _optionsDescriptionBody = QOptional<Label>("options-description-body");

            // Unsaved changes dialog
            _unsavedChangesModal = QOptional<VisualElement>("unsaved-changes-modal");
            _unsavedChangesYes = QOptional<Button>("unsaved-changes-yes");
            _unsavedChangesNo = QOptional<Button>("unsaved-changes-no");
            _unsavedChangesCancel = QOptional<Button>("unsaved-changes-cancel");

            // Apply and back buttons
            _applyButton = QRequired<Button>("apply-button");
            _backButton = QRequired<Button>("back-button");

            // Setup checkbox click handlers
            if(_invertYButton != null) {
                EventCallback<ClickEvent> handler = _ => ToggleCheckbox(_invertYButton);
                _invertYButton.RegisterCallback(handler);
                RegisterCleanup(() => _invertYButton.UnregisterCallback(handler));
            }
            if(_playerTrailsButton != null) {
                EventCallback<ClickEvent> handler = _ => ToggleCheckbox(_playerTrailsButton);
                _playerTrailsButton.RegisterCallback(handler);
                RegisterCleanup(() => _playerTrailsButton.UnregisterCallback(handler));
            }
            if(_streamerModeButton != null) {
                EventCallback<ClickEvent> handler = _ => ToggleCheckbox(_streamerModeButton);
                _streamerModeButton.RegisterCallback(handler);
                RegisterCleanup(() => _streamerModeButton.UnregisterCallback(handler));
            }
            if(_holdMantleButton != null) {
                EventCallback<ClickEvent> handler = _ => ToggleCheckbox(_holdMantleButton);
                _holdMantleButton.RegisterCallback(handler);
                RegisterCleanup(() => _holdMantleButton.UnregisterCallback(handler));
            }
            if(_profanityFilterButton != null) {
                EventCallback<ClickEvent> handler = _ => ToggleCheckbox(_profanityFilterButton);
                _profanityFilterButton.RegisterCallback(handler);
                RegisterCleanup(() => _profanityFilterButton.UnregisterCallback(handler));
            }
            if(_autoWallRunButton != null) {
                EventCallback<ClickEvent> handler = _ => ToggleCheckbox(_autoWallRunButton);
                _autoWallRunButton.RegisterCallback(handler);
                RegisterCleanup(() => _autoWallRunButton.UnregisterCallback(handler));
            }
            if(_vsyncButton != null) {
                EventCallback<ClickEvent> handler = _ => ToggleCheckbox(_vsyncButton);
                _vsyncButton.RegisterCallback(handler);
                RegisterCleanup(() => _vsyncButton.UnregisterCallback(handler));
            }
            if(_bloomButton != null) {
                EventCallback<ClickEvent> handler = _ => ToggleCheckbox(_bloomButton);
                _bloomButton.RegisterCallback(handler);
                RegisterCleanup(() => _bloomButton.UnregisterCallback(handler));
            }
            if(_motionBlurButton != null) {
                EventCallback<ClickEvent> handler = _ => ToggleCheckbox(_motionBlurButton);
                _motionBlurButton.RegisterCallback(handler);
                RegisterCleanup(() => _motionBlurButton.UnregisterCallback(handler));
            }
            if(_filmGrainButton != null) {
                EventCallback<ClickEvent> handler = _ => ToggleCheckbox(_filmGrainButton);
                _filmGrainButton.RegisterCallback(handler);
                RegisterCleanup(() => _filmGrainButton.UnregisterCallback(handler));
            }

            if(_vignetteButton == null) return;
            {
                EventCallback<ClickEvent> handler = _ => ToggleCheckbox(_vignetteButton);
                _vignetteButton.RegisterCallback(handler);
                RegisterCleanup(() => _vignetteButton.UnregisterCallback(handler));
            }
        }

        private void BindDropdownOpenStateClasses() {
            if(Root == null) {
                return;
            }

            foreach(var dropdown in Root.Query<DropdownField>().ToList()) {
                var cleanup = DropdownOpenStateBinder.Bind(dropdown);
                if(cleanup != null) {
                    RegisterCleanup(cleanup);
                }
            }
        }

        private void SetupDropdownTextFormatting() {
            if(Root == null) {
                return;
            }

            foreach(var dropdown in Root.Query<DropdownField>().ToList()) {
                if(dropdown == null) {
                    continue;
                }

                dropdown.formatSelectedValueCallback = value =>
                    string.IsNullOrWhiteSpace(value) ? string.Empty : value.ToUpperInvariant();

                dropdown.formatListItemCallback = value =>
                    string.IsNullOrWhiteSpace(value) ? string.Empty : value.ToUpperInvariant();
            }
        }

        private void SetupCallbacks() {
            SetupAudioCallbacks();
            SetupControlsCallbacks();
            SetupGraphicsCallbacks();
            SetupGameCallbacks();

            // Setup apply and back buttons
            EventCallback<ClickEvent> applyHandler = _ => {
                OnButtonClicked();
                ApplySettings();
            };
            _applyButton.RegisterCallback(applyHandler);
            RegisterCleanup(() => _applyButton.UnregisterCallback(applyHandler));
            if(useCallbacks) {
                RegisterHoverCallback(_applyButton);
            }

            EventCallback<ClickEvent> backHandler = _ => {
                OnButtonClicked(true);
                OnBackFromOptions();
            };
            _backButton.RegisterCallback(backHandler);
            RegisterCleanup(() => _backButton.UnregisterCallback(backHandler));
            if(useCallbacks) {
                RegisterHoverCallback(_backButton);
            }

            // Setup unsaved changes dialog buttons
            if(_unsavedChangesYes != null) {
                EventCallback<ClickEvent> yesHandler = evt => {
                    evt.StopPropagation();
                    evt.StopImmediatePropagation();
                    OnUnsavedChangesYes();
                };
                _unsavedChangesYes.RegisterCallback(yesHandler);
                RegisterCleanup(() => _unsavedChangesYes.UnregisterCallback(yesHandler));
                if(useCallbacks) {
                    RegisterHoverCallback(_unsavedChangesYes);
                }
            }

            if(_unsavedChangesNo != null) {
                EventCallback<ClickEvent> noHandler = evt => {
                    evt.StopPropagation();
                    evt.StopImmediatePropagation();
                    OnUnsavedChangesNo();
                };
                _unsavedChangesNo.RegisterCallback(noHandler);
                RegisterCleanup(() => _unsavedChangesNo.UnregisterCallback(noHandler));
                if(useCallbacks) {
                    RegisterHoverCallback(_unsavedChangesNo);
                }
            }

            if(_unsavedChangesCancel == null) return;
            {
                EventCallback<ClickEvent> cancelHandler = evt => {
                    evt.StopPropagation();
                    evt.StopImmediatePropagation();
                    OnUnsavedChangesCancel();
                };
                _unsavedChangesCancel.RegisterCallback(cancelHandler);
                RegisterCleanup(() => _unsavedChangesCancel.UnregisterCallback(cancelHandler));
                if(useCallbacks) {
                    RegisterHoverCallback(_unsavedChangesCancel);
                }
            }
        }

        private void RegisterHoverCallback(Button button) {
            if(MouseEnterCallback != null) {
                EventCallback<MouseEnterEvent> enterHandler = evt => MouseEnterCallback(evt);
                button.RegisterCallback(enterHandler);
                RegisterCleanup(() => button.UnregisterCallback(enterHandler));
            }

            if(MouseHoverCallback == null) return;
            {
                EventCallback<MouseOverEvent> hoverHandler = evt => MouseHoverCallback(evt);
                button.RegisterCallback(hoverHandler);
                RegisterCleanup(() => button.UnregisterCallback(hoverHandler));
            }
        }

        private void SetupAudioCallbacks() {
            // Update text fields when sliders change (with % sign for volumes)
            if(_masterVolumeSlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => {
                    if(_masterVolumeValue != null) {
                        _masterVolumeValue.value = Mathf.RoundToInt(evt.newValue * 100) + "%";
                    }
                };
                _masterVolumeSlider.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _masterVolumeSlider.UnregisterCallback(handler));
            }
            if(_musicVolumeSlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => {
                    if(_musicVolumeValue != null) {
                        _musicVolumeValue.value = Mathf.RoundToInt(evt.newValue * 100) + "%";
                    }
                };
                _musicVolumeSlider.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _musicVolumeSlider.UnregisterCallback(handler));
            }
            if(_sfxVolumeSlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => {
                    if(_sfxVolumeValue != null) {
                        _sfxVolumeValue.value = Mathf.RoundToInt(evt.newValue * 100) + "%";
                    }
                };
                _sfxVolumeSlider.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _sfxVolumeSlider.UnregisterCallback(handler));
            }
            if(_voiceVolumeSlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => {
                    if(_voiceVolumeValue != null) {
                        _voiceVolumeValue.value = Mathf.RoundToInt(evt.newValue * 100) + "%";
                    }
                };
                _voiceVolumeSlider.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _voiceVolumeSlider.UnregisterCallback(handler));
            }
            if(_voiceInputVolumeSlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => {
                    if(_voiceInputVolumeValue != null) {
                        _voiceInputVolumeValue.value = Mathf.RoundToInt(evt.newValue * 100) + "%";
                    }
                };
                _voiceInputVolumeSlider.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _voiceInputVolumeSlider.UnregisterCallback(handler));
            }
            
            RefreshVoiceDeviceDropdownChoices();
            RefreshVoiceDeviceDropdownChoicesDeferred();

            // Setup text field input validation and callbacks
            SetupVolumeInputField(_masterVolumeSlider, _masterVolumeValue, 0f, 1f, true);
            SetupVolumeInputField(_musicVolumeSlider, _musicVolumeValue, 0f, 1f, true);
            SetupVolumeInputField(_sfxVolumeSlider, _sfxVolumeValue, 0f, 1f, true);
        }

        private void SetupControlsCallbacks() {
            // Update text field when slider changes
            if(_sensitivitySlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => {
                    if(_sensitivityValue != null) {
                        _sensitivityValue.value = evt.newValue.ToString("F2");
                    }
                };
                _sensitivitySlider.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _sensitivitySlider.UnregisterCallback(handler));
            }

            // Setup sensitivity input field (with fixed width class)
            _sensitivityValue?.AddToClassList("sensitivity-input");

            SetupVolumeInputField(_sensitivitySlider, _sensitivityValue, 0.01f, 0.5f, false);
        }

        private void RefreshVoiceDeviceDropdownChoices(string preferredDevice = null) {
            if(_voiceDeviceDropdown == null) {
                return;
            }

            var devices = VoiceManager.Instance != null
                ? VoiceManager.GetAvailableInputDevices()
                : new List<string>();

            if(devices == null || devices.Count == 0) {
                devices = new List<string> { "Default" };
            }

            _voiceDeviceDropdown.choices = devices;

            var targetDevice = string.IsNullOrWhiteSpace(preferredDevice)
                ? SocialSettings.InputDevice
                : preferredDevice;

            var selectedIndex = string.IsNullOrWhiteSpace(targetDevice)
                ? -1
                : devices.IndexOf(targetDevice);

            if(selectedIndex < 0) {
                selectedIndex = 0;
            }

            _voiceDeviceDropdown.index = selectedIndex;
        }

        private void RefreshVoiceDeviceDropdownChoicesDeferred() {
            if(Root == null) {
                return;
            }

            // Device enumeration can lag behind panel open by a frame or two.
            Root.schedule.Execute(() => RefreshVoiceDeviceDropdownChoices()).StartingIn(200);
            Root.schedule.Execute(() => RefreshVoiceDeviceDropdownChoices()).StartingIn(700);
            Root.schedule.Execute(() => RefreshVoiceDeviceDropdownChoices()).StartingIn(1500);
        }
        
        private void SetupGameCallbacks() {
            SetupMainMenuBackgroundDropdown();

            // Setup grapple indicator dropdown
            if(_grappleIndicatorDropdown != null) {
                _grappleIndicatorDropdown.choices = new List<string> {
                    "Crosshair",
                    "Bottom",
                    "None"
                };
            }
        }

        private void SetupMainMenuBackgroundDropdown() {
            if(_mainMenuBackgroundDropdown == null) {
                return;
            }

            RefreshMainMenuBackgroundDropdownChoices(preserveCurrentSelection: false);

            EventCallback<ChangeEvent<string>> handler = evt => {
                var normalizedSelection = NormalizeMainMenuBackgroundSelection(evt.newValue);
                if(!string.Equals(_mainMenuBackgroundDropdown.value, normalizedSelection, StringComparison.Ordinal)) {
                    _mainMenuBackgroundDropdown.SetValueWithoutNotify(normalizedSelection);
                }

                ApplyMainMenuBackgroundSelectionPreview(normalizedSelection);
            };

            _mainMenuBackgroundDropdown.RegisterValueChangedCallback(handler);
            RegisterCleanup(() => _mainMenuBackgroundDropdown.UnregisterCallback(handler));
        }

        private void RefreshMainMenuBackgroundDropdownChoices(bool preserveCurrentSelection) {
            if(_mainMenuBackgroundDropdown == null) {
                return;
            }

            var previousSelection = preserveCurrentSelection
                ? _mainMenuBackgroundDropdown.value
                : null;

            var choices = new List<string> {
                MainMenuBackgroundRandomizer.RandomSelectionOption
            };

            var randomizer = ResolveMainMenuBackgroundRandomizer();
            if(randomizer != null) {
                var availableSelections = randomizer.GetAvailableSelectionNames();
                foreach(var name in availableSelections) {
                    if(string.IsNullOrWhiteSpace(name) || choices.Contains(name)) {
                        continue;
                    }

                    choices.Add(name);
                }
            } else {
                var persistedSelection = NormalizeMainMenuBackgroundSelection(GameSettings.Data.video?.mainMenuBackgroundSelection);
                if(!MainMenuBackgroundRandomizer.IsRandomSelection(persistedSelection) && !choices.Contains(persistedSelection)) {
                    choices.Add(persistedSelection);
                }

                if(!string.IsNullOrWhiteSpace(previousSelection) &&
                   !MainMenuBackgroundRandomizer.IsRandomSelection(previousSelection) &&
                   !choices.Contains(previousSelection)) {
                    choices.Add(previousSelection);
                }
            }

            _mainMenuBackgroundDropdown.choices = choices;

            var selectionToSet = previousSelection;
            if(string.IsNullOrWhiteSpace(selectionToSet)) {
                selectionToSet = GameSettings.Data.video?.mainMenuBackgroundSelection;
            }

            selectionToSet = NormalizeMainMenuBackgroundSelection(selectionToSet);
            if(!choices.Contains(selectionToSet)) {
                selectionToSet = MainMenuBackgroundRandomizer.RandomSelectionOption;
            }

            _mainMenuBackgroundDropdown.SetValueWithoutNotify(selectionToSet);
            _mainMenuBackgroundDropdown.SetEnabled(randomizer != null && choices.Count > 1);
        }

        private MainMenuBackgroundRandomizer ResolveMainMenuBackgroundRandomizer() {
            if(_mainMenuBackgroundRandomizer != null) {
                return _mainMenuBackgroundRandomizer;
            }

            _mainMenuBackgroundRandomizer = GetComponentInParent<MainMenuBackgroundRandomizer>();
            if(_mainMenuBackgroundRandomizer == null) {
                _mainMenuBackgroundRandomizer = FindFirstObjectByType<MainMenuBackgroundRandomizer>();
            }

            return _mainMenuBackgroundRandomizer;
        }

        private static string NormalizeMainMenuBackgroundSelection(string selection) {
            return MainMenuBackgroundRandomizer.IsRandomSelection(selection)
                ? MainMenuBackgroundRandomizer.RandomSelectionOption
                : selection;
        }

        private void ApplyMainMenuBackgroundSelectionPreview(string selection) {
            var randomizer = ResolveMainMenuBackgroundRandomizer();
            if(randomizer == null) {
                return;
            }

            randomizer.ApplySelectionForMainMenuEntry(selection);
        }

        private void SetupVolumeInputField(Slider slider, TextField textField, float minValue, float maxValue,
            bool isPercentage) {
            if(slider == null || textField == null) return;

            // Set max length (3 for percentage to allow "100%", 5 for sensitivity to allow decimals)
            textField.maxLength = isPercentage ? 4 : 5; // 4 to allow "100%"
            textField.isDelayed = false;

            // Filter input in real-time using ValueChanged callback (like join code input)
            EventCallback<ChangeEvent<string>> valueChangedHandler = evt => {
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
            };
            textField.RegisterValueChangedCallback(valueChangedHandler);
            RegisterCleanup(() => textField.UnregisterCallback(valueChangedHandler));

            // Handle value change on Enter or focus loss
            EventCallback<KeyDownEvent> keyDownHandler = evt => {
                if(evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
                ApplyTextFieldValue(slider, textField, minValue, maxValue, isPercentage);
                textField.Blur(); // Remove focus
            };
            textField.RegisterCallback(keyDownHandler);
            RegisterCleanup(() => textField.UnregisterCallback(keyDownHandler));

            EventCallback<BlurEvent> blurHandler = _ => {
                ApplyTextFieldValue(slider, textField, minValue, maxValue, isPercentage);
            };
            textField.RegisterCallback(blurHandler);
            RegisterCleanup(() => textField.UnregisterCallback(blurHandler));
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
            EventCallback<ChangeEvent<float>> sliderHandler = evt => {
                if(_shadowDistanceValue != null) {
                    _shadowDistanceValue.value = Mathf.RoundToInt(evt.newValue).ToString();
                }
            };
            _shadowDistanceSlider.RegisterValueChangedCallback(sliderHandler);
            RegisterCleanup(() => _shadowDistanceSlider.UnregisterCallback(sliderHandler));

            // Setup input field validation
            SetupShadowDistanceInputField();
        }

        private void SetupShadowDistanceInputField() {
            if(_shadowDistanceSlider == null || _shadowDistanceValue == null) return;

            _shadowDistanceValue.maxLength = 6;
            _shadowDistanceValue.isDelayed = false;

            // Filter input to only allow digits
            EventCallback<ChangeEvent<string>> valueChangedHandler = evt => {
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
            };
            _shadowDistanceValue.RegisterValueChangedCallback(valueChangedHandler);
            RegisterCleanup(() => _shadowDistanceValue.UnregisterCallback(valueChangedHandler));

            // Handle value change on Enter or focus loss
            if(_shadowDistanceValue == null) return;
            {
                EventCallback<KeyDownEvent> keyDownHandler = evt => {
                    if(evt.keyCode is not (KeyCode.Return or KeyCode.KeypadEnter)) return;
                    ApplyShadowDistanceTextFieldValue();
                    _shadowDistanceValue.Blur();
                };
                _shadowDistanceValue.RegisterCallback(keyDownHandler);
                RegisterCleanup(() => _shadowDistanceValue.UnregisterCallback(keyDownHandler));

                EventCallback<BlurEvent> blurHandler = _ => { ApplyShadowDistanceTextFieldValue(); };
                _shadowDistanceValue.RegisterCallback(blurHandler);
                RegisterCleanup(() => _shadowDistanceValue.UnregisterCallback(blurHandler));
            }
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
            if(_aspectRatioDropdown == null) return;
            EventCallback<ChangeEvent<string>> aspectRatioHandler = evt => { FilterResolutionsByAspectRatio(evt.newValue); };
            _aspectRatioDropdown.RegisterValueChangedCallback(aspectRatioHandler);
            RegisterCleanup(() => _aspectRatioDropdown.UnregisterCallback(aspectRatioHandler));
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
            if(_optionsContentScroll != null) {
                _optionsContentScroll.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
                _optionsContentScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            }

            // Setup tab click handlers - sounds play here on actual user clicks
            if(_tabVideo != null) {
                EventCallback<ClickEvent> handler = _ => {
                    OnButtonClicked();
                    SwitchOptionsTab("video");
                };
                _tabVideo.RegisterCallback(handler);
                RegisterCleanup(() => _tabVideo.UnregisterCallback(handler));
            }
            if(_tabAudio != null) {
                EventCallback<ClickEvent> handler = _ => {
                    OnButtonClicked();
                    SwitchOptionsTab("audio");
                };
                _tabAudio.RegisterCallback(handler);
                RegisterCleanup(() => _tabAudio.UnregisterCallback(handler));
            }
            if(_tabGame != null) {
                EventCallback<ClickEvent> handler = _ => {
                    OnButtonClicked();
                    SwitchOptionsTab("game");
                };
                _tabGame.RegisterCallback(handler);
                RegisterCleanup(() => _tabGame.UnregisterCallback(handler));
            }
            if(_tabControls != null) {
                EventCallback<ClickEvent> handler = _ => {
                    OnButtonClicked();
                    SwitchOptionsTab("controls");
                };
                _tabControls.RegisterCallback(handler);
                RegisterCleanup(() => _tabControls.UnregisterCallback(handler));
            }

            // Register hover callbacks for tabs
            SetupTabHoverCallbacks(_tabVideo);
            SetupTabHoverCallbacks(_tabAudio);
            SetupTabHoverCallbacks(_tabGame);
            SetupTabHoverCallbacks(_tabControls);

            // Start with Video tab active
            SwitchOptionsTab("video");
        }

        private void SetupSettingDescriptions() {
            if(Root == null || _optionsDescriptionPanel == null || _optionsDescriptionTitle == null || _optionsDescriptionBody == null) return;

            var rows = Root.Query<VisualElement>(className: "setting-row").ToList();
            foreach(var row in rows) {
                if(row == null) continue;

                EventCallback<PointerEnterEvent> pointerEnter = _ => SetDescriptionForRow(row);
                row.RegisterCallback(pointerEnter);
                RegisterCleanup(() => row.UnregisterCallback(pointerEnter));

                var controls = row.Query<VisualElement>().ToList();
                foreach(var control in controls) {
                    EventCallback<FocusInEvent> focusIn = _ => SetDescriptionForRow(row);
                    control.RegisterCallback(focusIn);
                    RegisterCleanup(() => control.UnregisterCallback(focusIn));
                }
            }

            SetDefaultDescriptionForTab("video");
        }

        private void SetDefaultDescriptionForTab(string tabName) {
            var tabContent = tabName.ToLowerInvariant() switch {
                "video" => _videoContent,
                "audio" => _audioContent,
                "game" => _gameContent,
                "controls" => _controlsContent,
                _ => null
            };

            if(tabContent == null) return;
            var rows = tabContent.Query<VisualElement>(className: "setting-row").ToList();
            var firstRow = rows.Count > 0 ? rows[0] : null;
            if(firstRow == null) {
                SetDescription("SETTING DETAILS", "Hover or select a setting to see what it changes.");
                return;
            }

            SetDescriptionForRow(firstRow);
        }

        private void SetDescriptionForRow(VisualElement row) {
            if(_optionsDescriptionTitle == null || _optionsDescriptionBody == null || row == null) return;
            var key = row.name ?? string.Empty;

            if(_settingDescriptions.TryGetValue(key, out var description)) {
                SetDescription(description.Title, description.Body);
                return;
            }

            var label = row.Q<Label>(className: "setting-label");
            var labelText = label != null && string.IsNullOrWhiteSpace(label.text) == false
                ? label.text
                : "SETTING";

            if(key.StartsWith("keybind-", StringComparison.OrdinalIgnoreCase)) {
                SetDescription(
                    $"KEYBIND: {labelText}",
                    "Assign primary and secondary keys for this action. Secondary binds are useful for alternates like mouse wheel or extra keys.");
                return;
            }

            SetDescription(labelText, "Adjust this setting to tune gameplay, visuals, audio, or controls.");
        }

        private void SetDescription(string title, string body) {
            if(_optionsDescriptionTitle != null) {
                _optionsDescriptionTitle.text = string.IsNullOrWhiteSpace(title) ? "SETTING DETAILS" : title;
            }

            if(_optionsDescriptionBody != null) {
                _optionsDescriptionBody.text = string.IsNullOrWhiteSpace(body)
                    ? "Hover or select a setting to see what it changes."
                    : body;
            }
        }

        private void SetupTabHoverCallbacks(Button tab) {
            if(tab == null) return;

            // Hover sounds only for non-active tabs (active tab is effectively a no-op click).
            if(MouseEnterCallback != null) {
                EventCallback<MouseEnterEvent> enterHandler = evt => {
                    if(tab.ClassListContains("options-tab-active")) return;
                    MouseEnterCallback(evt);
                };
                tab.RegisterCallback(enterHandler);
                RegisterCleanup(() => tab.UnregisterCallback(enterHandler));
            }

            EventCallback<MouseOverEvent> overHandler = evt => {
                if(MouseHoverCallback != null && !tab.ClassListContains("options-tab-active")) {
                    MouseHoverCallback(evt);
                }

                if(!tab.ClassListContains("options-tab-active") && tab.ClassListContains("options-tab-hover")) {
                    tab.MarkDirtyRepaint();
                }
            };
            tab.RegisterCallback(overHandler);
            RegisterCleanup(() => tab.UnregisterCallback(overHandler));

            // Visual hover state: Pointer enter/leave + inline style override (paint quirk workaround).
            EventCallback<PointerEnterEvent> pointerEnterHandler = _ => {
                if(tab.ClassListContains("options-tab-active")) return;
                if(!tab.ClassListContains("options-tab-hover")) {
                    tab.AddToClassList("options-tab-hover");
                }
                tab.style.color = new StyleColor(OptionsTabHoverTextColor);
                tab.style.borderBottomColor = new StyleColor(OptionsTabHoverBorderColor);
                tab.MarkDirtyRepaint();
            };
            tab.RegisterCallback(pointerEnterHandler);
            RegisterCleanup(() => tab.UnregisterCallback(pointerEnterHandler));

            EventCallback<PointerLeaveEvent> pointerLeaveHandler = _ => {
                tab.RemoveFromClassList("options-tab-hover");
                tab.style.color = StyleKeyword.Null;
                tab.style.borderBottomColor = StyleKeyword.Null;
                tab.MarkDirtyRepaint();
            };
            tab.RegisterCallback(pointerLeaveHandler);
            RegisterCleanup(() => tab.UnregisterCallback(pointerLeaveHandler));
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

            // Clear inline hover overrides (paint quirk workaround)
            if(_tabVideo != null) {
                _tabVideo.style.color = StyleKeyword.Null;
                _tabVideo.style.borderBottomColor = StyleKeyword.Null;
            }
            if(_tabAudio != null) {
                _tabAudio.style.color = StyleKeyword.Null;
                _tabAudio.style.borderBottomColor = StyleKeyword.Null;
            }
            if(_tabGame != null) {
                _tabGame.style.color = StyleKeyword.Null;
                _tabGame.style.borderBottomColor = StyleKeyword.Null;
            }
            if(_tabControls != null) {
                _tabControls.style.color = StyleKeyword.Null;
                _tabControls.style.borderBottomColor = StyleKeyword.Null;
            }

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
            SetDefaultDescriptionForTab(tabName);
        }

        private void SetupKeybinds() {
            if(KeybindManager.Instance == null) {
                Debug.LogWarning("[OptionsMenuManager] KeybindManager not found, keybinds will not work");
                return;
            }

            var keybindNames = new[] {
                "forward", "back", "left", "right", "jump", "interact", "shoot", "ads", "reload", "grapple", "primary",
                "secondary",
                "nextweapon", "previousweapon", "ptt"
            };

            foreach(var keybindName in keybindNames) {
                var buttons = new Button[2];
                buttons[0] = QOptional<Button>($"keybind-{keybindName}-0");
                buttons[1] = QOptional<Button>($"keybind-{keybindName}-1");

                if(buttons[0] == null || buttons[1] == null) continue;
                _keybindButtons[keybindName] = buttons;

                for(var i = 0; i < 2; i++) {
                    var index = i;
                    var button = buttons[i]; // Capture button reference directly
                    EventCallback<ClickEvent> handler = _ => OnKeybindButtonClicked(keybindName, index);
                    button.RegisterCallback(handler);
                    RegisterCleanup(() => {
                        button.UnregisterCallback(handler);
                    });
                }
            }

            LoadKeybindDisplayStrings();
        }

        private void OnKeybindButtonClicked(string keybindName, int bindingIndex) {
            if(KeybindManager.Instance == null) return;

            var button = _keybindButtons[keybindName][bindingIndex];
            button.text = "Press key...";
            button.SetEnabled(false);

            KeybindManager.Instance.StartRebinding(keybindName, bindingIndex, displayString => {
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
            var data = GameSettings.Data;

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
                var savedMsaa = data.video is { msaa: > 0 } ? data.video.msaa : currentMsaa;
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
                var savedShadowDistance = data.video is { shadowDistance: > 0f }
                    ? data.video.shadowDistance
                    : currentShadowDistance;
                _shadowDistanceSlider.value = Mathf.Clamp(savedShadowDistance, 0f, 500f);
                if(_shadowDistanceValue != null) {
                    _shadowDistanceValue.value = Mathf.RoundToInt(_shadowDistanceSlider.value).ToString();
                }
            }

            // Load shadow resolution
            if(_shadowResolutionDropdown == null) return;
            var savedShadowResolution = data.video is { shadowResolution: > 0 }
                ? data.video.shadowResolution
                : currentShadowResolution;
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
            var data = GameSettings.Data;

            // Load audio settings
            var masterDb = data.audio != null ? data.audio.masterVolumeDb : 0f;
            var musicDb = data.audio != null ? data.audio.musicVolumeDb : -20f;
            var sfxDb = data.audio != null ? data.audio.sfxVolumeDb : -8f;
            if(_masterVolumeSlider != null) _masterVolumeSlider.value = DbToLinear(masterDb);
            if(_musicVolumeSlider != null) _musicVolumeSlider.value = DbToLinear(musicDb);
            if(_sfxVolumeSlider != null) _sfxVolumeSlider.value = DbToLinear(sfxDb);
            if(_voiceVolumeSlider != null) _voiceVolumeSlider.value = SocialSettings.VoiceVolume;
            if(_voiceVolumeValue != null) _voiceVolumeValue.value = Mathf.RoundToInt(SocialSettings.VoiceVolume * 100) + "%";
            if(_voiceInputVolumeSlider != null) _voiceInputVolumeSlider.value = SocialSettings.VoiceInputVolume;
            if(_voiceInputVolumeValue != null) _voiceInputVolumeValue.value = Mathf.RoundToInt(SocialSettings.VoiceInputVolume * 100) + "%";

            // Load sensitivity
            var sensitivityValue = data.controls != null ? data.controls.sensitivity : 0.1f;

            if(_sensitivitySlider != null) _sensitivitySlider.value = sensitivityValue;
            if(_invertYButton != null) SetCheckboxValue(_invertYButton, data.controls is { invertY: true });
            if(_playerTrailsButton != null)
                SetCheckboxValue(_playerTrailsButton, data.controls == null || data.controls.playerTrails);
            if(_streamerModeButton != null)
                SetCheckboxValue(_streamerModeButton, data.social is { streamerModeEnabled: true });
            if(_holdMantleButton != null) SetCheckboxValue(_holdMantleButton, data.controls == null || data.controls.holdMantle);
            if(_profanityFilterButton != null) SetCheckboxValue(_profanityFilterButton, SocialSettings.ProfanityFilterEnabled);
            if(_autoWallRunButton != null) SetCheckboxValue(_autoWallRunButton, data.controls is { autoWallRun: true });
            RefreshMainMenuBackgroundDropdownChoices(preserveCurrentSelection: false);
            if(_mainMenuBackgroundDropdown != null) {
                var savedBackgroundSelection = data.video?.mainMenuBackgroundSelection;
                var normalizedSelection = NormalizeMainMenuBackgroundSelection(savedBackgroundSelection);
                if(!_mainMenuBackgroundDropdown.choices.Contains(normalizedSelection)) {
                    normalizedSelection = MainMenuBackgroundRandomizer.RandomSelectionOption;
                }

                _mainMenuBackgroundDropdown.SetValueWithoutNotify(normalizedSelection);
            }
            
            // Load grapple indicator setting (0 = Crosshair (default), 1 = Bottom, 2 = None)
            if(_grappleIndicatorDropdown != null) {
                var savedGrappleIndicator = data.controls != null ? data.controls.grappleIndicator : 0;
                _grappleIndicatorDropdown.index = Mathf.Clamp(savedGrappleIndicator, 0, _grappleIndicatorDropdown.choices.Count - 1);
            }

            if(_voiceModeDropdown != null) {
                _voiceModeDropdown.choices = Enum.GetNames(typeof(VoiceInputMode)).ToList();
                _voiceModeDropdown.index = (int)SocialSettings.InputMode;
            }

            RefreshVoiceDeviceDropdownChoices();
            RefreshVoiceDeviceDropdownChoicesDeferred();

            // Load window mode and resolution settings
            if(_windowModeDropdown != null) {
                // Default to current fullscreen mode
                var savedWindowMode = data.video != null ? data.video.windowMode : GetCurrentWindowModeIndex();
                _windowModeDropdown.index = Mathf.Clamp(savedWindowMode, 0, _windowModeDropdown.choices.Count - 1);
            }

            // Load aspect ratio (default to 16:9 or current resolution's aspect ratio)
            var savedAspectRatio = data.video != null ? data.video.aspectRatio : "";
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
                var savedWidth = data.video is { resolutionWidth: > 0 } ? data.video.resolutionWidth : Screen.width;
                var savedHeight = data.video is { resolutionHeight: > 0 } ? data.video.resolutionHeight : Screen.height;

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

            if(_bloomButton != null) SetCheckboxValue(_bloomButton, data.video == null || data.video.bloomEnabled);
            if(_motionBlurButton != null) SetCheckboxValue(_motionBlurButton, data.video == null || data.video.motionBlurEnabled);
            if(_filmGrainButton != null) SetCheckboxValue(_filmGrainButton, data.video == null || data.video.filmGrainEnabled);
            if(_vignetteButton != null) SetCheckboxValue(_vignetteButton, data.video == null || data.video.vignetteEnabled);
            if(_vsyncButton != null) SetCheckboxValue(_vsyncButton, data.video is { vsync: true });
            if(_fpsDropdown != null) _fpsDropdown.index = data.video != null ? data.video.targetFpsIndex : 1;

            // Store original values
            _originalMasterVolume = _masterVolumeSlider?.value ?? 0f;
            _originalMusicVolume = _musicVolumeSlider?.value ?? 0f;
            _originalSfxVolume = _sfxVolumeSlider?.value ?? 0f;
            _originalVoiceVolume = SocialSettings.VoiceVolume;

            _originalSensitivity = _sensitivitySlider?.value ?? 0.1f;
            _originalInvertY = GetCheckboxValue(_invertYButton);
            _originalPlayerTrails = GetCheckboxValue(_playerTrailsButton);
            _originalStreamerMode = GetCheckboxValue(_streamerModeButton);
            _originalHoldMantle = GetCheckboxValue(_holdMantleButton);
            _originalProfanityFilter = SocialSettings.ProfanityFilterEnabled;
            _originalAutoWallRun = GetCheckboxValue(_autoWallRunButton);
            _originalMainMenuBackgroundSelection = _mainMenuBackgroundDropdown?.value ?? MainMenuBackgroundRandomizer.RandomSelectionOption;
            _originalGrappleIndicator = _grappleIndicatorDropdown?.index ?? 0;
            _originalVoiceMode = (int)SocialSettings.InputMode;

            _originalWindowMode = _windowModeDropdown?.index ?? 0;
            _originalAspectRatio = _aspectRatioDropdown?.value ?? "";
            _originalResolutionIndex = _resolutionDropdown?.index ?? 0;
            _originalMsaa = _msaaDropdown?.index ?? 0;
            _originalShadowDistance = _shadowDistanceSlider?.value ?? 50f;
            _originalShadowResolution = _shadowResolutionDropdown?.index ?? 2;
            _originalBloom = GetCheckboxValue(_bloomButton);
            _originalMotionBlur = GetCheckboxValue(_motionBlurButton);
            _originalFilmGrain = GetCheckboxValue(_filmGrainButton);
            _originalVignette = GetCheckboxValue(_vignetteButton);
            _originalVsync = GetCheckboxValue(_vsyncButton);
            _originalTargetFPS = _fpsDropdown?.index ?? 1;

            ApplySettingsInternal();

            // Update display text fields (with % for volumes)
            if(_masterVolumeValue != null && _masterVolumeSlider != null) {
                _masterVolumeValue.value = Mathf.RoundToInt(_masterVolumeSlider.value * 100) + "%";
            }

            if(_musicVolumeValue != null && _musicVolumeSlider != null) {
                _musicVolumeValue.value = Mathf.RoundToInt(_musicVolumeSlider.value * 100) + "%";
            }

            if(_sfxVolumeValue != null && _sfxVolumeSlider != null) {
                _sfxVolumeValue.value = Mathf.RoundToInt(_sfxVolumeSlider.value * 100) + "%";
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
            
            if(_voiceVolumeSlider != null)
                volumeChanged |= !Mathf.Approximately(_voiceVolumeSlider.value, _originalVoiceVolume);

            var sensitivityChanged = false;
            if(_sensitivitySlider != null)
                sensitivityChanged = !Mathf.Approximately(_sensitivitySlider.value, _originalSensitivity);

            var invertYChanged = GetCheckboxValue(_invertYButton) != _originalInvertY;
            var playerTrailsChanged = GetCheckboxValue(_playerTrailsButton) != _originalPlayerTrails;
            var streamerModeChanged = GetCheckboxValue(_streamerModeButton) != _originalStreamerMode;
            var holdMantleChanged = GetCheckboxValue(_holdMantleButton) != _originalHoldMantle;
            var profanityFilterChanged = GetCheckboxValue(_profanityFilterButton) != _originalProfanityFilter;
            var autoWallRunChanged = GetCheckboxValue(_autoWallRunButton) != _originalAutoWallRun;
            var mainMenuBackgroundChanged = false;
            if(_mainMenuBackgroundDropdown != null) {
                mainMenuBackgroundChanged =
                    !string.Equals(_mainMenuBackgroundDropdown.value, _originalMainMenuBackgroundSelection, StringComparison.Ordinal);
            }
            
            var grappleIndicatorChanged = false;
            if(_grappleIndicatorDropdown != null) grappleIndicatorChanged = _grappleIndicatorDropdown.index != _originalGrappleIndicator;

            var voiceModeChanged = false;
            if(_voiceModeDropdown != null) voiceModeChanged = _voiceModeDropdown.index != _originalVoiceMode;

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

            var bloomChanged = GetCheckboxValue(_bloomButton) != _originalBloom;
            var motionBlurChanged = GetCheckboxValue(_motionBlurButton) != _originalMotionBlur;
            var filmGrainChanged = GetCheckboxValue(_filmGrainButton) != _originalFilmGrain;
            var vignetteChanged = GetCheckboxValue(_vignetteButton) != _originalVignette;
            var vsyncChanged = GetCheckboxValue(_vsyncButton) != _originalVsync;

            var fpsChanged = false;
            if(_fpsDropdown != null) fpsChanged = _fpsDropdown.index != _originalTargetFPS;

            return volumeChanged || sensitivityChanged || invertYChanged || playerTrailsChanged || streamerModeChanged ||
                   holdMantleChanged ||
                   profanityFilterChanged || autoWallRunChanged || mainMenuBackgroundChanged || voiceModeChanged ||
                   grappleIndicatorChanged || windowModeChanged || aspectRatioChanged || resolutionChanged || msaaChanged ||
                   shadowDistanceChanged || shadowResolutionChanged || bloomChanged || motionBlurChanged ||
                   filmGrainChanged || vignetteChanged || vsyncChanged || fpsChanged || hasKeybindChanges;
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
            NavigateBackFromOptions();
        }

        private void OnUnsavedChangesNo() {
            OnButtonClicked(true);
            if(KeybindManager.Instance != null) {
                KeybindManager.Instance.CancelBindings();
            }

            LoadSettings();
            if(_mainMenuBackgroundDropdown != null) {
                ApplyMainMenuBackgroundSelectionPreview(_mainMenuBackgroundDropdown.value);
            }
            HideUnsavedChangesDialog();
            NavigateBackFromOptions();
        }

        private void OnUnsavedChangesCancel() {
            OnButtonClicked(true);
            HideUnsavedChangesDialog();
        }

        private void NavigateBackFromOptions() {
            if(OnBackFromOptionsCallback == null) {
                return;
            }

            if(Root == null) {
                OnBackFromOptionsCallback.Invoke();
                return;
            }

            // Defer by one UI tick so modal close and pointer events settle before panel navigation.
            Root.schedule.Execute(() => OnBackFromOptionsCallback?.Invoke());
        }

        private void ApplySettings() {
            var data = GameSettings.Data;
            var mainMenuBackgroundSelectionChanged = _mainMenuBackgroundDropdown != null &&
                                                     !string.Equals(
                                                         NormalizeMainMenuBackgroundSelection(_mainMenuBackgroundDropdown.value),
                                                         _originalMainMenuBackgroundSelection,
                                                         StringComparison.Ordinal);

            // Save audio settings
            if(_masterVolumeSlider != null) {
                var masterDb = LinearToDb(_masterVolumeSlider.value);
                if(data.audio != null) data.audio.masterVolumeDb = masterDb;
            }

            if(_musicVolumeSlider != null) {
                var musicDb = LinearToDb(_musicVolumeSlider.value);
                if(data.audio != null) data.audio.musicVolumeDb = musicDb;
            }

            if(_sfxVolumeSlider != null) {
                var sfxDb = LinearToDb(_sfxVolumeSlider.value);
                if(data.audio != null) data.audio.sfxVolumeDb = sfxDb;
            }

            if(_voiceVolumeSlider != null) SocialSettings.VoiceVolume = _voiceVolumeSlider.value;
            if(_voiceInputVolumeSlider != null) SocialSettings.VoiceInputVolume = _voiceInputVolumeSlider.value;

            // Save control settings
            if(_sensitivitySlider != null) {
                if(data.controls != null) data.controls.sensitivity = _sensitivitySlider.value;
            }

            if(data.controls != null) data.controls.invertY = GetCheckboxValue(_invertYButton);
            if(data.controls != null) data.controls.playerTrails = GetCheckboxValue(_playerTrailsButton);
            if(data.social != null) data.social.streamerModeEnabled = GetCheckboxValue(_streamerModeButton);
            if(data.controls != null) data.controls.holdMantle = GetCheckboxValue(_holdMantleButton);
            if(data.controls != null) data.controls.autoWallRun = GetCheckboxValue(_autoWallRunButton);
            SocialSettings.ProfanityFilterEnabled = GetCheckboxValue(_profanityFilterButton);
            if(data.video != null && _mainMenuBackgroundDropdown != null) {
                data.video.mainMenuBackgroundSelection = NormalizeMainMenuBackgroundSelection(_mainMenuBackgroundDropdown.value);
            }
            
            // Save grapple indicator setting
            if(_grappleIndicatorDropdown != null) {
                if(data.controls != null) data.controls.grappleIndicator = _grappleIndicatorDropdown.index;
            }
            
            if(_voiceModeDropdown != null) {
                SocialSettings.InputMode = (VoiceInputMode)_voiceModeDropdown.index;
            }

            if (_voiceDeviceDropdown != null) {
                SocialSettings.InputDevice = _voiceDeviceDropdown.value;
                if (VoiceManager.Instance != null) {
                    VoiceManager.Instance.SetActiveMicAsync(_voiceDeviceDropdown.value).Forget();
                }
            }

            // Save window mode and resolution settings
            if(_windowModeDropdown != null) {
                if(data.video != null) data.video.windowMode = _windowModeDropdown.index;
            }

            if(_aspectRatioDropdown != null) {
                if(data.video != null) data.video.aspectRatio = _aspectRatioDropdown.value;
            }

            if(_resolutionDropdown is { index: >= 0 } &&
               _resolutionDropdown.index < _filteredResolutions.Count) {
                var selectedRes = _filteredResolutions[_resolutionDropdown.index];
                if(data.video != null) data.video.resolutionWidth = selectedRes.Width;
                if(data.video != null) data.video.resolutionHeight = selectedRes.Height;
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
                if(data.video != null) data.video.msaa = msaaValue;
            }

            if(_shadowDistanceSlider != null) {
                if(data.video != null) data.video.shadowDistance = _shadowDistanceSlider.value;
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
                if(data.video != null) data.video.shadowResolution = resolutionValue;
            }

            if(data.video != null && _bloomButton != null) data.video.bloomEnabled = GetCheckboxValue(_bloomButton);
            if(data.video != null && _motionBlurButton != null) data.video.motionBlurEnabled = GetCheckboxValue(_motionBlurButton);
            if(data.video != null && _filmGrainButton != null) data.video.filmGrainEnabled = GetCheckboxValue(_filmGrainButton);
            if(data.video != null && _vignetteButton != null) data.video.vignetteEnabled = GetCheckboxValue(_vignetteButton);
            if(data.video != null) data.video.vsync = GetCheckboxValue(_vsyncButton);
            if(_fpsDropdown != null) {
                if(data.video != null) data.video.targetFpsIndex = _fpsDropdown.index;
            }

            // Save keybinds
            if(KeybindManager.Instance != null) {
                KeybindManager.Instance.SaveBindings();
            }

            GameSettings.Save();

            ApplySettingsInternal();
            if(mainMenuBackgroundSelectionChanged && _mainMenuBackgroundDropdown != null) {
                var selection = NormalizeMainMenuBackgroundSelection(_mainMenuBackgroundDropdown.value);
                if(!MainMenuBackgroundRandomizer.IsRandomSelection(selection)) {
                    ApplyMainMenuBackgroundSelectionPreview(selection);
                }
            }

            // Update original values
            _originalMasterVolume = _masterVolumeSlider != null ? _masterVolumeSlider.value : 0f;
            _originalMusicVolume = _musicVolumeSlider != null ? _musicVolumeSlider.value : 0f;
            _originalSfxVolume = _sfxVolumeSlider != null ? _sfxVolumeSlider.value : 0f;
            _originalVoiceVolume = SocialSettings.VoiceVolume;

            _originalSensitivity = _sensitivitySlider != null ? _sensitivitySlider.value : 0.1f;
            _originalInvertY = GetCheckboxValue(_invertYButton);
            _originalPlayerTrails = GetCheckboxValue(_playerTrailsButton);
            _originalStreamerMode = GetCheckboxValue(_streamerModeButton);
            _originalHoldMantle = GetCheckboxValue(_holdMantleButton);
            _originalProfanityFilter = SocialSettings.ProfanityFilterEnabled;
            _originalAutoWallRun = GetCheckboxValue(_autoWallRunButton);
            _originalMainMenuBackgroundSelection = _mainMenuBackgroundDropdown != null
                ? NormalizeMainMenuBackgroundSelection(_mainMenuBackgroundDropdown.value)
                : MainMenuBackgroundRandomizer.RandomSelectionOption;
            _originalGrappleIndicator = _grappleIndicatorDropdown != null ? _grappleIndicatorDropdown.index : 0;
            _originalVoiceMode = (int)SocialSettings.InputMode;

            _originalWindowMode = _windowModeDropdown != null ? _windowModeDropdown.index : 0;
            _originalAspectRatio = _aspectRatioDropdown != null ? _aspectRatioDropdown.value : "";
            _originalResolutionIndex = _resolutionDropdown != null ? _resolutionDropdown.index : 0;
            _originalMsaa = _msaaDropdown != null ? _msaaDropdown.index : 0;
            _originalShadowDistance = _shadowDistanceSlider != null ? _shadowDistanceSlider.value : 50f;
            _originalShadowResolution = _shadowResolutionDropdown != null ? _shadowResolutionDropdown.index : 2;
            _originalBloom = GetCheckboxValue(_bloomButton);
            _originalMotionBlur = GetCheckboxValue(_motionBlurButton);
            _originalFilmGrain = GetCheckboxValue(_filmGrainButton);
            _originalVignette = GetCheckboxValue(_vignetteButton);
            _originalVsync = GetCheckboxValue(_vsyncButton);
            _originalTargetFPS = _fpsDropdown != null ? _fpsDropdown.index : 1;

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
            var bloomEnabled = _bloomButton != null
                ? GetCheckboxValue(_bloomButton)
                : GameSettings.Data.video == null || GameSettings.Data.video.bloomEnabled;
            var motionBlurEnabled = _motionBlurButton != null
                ? GetCheckboxValue(_motionBlurButton)
                : GameSettings.Data.video == null || GameSettings.Data.video.motionBlurEnabled;
            var filmGrainEnabled = _filmGrainButton != null
                ? GetCheckboxValue(_filmGrainButton)
                : GameSettings.Data.video == null || GameSettings.Data.video.filmGrainEnabled;
            var vignetteEnabled = _vignetteButton != null
                ? GetCheckboxValue(_vignetteButton)
                : GameSettings.Data.video == null || GameSettings.Data.video.vignetteEnabled;
            VideoSettingsRuntimeApplier.ApplyBloomEnabled(bloomEnabled);
            VideoSettingsRuntimeApplier.ApplyMotionBlurEnabled(motionBlurEnabled);
            VideoSettingsRuntimeApplier.ApplyFilmGrainEnabled(filmGrainEnabled);
            VideoSettingsRuntimeApplier.ApplyVignetteEnabled(vignetteEnabled);

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
            
            // Notify listeners that resolution changed
            OnResolutionChanged?.Invoke(selectedRes.Width, selectedRes.Height);
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
            var optionsPanel = Root?.Q<VisualElement>("options-panel");
            RefreshVoiceDeviceDropdownChoices();
            RefreshVoiceDeviceDropdownChoicesDeferred();
            RefreshMainMenuBackgroundDropdownChoices(preserveCurrentSelection: true);

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

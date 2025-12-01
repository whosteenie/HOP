using System;
using System.Collections.Generic;
using Game.Player;
using Network.Singletons;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Menu {
    /// <summary>
    /// Manages the character customization panel UI and material customization.
    /// </summary>
    public class CharacterCustomizationManager : MonoBehaviour {
        [Header("References")]
        [SerializeField] private MainMenuManager mainMenuManager;
        [SerializeField] private LoadoutManager loadoutManager;
        
        private UIDocument _uiDocument;

        private VisualElement _root;

        // Material packet selection UI
        private Button _colorPreviewButton;
        private VisualElement _materialPacketPanel;
        private Button _materialPacketBackButton;
        private VisualElement _materialPacketGrid;

        private readonly Dictionary<int, Button> _packetButtons = new();
        private int _availablePacketCount = 1;
        private const int PacketSlotTargetCount = 24;

        // Color picker UI
        private VisualElement _colorPreviewBox;
        private Slider _colorRSlider;
        private Slider _colorGSlider;
        private Slider _colorBSlider;
        private IntegerField _colorRInput;
        private IntegerField _colorGInput;
        private IntegerField _colorBInput;

        // Emission controls
        private Toggle _emissionToggle;
        private Button _emissionPreviewButton;
        private Slider _emissionRSlider;
        private Slider _emissionGSlider;
        private Slider _emissionBSlider;
        private IntegerField _emissionRInput;
        private IntegerField _emissionGInput;
        private IntegerField _emissionBInput;

        // Material property sliders
        private Slider _smoothnessSlider;
        private TextField _smoothnessValue;
        private Slider _metallicSlider;
        private TextField _metallicValue;
        private Slider _heightSlider;
        private TextField _heightValue;

        // Buttons removed - customization now auto-applies when leaving loadout

        // Unsaved changes modal
        private VisualElement _unsavedChangesModal;
        private Button _unsavedChangesYes;
        private Button _unsavedChangesNo;
        private Button _unsavedChangesCancel;

        // Current values (for unsaved changes detection)
        private Color _originalBaseColor;
        private float _originalSmoothness;
        private float _originalMetallic;

        // Current editing values
        private Color _currentBaseColor = Color.white;
        private float _currentSmoothness = 0.5f;
        private float _currentMetallic;
        private float _currentHeightStrength = 0.02f;
        private bool _currentEmissionEnabled;
        private Color _currentEmissionColor = Color.black;
        private int _currentPacketIndex;
        private int _originalPacketIndex;
        private float _originalHeightStrength;
        private const float MinHeightStrength = 0.005f;
        private const float MaxHeightStrength = 0.08f;

        private bool _originalEmissionEnabled;
        private Color _originalEmissionColor = Color.black;

        // Callbacks
        public Action<bool> OnButtonClickedCallback;
        public EventCallback<MouseEnterEvent> MouseEnterCallback;
        public Action OnBackFromCustomizationCallback;

        private void Awake() {
            if(mainMenuManager != null) {
                _uiDocument = mainMenuManager.uiDocument;
            }
            if(_uiDocument == null) {
                _uiDocument = FindFirstObjectByType<UIDocument>();
            }

            // Find LoadoutManager if not assigned
            if(loadoutManager == null) {
                loadoutManager = FindFirstObjectByType<LoadoutManager>();
            }
        }

        private void OnEnable() {
            if(_uiDocument == null) {
                if(mainMenuManager != null) {
                    _uiDocument = mainMenuManager.uiDocument;
                }
                if(_uiDocument == null) {
                    _uiDocument = FindFirstObjectByType<UIDocument>();
                }
            }
            
            if(_uiDocument == null) {
                Debug.LogError("[CharacterCustomizationManager] UIDocument not found!");
                return;
            }
            
            _root = _uiDocument.rootVisualElement;
            SetupUIReferences();
            SetupEventHandlers();
            LoadSavedCustomization();
            BuildMaterialPacketGrid();
            UpdatePacketSelectionHighlight();
        }

        private void SetupUIReferences() {
            // Customization is now integrated into loadout panel, no separate panel needed

            // Color picker
            _colorPreviewButton = _root.Q<Button>("color-preview-box");
            if(_colorPreviewButton == null) {
                _colorPreviewBox = _root.Q<VisualElement>("color-preview-box");
            } else {
                _colorPreviewBox = _colorPreviewButton;
            }
            _colorRSlider = _root.Q<Slider>("color-r-slider");
            _colorGSlider = _root.Q<Slider>("color-g-slider");
            _colorBSlider = _root.Q<Slider>("color-b-slider");
            _colorRInput = _root.Q<IntegerField>("color-r-input");
            _colorGInput = _root.Q<IntegerField>("color-g-input");
            _colorBInput = _root.Q<IntegerField>("color-b-input");

            // Emission controls
            _emissionToggle = _root.Q<Toggle>("emission-toggle");
            _emissionPreviewButton = _root.Q<Button>("emission-preview-box");
            _emissionRSlider = _root.Q<Slider>("emission-r-slider");
            _emissionGSlider = _root.Q<Slider>("emission-g-slider");
            _emissionBSlider = _root.Q<Slider>("emission-b-slider");
            _emissionRInput = _root.Q<IntegerField>("emission-r-input");
            _emissionGInput = _root.Q<IntegerField>("emission-g-input");
            _emissionBInput = _root.Q<IntegerField>("emission-b-input");

            // Packet selection panel
            _materialPacketPanel = _root.Q<VisualElement>("material-packet-panel");
            _materialPacketBackButton = _root.Q<Button>("material-packet-back");
            _root.Q<ScrollView>("material-packet-scroll");
            _materialPacketGrid = _root.Q<VisualElement>("material-packet-grid");

            // Material properties
            _smoothnessSlider = _root.Q<Slider>("smoothness-slider");
            _smoothnessValue = _root.Q<TextField>("smoothness-value");
            _metallicSlider = _root.Q<Slider>("metallic-slider");
            _metallicValue = _root.Q<TextField>("metallic-value");
            _heightSlider = _root.Q<Slider>("height-slider");
            _heightValue = _root.Q<TextField>("height-value");

            // Buttons removed - customization now auto-applies when leaving loadout

            // Unsaved changes modal
            _unsavedChangesModal = _root.Q<VisualElement>("unsaved-changes-modal");
            _unsavedChangesYes = _root.Q<Button>("unsaved-changes-yes");
            _unsavedChangesNo = _root.Q<Button>("unsaved-changes-no");
            _unsavedChangesCancel = _root.Q<Button>("unsaved-changes-cancel");
        }

        private void SetupEventHandlers() {
            // Color sliders
            if(_colorRSlider != null) _colorRSlider.RegisterValueChangedCallback(evt => OnColorRChanged(evt.newValue));
            if(_colorGSlider != null) _colorGSlider.RegisterValueChangedCallback(evt => OnColorGChanged(evt.newValue));
            if(_colorBSlider != null) _colorBSlider.RegisterValueChangedCallback(evt => OnColorBChanged(evt.newValue));

            // Color inputs
            if(_colorRInput != null) _colorRInput.RegisterValueChangedCallback(evt => OnColorRInputChanged(evt.newValue));
            if(_colorGInput != null) _colorGInput.RegisterValueChangedCallback(evt => OnColorGInputChanged(evt.newValue));
            if(_colorBInput != null) _colorBInput.RegisterValueChangedCallback(evt => OnColorBInputChanged(evt.newValue));

            // Color preview button -> open packet panel
            if(_colorPreviewButton != null) {
                _colorPreviewButton.RegisterCallback<MouseEnterEvent>(evt => {
                    if(MouseEnterCallback != null) {
                        MouseEnterCallback.Invoke(evt);
                    }
                });
                _colorPreviewButton.RegisterCallback<ClickEvent>(_ => {
                    if(OnButtonClickedCallback != null) {
                        OnButtonClickedCallback.Invoke(false);
                    }
                    ShowMaterialPacketPanel();
                });
            }

            // Packet panel back button
            if(_materialPacketBackButton != null) {
                _materialPacketBackButton.RegisterCallback<MouseEnterEvent>(evt => {
                    if(MouseEnterCallback != null) {
                        MouseEnterCallback.Invoke(evt);
                    }
                });
                _materialPacketBackButton.RegisterCallback<ClickEvent>(_ => {
                    if(OnButtonClickedCallback != null) {
                        OnButtonClickedCallback.Invoke(true);
                    }
                    HideMaterialPacketPanel();
                });
            }

            // Material sliders
            if(_smoothnessSlider != null) _smoothnessSlider.RegisterValueChangedCallback(evt => OnSmoothnessChanged(evt.newValue));
            if(_metallicSlider != null) _metallicSlider.RegisterValueChangedCallback(evt => OnMetallicChanged(evt.newValue));
            if(_heightSlider != null) _heightSlider.RegisterValueChangedCallback(evt => OnHeightStrengthChanged(evt.newValue));
            if(_emissionRSlider != null) _emissionRSlider.RegisterValueChangedCallback(evt => OnEmissionRChanged(evt.newValue));
            if(_emissionGSlider != null) _emissionGSlider.RegisterValueChangedCallback(evt => OnEmissionGChanged(evt.newValue));
            if(_emissionBSlider != null) _emissionBSlider.RegisterValueChangedCallback(evt => OnEmissionBChanged(evt.newValue));

            // Material value fields
            if(_smoothnessValue != null) {
                _smoothnessValue.RegisterValueChangedCallback(evt => {
                if(float.TryParse(evt.newValue, out var val)) {
                    val = Mathf.Clamp01(val);
                    _smoothnessSlider.value = val;
                    _currentSmoothness = val;
                    UpdateSmoothnessDisplay();
                    ApplyToLocalPlayer();
                    NotifyLoadoutDirty();
                }
                });
            }

            if(_metallicValue != null) {
                _metallicValue.RegisterValueChangedCallback(evt => {
                if(float.TryParse(evt.newValue, out var val)) {
                    val = Mathf.Clamp01(val);
                    _metallicSlider.value = val;
                    _currentMetallic = val;
                    UpdateMetallicDisplay();
                    ApplyToLocalPlayer();
                    NotifyLoadoutDirty();
                }
                });
            }

            if(_heightValue != null) {
                _heightValue.RegisterValueChangedCallback(evt => {
                if(float.TryParse(evt.newValue, out var val)) {
                    val = Mathf.Clamp(val, MinHeightStrength, MaxHeightStrength);
                    _heightSlider.value = val;
                    _currentHeightStrength = val;
                    UpdateHeightDisplay();
                    ApplyToLocalPlayer();
                    NotifyLoadoutDirty();
                }
                });
            }

            // Emission toggle & inputs
            if(_emissionToggle != null) {
                _emissionToggle.RegisterCallback<MouseEnterEvent>(evt => {
                    if(MouseEnterCallback != null) {
                        MouseEnterCallback.Invoke(evt);
                    }
                });
                _emissionToggle.RegisterValueChangedCallback(evt => OnEmissionToggleChanged(evt.newValue));
            }

            if(_emissionPreviewButton != null) {
                _emissionPreviewButton.RegisterCallback<MouseEnterEvent>(evt => {
                    if(MouseEnterCallback != null) {
                        MouseEnterCallback.Invoke(evt);
                    }
                });
            }

            if(_emissionRInput != null) _emissionRInput.RegisterValueChangedCallback(evt => OnEmissionRInputChanged(evt.newValue));
            if(_emissionGInput != null) _emissionGInput.RegisterValueChangedCallback(evt => OnEmissionGInputChanged(evt.newValue));
            if(_emissionBInput != null) _emissionBInput.RegisterValueChangedCallback(evt => OnEmissionBInputChanged(evt.newValue));

            // Buttons removed - customization now auto-applies when leaving loadout

            // Unsaved changes modal
            if(_unsavedChangesYes != null) _unsavedChangesYes.RegisterCallback<ClickEvent>(_ => OnUnsavedChangesYes());
            if(_unsavedChangesNo != null) _unsavedChangesNo.RegisterCallback<ClickEvent>(_ => OnUnsavedChangesNo());
            if(_unsavedChangesCancel != null) _unsavedChangesCancel.RegisterCallback<ClickEvent>(_ => OnUnsavedChangesCancel());
        }

        public void ShowCustomization() {
            // Ensure callbacks are set up (in case they weren't set in Initialize)
            if(mainMenuManager != null) {
                OnButtonClickedCallback = mainMenuManager.OnButtonClicked;
                MouseEnterCallback = MainMenuManager.MouseEnter;
                OnBackFromCustomizationCallback = () => {
                    Debug.Log("[CharacterCustomizationManager] Back callback invoked from ShowCustomization");
                    if(mainMenuManager == null) return;
                    var loadoutPanel = _root.Q<VisualElement>("loadout-panel");
                    if(loadoutPanel != null) {
                        mainMenuManager.ShowPanel(loadoutPanel);
                    }
                };
            }
            
            LoadSavedCustomization();
            UpdateColorUI(); // Ensure color preview is updated
            UpdateSmoothnessUI();
            UpdateMetallicUI();
        }

        private void LoadSavedCustomization() {
            // Load from PlayerPrefs (defaults to white, 0 smoothness, 0 metallic)
            var baseColorR = PlayerPrefs.GetFloat("PlayerBaseColorR", 1f);
            var baseColorG = PlayerPrefs.GetFloat("PlayerBaseColorG", 1f);
            var baseColorB = PlayerPrefs.GetFloat("PlayerBaseColorB", 1f);
            var baseColorA = PlayerPrefs.GetFloat("PlayerBaseColorA", 1f);

            _currentBaseColor = new Color(baseColorR, baseColorG, baseColorB, baseColorA);
            _currentSmoothness = PlayerPrefs.GetFloat("PlayerSmoothness", 0f);
            _currentMetallic = PlayerPrefs.GetFloat("PlayerMetallic", 0f);
            _currentPacketIndex = Mathf.Max(0, PlayerPrefs.GetInt("PlayerMaterialPacketIndex", 0));
            var savedHeight = PlayerPrefs.GetFloat("PlayerHeightStrength", 0.02f);
            _currentHeightStrength = Mathf.Clamp(savedHeight, MinHeightStrength, MaxHeightStrength);
            _currentEmissionEnabled = PlayerPrefs.GetInt("PlayerEmissionEnabled", 0) == 1;
            var emissionR = PlayerPrefs.GetFloat("PlayerEmissionColorR", 0f);
            var emissionG = PlayerPrefs.GetFloat("PlayerEmissionColorG", 0f);
            var emissionB = PlayerPrefs.GetFloat("PlayerEmissionColorB", 0f);
            var emissionA = PlayerPrefs.GetFloat("PlayerEmissionColorA", 1f);
            _currentEmissionColor = new Color(emissionR, emissionG, emissionB, emissionA);

            // Update UI
            UpdateColorUI();
            UpdateSmoothnessUI();
            UpdateMetallicUI();
            UpdateHeightUI();
            UpdateHeightControlState();
            UpdateEmissionUI();

            // Store as original values after UI adjustments (in case availability changed states)
            _originalBaseColor = _currentBaseColor;
            _originalSmoothness = _currentSmoothness;
            _originalMetallic = _currentMetallic;
            _originalPacketIndex = _currentPacketIndex;
            _originalHeightStrength = _currentHeightStrength;
            _originalEmissionEnabled = _currentEmissionEnabled;
            _originalEmissionColor = _currentEmissionColor;
        }

        private void UpdateColorUI() {
            var r = Mathf.RoundToInt(_currentBaseColor.r * 255f);
            var g = Mathf.RoundToInt(_currentBaseColor.g * 255f);
            var b = Mathf.RoundToInt(_currentBaseColor.b * 255f);

            if(_colorRSlider != null) _colorRSlider.SetValueWithoutNotify(r);
            if(_colorGSlider != null) _colorGSlider.SetValueWithoutNotify(g);
            if(_colorBSlider != null) _colorBSlider.SetValueWithoutNotify(b);
            if(_colorRInput != null) _colorRInput.SetValueWithoutNotify(r);
            if(_colorGInput != null) _colorGInput.SetValueWithoutNotify(g);
            if(_colorBInput != null) _colorBInput.SetValueWithoutNotify(b);

            // Update preview box
            UpdateColorPreview();
        }

        private void UpdateSmoothnessUI() {
            if(_smoothnessSlider != null) _smoothnessSlider.SetValueWithoutNotify(_currentSmoothness);
            UpdateSmoothnessDisplay();
        }

        private void UpdateSmoothnessDisplay() {
            if(_smoothnessValue != null) {
                _smoothnessValue.SetValueWithoutNotify(_currentSmoothness.ToString("F2"));
            }
        }

        private void UpdateMetallicUI() {
            if(_metallicSlider != null) _metallicSlider.SetValueWithoutNotify(_currentMetallic);
            UpdateMetallicDisplay();
        }

        private void UpdateMetallicDisplay() {
            if(_metallicValue != null) {
                _metallicValue.SetValueWithoutNotify(_currentMetallic.ToString("F2"));
            }
        }

        private void UpdateHeightUI() {
            if(_heightSlider != null) _heightSlider.SetValueWithoutNotify(_currentHeightStrength);
            UpdateHeightDisplay();
        }

        private void UpdateHeightDisplay() {
            if(_heightValue != null) {
                _heightValue.SetValueWithoutNotify(_currentHeightStrength.ToString("F3"));
            }
        }

        private void UpdateEmissionUI() {
            if(_emissionToggle != null) _emissionToggle.SetValueWithoutNotify(_currentEmissionEnabled);
            UpdateEmissionAvailability();
            UpdateEmissionColorControls();
        }

        private void UpdateEmissionColorControls() {
            var r = Mathf.RoundToInt(_currentEmissionColor.r * 255f);
            var g = Mathf.RoundToInt(_currentEmissionColor.g * 255f);
            var b = Mathf.RoundToInt(_currentEmissionColor.b * 255f);

            if(_emissionRSlider != null) _emissionRSlider.SetValueWithoutNotify(r);
            if(_emissionGSlider != null) _emissionGSlider.SetValueWithoutNotify(g);
            if(_emissionBSlider != null) _emissionBSlider.SetValueWithoutNotify(b);
            if(_emissionRInput != null) _emissionRInput.SetValueWithoutNotify(r);
            if(_emissionGInput != null) _emissionGInput.SetValueWithoutNotify(g);
            if(_emissionBInput != null) _emissionBInput.SetValueWithoutNotify(b);

            UpdateEmissionPreview();
        }

        private void UpdateEmissionPreview() {
            if(_emissionPreviewButton != null) {
                _emissionPreviewButton.style.backgroundColor = new StyleColor(_currentEmissionColor);
            }
        }

        private void UpdateEmissionAvailability() {
            var supportsEmission = CurrentPacketSupportsEmission();

            if(_emissionToggle != null) {
                _emissionToggle.SetEnabled(supportsEmission);
                _emissionToggle.tooltip = supportsEmission ? string.Empty : "This packet does not include an emission map.";
                if(!supportsEmission && _currentEmissionEnabled) {
                    _currentEmissionEnabled = false;
                    _emissionToggle.SetValueWithoutNotify(false);
                }
            }

            SetEmissionControlsEnabled(supportsEmission && _currentEmissionEnabled);
        }

        private void SetEmissionControlsEnabled(bool propertyEnabled) {
            if(_emissionPreviewButton != null) _emissionPreviewButton.SetEnabled(propertyEnabled);
            if(_emissionRSlider != null) _emissionRSlider.SetEnabled(propertyEnabled);
            if(_emissionGSlider != null) _emissionGSlider.SetEnabled(propertyEnabled);
            if(_emissionBSlider != null) _emissionBSlider.SetEnabled(propertyEnabled);
            if(_emissionRInput != null) _emissionRInput.SetEnabled(propertyEnabled);
            if(_emissionGInput != null) _emissionGInput.SetEnabled(propertyEnabled);
            if(_emissionBInput != null) _emissionBInput.SetEnabled(propertyEnabled);
        }

        private void OnColorRChanged(float value) {
            value = Mathf.Clamp(value, 0f, 255f);
            _currentBaseColor.r = value / 255f;
            _colorRInput.value = Mathf.RoundToInt(value);
            UpdateColorPreview();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void OnColorGChanged(float value) {
            value = Mathf.Clamp(value, 0f, 255f);
            _currentBaseColor.g = value / 255f;
            _colorGInput.value = Mathf.RoundToInt(value);
            UpdateColorPreview();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void OnColorBChanged(float value) {
            value = Mathf.Clamp(value, 0f, 255f);
            _currentBaseColor.b = value / 255f;
            _colorBInput.value = Mathf.RoundToInt(value);
            UpdateColorPreview();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void OnColorRInputChanged(int value) {
            value = Mathf.Clamp(value, 0, 255);
            _currentBaseColor.r = value / 255f;
            _colorRSlider.value = value;
            UpdateColorPreview();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void OnColorGInputChanged(int value) {
            value = Mathf.Clamp(value, 0, 255);
            _currentBaseColor.g = value / 255f;
            _colorGSlider.value = value;
            UpdateColorPreview();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void OnColorBInputChanged(int value) {
            value = Mathf.Clamp(value, 0, 255);
            _currentBaseColor.b = value / 255f;
            _colorBSlider.value = value;
            UpdateColorPreview();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void UpdateColorPreview() {
            if(_colorPreviewBox != null) {
                _colorPreviewBox.style.backgroundColor = new StyleColor(_currentBaseColor);
            }

            if(_colorPreviewButton != null) {
                _colorPreviewButton.tooltip = $"Packet: {GetPacketName(_currentPacketIndex)}";
            }
        }

        private void OnSmoothnessChanged(float value) {
            _currentSmoothness = Mathf.Clamp01(value);
            UpdateSmoothnessDisplay();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void OnMetallicChanged(float value) {
            _currentMetallic = Mathf.Clamp01(value);
            UpdateMetallicDisplay();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void OnHeightStrengthChanged(float value) {
            _currentHeightStrength = Mathf.Clamp(value, MinHeightStrength, MaxHeightStrength);
            UpdateHeightDisplay();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void OnEmissionToggleChanged(bool isEnabled) {
            _currentEmissionEnabled = isEnabled;
            UpdateEmissionAvailability();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void OnEmissionRChanged(float value) {
            value = Mathf.Clamp(value, 0f, 255f);
            _currentEmissionColor.r = value / 255f;
            if(_emissionRInput != null) {
                _emissionRInput.value = Mathf.RoundToInt(value);
            }
            UpdateEmissionPreview();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void OnEmissionGChanged(float value) {
            value = Mathf.Clamp(value, 0f, 255f);
            _currentEmissionColor.g = value / 255f;
            if(_emissionGInput != null) {
                _emissionGInput.value = Mathf.RoundToInt(value);
            }
            UpdateEmissionPreview();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void OnEmissionBChanged(float value) {
            value = Mathf.Clamp(value, 0f, 255f);
            _currentEmissionColor.b = value / 255f;
            if(_emissionBInput != null) {
                _emissionBInput.value = Mathf.RoundToInt(value);
            }
            UpdateEmissionPreview();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void OnEmissionRInputChanged(int value) {
            value = Mathf.Clamp(value, 0, 255);
            _currentEmissionColor.r = value / 255f;
            if(_emissionRSlider != null) {
                _emissionRSlider.value = value;
            }
            UpdateEmissionPreview();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void OnEmissionGInputChanged(int value) {
            value = Mathf.Clamp(value, 0, 255);
            _currentEmissionColor.g = value / 255f;
            if(_emissionGSlider != null) {
                _emissionGSlider.value = value;
            }
            UpdateEmissionPreview();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void OnEmissionBInputChanged(int value) {
            value = Mathf.Clamp(value, 0, 255);
            _currentEmissionColor.b = value / 255f;
            if(_emissionBSlider != null) {
                _emissionBSlider.value = value;
            }
            UpdateEmissionPreview();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private bool HasUnsavedChanges() {
            return !ColorsEqual(_currentBaseColor, _originalBaseColor) ||
                   Mathf.Abs(_currentSmoothness - _originalSmoothness) > 0.001f ||
                   Mathf.Abs(_currentMetallic - _originalMetallic) > 0.001f ||
                   Mathf.Abs(_currentHeightStrength - _originalHeightStrength) > 0.001f ||
                   _currentPacketIndex != _originalPacketIndex ||
                   _currentEmissionEnabled != _originalEmissionEnabled ||
                   !ColorsEqual(_currentEmissionColor, _originalEmissionColor);
        }

        private static bool ColorsEqual(Color a, Color b) {
            return Mathf.Abs(a.r - b.r) < 0.001f &&
                   Mathf.Abs(a.g - b.g) < 0.001f &&
                   Mathf.Abs(a.b - b.b) < 0.001f &&
                   Mathf.Abs(a.a - b.a) < 0.001f;
        }

        /// <summary>
        /// Applies the current customization values. Called automatically when leaving loadout.
        /// </summary>
        public void ApplyCustomization() {
            // Save to PlayerPrefs
            PlayerPrefs.SetFloat("PlayerBaseColorR", _currentBaseColor.r);
            PlayerPrefs.SetFloat("PlayerBaseColorG", _currentBaseColor.g);
            PlayerPrefs.SetFloat("PlayerBaseColorB", _currentBaseColor.b);
            PlayerPrefs.SetFloat("PlayerBaseColorA", _currentBaseColor.a);
            PlayerPrefs.SetFloat("PlayerSmoothness", _currentSmoothness);
            PlayerPrefs.SetFloat("PlayerMetallic", _currentMetallic);
            PlayerPrefs.SetFloat("PlayerHeightStrength", _currentHeightStrength);
            PlayerPrefs.SetInt("PlayerEmissionEnabled", _currentEmissionEnabled ? 1 : 0);
            PlayerPrefs.SetFloat("PlayerEmissionColorR", _currentEmissionColor.r);
            PlayerPrefs.SetFloat("PlayerEmissionColorG", _currentEmissionColor.g);
            PlayerPrefs.SetFloat("PlayerEmissionColorB", _currentEmissionColor.b);
            PlayerPrefs.SetFloat("PlayerEmissionColorA", _currentEmissionColor.a);
            PlayerPrefs.SetInt("PlayerMaterialPacketIndex", _currentPacketIndex);
            PlayerPrefs.Save();

            // Update original values
            _originalBaseColor = _currentBaseColor;
            _originalSmoothness = _currentSmoothness;
            _originalMetallic = _currentMetallic;
            _originalPacketIndex = _currentPacketIndex;
            _originalHeightStrength = _currentHeightStrength;
            _originalEmissionEnabled = _currentEmissionEnabled;
            _originalEmissionColor = _currentEmissionColor;

            // Apply to local player if in game
            var localPlayer = FindFirstObjectByType<PlayerController>();
            if(localPlayer != null && localPlayer.IsOwner) {
                localPlayer.playerMaterialPacketIndex.Value = _currentPacketIndex;
                localPlayer.playerBaseColor.Value = new Vector4(_currentBaseColor.r, _currentBaseColor.g, _currentBaseColor.b, _currentBaseColor.a);
                localPlayer.playerSmoothness.Value = _currentSmoothness;
                localPlayer.playerMetallic.Value = _currentMetallic;
                localPlayer.playerHeightStrength.Value = _currentHeightStrength;
                localPlayer.playerEmissionEnabled.Value = _currentEmissionEnabled;
                localPlayer.playerEmissionColor.Value = new Vector4(_currentEmissionColor.r, _currentEmissionColor.g, _currentEmissionColor.b, _currentEmissionColor.a);
                localPlayer.SaveMaterialCustomizationToPrefs();
            }

            // Update loadout preview model
            if(loadoutManager == null) return;
            var specularColor = new Color(0.2f, 0.2f, 0.2f, 1f); // Default specular color
            var heightStrength = _currentHeightStrength;
            loadoutManager.UpdatePreviewModelMaterial(
                _currentPacketIndex,
                _currentBaseColor,
                _currentSmoothness,
                _currentMetallic,
                specularColor,
                heightStrength,
                _currentEmissionEnabled,
                _currentEmissionColor);
            loadoutManager.NotifyCustomizationApplied();
        }

        private void ShowUnsavedChangesDialog() {
            if(_unsavedChangesModal == null) return;
            _unsavedChangesModal.RemoveFromClassList("hidden");
            _unsavedChangesModal.BringToFront();
        }

        private void HideUnsavedChangesDialog() {
            if(_unsavedChangesModal != null) {
                _unsavedChangesModal.AddToClassList("hidden");
            }
        }

        private void OnUnsavedChangesYes() {
            ApplyCustomization();
            HideUnsavedChangesDialog();
            if(OnBackFromCustomizationCallback != null) {
                OnBackFromCustomizationCallback.Invoke();
            }
        }

        private void OnUnsavedChangesNo() {
            // Discard changes - reload original values
            _currentBaseColor = _originalBaseColor;
            _currentSmoothness = _originalSmoothness;
            _currentMetallic = _originalMetallic;
            _currentPacketIndex = _originalPacketIndex;
            _currentHeightStrength = _originalHeightStrength;
            _currentEmissionEnabled = _originalEmissionEnabled;
            _currentEmissionColor = _originalEmissionColor;
            UpdateColorUI();
            UpdateSmoothnessUI();
            UpdateMetallicUI();
            UpdateHeightUI();
            UpdateEmissionUI();
            UpdatePacketSelectionHighlight();
            ApplyToLocalPlayer();
            if(loadoutManager != null) {
                loadoutManager.NotifyCustomizationApplied();
            }

            HideUnsavedChangesDialog();
            if(OnBackFromCustomizationCallback != null) {
                OnBackFromCustomizationCallback.Invoke();
            }
        }

        private void OnUnsavedChangesCancel() {
            HideUnsavedChangesDialog();
        }

        #region Material Packet Selection

        private void ShowMaterialPacketPanel() {
            if(_materialPacketPanel == null) return;
            _materialPacketPanel.RemoveFromClassList("hidden");
            _materialPacketPanel.style.display = DisplayStyle.Flex;
            _materialPacketPanel.BringToFront();
            if(loadoutManager != null) {
                loadoutManager.SetPreviewRotationEnabled(false);
            }
            UpdatePacketSelectionHighlight();
        }

        private void HideMaterialPacketPanel() {
            if(_materialPacketPanel == null) return;
            _materialPacketPanel.AddToClassList("hidden");
            _materialPacketPanel.style.display = StyleKeyword.Null;
            if(loadoutManager != null) {
                loadoutManager.SetPreviewRotationEnabled(true);
            }
        }

        private void BuildMaterialPacketGrid() {
            if(_materialPacketGrid == null) return;

            _materialPacketGrid.Clear();
            _packetButtons.Clear();

            var packetManager = PlayerMaterialPacketManager.Instance;
            List<PlayerMaterialPacket> packets = null;
            if(packetManager != null) {
                packets = packetManager.GetAllPackets();
            }
            if(packets == null || packets.Count == 0) {
                PlayerMaterialPacket fallbackPacket = null;
                if(packetManager != null) {
                    fallbackPacket = packetManager.GetNonePacket();
                }
                if(fallbackPacket == null) {
                    fallbackPacket = ScriptableObject.CreateInstance<PlayerMaterialPacket>();
                }
                fallbackPacket.packetName = string.IsNullOrEmpty(fallbackPacket.packetName) ? "None" : fallbackPacket.packetName;
                packets = new List<PlayerMaterialPacket> { fallbackPacket };
            }

            _availablePacketCount = packets.Count;

            for(var i = 0; i < packets.Count; i++) {
                var packet = packets[i];
                var button = CreatePacketButton(packet.packetName, i, false);
                _packetButtons[i] = button;
                _materialPacketGrid.Add(button);
            }

            var placeholderCount = Mathf.Max(0, PacketSlotTargetCount - _availablePacketCount);
            for(var j = 0; j < placeholderCount; j++) {
                var placeholderLabel = $"Locked {j + 1}";
                var placeholderButton = CreatePacketButton(placeholderLabel, _availablePacketCount + j, true);
                _materialPacketGrid.Add(placeholderButton);
            }

            ClampCurrentPacketIndex();
            UpdatePacketSelectionHighlight();
        }

        private Button CreatePacketButton(string label, int index, bool isPlaceholder) {
            var button = new Button { text = label };
            button.AddToClassList("material-packet-button");

            if(isPlaceholder) {
                button.AddToClassList("packet-button-placeholder");
                button.SetEnabled(false);
                return button;
            }

            button.RegisterCallback<MouseEnterEvent>(evt => {
                if(MouseEnterCallback != null) {
                    MouseEnterCallback.Invoke(evt);
                }
            });
            button.RegisterCallback<ClickEvent>(_ => {
                if(OnButtonClickedCallback != null) {
                    OnButtonClickedCallback.Invoke(false);
                }
                OnPacketButtonClicked(index);
            });

            return button;
        }

        private void OnPacketButtonClicked(int packetIndex) {
            if(packetIndex < 0 || packetIndex >= _availablePacketCount) return;
            if(packetIndex == _currentPacketIndex) return;

            _currentPacketIndex = packetIndex;
            UpdatePacketSelectionHighlight();
            UpdateColorPreview();
            ApplyToLocalPlayer();
            NotifyLoadoutDirty();
        }

        private void UpdatePacketSelectionHighlight() {
            foreach(var kvp in _packetButtons) {
                if(kvp.Value == null) continue;

                if(kvp.Key == _currentPacketIndex) {
                    kvp.Value.AddToClassList("packet-button-selected");
                } else {
                    kvp.Value.RemoveFromClassList("packet-button-selected");
                }
            }

            UpdateHeightControlState();
            UpdateEmissionAvailability();
        }

        private void ClampCurrentPacketIndex() {
            if(_availablePacketCount <= 0) {
                _availablePacketCount = 1;
            }

            _currentPacketIndex = Mathf.Clamp(_currentPacketIndex, 0, _availablePacketCount - 1);
        }

        private static string GetPacketName(int index) {
            var manager = PlayerMaterialPacketManager.Instance;
            if(manager == null) return "None";

            var clampedIndex = Mathf.Clamp(index, 0, Mathf.Max(0, manager.GetPacketCount() - 1));
            var packet = manager.GetPacket(clampedIndex);
            return packet != null ? packet.packetName : "None";
        }

        private bool CurrentPacketSupportsHeight() {
            var manager = PlayerMaterialPacketManager.Instance;
            if(manager == null) return false;

            var packet = manager.GetPacket(Mathf.Clamp(_currentPacketIndex, 0, manager.GetPacketCount() - 1));
            return packet != null && packet.heightMap != null;
        }

        private bool CurrentPacketSupportsEmission() {
            var manager = PlayerMaterialPacketManager.Instance;
            if(manager == null) return false;

            var packet = manager.GetPacket(Mathf.Clamp(_currentPacketIndex, 0, manager.GetPacketCount() - 1));
            return packet != null && packet.emissionMap != null;
        }

        private void UpdateHeightControlState() {
            var supportsHeight = CurrentPacketSupportsHeight();

            if(_heightSlider != null) {
                _heightSlider.SetEnabled(supportsHeight);
                _heightSlider.tooltip = supportsHeight ? string.Empty : "This packet does not include a height map.";
            }

            if(_heightValue == null) return;
            _heightValue.SetEnabled(supportsHeight);
            _heightValue.tooltip = supportsHeight ? string.Empty : "This packet does not include a height map.";
        }

        #endregion

        /// <summary>
        /// Applies the current customization values to the local player's visual controller and preview model.
        /// </summary>
        private void ApplyToLocalPlayer() {
            var localPlayer = FindFirstObjectByType<PlayerController>();
            if(localPlayer != null && localPlayer.IsOwner) {
                localPlayer.playerMaterialPacketIndex.Value = _currentPacketIndex;
                localPlayer.playerBaseColor.Value = new Vector4(_currentBaseColor.r, _currentBaseColor.g, _currentBaseColor.b, _currentBaseColor.a);
                localPlayer.playerSmoothness.Value = _currentSmoothness;
                localPlayer.playerMetallic.Value = _currentMetallic;
                localPlayer.playerHeightStrength.Value = _currentHeightStrength;
                localPlayer.playerEmissionEnabled.Value = _currentEmissionEnabled;
                localPlayer.playerEmissionColor.Value = new Vector4(_currentEmissionColor.r, _currentEmissionColor.g, _currentEmissionColor.b, _currentEmissionColor.a);
            }

            // Also update preview model in real-time
            if(loadoutManager == null) return;
            var specularColor = new Color(0.2f, 0.2f, 0.2f, 1f); // Default specular color
            var heightStrength = _currentHeightStrength;
            loadoutManager.UpdatePreviewModelMaterial(
                _currentPacketIndex,
                _currentBaseColor,
                _currentSmoothness,
                _currentMetallic,
                specularColor,
                heightStrength,
                _currentEmissionEnabled,
                _currentEmissionColor);
        }

        private void NotifyLoadoutDirty() {
            if(loadoutManager != null) {
                loadoutManager.NotifyCustomizationDirty();
            }
        }

        public void ReloadSavedCustomization() {
            LoadSavedCustomization();
            BuildMaterialPacketGrid();
            UpdatePacketSelectionHighlight();
            ApplyToLocalPlayer();
            if(loadoutManager != null) {
                loadoutManager.NotifyCustomizationApplied();
            }
        }
    }
}


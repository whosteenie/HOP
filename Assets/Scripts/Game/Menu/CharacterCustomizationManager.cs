using System;
using System.Collections.Generic;
using Game.Player.Core;
using Game.Player.Visual;
using Game.Settings;
using Game.UI.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Menu {
    /// <summary>
    /// Manages the character customization panel UI and material customization.
    /// </summary>
    public class CharacterCustomizationManager : UIElementBase {
        public static CharacterCustomizationManager Instance { get; private set; }

        [Header("References")]
        [SerializeField] private MainMenuManager mainMenuManager;
        [SerializeField] private LoadoutManager loadoutManager;

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

        protected override void Awake() {
            base.Awake();
            if(Instance != null && Instance != this) {
                Debug.LogWarning("[CharacterCustomizationManager] Multiple instances detected. Using the most recently awakened instance.");
            }
            Instance = this;

            if(mainMenuManager == null) {
                mainMenuManager = MainMenuManager.Instance;
            }
            if(mainMenuManager != null && uiDocument == null) {
                uiDocument = mainMenuManager.uiDocument;
            }
            if(uiDocument == null) {
                uiDocument = GetComponent<UIDocument>();
            }
            if(uiDocument == null) {
                uiDocument = GetComponentInParent<UIDocument>();
            }

            if(loadoutManager == null) {
                loadoutManager = LoadoutManager.Instance;
            }
        }

        protected override void OnEnable() {
            base.OnEnable();
            if(uiDocument == null) {
                if(mainMenuManager == null) {
                    mainMenuManager = MainMenuManager.Instance;
                }
                if(mainMenuManager != null) {
                    uiDocument = mainMenuManager.uiDocument;
                }
                if(uiDocument == null) {
                    uiDocument = GetComponent<UIDocument>();
                }
                if(uiDocument == null) {
                    uiDocument = GetComponentInParent<UIDocument>();
                }
            }

            if(Root != null || uiDocument == null) return;
            // Force initialization if Root is null
            if(uiDocument.rootVisualElement != null) {
                // This will be set by base.Awake(), but if OnEnable is called before Awake,
                // we need to ensure Root is set
            }
        }

        protected override void OnDestroy() {
            if(Instance == this) {
                Instance = null;
            }
            base.OnDestroy();
        }

        protected override void OnInitialize() {
            SetupUIReferences();
            SetupEventHandlers();
            LoadSavedCustomization();
            BuildMaterialPacketGrid();
            UpdatePacketSelectionHighlight();
        }

        protected override Dictionary<string, Type> GetRequiredElements() {
            return new Dictionary<string, Type>();
        }

        private void SetupUIReferences() {
            // Customization is now integrated into loadout panel, no separate panel needed

            // Color picker
            _colorPreviewButton = QOptional<Button>("color-preview-box");
            if(_colorPreviewButton == null) {
                _colorPreviewBox = QOptional<VisualElement>("color-preview-box");
            } else {
                _colorPreviewBox = _colorPreviewButton;
            }
            _colorRSlider = QOptional<Slider>("color-r-slider");
            _colorGSlider = QOptional<Slider>("color-g-slider");
            _colorBSlider = QOptional<Slider>("color-b-slider");
            _colorRInput = QOptional<IntegerField>("color-r-input");
            _colorGInput = QOptional<IntegerField>("color-g-input");
            _colorBInput = QOptional<IntegerField>("color-b-input");

            // Emission controls
            _emissionToggle = QOptional<Toggle>("emission-toggle");
            _emissionPreviewButton = QOptional<Button>("emission-preview-box");
            _emissionRSlider = QOptional<Slider>("emission-r-slider");
            _emissionGSlider = QOptional<Slider>("emission-g-slider");
            _emissionBSlider = QOptional<Slider>("emission-b-slider");
            _emissionRInput = QOptional<IntegerField>("emission-r-input");
            _emissionGInput = QOptional<IntegerField>("emission-g-input");
            _emissionBInput = QOptional<IntegerField>("emission-b-input");

            // Packet selection panel
            _materialPacketPanel = QOptional<VisualElement>("material-packet-panel");
            _materialPacketBackButton = QOptional<Button>("material-packet-back");
            QOptional<ScrollView>("material-packet-scroll");
            _materialPacketGrid = QOptional<VisualElement>("material-packet-grid");

            // Material properties
            _smoothnessSlider = QOptional<Slider>("smoothness-slider");
            _smoothnessValue = QOptional<TextField>("smoothness-value");
            _metallicSlider = QOptional<Slider>("metallic-slider");
            _metallicValue = QOptional<TextField>("metallic-value");
            _heightSlider = QOptional<Slider>("height-slider");
            _heightValue = QOptional<TextField>("height-value");

            // Buttons removed - customization now auto-applies when leaving loadout

            // Unsaved changes modal
            _unsavedChangesModal = QOptional<VisualElement>("unsaved-changes-modal");
            _unsavedChangesYes = QOptional<Button>("unsaved-changes-yes");
            _unsavedChangesNo = QOptional<Button>("unsaved-changes-no");
            _unsavedChangesCancel = QOptional<Button>("unsaved-changes-cancel");
        }

        private void SetupEventHandlers() {
            // Color sliders
            if(_colorRSlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => OnColorRChanged(evt.newValue);
                _colorRSlider.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _colorRSlider.UnregisterCallback(handler));
            }
            if(_colorGSlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => OnColorGChanged(evt.newValue);
                _colorGSlider.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _colorGSlider.UnregisterCallback(handler));
            }
            if(_colorBSlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => OnColorBChanged(evt.newValue);
                _colorBSlider.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _colorBSlider.UnregisterCallback(handler));
            }

            // Color inputs
            if(_colorRInput != null) {
                EventCallback<ChangeEvent<int>> handler = evt => OnColorRInputChanged(evt.newValue);
                _colorRInput.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _colorRInput.UnregisterCallback(handler));
            }
            if(_colorGInput != null) {
                EventCallback<ChangeEvent<int>> handler = evt => OnColorGInputChanged(evt.newValue);
                _colorGInput.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _colorGInput.UnregisterCallback(handler));
            }
            if(_colorBInput != null) {
                EventCallback<ChangeEvent<int>> handler = evt => OnColorBInputChanged(evt.newValue);
                _colorBInput.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _colorBInput.UnregisterCallback(handler));
            }

            // Color preview button -> open packet panel
            if(_colorPreviewButton != null) {
                EventCallback<MouseEnterEvent> enterHandler = evt => {
                    if(MouseEnterCallback != null) {
                        MouseEnterCallback.Invoke(evt);
                    }
                };
                _colorPreviewButton.RegisterCallback(enterHandler);
                RegisterCleanup(() => _colorPreviewButton.UnregisterCallback(enterHandler));

                EventCallback<ClickEvent> clickHandler = _ => {
                    if(OnButtonClickedCallback != null) {
                        OnButtonClickedCallback.Invoke(false);
                    }
                    ShowMaterialPacketPanel();
                };
                _colorPreviewButton.RegisterCallback(clickHandler);
                RegisterCleanup(() => _colorPreviewButton.UnregisterCallback(clickHandler));
            }

            // Packet panel back button
            if(_materialPacketBackButton != null) {
                EventCallback<MouseEnterEvent> enterHandler = evt => {
                    if(MouseEnterCallback != null) {
                        MouseEnterCallback.Invoke(evt);
                    }
                };
                _materialPacketBackButton.RegisterCallback(enterHandler);
                RegisterCleanup(() => _materialPacketBackButton.UnregisterCallback(enterHandler));

                EventCallback<ClickEvent> clickHandler = _ => {
                    if(OnButtonClickedCallback != null) {
                        OnButtonClickedCallback.Invoke(true);
                    }
                    HideMaterialPacketPanel();
                };
                _materialPacketBackButton.RegisterCallback(clickHandler);
                RegisterCleanup(() => _materialPacketBackButton.UnregisterCallback(clickHandler));
            }

            // Material sliders
            if(_smoothnessSlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => OnSmoothnessChanged(evt.newValue);
                _smoothnessSlider.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _smoothnessSlider.UnregisterCallback(handler));
            }
            if(_metallicSlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => OnMetallicChanged(evt.newValue);
                _metallicSlider.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _metallicSlider.UnregisterCallback(handler));
            }
            if(_heightSlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => OnHeightStrengthChanged(evt.newValue);
                _heightSlider.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _heightSlider.UnregisterCallback(handler));
            }
            if(_emissionRSlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => OnEmissionRChanged(evt.newValue);
                _emissionRSlider.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _emissionRSlider.UnregisterCallback(handler));
            }
            if(_emissionGSlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => OnEmissionGChanged(evt.newValue);
                _emissionGSlider.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _emissionGSlider.UnregisterCallback(handler));
            }
            if(_emissionBSlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => OnEmissionBChanged(evt.newValue);
                _emissionBSlider.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _emissionBSlider.UnregisterCallback(handler));
            }

            // Material value fields
            if(_smoothnessValue != null) {
                EventCallback<ChangeEvent<string>> handler = evt => {
                    if(!float.TryParse(evt.newValue, out var val)) return;
                    val = Mathf.Clamp01(val);
                    _smoothnessSlider.value = val;
                    _currentSmoothness = val;
                    UpdateSmoothnessDisplay();
                    ApplyToLocalPlayer();
                    NotifyLoadoutDirty();
                };
                _smoothnessValue.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _smoothnessValue.UnregisterCallback(handler));
            }

            if(_metallicValue != null) {
                EventCallback<ChangeEvent<string>> handler = evt => {
                    if(!float.TryParse(evt.newValue, out var val)) return;
                    val = Mathf.Clamp01(val);
                    _metallicSlider.value = val;
                    _currentMetallic = val;
                    UpdateMetallicDisplay();
                    ApplyToLocalPlayer();
                    NotifyLoadoutDirty();
                };
                _metallicValue.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _metallicValue.UnregisterCallback(handler));
            }

            if(_heightValue != null) {
                EventCallback<ChangeEvent<string>> handler = evt => {
                    if(!float.TryParse(evt.newValue, out var val)) return;
                    val = Mathf.Clamp(val, MinHeightStrength, MaxHeightStrength);
                    _heightSlider.value = val;
                    _currentHeightStrength = val;
                    UpdateHeightDisplay();
                    ApplyToLocalPlayer();
                    NotifyLoadoutDirty();
                };
                _heightValue.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _heightValue.UnregisterCallback(handler));
            }

            // Emission toggle & inputs
            if(_emissionToggle != null) {
                EventCallback<MouseEnterEvent> enterHandler = evt => {
                    if(MouseEnterCallback != null) {
                        MouseEnterCallback.Invoke(evt);
                    }
                };
                _emissionToggle.RegisterCallback(enterHandler);
                RegisterCleanup(() => _emissionToggle.UnregisterCallback(enterHandler));

                EventCallback<ChangeEvent<bool>> toggleHandler = evt => OnEmissionToggleChanged(evt.newValue);
                _emissionToggle.RegisterValueChangedCallback(toggleHandler);
                RegisterCleanup(() => _emissionToggle.UnregisterCallback(toggleHandler));
            }

            if(_emissionPreviewButton != null) {
                EventCallback<MouseEnterEvent> enterHandler = evt => {
                    if(MouseEnterCallback != null) {
                        MouseEnterCallback.Invoke(evt);
                    }
                };
                _emissionPreviewButton.RegisterCallback(enterHandler);
                RegisterCleanup(() => _emissionPreviewButton.UnregisterCallback(enterHandler));
            }

            if(_emissionRInput != null) {
                EventCallback<ChangeEvent<int>> handler = evt => OnEmissionRInputChanged(evt.newValue);
                _emissionRInput.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _emissionRInput.UnregisterCallback(handler));
            }
            if(_emissionGInput != null) {
                EventCallback<ChangeEvent<int>> handler = evt => OnEmissionGInputChanged(evt.newValue);
                _emissionGInput.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _emissionGInput.UnregisterCallback(handler));
            }
            if(_emissionBInput != null) {
                EventCallback<ChangeEvent<int>> handler = evt => OnEmissionBInputChanged(evt.newValue);
                _emissionBInput.RegisterValueChangedCallback(handler);
                RegisterCleanup(() => _emissionBInput.UnregisterCallback(handler));
            }

            // Buttons removed - customization now auto-applies when leaving loadout

            // Unsaved changes modal
            if(_unsavedChangesYes != null) {
                EventCallback<ClickEvent> handler = _ => OnUnsavedChangesYes();
                _unsavedChangesYes.RegisterCallback(handler);
                RegisterCleanup(() => _unsavedChangesYes.UnregisterCallback(handler));
            }
            if(_unsavedChangesNo != null) {
                EventCallback<ClickEvent> handler = _ => OnUnsavedChangesNo();
                _unsavedChangesNo.RegisterCallback(handler);
                RegisterCleanup(() => _unsavedChangesNo.UnregisterCallback(handler));
            }

            if(_unsavedChangesCancel == null) return;
            {
                EventCallback<ClickEvent> handler = _ => OnUnsavedChangesCancel();
                _unsavedChangesCancel.RegisterCallback(handler);
                RegisterCleanup(() => _unsavedChangesCancel.UnregisterCallback(handler));
            }
        }

        public void ShowCustomization() {
            // Ensure callbacks are set up (in case they weren't set in Initialize)
            if(mainMenuManager != null) {
                OnButtonClickedCallback = MainMenuManager.OnButtonClicked;
                MouseEnterCallback = MainMenuManager.MouseEnter;
                OnBackFromCustomizationCallback = () => {
                    Debug.Log("[CharacterCustomizationManager] Back callback invoked from ShowCustomization");
                    if(mainMenuManager == null) return;
                    var loadoutPanel = QOptional<VisualElement>("loadout-panel");
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
            var c = GameSettings.Data.player.customization;

            _currentPacketIndex = Mathf.Max(0, c.materialPacketIndex);
            _currentBaseColor = new Color(c.baseColor.x, c.baseColor.y, c.baseColor.z, c.baseColor.w);
            _currentSmoothness = c.smoothness;
            _currentMetallic = c.metallic;
            _currentHeightStrength = Mathf.Clamp(c.heightStrength, MinHeightStrength, MaxHeightStrength);
            _currentEmissionEnabled = c.emissionEnabled;
            _currentEmissionColor = new Color(c.emissionColor.x, c.emissionColor.y, c.emissionColor.z, c.emissionColor.w);

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

        /// <summary>
        /// Applies the current customization values. Called automatically when leaving loadout.
        /// </summary>
        public void ApplyCustomization() {
            // Save to PlayerPrefs
            var c = GameSettings.Data.player.customization;
            c.materialPacketIndex = Mathf.Max(0, _currentPacketIndex);
            c.baseColor = new Vector4(_currentBaseColor.r, _currentBaseColor.g, _currentBaseColor.b, _currentBaseColor.a);
            c.smoothness = Mathf.Clamp01(_currentSmoothness);
            c.metallic = Mathf.Clamp01(_currentMetallic);
            c.heightStrength = Mathf.Clamp(_currentHeightStrength, MinHeightStrength, MaxHeightStrength);
            c.emissionEnabled = _currentEmissionEnabled;
            c.emissionColor = new Vector4(_currentEmissionColor.r, _currentEmissionColor.g, _currentEmissionColor.b, _currentEmissionColor.a);

            GameSettings.Save();

            // Update original values
            _originalBaseColor = _currentBaseColor;
            _originalSmoothness = _currentSmoothness;
            _originalMetallic = _currentMetallic;
            _originalPacketIndex = _currentPacketIndex;
            _originalHeightStrength = _currentHeightStrength;
            _originalEmissionEnabled = _currentEmissionEnabled;
            _originalEmissionColor = _currentEmissionColor;

            // Apply to local player if in game
            var localPlayer = PlayerController.LocalPlayer;
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

            EventCallback<MouseEnterEvent> enterHandler = evt => {
                if(MouseEnterCallback != null) {
                    MouseEnterCallback.Invoke(evt);
                }
            };
            button.RegisterCallback(enterHandler);
            RegisterCleanup(() => button.UnregisterCallback(enterHandler));

            EventCallback<ClickEvent> clickHandler = _ => {
                if(OnButtonClickedCallback != null) {
                    OnButtonClickedCallback.Invoke(false);
                }
                OnPacketButtonClicked(index);
            };
            button.RegisterCallback(clickHandler);
            RegisterCleanup(() => button.UnregisterCallback(clickHandler));

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
            var localPlayer = PlayerController.LocalPlayer;
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


using System;
using System.Collections;
using System.Collections.Generic;
using Game.Player;
using UnityEngine;
using UnityEngine.UIElements;

namespace Network.Singletons {
    public class LoadoutManager : MonoBehaviour {
        [Header("Weapon Data")]
        [SerializeField] private WeaponData[] primaryWeapons;
        [SerializeField] private WeaponData[] secondaryWeapons;
        [SerializeField] private WeaponData[] tertiaryWeapons;

        [Header("Color/Material Options")]
        [SerializeField] private Material[] playerMaterials; // Must have 6 materials matching color-0 through color-5

        [Header("3D Preview")]
        [SerializeField] private Camera previewCamera;
        [SerializeField] private Transform previewPositionTransform; // Transform where the preview model should spawn
        [SerializeField] private GameObject playerModelPrefab;
        [SerializeField] private GameObject previewPlayerRoot;
        [SerializeField] private List<GameObject> previewPrimaryWeapons = new();
        [SerializeField] private List<GameObject> previewSecondaryWeapons = new();
        [SerializeField] private Transform secondaryWeaponParent; // Optional explicit parent for secondary holster models
        private RenderTexture previewRenderTexture; // Will be created/updated dynamically
        [SerializeField] private float panelTransitionDuration = 0.4f;

        private UIDocument _uiDocument;
        private VisualElement _root;

        // UI Elements
        private TextField _playerNameInput;
        private Button _applyLoadoutButton;
        private Button _backLoadoutButton;

        private VisualElement _primarySlot;
        private VisualElement _secondarySlot;
        private VisualElement _tertiarySlot;
        private VisualElement _primaryDropdown;
        private VisualElement _secondaryDropdown;
        private VisualElement _tertiaryDropdown;
        private ScrollView _primaryDropdownScroll;
        private ScrollView _secondaryDropdownScroll;
        private ScrollView _tertiaryDropdownScroll;

        private Image _primaryWeaponImage;
        private Image _secondaryWeaponImage;
        private Image _tertiaryWeaponImage;

        private GameObject _previewPlayerModel;
        private readonly List<GameObject> _previewWeaponModels = new();
        private readonly List<GameObject> _previewSecondaryWeaponModels = new();

        // Rotation state
        private bool _isDragging;
        private Vector2 _lastMousePosition;
        private float _currentRotationVelocity;
        private float _rotationY;
        private VisualElement _viewport;
        private const float MIN_MOVEMENT_THRESHOLD = 0.5f; // Minimum pixel movement to register as actual drag
        private float _lastMovementDelta; // Track last movement delta to check on release
        private bool _rotationEnabled = true;

        // Animation state
        private VisualElement _weaponContainer;
        private VisualElement _customizationContainer;
        private VisualElement _nameContainer;
        private VisualElement _backgroundElement;
        private Coroutine _backgroundFadeCoroutine;
        private Coroutine _slideInCoroutine;
        private Coroutine _slideOutCoroutine;
        private bool _isSlidingIn;
        private const float SLIDE_ANIMATION_DURATION = 0.4f;
        private const float BACKGROUND_FADE_DURATION = 0.2f;
        private bool _containersInitialized;
        private static readonly Vector2 WeaponOffscreenPercent = new(-200f, 0f);
        private static readonly Vector2 CustomizationOffscreenPercent = new(200f, 0f);
        private static readonly Vector2 NameOffscreenPercent = new(0f, 200f);

        // Current selections
        private int _selectedPrimaryIndex;
        private int _selectedSecondaryIndex;
        private int _selectedTertiaryIndex;
        private string _currentPrimarySlotClass;
        private string _currentSecondarySlotClass;
        private string _currentTertiarySlotClass;
        private VisualElement _currentOpenDropdown;
        private bool _outsideClickHandlerRegistered;
        private string _currentPlayerName;
        private string _savedPlayerName;

        private int _savedPrimaryIndex;
        private int _savedSecondaryIndex;
        private int _savedTertiaryIndex;
        private bool _customizationDirty;
        private bool _hasUnsavedChanges;

        [SerializeField] private MainMenuManager mainMenuManager;

        // Unsaved changes UI
        private VisualElement _loadoutUnsavedModal;
        private Button _loadoutUnsavedYes;
        private Button _loadoutUnsavedNo;
        private Button _loadoutUnsavedCancel;

        private void Awake() {
            _uiDocument = mainMenuManager.uiDocument;
            ResetPreviewCameraTarget();
        }

        private void OnEnable() {
            _root = _uiDocument.rootVisualElement;
            SetupUIReferences();
            SetupEventHandlers();
            RegisterOutsideClickHandler();
            LoadSavedLoadout();
            
            // Apply button is always visible now
        }

        private void OnDisable() {
            if(_root == null || !_outsideClickHandlerRegistered) return;
            _root.UnregisterCallback<PointerDownEvent>(OnRootPointerDown, TrickleDown.TrickleDown);
            _outsideClickHandlerRegistered = false;
            
            // Clean up viewport handlers
            if(_viewport != null) {
                _viewport.UnregisterCallback<PointerDownEvent>(OnViewportPointerDown);
                _viewport.UnregisterCallback<PointerMoveEvent>(OnViewportPointerMove);
                _viewport.UnregisterCallback<PointerUpEvent>(OnViewportPointerUp);
                _viewport.UnregisterCallback<PointerLeaveEvent>(OnViewportPointerLeave);
            }
            
            // Clean up root-level viewport handlers
            if(_root != null) {
                _root.UnregisterCallback<PointerDownEvent>(OnRootPointerDownForViewport, TrickleDown.TrickleDown);
                _root.UnregisterCallback<PointerMoveEvent>(OnRootPointerMoveForViewport, TrickleDown.TrickleDown);
                _root.UnregisterCallback<PointerUpEvent>(OnRootPointerUpForViewport, TrickleDown.TrickleDown);
            }

            ResetPreviewCameraTarget();
        }
        
        private void OnDestroy() {
            ReleasePreviewRenderTexture(true);
        }

        public void ShowLoadout() {
            Setup3DPreview();
            
            // Ensure containers start off-screen the first time
            if(!_containersInitialized) {
                SetContainerTranslate(_weaponContainer, WeaponOffscreenPercent);
                SetContainerTranslate(_customizationContainer, CustomizationOffscreenPercent);
                SetContainerTranslate(_nameContainer, NameOffscreenPercent);
                _containersInitialized = true;
            }
            
            // Stop any slide-out animation and start slide-in
            StopSlideAnimations();
            FadeBackground(true);
            StartSlideIn();
        }

        private void HideLoadout() {
            // Background slide-out handled separately
        }

        private void ReleasePreviewRenderTexture(bool destroyAsset = false) {
            if(previewRenderTexture == null) return;

            if(previewCamera != null && previewCamera.targetTexture == previewRenderTexture) {
                previewCamera.targetTexture = null;
            }

            previewRenderTexture.Release();

            if(destroyAsset) {
                DestroyImmediate(previewRenderTexture, true);
            }

            previewRenderTexture = null;
        }

        private void ResetPreviewCameraTarget() {
            if(previewCamera == null) return;

            if(previewCamera.targetTexture != null) {
                previewCamera.targetTexture = null;
            }

            previewCamera.enabled = false;

            var parentCamera = previewCamera.transform.parent?.GetComponent<Camera>();
            if(parentCamera != null && parentCamera.targetTexture != null) {
                parentCamera.targetTexture = null;
            }
        }

        private void SetupUIReferences() {
            // Get container references for animations
            _weaponContainer = _root.Q<VisualElement>("weapon-selection-container");
            _customizationContainer = _root.Q<VisualElement>("customization-container");
            _nameContainer = _root.Q<VisualElement>("name-buttons-container");
            _backgroundElement = _root.Q<VisualElement>("player-model-background");

            if(_backgroundElement != null) {
                _backgroundElement.style.opacity = new StyleFloat(0f);
                _backgroundElement.AddToClassList("hidden");
                _backgroundElement.style.display = StyleKeyword.Null;
            }

            // Unsaved changes modal
            _loadoutUnsavedModal = _root.Q<VisualElement>("loadout-unsaved-changes-modal");
            _loadoutUnsavedYes = _root.Q<Button>("loadout-unsaved-yes");
            _loadoutUnsavedNo = _root.Q<Button>("loadout-unsaved-no");
            _loadoutUnsavedCancel = _root.Q<Button>("loadout-unsaved-cancel");
            _loadoutUnsavedYes?.RegisterCallback<ClickEvent>(_ => OnLoadoutUnsavedYes());
            _loadoutUnsavedNo?.RegisterCallback<ClickEvent>(_ => OnLoadoutUnsavedNo());
            _loadoutUnsavedCancel?.RegisterCallback<ClickEvent>(_ => OnLoadoutUnsavedCancel());
            _loadoutUnsavedYes?.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);
            _loadoutUnsavedNo?.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);
            _loadoutUnsavedCancel?.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);
            
            // Containers start off-screen via USS, no need to initialize positions here
            
            // Name input
            _playerNameInput = _root.Q<TextField>("player-name-input");
            _applyLoadoutButton = _root.Q<Button>("apply-loadout-button");
            
            if(_applyLoadoutButton != null) {
                // Unregister any existing handlers first
                _applyLoadoutButton.clicked -= OnApplyLoadoutClicked;
                _applyLoadoutButton.UnregisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);
                
                // Register handlers
                _applyLoadoutButton.clicked += () => {
                    Debug.Log("[LoadoutManager] Apply button clicked");
                    if(mainMenuManager != null) {
                        mainMenuManager.OnButtonClicked();
                    }
                    OnApplyLoadoutClicked();
                };
                _applyLoadoutButton.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);
            } else {
                Debug.LogError("[LoadoutManager] Apply button not found!");
            }

            // Weapon slots (main equipped slot)
            _primarySlot = _root.Q<VisualElement>("primary-weapon-slot");
            _secondarySlot = _root.Q<VisualElement>("secondary-weapon-slot");
            _tertiarySlot = _root.Q<VisualElement>("tertiary-weapon-slot");

            _primaryDropdown = _root.Q<VisualElement>("primary-dropdown");
            _secondaryDropdown = _root.Q<VisualElement>("secondary-dropdown");
            _tertiaryDropdown = _root.Q<VisualElement>("tertiary-dropdown");
            _primaryDropdownScroll = _primaryDropdown?.Q<ScrollView>("primary-scroll");
            _secondaryDropdownScroll = _secondaryDropdown?.Q<ScrollView>("secondary-scroll");
            _tertiaryDropdownScroll = _tertiaryDropdown?.Q<ScrollView>("tertiary-scroll");

            _primaryWeaponImage = _root.Q<Image>("primary-weapon-image");
            _secondaryWeaponImage = _root.Q<Image>("secondary-weapon-image");
            _tertiaryWeaponImage = _root.Q<Image>("tertiary-weapon-image");

            // Back button
            _backLoadoutButton = _root.Q<Button>("back-to-main-from-loadout");
            if(_backLoadoutButton != null) {
                // Unregister any existing handlers first
                _backLoadoutButton.clicked -= OnBackClicked;
                _backLoadoutButton.UnregisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);
                
                // Register handlers
                _backLoadoutButton.clicked += () => {
                    Debug.Log("[LoadoutManager] Back button clicked");
                    if(mainMenuManager != null) {
                        mainMenuManager.OnButtonClicked(true);
                    }
                    OnBackClicked();
                };
                _backLoadoutButton.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);
            } else {
                Debug.LogError("[LoadoutManager] Back button not found!");
            }
        }

        private void SetupEventHandlers() {
            // Name input change
            _playerNameInput.RegisterValueChangedCallback(evt => OnNameChanged(evt.newValue));

            // Weapon slot clicks (main equipped slot - opens dropdown)
            _primarySlot.RegisterCallback<ClickEvent>(_ => ToggleWeaponDropdown(_primaryDropdown));
            _primarySlot.RegisterCallback<ClickEvent>(_ => mainMenuManager.OnButtonClicked());
            _primarySlot.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);

            _secondarySlot.RegisterCallback<ClickEvent>(_ => ToggleWeaponDropdown(_secondaryDropdown));
            _secondarySlot.RegisterCallback<ClickEvent>(_ => mainMenuManager.OnButtonClicked());
            _secondarySlot.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);

            _tertiarySlot.RegisterCallback<ClickEvent>(_ => ToggleWeaponDropdown(_tertiaryDropdown));
            _tertiarySlot.RegisterCallback<ClickEvent>(_ => mainMenuManager.OnButtonClicked());
            _tertiarySlot.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);

            // Populate weapon dropdowns
            PopulateWeaponDropdown(_primaryDropdown.Q<ScrollView>("primary-scroll"), primaryWeapons,
                _selectedPrimaryIndex, SelectPrimaryWeapon);
            PopulateWeaponDropdown(_secondaryDropdown.Q<ScrollView>("secondary-scroll"), secondaryWeapons,
                _selectedSecondaryIndex, SelectSecondaryWeapon);
            PopulateWeaponDropdown(_tertiaryDropdown.Q<ScrollView>("tertiary-scroll"), tertiaryWeapons,
                _selectedTertiaryIndex, SelectTertiaryWeapon);
        }

        private void RegisterOutsideClickHandler() {
            if(_outsideClickHandlerRegistered || _root == null) return;
            _root.RegisterCallback<PointerDownEvent>(OnRootPointerDown, TrickleDown.TrickleDown);
            _outsideClickHandlerRegistered = true;
        }

        private void LoadSavedLoadout() {
            // Load name
            _savedPlayerName = PlayerPrefs.GetString("PlayerName", "Player");
            _currentPlayerName = _savedPlayerName;

            if(_playerNameInput != null) {
                _playerNameInput.value = _currentPlayerName;
            }

            // Load weapons (equipped weapon index saved per slot)
            _selectedPrimaryIndex = PlayerPrefs.GetInt("PrimaryWeaponIndex", 0);
            _selectedSecondaryIndex = PlayerPrefs.GetInt("SecondaryWeaponIndex", 0);
            _selectedTertiaryIndex = PlayerPrefs.GetInt("TertiaryWeaponIndex", 0);
            _savedPrimaryIndex = _selectedPrimaryIndex;
            _savedSecondaryIndex = _selectedSecondaryIndex;
            _savedTertiaryIndex = _selectedTertiaryIndex;
            _customizationDirty = false;
            UpdateDirtyState();

            UpdateWeaponImages();
            UpdatePlayerModel();
        }


        private void OnNameChanged(string newName) {
            _currentPlayerName = newName;
            // Apply button is always visible, no need to show/hide it
            UpdateDirtyState();
        }

        private void OnApplyLoadoutClicked() {
            // Save name
            _savedPlayerName = _currentPlayerName;
            PlayerPrefs.SetString("PlayerName", _savedPlayerName);
            
            // Save weapons (already saved when selected, but ensure they're current)
            PlayerPrefs.SetInt("PrimaryWeaponIndex", _selectedPrimaryIndex);
            PlayerPrefs.SetInt("SecondaryWeaponIndex", _selectedSecondaryIndex);
            PlayerPrefs.SetInt("TertiaryWeaponIndex", _selectedTertiaryIndex);
            
            // Save customization (apply customization changes)
            var customizationManager = FindFirstObjectByType<CharacterCustomizationManager>();
            if(customizationManager != null) {
                customizationManager.ApplyCustomization();
            }
            
            PlayerPrefs.Save();
            Debug.Log($"[LoadoutManager] All loadout settings saved: Name={_savedPlayerName}, Weapons={_selectedPrimaryIndex}/{_selectedSecondaryIndex}/{_selectedTertiaryIndex}");

            _savedPrimaryIndex = _selectedPrimaryIndex;
            _savedSecondaryIndex = _selectedSecondaryIndex;
            _savedTertiaryIndex = _selectedTertiaryIndex;
            _customizationDirty = false;
            UpdateDirtyState();
        }

        private void ToggleWeaponDropdown(VisualElement dropdown) {
            if(dropdown == null) {
                CloseAllDropdowns();
                return;
            }

            var isCurrentlyOpen = _currentOpenDropdown == dropdown && !dropdown.ClassListContains("hidden");
            if(isCurrentlyOpen) {
                CloseAllDropdowns();
                return;
            }

            var shouldOpen = dropdown.ClassListContains("hidden");
            CloseAllDropdowns();

            if(shouldOpen) {
                RefreshDropdownContent(dropdown);
                dropdown.RemoveFromClassList("hidden");
                SetSlotDropdownOpen(dropdown, true);
                _currentOpenDropdown = dropdown;
            } else {
                SetSlotDropdownOpen(dropdown, false);
            }
        }

        private void RefreshDropdownContent(VisualElement dropdown) {
            if(dropdown == null) return;

            if(dropdown == _primaryDropdown) {
                PopulateWeaponDropdown(_primaryDropdownScroll, primaryWeapons, _selectedPrimaryIndex,
                    SelectPrimaryWeapon);
            } else if(dropdown == _secondaryDropdown) {
                PopulateWeaponDropdown(_secondaryDropdownScroll, secondaryWeapons, _selectedSecondaryIndex,
                    SelectSecondaryWeapon);
            } else if(dropdown == _tertiaryDropdown) {
                PopulateWeaponDropdown(_tertiaryDropdownScroll, tertiaryWeapons, _selectedTertiaryIndex,
                    SelectTertiaryWeapon);
            }
        }

        private void PopulateWeaponDropdown(ScrollView scroll, WeaponData[] weapons, int selectedIndex,
            Action<int> onSelect) {
            if(scroll == null) return;

            var container = scroll.contentContainer;
            container.Clear();
            
            // Set container to horizontal layout
            container.style.flexDirection = FlexDirection.Row;

            if(weapons is not { Length: > 1 }) {
                // No alternatives to show
                return;
            }

            for(var i = 0; i < weapons.Length; i++) {
                if(i == selectedIndex) continue;

                var weaponOption = new VisualElement();
                weaponOption.AddToClassList("weapon-option");

                var weaponImage = new Image();
                weaponImage.AddToClassList("weapon-option-image");
                weaponImage.sprite = weapons[i].icon;
                weaponOption.Add(weaponImage);

                var index = i;
                weaponOption.RegisterCallback<ClickEvent>(evt => {
                    onSelect(index);
                    ToggleWeaponDropdown(null);
                    evt?.StopPropagation();
                    evt?.StopImmediatePropagation();
                });

                container.Add(weaponOption);
            }
        }

        private void CloseAllDropdowns() {
            _primaryDropdown?.AddToClassList("hidden");
            _secondaryDropdown?.AddToClassList("hidden");
            _tertiaryDropdown?.AddToClassList("hidden");
            SetSlotDropdownOpen(_primaryDropdown, false);
            SetSlotDropdownOpen(_secondaryDropdown, false);
            SetSlotDropdownOpen(_tertiaryDropdown, false);
            _currentOpenDropdown = null;
        }

        private void SetSlotDropdownOpen(VisualElement dropdown, bool isOpen) {
            var slot = GetSlotForDropdown(dropdown);
            if(slot == null) return;

            if(isOpen) {
                slot.AddToClassList("dropdown-open");
            } else {
                slot.RemoveFromClassList("dropdown-open");
            }
        }

        private void SelectPrimaryWeapon(int index) {
            _selectedPrimaryIndex = index;
            UpdateWeaponImages();
            UpdateDirtyState();
        }

        private void SelectSecondaryWeapon(int index) {
            _selectedSecondaryIndex = index;
            UpdateWeaponImages();
            UpdateDirtyState();
        }

        private void SelectTertiaryWeapon(int index) {
            _selectedTertiaryIndex = index;
            UpdateWeaponImages();
            UpdateDirtyState();
        }

        private void UpdateWeaponImages() {
            UpdateWeaponSlot(primaryWeapons, ref _selectedPrimaryIndex, _primaryWeaponImage, _primarySlot,
                ref _currentPrimarySlotClass, "weapon-primary");
            UpdateWeaponSlot(secondaryWeapons, ref _selectedSecondaryIndex, _secondaryWeaponImage, _secondarySlot,
                ref _currentSecondarySlotClass, "weapon-secondary");
            UpdateWeaponSlot(tertiaryWeapons, ref _selectedTertiaryIndex, _tertiaryWeaponImage, _tertiarySlot,
                ref _currentTertiarySlotClass, "weapon-tertiary");

            UpdatePreviewWeaponModel();
        }

        private void OnRootPointerDown(PointerDownEvent evt) {
            if(_currentOpenDropdown == null || evt == null) return;

            if(evt.target is VisualElement ve && IsWithinDropdownOrSlot(ve)) {
                return;
            }

            CloseAllDropdowns();
        }

        private bool IsWithinDropdownOrSlot(VisualElement element) {
            var slot = GetSlotForDropdown(_currentOpenDropdown);

            while(element != null) {
                if(element == _currentOpenDropdown || element == slot) return true;
                element = element.parent;
            }

            return false;
        }

        private VisualElement GetSlotForDropdown(VisualElement dropdown) {
            if(dropdown == _primaryDropdown) return _primarySlot;
            if(dropdown == _secondaryDropdown) return _secondarySlot;
            return dropdown == _tertiaryDropdown ? _tertiarySlot : null;
        }

        private static void UpdateWeaponSlot(WeaponData[] weapons, ref int selectedIndex, Image targetImage,
            VisualElement slotElement, ref string currentClass, string classPrefix) {
            if(weapons == null || weapons.Length == 0) {
                selectedIndex = 0;
                if(targetImage != null) {
                    targetImage.sprite = null;
                    targetImage.style.visibility = Visibility.Hidden;
                }

                UpdateWeaponSlotClass(slotElement, ref currentClass, null);
                return;
            }

            selectedIndex = Mathf.Clamp(selectedIndex, 0, weapons.Length - 1);
            var weapon = weapons[selectedIndex];

            if(targetImage != null) {
                targetImage.sprite = weapon?.icon;
                targetImage.style.visibility = weapon != null ? Visibility.Visible : Visibility.Hidden;
            }

            var displayName = weapon != null && !string.IsNullOrEmpty(weapon.weaponName)
                ? weapon.weaponName
                : weapon?.weaponPrefab != null
                    ? weapon.weaponPrefab.name
                    : "weapon";

            var targetClass = weapon != null ? $"{classPrefix}-{SanitizeForClassName(displayName)}" : null;
            UpdateWeaponSlotClass(slotElement, ref currentClass, targetClass);
        }

        private static void UpdateWeaponSlotClass(VisualElement slotElement, ref string currentClass, string newClass) {
            if(slotElement == null) return;

            if(!string.IsNullOrEmpty(currentClass)) {
                slotElement.RemoveFromClassList(currentClass);
            }

            if(!string.IsNullOrEmpty(newClass)) {
                slotElement.AddToClassList(newClass);
            }

            currentClass = newClass;
        }

        private static string SanitizeForClassName(string value) {
            if(string.IsNullOrEmpty(value)) return "unknown";

            value = value.Trim().ToLowerInvariant();

            var sanitizedChars = new char[value.Length];
            for(var i = 0; i < value.Length; i++) {
                var c = value[i];
                sanitizedChars[i] = char.IsLetterOrDigit(c) ? c : '-';
            }

            return new string(sanitizedChars);
        }

        private void Setup3DPreview() {
            if(previewCamera == null) return;

            // Ensure main camera (parent) is enabled to render the world
            var mainCam = previewCamera.transform.parent?.GetComponent<Camera>();
            if(mainCam != null) {
                mainCam.enabled = true;
            }

            // Create or update render texture to match current screen resolution
            var screenWidth = Screen.width;
            var screenHeight = Screen.height;
            
            if(previewRenderTexture == null || previewRenderTexture.width != screenWidth || previewRenderTexture.height != screenHeight) {
                // Release old texture if it exists
                ReleasePreviewRenderTexture();
                
                // Create new render texture matching screen resolution with 8x MSAA
                previewRenderTexture = new RenderTexture(screenWidth, screenHeight, 24, RenderTextureFormat.ARGB32)
                    {
                        antiAliasing = 8, // 8x MSAA for smooth rendering
                        name = "PlayerPreviewRT"
                    };
                Debug.Log($"[LoadoutManager] Created render texture: {screenWidth}x{screenHeight} with 8x MSAA");
            }

            // Ensure preview camera is enabled and rendering to RenderTexture
            previewCamera.targetTexture = previewRenderTexture;
            previewCamera.enabled = true;
            
            previewCamera.Render();

            if(_previewPlayerModel == null) {
                if(previewPlayerRoot != null) {
                    _previewPlayerModel = previewPlayerRoot;
                    _previewPlayerModel.SetActive(true);
                } else if(playerModelPrefab != null) {
                    Vector3 modelPosition;
                    Quaternion modelRotation;
                    
                    if(previewPositionTransform != null) {
                        modelPosition = previewPositionTransform.position;
                        modelRotation = previewPositionTransform.rotation;
                    } else {
                        modelPosition = Vector3.zero;
                        modelRotation = Quaternion.Euler(0, 180, 0);
                    }
                    
                    _previewPlayerModel = Instantiate(playerModelPrefab, modelPosition, modelRotation);
                } else {
                    Debug.LogWarning("[LoadoutManager] No preview player root or prefab assigned.");
                    return;
                }
            }
            // Material at index 1 should be set to "None" in the prefab for verification
            // We'll apply the new material packet system via UpdatePlayerModel()
            CachePreviewWeaponModels();
            UpdatePreviewWeaponModel();

            // Initialize rotation state
            _rotationY = 180f; // Match the initial rotation from Instantiate
            _currentRotationVelocity = 0f;
            _isDragging = false;

            // Setup the background (full-screen display) and viewport (input detection)
            var background = _root.Q<VisualElement>("player-model-background");
            _viewport = _root.Q<VisualElement>("player-model-viewport");
            var uiOverlay = _root.Q<VisualElement>("ui-overlay");
            
            Debug.Log($"[LoadoutManager] Setup3DPreview - Background: {background != null}, Viewport: {_viewport != null}, Overlay: {uiOverlay != null}, RenderTexture: {previewRenderTexture != null}, Model: {_previewPlayerModel != null}");
            
            // Set overlay picking mode to ignore so events can pass through
            if(uiOverlay != null) {
                uiOverlay.pickingMode = PickingMode.Ignore;
                Debug.Log($"[LoadoutManager] UI Overlay picking mode set to: {uiOverlay.pickingMode}");
            }
            
            // Set the render texture as background on the full-screen element
            if(background != null && previewRenderTexture != null) {
                background.style.backgroundImage =
                    new StyleBackground(Background.FromRenderTexture(previewRenderTexture));
                background.pickingMode = PickingMode.Ignore; // Don't capture input, just display
                Debug.Log("[LoadoutManager] Background image set on full-screen element");
            }
            
            // Setup viewport for input detection only
            if(_viewport != null && previewRenderTexture != null) {
                // CRITICAL: Set picking mode to Position so we can receive mouse events for rotation
                _viewport.pickingMode = PickingMode.Position;
                
                // Ensure the viewport can receive input events
                _viewport.focusable = false; // Don't make it focusable, just receive pointer events
                
                // Make sure viewport is visible and can receive events
                _viewport.style.display = DisplayStyle.Flex;
                _viewport.style.visibility = Visibility.Visible;
                
                Debug.Log($"[LoadoutManager] Viewport picking mode set to: {_viewport.pickingMode}, Size: {_viewport.layout.width}x{_viewport.layout.height}, Display: {_viewport.style.display}, Visibility: {_viewport.style.visibility}");
                
                // Unregister any existing handlers first to avoid duplicates
                _viewport.UnregisterCallback<PointerDownEvent>(OnViewportPointerDown);
                _viewport.UnregisterCallback<PointerMoveEvent>(OnViewportPointerMove);
                _viewport.UnregisterCallback<PointerUpEvent>(OnViewportPointerUp);
                _viewport.UnregisterCallback<PointerLeaveEvent>(OnViewportPointerLeave);
                
                // Setup rotation handlers - register with default propagation
                _viewport.RegisterCallback<PointerDownEvent>(OnViewportPointerDown);
                _viewport.RegisterCallback<PointerMoveEvent>(OnViewportPointerMove);
                _viewport.RegisterCallback<PointerUpEvent>(OnViewportPointerUp);
                _viewport.RegisterCallback<PointerLeaveEvent>(OnViewportPointerLeave);
                
                Debug.Log("[LoadoutManager] Viewport event handlers registered");
                
                // Also try registering on root to catch events that might be blocked
                if(_root != null) {
                    _root.RegisterCallback<PointerDownEvent>(OnRootPointerDownForViewport, TrickleDown.TrickleDown);
                    _root.RegisterCallback<PointerMoveEvent>(OnRootPointerMoveForViewport, TrickleDown.TrickleDown);
                    _root.RegisterCallback<PointerUpEvent>(OnRootPointerUpForViewport, TrickleDown.TrickleDown);
                    Debug.Log("[LoadoutManager] Root-level event handlers registered for viewport");
                }
            } else {
                Debug.LogWarning($"[LoadoutManager] Viewport or RenderTexture is null! Viewport: {_viewport != null}, RenderTexture: {previewRenderTexture != null}");
            }

            UpdatePlayerModel();
        }


        private void UpdatePlayerModel() {
            if(_previewPlayerModel == null) return;

            var skinnedRenderer = _previewPlayerModel.GetComponentInChildren<SkinnedMeshRenderer>();
            if(skinnedRenderer == null) return;

            // Apply new material packet system to preview model
            var packetIndex = PlayerPrefs.GetInt("PlayerMaterialPacketIndex", 0);
            var baseColorR = PlayerPrefs.GetFloat("PlayerBaseColorR", 1f);
            var baseColorG = PlayerPrefs.GetFloat("PlayerBaseColorG", 1f);
            var baseColorB = PlayerPrefs.GetFloat("PlayerBaseColorB", 1f);
            var baseColorA = PlayerPrefs.GetFloat("PlayerBaseColorA", 1f);
            var baseColor = new Color(baseColorR, baseColorG, baseColorB, baseColorA);
            var smoothness = PlayerPrefs.GetFloat("PlayerSmoothness", 0.5f);
            var metallic = PlayerPrefs.GetFloat("PlayerMetallic", 0f);
            var specularR = PlayerPrefs.GetFloat("PlayerSpecularColorR", 0.2f);
            var specularG = PlayerPrefs.GetFloat("PlayerSpecularColorG", 0.2f);
            var specularB = PlayerPrefs.GetFloat("PlayerSpecularColorB", 0.2f);
            var specularA = PlayerPrefs.GetFloat("PlayerSpecularColorA", 1f);
            var specularColor = new Color(specularR, specularG, specularB, specularA);
            var heightStrength = PlayerPrefs.GetFloat("PlayerHeightStrength", 0.02f);
            var emissionEnabled = PlayerPrefs.GetInt("PlayerEmissionEnabled", 0) == 1;
            var emissionR = PlayerPrefs.GetFloat("PlayerEmissionColorR", 0f);
            var emissionG = PlayerPrefs.GetFloat("PlayerEmissionColorG", 0f);
            var emissionB = PlayerPrefs.GetFloat("PlayerEmissionColorB", 0f);
            var emissionA = PlayerPrefs.GetFloat("PlayerEmissionColorA", 1f);
            var emissionColor = new Color(emissionR, emissionG, emissionB, emissionA);

            var packet = PlayerMaterialPacketManager.Instance?.GetPacket(packetIndex);
            if(packet == null) {
                Debug.LogWarning("[LoadoutManager] Could not load material packet for preview model.");
                return;
            }

            var generatedMaterial = PlayerMaterialGenerator.GenerateMaterial(
                packet,
                baseColor,
                smoothness,
                metallic,
                specularColor,
                heightStrength,
                emissionEnabled,
                emissionColor
            );

            var materials = skinnedRenderer.materials;
            if(materials.Length > 1) {
                materials[1] = generatedMaterial;
                skinnedRenderer.materials = materials;
            } else {
                Debug.LogWarning("[LoadoutManager] Preview player model does not have enough material slots for customization.");
            }
        }

        private void CachePreviewWeaponModels() {
            CachePreviewPrimaryWeaponModels();
            CachePreviewSecondaryWeaponModels();
        }

        private void CachePreviewPrimaryWeaponModels() {
            _previewWeaponModels.Clear();
            if(previewPrimaryWeapons is { Count: > 0 }) {
                foreach(var weapon in previewPrimaryWeapons) {
                    if(weapon == null) continue;
                    weapon.SetActive(false);
                    _previewWeaponModels.Add(weapon);
                }
                return;
            }

            if(_previewPlayerModel == null) return;

            var weaponSocket = _previewPlayerModel.transform.Find("WeaponSocket");
            if(weaponSocket == null) {
                Debug.LogWarning("[LoadoutManager] WeaponSocket not found on preview model, and no weapons assigned in inspector.");
                return;
            }

            foreach(Transform child in weaponSocket) {
                child.gameObject.SetActive(false);
                _previewWeaponModels.Add(child.gameObject);
            }
        }

        private void CachePreviewSecondaryWeaponModels() {
            _previewSecondaryWeaponModels.Clear();

            if(previewSecondaryWeapons is { Count: > 0 }) {
                foreach(var weapon in previewSecondaryWeapons) {
                    if(weapon == null) continue;
                    weapon.SetActive(false);
                    _previewSecondaryWeaponModels.Add(weapon);
                }
                return;
            }

            if(_previewPlayerModel == null) return;

            var parent = secondaryWeaponParent != null
                ? secondaryWeaponParent
                : FindChildRecursive(_previewPlayerModel.transform, "hip");

            if(parent == null) {
                Debug.LogWarning("[LoadoutManager] Secondary weapon parent not found on preview model. Assign it in inspector for accurate holster previews.");
                return;
            }

            var secondaryLookup = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            foreach(Transform child in parent) {
                if(child == null) continue;
                child.gameObject.SetActive(false);
                secondaryLookup[child.name] = child.gameObject;
            }

            if(secondaryWeapons == null || secondaryWeapons.Length == 0) return;

            for(var i = 0; i < secondaryWeapons.Length; i++) {
                GameObject resolved = null;
                var weaponData = secondaryWeapons[i];
                if(weaponData?.weaponPrefab != null) {
                    var targetName = weaponData.weaponPrefab.name;
                    if(secondaryLookup.TryGetValue(targetName, out var found)) {
                        resolved = found;
                    }
                }

                _previewSecondaryWeaponModels.Add(resolved);
            }
        }

        private static Transform FindChildRecursive(Transform root, string nameContains) {
            if(root == null || string.IsNullOrEmpty(nameContains)) return null;
            var lower = nameContains.ToLowerInvariant();
            var stack = new Stack<Transform>();
            for(var i = 0; i < root.childCount; i++) {
                stack.Push(root.GetChild(i));
            }

            while(stack.Count > 0) {
                var current = stack.Pop();
                if(current.name.ToLowerInvariant().Contains(lower)) {
                    return current;
                }

                for(var i = 0; i < current.childCount; i++) {
                    stack.Push(current.GetChild(i));
                }
            }

            return null;
        }

        private void UpdatePreviewWeaponModel() {
            UpdateWeaponModelSet(_previewWeaponModels, _selectedPrimaryIndex);
            UpdateWeaponModelSet(_previewSecondaryWeaponModels, _selectedSecondaryIndex);
        }

        private static void UpdateWeaponModelSet(List<GameObject> models, int selectedIndex) {
            if(models == null || models.Count == 0) return;

            var safeIndex = Mathf.Clamp(selectedIndex, 0, models.Count - 1);
            for(var i = 0; i < models.Count; i++) {
                var weapon = models[i];
                if(weapon == null) continue;
                var shouldShow = i == safeIndex;
                if(weapon.activeSelf != shouldShow) {
                    weapon.SetActive(shouldShow);
                }
            }
        }

        /// <summary>
        /// Public method to update the preview model material. Called by CharacterCustomizationManager when settings change.
        /// </summary>
        public void UpdatePreviewModelMaterial(int packetIndex, Color baseColor, float smoothness, float metallic, Color specularColor, float heightStrength, bool emissionEnabled, Color emissionColor) {
            if(_previewPlayerModel == null) return;

            var skinnedRenderer = _previewPlayerModel.GetComponentInChildren<SkinnedMeshRenderer>();
            if(skinnedRenderer == null) return;

            var packet = PlayerMaterialPacketManager.Instance?.GetPacket(packetIndex);
            if(packet == null) {
                Debug.LogWarning("[LoadoutManager] Could not load material packet for preview model.");
                return;
            }

            var generatedMaterial = PlayerMaterialGenerator.GenerateMaterial(
                packet,
                baseColor,
                smoothness,
                metallic,
                specularColor,
                heightStrength,
                emissionEnabled,
                emissionColor
            );

            var materials = skinnedRenderer.materials;
            if(materials.Length > 1) {
                materials[1] = generatedMaterial;
                skinnedRenderer.materials = materials;
            } else {
                Debug.LogWarning("[LoadoutManager] Preview player model does not have enough material slots for customization.");
            }
        }

        private void OnViewportPointerDown(PointerDownEvent evt) {
            // Use the helper method that doesn't check event target
            HandleViewportPointerDown(evt.position);
            evt.StopPropagation();
            evt.PreventDefault(); // Prevent default behavior
        }

        private void OnViewportPointerMove(PointerMoveEvent evt) {
            // Use the helper method that doesn't check event target
            HandleViewportPointerMove(evt.position);
        }

        private void OnViewportPointerUp(PointerUpEvent evt) {
            // Use the helper method that doesn't check event target
            HandleViewportPointerUp();
        }

        private void OnViewportPointerLeave(PointerLeaveEvent evt) {
            if(_isDragging) {
                _isDragging = false;
                // Velocity is already set correctly from last move
                // If it's zero, no spin. If non-zero, deceleration will happen in Update()
            }
        }
        
        // Root-level handlers to catch events that might be blocked
        private void OnRootPointerDownForViewport(PointerDownEvent evt) {
            if(_viewport == null || _previewPlayerModel == null) return;
            
            // Check if the click is within the viewport bounds using layout
            var viewportRect = _viewport.layout;
            var clickPos = evt.position;
            
            // Check if click is within viewport bounds
            if(clickPos.x >= viewportRect.xMin && clickPos.x <= viewportRect.xMax &&
               clickPos.y >= viewportRect.yMin && clickPos.y <= viewportRect.yMax) {
                Debug.Log($"[LoadoutManager] OnRootPointerDownForViewport - Click detected in viewport area at {clickPos}, Viewport bounds: {viewportRect}");
                // Create a synthetic event for the viewport handler
                HandleViewportPointerDown(clickPos);
            }
        }
        
        private void OnRootPointerMoveForViewport(PointerMoveEvent evt) {
            if(_viewport == null || !_isDragging || _previewPlayerModel == null) return;
            
            // Check if the move is within the viewport bounds
            var viewportRect = _viewport.layout;
            var movePos = evt.position;
            
            if(movePos.x >= viewportRect.xMin && movePos.x <= viewportRect.xMax &&
               movePos.y >= viewportRect.yMin && movePos.y <= viewportRect.yMax) {
                HandleViewportPointerMove(movePos);
            }
        }
        
        private void OnRootPointerUpForViewport(PointerUpEvent evt) {
            if(_viewport == null || !_isDragging) return;
            
            // Check if the release is within the viewport bounds
            var viewportRect = _viewport.layout;
            var upPos = evt.position;
            
            if(upPos.x >= viewportRect.xMin && upPos.x <= viewportRect.xMax &&
               upPos.y >= viewportRect.yMin && upPos.y <= viewportRect.yMax) {
                HandleViewportPointerUp();
            }
        }
        
        // Helper methods that don't rely on event target
        private void HandleViewportPointerDown(Vector2 position) {
            if(_previewPlayerModel == null || !_rotationEnabled) {
                Debug.LogWarning("[LoadoutManager] HandleViewportPointerDown called but model is null!");
                return;
            }
            
            // If model is spinning, stop it immediately
            _currentRotationVelocity = 0f;
            _lastMovementDelta = 0f; // Reset movement tracking
            
            // Start dragging
            _isDragging = true;
            _lastMousePosition = position;
        }
        
        private void HandleViewportPointerMove(Vector2 position) {
            if(!_isDragging || _previewPlayerModel == null || !_rotationEnabled) return;
            
            var deltaX = position.x - _lastMousePosition.x;
            _lastMovementDelta = deltaX; // Track the last movement
            
            // Only update rotation and velocity if there's actual movement
            if(Mathf.Abs(deltaX) > MIN_MOVEMENT_THRESHOLD) {
                // Reverse direction: negative deltaX rotates right (positive Y rotation)
                _rotationY -= deltaX * 0.5f; // Adjust sensitivity as needed
                
                _previewPlayerModel.transform.rotation = Quaternion.Euler(0, _rotationY, 0);
                
                // Set velocity based on movement
                _currentRotationVelocity = -deltaX * 0.5f;
            } else {
                // No movement = no velocity
                _currentRotationVelocity = 0f;
            }
            
            _lastMousePosition = position;
        }
        
        private void HandleViewportPointerUp() {
            if(!_rotationEnabled) {
                _isDragging = false;
                _currentRotationVelocity = 0f;
                return;
            }

            if(_isDragging) {
                _isDragging = false;
                
                // Only preserve velocity if the last movement was significant
                // This prevents spin when user stopped moving before release
                if(Mathf.Abs(_lastMovementDelta) <= MIN_MOVEMENT_THRESHOLD) {
                    _currentRotationVelocity = 0f;
                }
                // Otherwise, velocity is already set from last move and will decelerate
            }
        }

        private void Update() {
            if(!_rotationEnabled) return;

            // Handle deceleration when not dragging
            if(!_isDragging && Mathf.Abs(_currentRotationVelocity) > 0.01f && _previewPlayerModel != null) {
                _rotationY += _currentRotationVelocity;
                _previewPlayerModel.transform.rotation = Quaternion.Euler(0, _rotationY, 0);
                
                // Apply friction/deceleration - higher value (closer to 1.0) = slower deceleration
                _currentRotationVelocity *= 0.97f; // Reduced deceleration for longer spins
                
                // Stop if velocity is too small
                if(Mathf.Abs(_currentRotationVelocity) < 0.01f) {
                    _currentRotationVelocity = 0f;
                }
            }
        }

        private void OnBackClicked() {
            // Auto-apply customization changes when leaving loadout
            if(_hasUnsavedChanges) {
                ShowLoadoutUnsavedModal();
                return;
            }

            StartCoroutine(HideLoadoutAndSwitchPanel());
        }
        
        private IEnumerator HideLoadoutAndSwitchPanel() {
            // Start slide-out animation immediately
            StopSlideAnimations();
            FadeBackground(false);
            StartSlideOut();

            // Show main menu panel immediately
            var loadoutPanel = _root.Q<VisualElement>("loadout-panel");
            if(mainMenuManager != null) {
                mainMenuManager.ShowPanel(mainMenuManager.MainMenuPanel);
            } else {
                var mainMenuPanel = _root.Q<VisualElement>("main-menu-panel");
                mainMenuPanel.RemoveFromClassList("hidden");
                mainMenuPanel.style.display = DisplayStyle.Flex;
            }

            // Keep loadout panel visible for animation
            if(loadoutPanel != null) {
                loadoutPanel.RemoveFromClassList("hidden");
                loadoutPanel.style.display = DisplayStyle.Flex;
                loadoutPanel.BringToFront();
            }

            // Wait for slide-out animation to finish
            yield return new WaitForSeconds(SLIDE_ANIMATION_DURATION);

            // Hide after animation completes
            if(loadoutPanel != null) {
                loadoutPanel.AddToClassList("hidden");
                loadoutPanel.style.display = StyleKeyword.Null;
            }
        }

        // private IEnumerator TransitionCameraToPreview() {
        //     // Get the parent camera (main camera) if preview camera is a child
        //     var cameraToMove = previewCamera.transform.parent != null ? previewCamera.transform.parent : previewCamera.transform;
        //     var startPosition = cameraToMove.position;
        //     var startRotation = cameraToMove.rotation;
        //
        //     var elapsed = 0f;
        //
        //     while(elapsed < cameraTransitionDuration) {
        //         elapsed += Time.deltaTime;
        //         var t = Mathf.SmoothStep(0f, 1f, elapsed / cameraTransitionDuration);
        //
        //         cameraToMove.position = Vector3.Lerp(startPosition, previewCameraTransform.position, t);
        //         cameraToMove.rotation = Quaternion.Slerp(startRotation, previewCameraTransform.rotation, t);
        //
        //         yield return null;
        //     }
        // }

        // private IEnumerator TransitionCameraToOriginal() {
        //     if(previewCamera == null) yield break;
        //
        //     // Get the parent camera (main camera) if preview camera is a child
        //     var cameraToMove = previewCamera.transform.parent != null ? previewCamera.transform.parent : previewCamera.transform;
        //     var startPosition = cameraToMove.position;
        //     var startRotation = cameraToMove.rotation;
        //
        //     var elapsed = 0f;
        //
        //     while(elapsed < cameraTransitionDuration) {
        //         elapsed += Time.deltaTime;
        //         var t = Mathf.SmoothStep(0f, 1f, elapsed / cameraTransitionDuration);
        //
        //         cameraToMove.position = Vector3.Lerp(startPosition, originalCameraTransform.position, t);
        //         cameraToMove.rotation = Quaternion.Slerp(startRotation, originalCameraTransform.rotation, t);
        //
        //         yield return null;
        //     }
        //
        //     if(cameraToMove.position != originalCameraTransform.position ||
        //        cameraToMove.rotation != originalCameraTransform.rotation) yield break;
        //     
        //     // Camera has fully transitioned back - reset rotation state for next time
        //     _rotationY = 180f;
        //     _currentRotationVelocity = 0f;
        //     _isDragging = false;
        //     
        //     if(_previewPlayerModel != null) {
        //         Destroy(_previewPlayerModel);
        //     }
        // }
        
        private void StopSlideAnimations() {
            if(_slideInCoroutine != null) {
                StopCoroutine(_slideInCoroutine);
                _slideInCoroutine = null;
            }
            if(_slideOutCoroutine != null) {
                StopCoroutine(_slideOutCoroutine);
                _slideOutCoroutine = null;
            }
            if(_backgroundFadeCoroutine != null) {
                StopCoroutine(_backgroundFadeCoroutine);
                _backgroundFadeCoroutine = null;
            }
        }
        
        private void StartSlideIn() {
            _isSlidingIn = true;
            _slideInCoroutine = StartCoroutine(AnimateContainersSlideIn());
        }
        
        private void StartSlideOut() {
            _isSlidingIn = false;
            _slideOutCoroutine = StartCoroutine(AnimateContainersSlideOut());
        }
        
        private void SetContainerTranslate(VisualElement element, Vector2 percent) {
            if(element == null) return;
            element.style.translate = new StyleTranslate(PercentToTranslate(percent));
        }

        private void FadeBackground(bool fadeIn) {
            if(_backgroundElement == null) return;
            if(_backgroundFadeCoroutine != null) {
                StopCoroutine(_backgroundFadeCoroutine);
                _backgroundFadeCoroutine = null;
            }
            _backgroundFadeCoroutine = StartCoroutine(AnimateBackgroundFade(fadeIn));
        }

        private IEnumerator AnimateBackgroundFade(bool fadeIn) {
            if(_backgroundElement == null) yield break;

            if(fadeIn) {
                _backgroundElement.RemoveFromClassList("hidden");
                _backgroundElement.style.display = DisplayStyle.Flex;
            }

            var startOpacity = _backgroundElement.resolvedStyle.opacity;
            var targetOpacity = fadeIn ? 1f : 0f;
            var elapsed = 0f;

            while(elapsed < BACKGROUND_FADE_DURATION) {
                elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(elapsed / BACKGROUND_FADE_DURATION);
                var value = Mathf.Lerp(startOpacity, targetOpacity, t);
                _backgroundElement.style.opacity = new StyleFloat(value);
                yield return null;
            }

            _backgroundElement.style.opacity = new StyleFloat(targetOpacity);

            if(!fadeIn) {
                _backgroundElement.AddToClassList("hidden");
                _backgroundElement.style.display = StyleKeyword.Null;
            }

            _backgroundFadeCoroutine = null;
        }

        private void UpdateDirtyState() {
            var nameDirty = !string.Equals(_currentPlayerName ?? string.Empty, _savedPlayerName ?? string.Empty, StringComparison.Ordinal);
            var primaryDirty = _selectedPrimaryIndex != _savedPrimaryIndex;
            var secondaryDirty = _selectedSecondaryIndex != _savedSecondaryIndex;
            var tertiaryDirty = _selectedTertiaryIndex != _savedTertiaryIndex;

            _hasUnsavedChanges = nameDirty || primaryDirty || secondaryDirty || tertiaryDirty || _customizationDirty;
        }

        private void ShowLoadoutUnsavedModal() {
            if(_loadoutUnsavedModal == null) return;
            _loadoutUnsavedModal.RemoveFromClassList("hidden");
            _loadoutUnsavedModal.style.display = DisplayStyle.Flex;
            _loadoutUnsavedModal.BringToFront();
        }

        private void HideLoadoutUnsavedModal() {
            if(_loadoutUnsavedModal == null) return;
            _loadoutUnsavedModal.AddToClassList("hidden");
            _loadoutUnsavedModal.style.display = StyleKeyword.Null;
        }

        private void OnLoadoutUnsavedYes() {
            mainMenuManager?.OnButtonClicked();
            OnApplyLoadoutClicked();
            HideLoadoutUnsavedModal();
            StartCoroutine(HideLoadoutAndSwitchPanel());
        }

        private void OnLoadoutUnsavedNo() {
            mainMenuManager?.OnButtonClicked(true);
            RevertLoadoutChanges();
            HideLoadoutUnsavedModal();
            StartCoroutine(HideLoadoutAndSwitchPanel());
        }

        private void OnLoadoutUnsavedCancel() {
            mainMenuManager?.OnButtonClicked();
            HideLoadoutUnsavedModal();
        }

        private void RevertLoadoutChanges() {
            _currentPlayerName = _savedPlayerName;
            _playerNameInput?.SetValueWithoutNotify(_savedPlayerName);

            _selectedPrimaryIndex = _savedPrimaryIndex;
            _selectedSecondaryIndex = _savedSecondaryIndex;
            _selectedTertiaryIndex = _savedTertiaryIndex;
            UpdateWeaponImages();

            var customizationManager = FindFirstObjectByType<CharacterCustomizationManager>();
            customizationManager?.ReloadSavedCustomization();

            _customizationDirty = false;
            UpdateDirtyState();
        }

        public void NotifyCustomizationDirty() {
            _customizationDirty = true;
            UpdateDirtyState();
        }

        public void NotifyCustomizationApplied() {
            _customizationDirty = false;
            UpdateDirtyState();
        }

        public void SetPreviewRotationEnabled(bool enabled) {
            _rotationEnabled = enabled;
            if(!enabled) {
                _isDragging = false;
                _currentRotationVelocity = 0f;
            }
        }

        private IEnumerator AnimateContainersSlideIn() {
            // Get current positions (in case we're interrupting a slide-out)
            var weaponStart = GetCurrentTranslatePercent(_weaponContainer);
            var customizationStart = GetCurrentTranslatePercent(_customizationContainer);
            var nameStart = GetCurrentTranslatePercent(_nameContainer);
            
            // Target positions (on-screen)
            var weaponTarget = Vector2.zero;
            var customizationTarget = Vector2.zero;
            var nameTarget = Vector2.zero;
            
            var elapsed = 0f;
            
            while(elapsed < SLIDE_ANIMATION_DURATION) {
                elapsed += Time.deltaTime;
                var t = Mathf.SmoothStep(0f, 1f, elapsed / SLIDE_ANIMATION_DURATION);
                
                // Interpolate positions
                if(_weaponContainer != null) {
                    _weaponContainer.style.translate = new StyleTranslate(PercentToTranslate(Vector2.Lerp(weaponStart, weaponTarget, t)));
                }
                if(_customizationContainer != null) {
                    _customizationContainer.style.translate = new StyleTranslate(PercentToTranslate(Vector2.Lerp(customizationStart, customizationTarget, t)));
                }
                if(_nameContainer != null) {
                    _nameContainer.style.translate = new StyleTranslate(PercentToTranslate(Vector2.Lerp(nameStart, nameTarget, t)));
                }
                
                yield return null;
            }
            
            // Ensure final positions
            if(_weaponContainer != null) {
                _weaponContainer.style.translate = new StyleTranslate(PercentToTranslate(weaponTarget));
            }
            if(_customizationContainer != null) {
                _customizationContainer.style.translate = new StyleTranslate(PercentToTranslate(customizationTarget));
            }
            if(_nameContainer != null) {
                _nameContainer.style.translate = new StyleTranslate(PercentToTranslate(nameTarget));
            }
            
            _slideInCoroutine = null;
        }
        
        private IEnumerator AnimateContainersSlideOut() {
            // Get current positions (in case we're interrupting a slide-in)
            var weaponStart = GetCurrentTranslatePercent(_weaponContainer);
            var customizationStart = GetCurrentTranslatePercent(_customizationContainer);
            var nameStart = GetCurrentTranslatePercent(_nameContainer);
            
            // Target positions (off-screen)
            var weaponTarget = WeaponOffscreenPercent;
            var customizationTarget = CustomizationOffscreenPercent;
            var nameTarget = NameOffscreenPercent;
            
            var elapsed = 0f;
            
            while(elapsed < SLIDE_ANIMATION_DURATION) {
                elapsed += Time.deltaTime;
                var t = Mathf.SmoothStep(0f, 1f, elapsed / SLIDE_ANIMATION_DURATION);
                
                // Interpolate positions
                if(_weaponContainer != null) {
                    _weaponContainer.style.translate = new StyleTranslate(PercentToTranslate(Vector2.Lerp(weaponStart, weaponTarget, t)));
                }
                if(_customizationContainer != null) {
                    _customizationContainer.style.translate = new StyleTranslate(PercentToTranslate(Vector2.Lerp(customizationStart, customizationTarget, t)));
                }
                if(_nameContainer != null) {
                    _nameContainer.style.translate = new StyleTranslate(PercentToTranslate(Vector2.Lerp(nameStart, nameTarget, t)));
                }
                
                yield return null;
            }
            
            // Ensure final positions
            if(_weaponContainer != null) {
                _weaponContainer.style.translate = new StyleTranslate(PercentToTranslate(weaponTarget));
            }
            if(_customizationContainer != null) {
                _customizationContainer.style.translate = new StyleTranslate(PercentToTranslate(customizationTarget));
            }
            if(_nameContainer != null) {
                _nameContainer.style.translate = new StyleTranslate(PercentToTranslate(nameTarget));
            }
            
            _slideOutCoroutine = null;
        }
        
        private Vector2 GetCurrentTranslatePercent(VisualElement element) {
            if(element == null) return Vector2.zero;
            var styleTranslate = element.style.translate;
            if(styleTranslate.keyword != StyleKeyword.None) {
                return Vector2.zero;
            }
            var translate = styleTranslate.value;
            var x = translate.x.unit == LengthUnit.Percent ? translate.x.value : 0f;
            var y = translate.y.unit == LengthUnit.Percent ? translate.y.value : 0f;
            return new Vector2(x, y);
        }
        
        private Translate PercentToTranslate(Vector2 percent) {
            return new Translate(new Length(percent.x, LengthUnit.Percent), new Length(percent.y, LengthUnit.Percent));
        }
    }

    [Serializable]
    public class WeaponData {
        public string weaponName;
        public Sprite icon;
        public GameObject weaponPrefab;
    }
}
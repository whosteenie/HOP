using System;
using System.Collections;
using System.Collections.Generic;
using Game.Player;
using Network.Singletons;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Menu {
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
        private RenderTexture _previewRenderTexture; // Will be created/updated dynamically

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
        private float _initialRotationY; // Cached initial rotation from editor/prefab
        private bool _hasCachedInitialRotation; // Track if we've cached the initial rotation
        private VisualElement _viewport;
        private const float MinMovementThreshold = 0.5f; // Minimum pixel movement to register as actual drag
        private bool _rotationEnabled = true;
        
        // Bounds cache for preview model anti-culling fix
        private static readonly Bounds MaxBounds = new(Vector3.zero, new Vector3(float.MaxValue, float.MaxValue, float.MaxValue));
        
        // Velocity sampling
        private struct MovementSample {
            public float Time;
            public float X;
        }
        private readonly List<MovementSample> _movementSamples = new();
        private const float VelocitySampleWindow = 0.1f; // 100ms window
        private const float RotationSensitivity = 0.5f;
        private const float MinSpinVelocityThreshold = 300f; // Minimum degrees/sec to trigger spin

        // Animation state
        private VisualElement _weaponContainer;
        private VisualElement _customizationContainer;
        private VisualElement _nameContainer;
        private VisualElement _backgroundElement;
        private Coroutine _backgroundFadeCoroutine;
        private Coroutine _slideInCoroutine;
        private Coroutine _slideOutCoroutine;
        private const float SlideAnimationDuration = 0.4f;
        private const float BackgroundFadeDuration = 0.2f;
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
        
        // Preview active tracking for brute force rendering
        private bool _previewActive;

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
            
            // Subscribe to resolution changes
            OptionsMenuManager.OnResolutionChanged += OnResolutionChanged;
            
            // Apply button is always visible now
        }

        private void OnDisable() {
            // Stop brute force rendering
            _previewActive = false;
            
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

            // Unsubscribe from resolution changes
            OptionsMenuManager.OnResolutionChanged -= OnResolutionChanged;

            ResetPreviewCameraTarget();
        }
        
        private void OnDestroy() {
            ReleasePreviewRenderTexture(true);
        }

        public void ShowLoadout() {
            Setup3DPreview();
            
            // Mark preview as active for brute force rendering
            _previewActive = true;
            
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
            
            // Start aggressive rendering coroutine for first few seconds
            StartCoroutine(BruteForceInitialRendering());
        }

        private void ReleasePreviewRenderTexture(bool destroyAsset = false) {
            if(_previewRenderTexture == null) return;

            if(previewCamera != null && previewCamera.targetTexture == _previewRenderTexture) {
                previewCamera.targetTexture = null;
            }

            _previewRenderTexture.Release();

            if(destroyAsset) {
                DestroyImmediate(_previewRenderTexture, true);
            }

            _previewRenderTexture = null;
        }

        /// <summary>
        /// Recreates the render texture with the specified dimensions.
        /// Called when resolution changes or when preview is first set up.
        /// </summary>
        private void RecreateRenderTexture(int width, int height) {
            // Release old texture
            ReleasePreviewRenderTexture();
            
            // BRUTE FORCE: Create render texture with explicit settings that work in builds
            // Use ARGB32 format which is most compatible, and ensure it's created properly
            _previewRenderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) {
                antiAliasing = 8, // 8x MSAA for smooth rendering
                name = "PlayerPreviewRT",
                useMipMap = false,
                autoGenerateMips = false
            };
            
            // BRUTE FORCE: Force the render texture to be created immediately
            _previewRenderTexture.Create();
            
            // Update camera target
            if(previewCamera != null) {
                previewCamera.targetTexture = _previewRenderTexture;
            }
            
            // Update background image if it exists
            if(_backgroundElement == null || _previewRenderTexture == null) return;
            _backgroundElement.style.backgroundImage =
                new StyleBackground(Background.FromRenderTexture(_previewRenderTexture));
            _backgroundElement.MarkDirtyRepaint();
        }

        /// <summary>
        /// Called when resolution changes via OptionsMenuManager event.
        /// Recreates the render texture if the preview is currently active.
        /// </summary>
        private void OnResolutionChanged(int width, int height) {
            // Only recreate if preview is currently active
            if(_previewRenderTexture != null && previewCamera != null && previewCamera.enabled) {
                RecreateRenderTexture(width, height);
            }
        }

        private void ResetPreviewCameraTarget() {
            if(previewCamera == null) return;

            if(previewCamera.targetTexture != null) {
                previewCamera.targetTexture = null;
            }

            previewCamera.enabled = false;

            Camera parentCamera = null;
            if(previewCamera.transform.parent != null) {
                parentCamera = previewCamera.transform.parent.GetComponent<Camera>();
            }
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
            if(_loadoutUnsavedYes != null) {
                _loadoutUnsavedYes.RegisterCallback<ClickEvent>(_ => OnLoadoutUnsavedYes());
                _loadoutUnsavedYes.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);
            }
            if(_loadoutUnsavedNo != null) {
                _loadoutUnsavedNo.RegisterCallback<ClickEvent>(_ => OnLoadoutUnsavedNo());
                _loadoutUnsavedNo.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);
            }
            if(_loadoutUnsavedCancel != null) {
                _loadoutUnsavedCancel.RegisterCallback<ClickEvent>(_ => OnLoadoutUnsavedCancel());
                _loadoutUnsavedCancel.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);
            }
            
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
            _primaryDropdownScroll = _primaryDropdown != null ? _primaryDropdown.Q<ScrollView>("primary-scroll") : null;
            _secondaryDropdownScroll = _secondaryDropdown != null ? _secondaryDropdown.Q<ScrollView>("secondary-scroll") : null;
            _tertiaryDropdownScroll = _tertiaryDropdown != null ? _tertiaryDropdown.Q<ScrollView>("tertiary-scroll") : null;

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
                    if(evt == null) return;
                    evt.StopPropagation();
                    evt.StopImmediatePropagation();
                });

                container.Add(weaponOption);
            }
        }

        private void CloseAllDropdowns() {
            if(_primaryDropdown != null) {
                _primaryDropdown.AddToClassList("hidden");
            }
            if(_secondaryDropdown != null) {
                _secondaryDropdown.AddToClassList("hidden");
            }
            if(_tertiaryDropdown != null) {
                _tertiaryDropdown.AddToClassList("hidden");
            }
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
                targetImage.sprite = weapon != null ? weapon.icon : null;
                targetImage.style.visibility = weapon != null ? Visibility.Visible : Visibility.Hidden;
            }

            var displayName = weapon != null && !string.IsNullOrEmpty(weapon.weaponName)
                ? weapon.weaponName
                : weapon != null && weapon.weaponPrefab != null
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
            if(previewCamera == null) {
                Debug.LogError("[LoadoutManager] Preview camera is null!");
                return;
            }

            // Ensure main camera (parent) is enabled to render the world
            Camera mainCam = null;
            if(previewCamera.transform.parent != null) {
                mainCam = previewCamera.transform.parent.GetComponent<Camera>();
            }
            if(mainCam != null) {
                mainCam.enabled = true;
            }

            // Create or update render texture to match current screen resolution
            var screenWidth = Screen.width;
            var screenHeight = Screen.height;
            
            if(_previewRenderTexture == null || 
               _previewRenderTexture.width != screenWidth || 
               _previewRenderTexture.height != screenHeight) {
                RecreateRenderTexture(screenWidth, screenHeight);
            }

            // BRUTE FORCE: Ensure camera can see everything - set culling mask to Everything
            // This ensures the model is visible regardless of layer
            previewCamera.cullingMask = -1; // Everything
            
            // Ensure preview camera is enabled and rendering to RenderTexture
            previewCamera.targetTexture = _previewRenderTexture;
            previewCamera.enabled = true;
            
            // BRUTE FORCE: Force camera to render immediately
            previewCamera.Render();
            
            // Don't render here - model isn't set up yet! Render after setup completes.

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
            
            // BRUTE FORCE: Ensure model is definitely active and visible
            if(_previewPlayerModel != null) {
                _previewPlayerModel.SetActive(true);
                // Force all renderers to be enabled
                var renderers = _previewPlayerModel.GetComponentsInChildren<Renderer>(true);
                foreach(var r in renderers) {
                    if(r != null) {
                        r.enabled = true;
                    }
                }
            }
            
            // Cache initial rotation from editor/prefab (only on first setup)
            if(_previewPlayerModel != null && !_hasCachedInitialRotation) {
                _initialRotationY = _previewPlayerModel.transform.rotation.eulerAngles.y;
                _hasCachedInitialRotation = true;
            }
            
            // Sync _rotationY with model's actual rotation to prevent first-rotation snap
            // This ensures _rotationY matches the model's current rotation state
            if(_previewPlayerModel != null) {
                _rotationY = _previewPlayerModel.transform.rotation.eulerAngles.y;
            }
            
            // Apply bounds update to prevent culling (same treatment as real player models)
            ForcePreviewModelBoundsUpdate();
            
            // Material at index 1 should be set to "None" in the prefab for verification
            // We'll apply the new material packet system via UpdatePlayerModel()
            CachePreviewWeaponModels();
            UpdatePreviewWeaponModel();

            // Initialize rotation state (don't reset here - will reset after fade completes)
            _currentRotationVelocity = 0f;
            _isDragging = false;

            // Set up the background (full-screen display) and viewport (input detection)
            var background = _root.Q<VisualElement>("player-model-background");
            _viewport = _root.Q<VisualElement>("player-model-viewport");
            var uiOverlay = _root.Q<VisualElement>("ui-overlay");
            
            // Set overlay picking mode to ignore so events can pass through
            if(uiOverlay != null) {
                uiOverlay.pickingMode = PickingMode.Ignore;
            }
            
            // Set the render texture as background on the full-screen element
            if(background != null && _previewRenderTexture != null) {
                // CRITICAL: Ensure background is visible immediately (even if transparent)
                // This allows the render texture to be displayed as soon as it's rendered
                background.style.display = DisplayStyle.Flex;
                background.style.visibility = Visibility.Visible;
                background.RemoveFromClassList("hidden");
                background.style.backgroundImage =
                    new StyleBackground(Background.FromRenderTexture(_previewRenderTexture));
                background.pickingMode = PickingMode.Ignore; // Don't capture input, just display
                
                // BRUTE FORCE: Force UI to update immediately
                background.MarkDirtyRepaint();
            } else {
                Debug.LogError($"[LoadoutManager] Background or RenderTexture is null! Background: {background != null}, RenderTexture: {_previewRenderTexture != null}");
            }
            
            // Setup viewport for input detection only
            if(_viewport != null && _previewRenderTexture != null) {
                // CRITICAL: Set picking mode to Position so we can receive mouse events for rotation
                _viewport.pickingMode = PickingMode.Position;
                
                // Ensure the viewport can receive input events
                _viewport.focusable = false; // Don't make it focusable, just receive pointer events
                
                // Make sure viewport is visible and can receive events
                _viewport.style.display = DisplayStyle.Flex;
                _viewport.style.visibility = Visibility.Visible;
                
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
                
                // Also try registering on root to catch events that might be blocked
                if(_root != null) {
                    _root.RegisterCallback<PointerDownEvent>(OnRootPointerDownForViewport, TrickleDown.TrickleDown);
                    _root.RegisterCallback<PointerMoveEvent>(OnRootPointerMoveForViewport, TrickleDown.TrickleDown);
                    _root.RegisterCallback<PointerUpEvent>(OnRootPointerUpForViewport, TrickleDown.TrickleDown);
                }
            } else {
                Debug.LogWarning($"[LoadoutManager] Viewport or RenderTexture is null! Viewport: {_viewport != null}, RenderTexture: {_previewRenderTexture != null}");
            }

            UpdatePlayerModel();
            
            // Force another bounds update after material is applied (in case material changes affect bounds)
            StartCoroutine(DelayedPreviewBoundsUpdate());
            
            // BRUTE FORCE: Multiple renders to ensure capture
            // When a camera with targetTexture is enabled and objects are instantiated in the same frame,
            // Unity's automatic rendering can occur before the objects exist, resulting in an empty frame.
            // The preview only becomes visible after something triggers a refresh (e.g., moving scene view camera).
            // By manually rendering multiple times after setup completes, we ensure the camera captures the model.
            if(previewCamera == null || !previewCamera.enabled) return;
            // Force bounds update right before rendering to prevent culling
            // Unity can recalculate bounds from the mesh, overriding our MaxBounds setting
            ForcePreviewModelBoundsUpdate();
                
            // Render multiple times immediately
            for(var i = 0; i < 3; i++) {
                previewCamera.Render();
            }
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

            PlayerMaterialPacket packet = null;
            if(PlayerMaterialPacketManager.Instance != null) {
                packet = PlayerMaterialPacketManager.Instance.GetPacket(packetIndex);
            }
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
                GameObject o;
                (o = child.gameObject).SetActive(false);
                _previewWeaponModels.Add(o);
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
                GameObject o;
                (o = child.gameObject).SetActive(false);
                secondaryLookup[child.name] = o;
            }

            if(secondaryWeapons == null || secondaryWeapons.Length == 0) return;

            foreach(var t in secondaryWeapons) {
                GameObject resolved = null;
                if(t != null && t.weaponPrefab != null) {
                    var targetName = t.weaponPrefab.name;
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

            PlayerMaterialPacket packet = null;
            if(PlayerMaterialPacketManager.Instance != null) {
                packet = PlayerMaterialPacketManager.Instance.GetPacket(packetIndex);
            }
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
            if(!(clickPos.x >= viewportRect.xMin) || !(clickPos.x <= viewportRect.xMax) ||
               !(clickPos.y >= viewportRect.yMin) || !(clickPos.y <= viewportRect.yMax)) return;
            Debug.Log($"[LoadoutManager] OnRootPointerDownForViewport - Click detected in viewport area at {clickPos}, Viewport bounds: {viewportRect}");
            // Create a synthetic event for the viewport handler
            HandleViewportPointerDown(clickPos);
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
            
            // Start dragging
            _isDragging = true;
            _lastMousePosition = position;
            
            // Initialize sampling
            _movementSamples.Clear();
            _movementSamples.Add(new MovementSample { Time = Time.time, X = position.x });
        }
        
        private void HandleViewportPointerMove(Vector2 position) {
            if(!_isDragging || _previewPlayerModel == null || !_rotationEnabled) return;
            
            var deltaX = position.x - _lastMousePosition.x;
            
            // Add sample
            var now = Time.time;
            _movementSamples.Add(new MovementSample { Time = now, X = position.x });
            
            // Prune old samples
            for(var i = _movementSamples.Count - 1; i >= 0; i--) {
                if (now - _movementSamples[i].Time > VelocitySampleWindow) {
                    _movementSamples.RemoveAt(i);
                }
            }
            
            // Only update rotation if there's actual movement
            if(Mathf.Abs(deltaX) > MinMovementThreshold) {
                // Reverse direction: negative deltaX rotates right (positive Y rotation)
                _rotationY -= deltaX * RotationSensitivity;
                _previewPlayerModel.transform.rotation = Quaternion.Euler(0, _rotationY, 0);
            }
            
            _lastMousePosition = position;
        }
        
        private void HandleViewportPointerUp() {
            if(!_rotationEnabled) {
                _isDragging = false;
                _currentRotationVelocity = 0f;
                return;
            }

            if(!_isDragging) return;
            _isDragging = false;
                
            // Calculate velocity from samples
            var now = Time.time;
                
            // Prune old samples first
            for (var i = _movementSamples.Count - 1; i >= 0; i--) {
                if (now - _movementSamples[i].Time > VelocitySampleWindow) {
                    _movementSamples.RemoveAt(i);
                }
            }
                
            if (_movementSamples.Count >= 2) {
                var first = _movementSamples[0];
                var last = _movementSamples[^1];
                var timeDelta = last.Time - first.Time;
                    
                if (timeDelta > 0.001f) {
                    var distDelta = last.X - first.X;
                    var pixelsPerSec = distDelta / timeDelta;
                        
                    // Convert to degrees per second
                    // Note: Negative sign because dragging left (negative X) should rotate right (positive Y)
                    _currentRotationVelocity = -pixelsPerSec * RotationSensitivity;

                    // Apply minimum threshold
                    if(Mathf.Abs(_currentRotationVelocity) < MinSpinVelocityThreshold) {
                        _currentRotationVelocity = 0f;
                    }
                } else {
                    _currentRotationVelocity = 0f;
                }
            } else {
                // Not enough samples or user stopped moving long enough ago
                _currentRotationVelocity = 0f;
            }
        }

        private void Update() {
            // BRUTE FORCE: Always update bounds and force render when preview is active
            // This ensures visibility in builds where cameras with RenderTexture targets don't always render automatically
            if(_previewActive && previewCamera != null && previewCamera.enabled && _previewPlayerModel != null) {
                // Always update bounds to prevent culling
                // Unity can recalculate bounds from the mesh, overriding our MaxBounds setting
                ForcePreviewModelBoundsUpdate();
                
                // BRUTE FORCE: Force render every single frame when preview is active
                // In builds, cameras with RenderTexture targets don't always render automatically
                // This ensures the preview is always visible, even on game launch
                if(_previewRenderTexture != null) {
                    previewCamera.Render();
                }
            }
            
            if(!_rotationEnabled) return;

            // Handle deceleration when not dragging
            if(_isDragging || !(Mathf.Abs(_currentRotationVelocity) > 0.1f) || _previewPlayerModel == null) return;
            _rotationY += _currentRotationVelocity * Time.deltaTime;
            _previewPlayerModel.transform.rotation = Quaternion.Euler(0, _rotationY, 0);
                
            // Apply friction/deceleration
            // Lerp towards 0 over time
            const float decelerationRate = 2.0f; // Adjust for how quickly it stops
            _currentRotationVelocity = Mathf.Lerp(_currentRotationVelocity, 0f, Time.deltaTime * decelerationRate);
                
            // Stop if velocity is too small
            if(Mathf.Abs(_currentRotationVelocity) < 1f) {
                _currentRotationVelocity = 0f;
            }
        }
        
        /// <summary>
        /// BRUTE FORCE: Backup render in LateUpdate to ensure it happens after all updates.
        /// This is especially important in builds where render timing can differ from editor.
        /// </summary>
        private void LateUpdate() {
            // BRUTE FORCE: Force render again in LateUpdate as backup
            // This ensures rendering happens even if Update() timing is off in builds
            if(!_previewActive || previewCamera == null || !previewCamera.enabled ||
               _previewPlayerModel == null || _previewRenderTexture == null) return;
            ForcePreviewModelBoundsUpdate();
            previewCamera.Render();
        }
        
        /// <summary>
        /// BRUTE FORCE: Aggressively render the camera multiple times immediately after setup.
        /// This helps fix visibility issues on game launch where a single render might not be enough.
        /// </summary>
        private IEnumerator BruteForceInitialRendering() {
            // Render multiple times over the next few frames to ensure model is captured
            // This is especially important in builds where timing can be different
            for(var i = 0; i < 20; i++) {
                yield return null; // Wait a frame
                
                if(!_previewActive || previewCamera == null || !previewCamera.enabled || _previewPlayerModel == null) break;
                
                // Force bounds update before each render
                ForcePreviewModelBoundsUpdate();
                if(_previewRenderTexture != null) {
                    previewCamera.Render();
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
            // Mark preview as inactive to stop brute force rendering
            _previewActive = false;
            
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
            yield return new WaitForSeconds(SlideAnimationDuration);

            // Hide after animation completes
            if(loadoutPanel == null) yield break;
            loadoutPanel.AddToClassList("hidden");
            loadoutPanel.style.display = StyleKeyword.Null;
        }
        
        private void StopSlideAnimations() {
            if(_slideInCoroutine != null) {
                StopCoroutine(_slideInCoroutine);
                _slideInCoroutine = null;
            }
            if(_slideOutCoroutine != null) {
                StopCoroutine(_slideOutCoroutine);
                _slideOutCoroutine = null;
            }

            if(_backgroundFadeCoroutine == null) return;
            StopCoroutine(_backgroundFadeCoroutine);
            _backgroundFadeCoroutine = null;
        }
        
        private void StartSlideIn() {
            _slideInCoroutine = StartCoroutine(AnimateContainersSlideIn());
        }
        
        private void StartSlideOut() {
            _slideOutCoroutine = StartCoroutine(AnimateContainersSlideOut());
        }
        
        private static void SetContainerTranslate(VisualElement element, Vector2 percent) {
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

            while(elapsed < BackgroundFadeDuration) {
                elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(elapsed / BackgroundFadeDuration);
                var value = Mathf.Lerp(startOpacity, targetOpacity, t);
                _backgroundElement.style.opacity = new StyleFloat(value);
                yield return null;
            }

            _backgroundElement.style.opacity = new StyleFloat(targetOpacity);

            if(!fadeIn) {
                _backgroundElement.AddToClassList("hidden");
                _backgroundElement.style.display = StyleKeyword.Null;
                
                // Fade-out completed - reset preview rotation to initial value while user is not in loadout
                // This ensures the rotation is already reset when they re-enter, so they don't see it change
                // Only reset if the model exists, and we have a cached initial rotation
                if(_previewPlayerModel != null && _hasCachedInitialRotation) {
                    _rotationY = _initialRotationY;
                    _previewPlayerModel.transform.rotation = Quaternion.Euler(0, _initialRotationY, 0);
                    _currentRotationVelocity = 0f;
                }
            }

            _backgroundFadeCoroutine = null;
        }

        private void UpdateDirtyState() {
            var currentName = _currentPlayerName != null ? _currentPlayerName : string.Empty;
            var savedName = _savedPlayerName != null ? _savedPlayerName : string.Empty;
            var nameDirty = !string.Equals(currentName, savedName, StringComparison.Ordinal);
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
            if(mainMenuManager != null) {
                mainMenuManager.OnButtonClicked();
            }
            OnApplyLoadoutClicked();
            HideLoadoutUnsavedModal();
            StartCoroutine(HideLoadoutAndSwitchPanel());
        }

        private void OnLoadoutUnsavedNo() {
            if(mainMenuManager != null) {
                mainMenuManager.OnButtonClicked(true);
            }
            RevertLoadoutChanges();
            HideLoadoutUnsavedModal();
            StartCoroutine(HideLoadoutAndSwitchPanel());
        }

        private void OnLoadoutUnsavedCancel() {
            if(mainMenuManager != null) {
                mainMenuManager.OnButtonClicked();
            }
            HideLoadoutUnsavedModal();
        }

        private void RevertLoadoutChanges() {
            _currentPlayerName = _savedPlayerName;
            if(_playerNameInput != null) {
                _playerNameInput.SetValueWithoutNotify(_savedPlayerName);
            }

            _selectedPrimaryIndex = _savedPrimaryIndex;
            _selectedSecondaryIndex = _savedSecondaryIndex;
            _selectedTertiaryIndex = _savedTertiaryIndex;
            UpdateWeaponImages();

            var customizationManager = FindFirstObjectByType<CharacterCustomizationManager>();
            if(customizationManager != null) {
                customizationManager.ReloadSavedCustomization();
            }

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

        public void SetPreviewRotationEnabled(bool isEnabled) {
            _rotationEnabled = isEnabled;
            if(isEnabled) return;
            _isDragging = false;
            _currentRotationVelocity = 0f;
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
            
            while(elapsed < SlideAnimationDuration) {
                elapsed += Time.deltaTime;
                var t = Mathf.SmoothStep(0f, 1f, elapsed / SlideAnimationDuration);
                
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
            
            while(elapsed < SlideAnimationDuration) {
                elapsed += Time.deltaTime;
                var t = Mathf.SmoothStep(0f, 1f, elapsed / SlideAnimationDuration);
                
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
        
        private static Vector2 GetCurrentTranslatePercent(VisualElement element) {
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
        
        private static Translate PercentToTranslate(Vector2 percent) {
            return new Translate(new Length(percent.x, LengthUnit.Percent), new Length(percent.y, LengthUnit.Percent));
        }

        /// <summary>
        /// Forces all SkinnedMeshRenderers on the preview model to update their bounds to prevent culling.
        /// This is the same "band-aid" treatment used for real player models.
        /// </summary>
        private void ForcePreviewModelBoundsUpdate() {
            if(_previewPlayerModel == null) return;
            
            var skinnedRenderers = _previewPlayerModel.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach(var smr in skinnedRenderers) {
                if(smr == null) continue;
                smr.updateWhenOffscreen = true;
                smr.localBounds = MaxBounds;
                _ = smr.bounds; // Force Unity to recognize bounds change
            }
        }

        /// <summary>
        /// Delayed bounds update to ensure Unity has positioned everything before recalculating bounds.
        /// This helps fix visibility issues where renderers are culled incorrectly.
        /// </summary>
        private IEnumerator DelayedPreviewBoundsUpdate() {
            // Wait a frame to let Unity position everything
            yield return null;
            
            // Force bounds update again after positioning
            ForcePreviewModelBoundsUpdate();
            
            // Wait another frame and update once more to be thorough
            yield return null;
            ForcePreviewModelBoundsUpdate();
        }
    }

    [Serializable]
    public class WeaponData {
        public string weaponName;
        public Sprite icon;
        public GameObject weaponPrefab;
    }
}
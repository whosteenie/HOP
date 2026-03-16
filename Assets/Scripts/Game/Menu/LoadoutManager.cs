using System;
using System.Collections;
using System.Collections.Generic;
using Events;
using Game.Player.Visual;
using Game.Progression;
using Game.Settings;
using Game.UI.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Menu {
    public class LoadoutManager : UIElementBase {
        public static LoadoutManager Instance { get; private set; }

        [Header("Weapon Data")]
        [SerializeField] private WeaponData[] primaryWeapons;

        [SerializeField] private WeaponData[] secondaryWeapons;
        [SerializeField] private WeaponData[] tertiaryWeapons;

        // --- Profile Inspection State ---
        private bool _isInspectMode;
        private ulong _inspectTargetSteamId;
        private string _inspectTargetName;
        // --------------------------------

        [Header("Color/Material Options")]
        [SerializeField] private Material[] playerMaterials; // Must have 6 materials matching color-0 through color-5

        [Header("3D Preview")]
        [SerializeField] private Camera previewCamera;

        [SerializeField] private Transform previewPositionTransform; // Transform where the preview model should spawn
        [SerializeField] private GameObject playerModelPrefab;
        [SerializeField] private GameObject previewPlayerRoot;
        [SerializeField] private List<GameObject> previewPrimaryWeapons = new();
        [SerializeField] private List<GameObject> previewSecondaryWeapons = new();

        [Header("UI Templates")]
        [SerializeField] private VisualTreeAsset weaponOptionTemplate;
        private bool _missingWeaponOptionTemplateLogged;
        private bool _invalidWeaponOptionTemplateLogged;

        [SerializeField]
        private Transform secondaryWeaponParent; // Optional explicit parent for secondary holster models

        private RenderTexture _previewRenderTexture; // Will be created/updated dynamically

        // UI Elements
        private Label _playerNameLabel;
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
        private SkinnedMeshRenderer[] _cachedPreviewRenderers;
        private SkinnedMeshRenderer _previewSkinnedRenderer;
        private readonly List<GameObject> _previewWeaponModels = new();
        private readonly List<GameObject> _previewSecondaryWeaponModels = new();
        private readonly Dictionary<GameObject, Renderer[]> _previewWeaponRenderers = new();
        private readonly HashSet<GameObject> _prewarmedPreviewWeapons = new();
        private GameObject _previewWeaponCacheModel;
        private bool _previewWeaponsCached;
        private bool _viewportRootHandlersRegistered;
        private Coroutine _previewWeaponPrewarmCoroutine;

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
        private bool _previewRotationBlockedByUnsavedModal;

        // Bounds cache for preview model anti-culling fix
        private static readonly Bounds MaxBounds = new(Vector3.zero,
            new Vector3(float.MaxValue, float.MaxValue, float.MaxValue));

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
        private VisualElement _statsContainer;
        private VisualElement _challengesContainer; // New: Right side
        private Button _statsButton;
        private bool _showingStats;
        private VisualElement _nameContainer;
        private VisualElement _backgroundElement;
        private Coroutine _backgroundFadeCoroutine;
        private Coroutine _slideInCoroutine;
        private Coroutine _slideOutCoroutine;
        private const float SlideAnimationDuration = 0.3f;
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
        private const float PreviewResolutionScale = 0.75f;
        private const int PreviewMsaa = 2;
        private const int PreviewWarmupFrames = 2;
        private Vector3 _cachedPreviewAnchorPosition;
        private Quaternion _cachedPreviewAnchorRotation = Quaternion.identity;
        private bool _hasCachedPreviewAnchorPose;

        public Action OnApplyCustomizationRequested;
        public Action OnReloadCustomizationRequested;

        protected override void Awake() {
            base.Awake();
            if(Instance != null && Instance != this) {
                Debug.LogWarning("[LoadoutManager] Multiple instances detected. Using the most recently awakened instance.");
            }
            Instance = this;

            if(mainMenuManager == null) {
                mainMenuManager = MainMenuManager.Instance;
            }
            if(mainMenuManager != null && uiDocument == null) {
                uiDocument = mainMenuManager.uiDocument;
            }
            ResetPreviewCameraTarget();
        }

        protected override void OnEnable() {
            base.OnEnable();
            if(mainMenuManager != null) {
                mainMenuManager.OnLoadoutPanelRequested -= ShowLoadout;
                mainMenuManager.OnLoadoutPanelRequested += ShowLoadout;
                mainMenuManager.OnLoadoutProfileViewRequested -= ShowProfileView;
                mainMenuManager.OnLoadoutProfileViewRequested += ShowProfileView;
            }
            // Subscribe to resolution changes
            EventBus.Unsubscribe<ResolutionChangedEvent>(OnResolutionChanged);
            EventBus.Subscribe<ResolutionChangedEvent>(OnResolutionChanged);
        }

        protected override void OnDisable() {
            if(mainMenuManager != null) {
                mainMenuManager.OnLoadoutPanelRequested -= ShowLoadout;
                mainMenuManager.OnLoadoutProfileViewRequested -= ShowProfileView;
            }
            // Stop brute force rendering
            _previewActive = false;
            _showingStats = false;
            _previewRotationBlockedByUnsavedModal = false;

            if(Root != null && _outsideClickHandlerRegistered) {
                Root.UnregisterCallback<PointerDownEvent>(OnRootPointerDown, TrickleDown.TrickleDown);
                _outsideClickHandlerRegistered = false;
            }

            // Clean up viewport handlers
            if(_viewport != null) {
                _viewport.UnregisterCallback<PointerDownEvent>(OnViewportPointerDown);
                _viewport.UnregisterCallback<PointerMoveEvent>(OnViewportPointerMove);
                _viewport.UnregisterCallback<PointerUpEvent>(OnViewportPointerUp);
                _viewport.UnregisterCallback<PointerLeaveEvent>(OnViewportPointerLeave);
            }

            // Clean up root-level viewport handlers
            if(Root != null && _viewportRootHandlersRegistered) {
                Root.UnregisterCallback<PointerDownEvent>(OnRootPointerDownForViewport, TrickleDown.TrickleDown);
                Root.UnregisterCallback<PointerMoveEvent>(OnRootPointerMoveForViewport, TrickleDown.TrickleDown);
                Root.UnregisterCallback<PointerUpEvent>(OnRootPointerUpForViewport, TrickleDown.TrickleDown);
                _viewportRootHandlersRegistered = false;
            }

            // Unsubscribe from resolution changes
            EventBus.Unsubscribe<ResolutionChangedEvent>(OnResolutionChanged);

            ReleasePreviewRenderTexture();
            ResetPreviewCameraTarget();
            base.OnDisable();
        }

        protected override void OnInitialize() {
            SetupUIReferences();
            SetupEventHandlers();
            RegisterOutsideClickHandler();
            LoadSavedLoadout();
        }

        protected override Dictionary<string, Type> GetRequiredElements() {
            return new Dictionary<string, Type> {
                { "weapon-selection-container", typeof(VisualElement) },
                { "apply-loadout-button", typeof(Button) },
                { "career-toggle-button", typeof(Button) },
                { "stats-container", typeof(VisualElement) },
                { "challenges-container", typeof(VisualElement) },
                { "stats-level-label", typeof(Label) },
                { "stats-xp-bar", typeof(ProgressBar) },
                { "stats-xp-text", typeof(Label) }
            };
        }

        protected override void OnDestroy() {
            if(Instance == this) {
                Instance = null;
            }
            ReleasePreviewRenderTexture();
            base.OnDestroy();
        }

        public void ShowLoadout() {
            Setup3DPreview();

            // Mark preview as active for brute force rendering
            _previewActive = true;

            // Reset Stats state (User request: "reset state to showing loadout when re-entering")
            _showingStats = false;
            if(_statsButton != null) _statsButton.text = "CAREER";
            if(_statsContainer != null) _statsContainer.style.display = DisplayStyle.None;
            if(_challengesContainer != null) _challengesContainer.style.display = DisplayStyle.None;
            if(_weaponContainer != null) _weaponContainer.style.display = DisplayStyle.Flex;
            if(_customizationContainer != null) _customizationContainer.style.display = DisplayStyle.Flex;

            // Reload data (Local or Remote based on mode)
            LoadLoadoutData();

            // Finish init/animation
            FinishShowLoadout();
        }

        public void ShowProfileView(ulong steamId, string playerName, bool isEditable) {
            _isInspectMode = !isEditable;
            _inspectTargetSteamId = steamId;
            _inspectTargetName = playerName;

            ShowLoadout();

            if(_isInspectMode) {
                // Disable editing UI
                if(_applyLoadoutButton == null) return;
                _applyLoadoutButton.SetEnabled(false);
                _applyLoadoutButton.text = $"VIEWING {playerName.ToUpper()}";
            } else {
                // Normal mode
                if(_applyLoadoutButton == null) return;
                _applyLoadoutButton.SetEnabled(true);
                _applyLoadoutButton.text = "APPLY LOADOUT";
            }
        }

        private void LoadLoadoutData() {
            if(_isInspectMode) {
                LoadRemoteLoadout(_inspectTargetSteamId);
            } else {
                LoadLocalLoadout();
            }
        }

        private void LoadRemoteLoadout(ulong steamId) {
            // MOCK DATA for now
            // In a real implementation, we would fetch this from Steam Lobby Data or NetworkVariable
            UnityEngine.Random.InitState((int)steamId); // Deterministic mock based on ID

            _selectedPrimaryIndex = UnityEngine.Random.Range(0, primaryWeapons.Length);
            _selectedSecondaryIndex = UnityEngine.Random.Range(0, secondaryWeapons.Length);
            _selectedTertiaryIndex = UnityEngine.Random.Range(0, tertiaryWeapons.Length);

            if(_playerNameLabel != null) _playerNameLabel.text = _inspectTargetName;

            UpdateDropdownSelection(_primaryDropdown, _selectedPrimaryIndex, primaryWeapons);
            UpdateDropdownSelection(_secondaryDropdown, _selectedSecondaryIndex, secondaryWeapons);
            UpdateDropdownSelection(_tertiaryDropdown, _selectedTertiaryIndex, tertiaryWeapons);

            UpdateWeaponPreview();
        }

        private void LoadLocalLoadout() {
            var p = GameSettings.Data.player;
            _selectedPrimaryIndex = p.primaryWeaponIndex;
            _selectedSecondaryIndex = p.secondaryWeaponIndex;
            _selectedTertiaryIndex = p.tertiaryWeaponIndex;
            if(_playerNameLabel != null) _playerNameLabel.text = Social.StreamerMode.GetLocalDisplayName();

            UpdateDropdownSelection(_primaryDropdown, _selectedPrimaryIndex, primaryWeapons);
            UpdateDropdownSelection(_secondaryDropdown, _selectedSecondaryIndex, secondaryWeapons);
            UpdateDropdownSelection(_tertiaryDropdown, _selectedTertiaryIndex, tertiaryWeapons);

            UpdateWeaponPreview();
        }

        private void FinishShowLoadout() {
            // Reset containers to off-screen positions to ensure a consistent slide-in animation
            SetContainerTranslate(_weaponContainer, WeaponOffscreenPercent);
            SetContainerTranslate(_customizationContainer, CustomizationOffscreenPercent);
            SetContainerTranslate(_nameContainer, NameOffscreenPercent);
            SetContainerTranslate(_statsContainer, WeaponOffscreenPercent); // Stats slides from left
            SetContainerTranslate(_challengesContainer, CustomizationOffscreenPercent); // Challenges from right

            // Stop any slide-out animation and start slide-in
            StopSlideAnimations();
            FadeBackground(true);
            StartSlideIn();

            // Small warmup render pass for build stability.
            StartCoroutine(BruteForceInitialRendering());
        }

        private void ReleasePreviewRenderTexture(bool destroyAsset = true) {
            if(_previewRenderTexture == null) return;

            ClearPreviewTextureRef();

            if(previewCamera != null && previewCamera.targetTexture == _previewRenderTexture) {
                previewCamera.targetTexture = null;
            }

            _previewRenderTexture.Release();

            if(destroyAsset) {
                if(Application.isPlaying) {
                    Destroy(_previewRenderTexture);
                } else {
                    DestroyImmediate(_previewRenderTexture, true);
                }
            }

            _previewRenderTexture = null;
        }

        /// <summary>Clears the preview background texture reference.</summary>
        private void ClearPreviewTextureRef() {
            if(_backgroundElement != null) {
                _backgroundElement.style.backgroundImage = StyleKeyword.Null;
            }

            if(Root == null) return;
            var background = Root.Q<VisualElement>("player-model-background");
            if(background != null) {
                background.style.backgroundImage = StyleKeyword.Null;
            }
        }

        /// <summary>
        /// Recreates the render texture with the specified dimensions.
        /// Called when resolution changes or when preview is first set up.
        /// </summary>
        private void RecreateRenderTexture(int width, int height) {
            // Release old texture
            ReleasePreviewRenderTexture();

            _previewRenderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) {
                antiAliasing = PreviewMsaa,
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
        private void OnResolutionChanged(ResolutionChangedEvent evt) {
            if(evt == null) return;
            var width = evt.Width;
            var height = evt.Height;
            // Only recreate if preview is currently active
            if(_previewRenderTexture != null && previewCamera != null && previewCamera.enabled) {
                RecreateRenderTexture(GetPreviewRenderWidth(width), GetPreviewRenderHeight(height));
            }
        }

        private static int GetPreviewRenderWidth(int sourceWidth) {
            return Mathf.Max(256, Mathf.RoundToInt(sourceWidth * PreviewResolutionScale));
        }

        private static int GetPreviewRenderHeight(int sourceHeight) {
            return Mathf.Max(256, Mathf.RoundToInt(sourceHeight * PreviewResolutionScale));
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
            _weaponContainer = QRequired<VisualElement>("weapon-selection-container");
            _customizationContainer = QOptional<VisualElement>("customization-container");
            _nameContainer = QOptional<VisualElement>("name-buttons-container");
            _backgroundElement = QOptional<VisualElement>("player-model-background");
            _playerNameLabel = QOptional<Label>("player-name-label");

            if(_backgroundElement != null) {
                _backgroundElement.style.opacity = new StyleFloat(0f);
                _backgroundElement.AddToClassList("hidden");
                _backgroundElement.style.display = StyleKeyword.Null;
            }

            // Unsaved changes modal
            _loadoutUnsavedModal = QOptional<VisualElement>("loadout-unsaved-changes-modal");
            _loadoutUnsavedYes = QOptional<Button>("loadout-unsaved-yes");
            _loadoutUnsavedNo = QOptional<Button>("loadout-unsaved-no");
            _loadoutUnsavedCancel = QOptional<Button>("loadout-unsaved-cancel");
            if(_loadoutUnsavedYes != null) {
                EventCallback<ClickEvent> yesHandler = _ => OnLoadoutUnsavedYes();
                _loadoutUnsavedYes.RegisterCallback(yesHandler);
                RegisterCleanup(() => _loadoutUnsavedYes.UnregisterCallback(yesHandler));
                EventCallback<MouseEnterEvent> yesEnterHandler = MainMenuManager.MouseEnter;
                _loadoutUnsavedYes.RegisterCallback(yesEnterHandler);
                RegisterCleanup(() => _loadoutUnsavedYes.UnregisterCallback(yesEnterHandler));
            }

            if(_loadoutUnsavedNo != null) {
                EventCallback<ClickEvent> noHandler = _ => OnLoadoutUnsavedNo();
                _loadoutUnsavedNo.RegisterCallback(noHandler);
                RegisterCleanup(() => _loadoutUnsavedNo.UnregisterCallback(noHandler));
                EventCallback<MouseEnterEvent> noEnterHandler = MainMenuManager.MouseEnter;
                _loadoutUnsavedNo.RegisterCallback(noEnterHandler);
                RegisterCleanup(() => _loadoutUnsavedNo.UnregisterCallback(noEnterHandler));
            }
            if(_loadoutUnsavedCancel != null) {
                EventCallback<ClickEvent> cancelHandler = _ => OnLoadoutUnsavedCancel();
                _loadoutUnsavedCancel.RegisterCallback(cancelHandler);
                RegisterCleanup(() => _loadoutUnsavedCancel.UnregisterCallback(cancelHandler));
                EventCallback<MouseEnterEvent> cancelEnterHandler = MainMenuManager.MouseEnter;
                _loadoutUnsavedCancel.RegisterCallback(cancelEnterHandler);
                RegisterCleanup(() => _loadoutUnsavedCancel.UnregisterCallback(cancelEnterHandler));
            }

            _statsButton = QRequired<Button>("career-toggle-button");
            Action statsClickHandler = () => {
                // Play positive sound when going to career, negative when going back to loadout
                UISound.PlayButtonClick(isBack: _showingStats);
                ToggleStats();
            };
            _statsButton.clicked += statsClickHandler;
            RegisterCleanup(() => _statsButton.clicked -= statsClickHandler);
            EventCallback<MouseEnterEvent> statsEnterHandler = MainMenuManager.MouseEnter;
            _statsButton.RegisterCallback(statsEnterHandler);
            RegisterCleanup(() => _statsButton.UnregisterCallback(statsEnterHandler));

            // Cache career UI references from UXML
            BuildStatsUI();

            // Containers start off-screen via USS, no need to initialize positions here

            if(_playerNameLabel != null) _playerNameLabel.text = Social.StreamerMode.GetLocalDisplayName();

            _applyLoadoutButton = QRequired<Button>("apply-loadout-button");

            Action applyClickHandler = () => {
                if(mainMenuManager != null) {
                    MainMenuManager.OnButtonClicked();
                }
                OnApplyLoadoutClicked();
            };
            _applyLoadoutButton.clicked += applyClickHandler;
            RegisterCleanup(() => _applyLoadoutButton.clicked -= applyClickHandler);
            EventCallback<MouseEnterEvent> applyEnterHandler = MainMenuManager.MouseEnter;
            _applyLoadoutButton.RegisterCallback(applyEnterHandler);
            RegisterCleanup(() => _applyLoadoutButton.UnregisterCallback(applyEnterHandler));

            // Weapon slots (main equipped slot)
            _primarySlot = QOptional<VisualElement>("primary-weapon-slot");
            _secondarySlot = QOptional<VisualElement>("secondary-weapon-slot");
            _tertiarySlot = QOptional<VisualElement>("tertiary-weapon-slot");

            _primaryDropdown = QOptional<VisualElement>("primary-dropdown");
            _secondaryDropdown = QOptional<VisualElement>("secondary-dropdown");
            _tertiaryDropdown = QOptional<VisualElement>("tertiary-dropdown");
            _primaryDropdownScroll = _primaryDropdown != null ? _primaryDropdown.Q<ScrollView>("primary-scroll") : null;
            _secondaryDropdownScroll =
                _secondaryDropdown != null ? _secondaryDropdown.Q<ScrollView>("secondary-scroll") : null;
            _tertiaryDropdownScroll =
                _tertiaryDropdown != null ? _tertiaryDropdown.Q<ScrollView>("tertiary-scroll") : null;

            _primaryWeaponImage = QOptional<Image>("primary-weapon-image");
            _secondaryWeaponImage = QOptional<Image>("secondary-weapon-image");
            _tertiaryWeaponImage = QOptional<Image>("tertiary-weapon-image");

            // Back button
            _backLoadoutButton = QOptional<Button>("back-to-main-from-loadout");
            if(_backLoadoutButton != null) {
                // Unregister any existing handlers first
                _backLoadoutButton.clicked -= OnBackClicked;
                _backLoadoutButton.UnregisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);

                // Register handlers
                _backLoadoutButton.clicked += () => {
                    if(mainMenuManager != null) {
                        MainMenuManager.OnButtonClicked(true);
                    }

                    OnBackClicked();
                };
                _backLoadoutButton.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);
            } else {
                Debug.LogError("[LoadoutManager] Back button not found!");
            }
        }

        private void SetupEventHandlers() {
            // Weapon slot clicks (main equipped slot - opens dropdown)
            _primarySlot.RegisterCallback<ClickEvent>(_ => ToggleWeaponDropdown(_primaryDropdown));
            _primarySlot.RegisterCallback<ClickEvent>(_ => MainMenuManager.OnButtonClicked());
            _primarySlot.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);

            _secondarySlot.RegisterCallback<ClickEvent>(_ => ToggleWeaponDropdown(_secondaryDropdown));
            _secondarySlot.RegisterCallback<ClickEvent>(_ => MainMenuManager.OnButtonClicked());
            _secondarySlot.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);

            _tertiarySlot.RegisterCallback<ClickEvent>(_ => ToggleWeaponDropdown(_tertiaryDropdown));
            _tertiarySlot.RegisterCallback<ClickEvent>(_ => MainMenuManager.OnButtonClicked());
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
            if(_outsideClickHandlerRegistered || Root == null) return;
            Root.RegisterCallback<PointerDownEvent>(OnRootPointerDown, TrickleDown.TrickleDown);
            RegisterCleanup(() => {
                if(Root != null) {
                    Root.UnregisterCallback<PointerDownEvent>(OnRootPointerDown, TrickleDown.TrickleDown);
                }
            });
            _outsideClickHandlerRegistered = true;
        }

        private void LoadSavedLoadout() {
            if(_playerNameLabel != null) _playerNameLabel.text = Social.StreamerMode.GetLocalDisplayName();

            // Load weapons (equipped weapon index saved per slot)
            var p = GameSettings.Data.player;
            _selectedPrimaryIndex = p.primaryWeaponIndex;
            _selectedSecondaryIndex = p.secondaryWeaponIndex;
            _selectedTertiaryIndex = p.tertiaryWeaponIndex;
            _savedPrimaryIndex = _selectedPrimaryIndex;
            _savedSecondaryIndex = _selectedSecondaryIndex;
            _savedTertiaryIndex = _selectedTertiaryIndex;
            _customizationDirty = false;
            UpdateDirtyState();

            UpdateWeaponImages();
            UpdatePlayerModel();
        }


        private void OnApplyLoadoutClicked() {
            // Deprecated: Custom name saving
            // Player display name is derived from Steam/StreamerMode and is not saved in settings.

            // Save weapons (already saved when selected, but ensure they're current)
            var p = GameSettings.Data.player;
            p.primaryWeaponIndex = _selectedPrimaryIndex;
            p.secondaryWeaponIndex = _selectedSecondaryIndex;
            p.tertiaryWeaponIndex = _selectedTertiaryIndex;

            // Save customization (apply customization changes)
            OnApplyCustomizationRequested?.Invoke();

            GameSettings.Save();
            Debug.Log(
                $"[LoadoutManager] All loadout settings saved: Weapons={_selectedPrimaryIndex}/{_selectedSecondaryIndex}/{_selectedTertiaryIndex}");

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

        private bool EnsureWeaponOptionTemplateAssigned() {
            if(weaponOptionTemplate != null) {
                return true;
            }

            if(_missingWeaponOptionTemplateLogged) {
                return false;
            }

            _missingWeaponOptionTemplateLogged = true;
            Debug.LogError(
                "[LoadoutManager] Missing `weaponOptionTemplate` assignment. " +
                "Assign a weapon option VisualTreeAsset in the inspector.",
                this);
            return false;
        }

        private void PopulateWeaponDropdown(ScrollView scroll, WeaponData[] weapons, int selectedIndex,
            Action<int> onSelect) {
            if(scroll == null) return;
            if(!EnsureWeaponOptionTemplateAssigned()) return;

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

                var weaponOption = weaponOptionTemplate.CloneTree();
                var weaponImage = weaponOption.Q<Image>("weapon-image");
                if(weaponImage == null) {
                    if(_invalidWeaponOptionTemplateLogged) return;
                    _invalidWeaponOptionTemplateLogged = true;
                    Debug.LogError(
                        "[LoadoutManager] `weaponOptionTemplate` is missing required child `weapon-image`.",
                        this);
                    return;
                }

                if(weapons[i].icon != null) {
                    weaponImage.sprite = weapons[i].icon;
                }

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

            // Create or update render texture at reduced resolution for significantly lower GPU cost.
            var screenWidth = GetPreviewRenderWidth(Screen.width);
            var screenHeight = GetPreviewRenderHeight(Screen.height);

            if(_previewRenderTexture == null ||
               _previewRenderTexture.width != screenWidth ||
               _previewRenderTexture.height != screenHeight) {
                RecreateRenderTexture(screenWidth, screenHeight);
            }

            // Keep this broad for compatibility across existing project layer setups.
            previewCamera.cullingMask = -1; // Everything

            // Ensure preview camera is enabled and rendering to RenderTexture
            previewCamera.targetTexture = _previewRenderTexture;
            previewCamera.enabled = true;

            if(_previewPlayerModel == previewPlayerRoot &&
               previewPlayerRoot != null &&
               !previewPlayerRoot.activeInHierarchy &&
               playerModelPrefab != null) {
                _previewPlayerModel = null;
            }

            if(_previewPlayerModel == null) {
                if(previewPlayerRoot != null && previewPlayerRoot.activeInHierarchy) {
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
                } else if(previewPlayerRoot != null) {
                    _previewPlayerModel = previewPlayerRoot;
                    _previewPlayerModel.SetActive(true);
                } else {
                    Debug.LogWarning("[LoadoutManager] No preview player root or prefab assigned.");
                    return;
                }
            }

            if(_previewWeaponCacheModel != _previewPlayerModel) {
                _previewWeaponCacheModel = _previewPlayerModel;
                _previewWeaponsCached = false;
                _previewWeaponRenderers.Clear();
                _prewarmedPreviewWeapons.Clear();
            }

            AlignPreviewModelToAnchor();
            DisablePreviewModelRootMotion();

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

                // Cache SkinnedMeshRenderers for update loop
                _cachedPreviewRenderers = _previewPlayerModel.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                _previewSkinnedRenderer = _previewPlayerModel.GetComponentInChildren<SkinnedMeshRenderer>(true);
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
            var background = QOptional<VisualElement>("player-model-background");
            _viewport = QOptional<VisualElement>("player-model-viewport");
            var uiOverlay = QOptional<VisualElement>("ui-overlay");

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
                Debug.LogError(
                    $"[LoadoutManager] Background or RenderTexture is null! Background: {background != null}, RenderTexture: {_previewRenderTexture != null}");
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
                if(Root != null && !_viewportRootHandlersRegistered) {
                    Root.RegisterCallback<PointerDownEvent>(OnRootPointerDownForViewport, TrickleDown.TrickleDown);
                    Root.RegisterCallback<PointerMoveEvent>(OnRootPointerMoveForViewport, TrickleDown.TrickleDown);
                    Root.RegisterCallback<PointerUpEvent>(OnRootPointerUpForViewport, TrickleDown.TrickleDown);
                    _viewportRootHandlersRegistered = true;
                }
            } else {
                Debug.LogWarning(
                    $"[LoadoutManager] Viewport or RenderTexture is null! Viewport: {_viewport != null}, RenderTexture: {_previewRenderTexture != null}");
            }

            UpdatePlayerModel();

            // Force another bounds update after material is applied (in case material changes affect bounds)
            StartCoroutine(DelayedPreviewBoundsUpdate());

            if(previewCamera == null || !previewCamera.enabled) return;
            ForcePreviewModelBoundsUpdate();
            previewCamera.Render();
        }

        private void AlignPreviewModelToAnchor() {
            if(_previewPlayerModel == null) return;

            if(previewPositionTransform != null) {
                _previewPlayerModel.transform.SetPositionAndRotation(
                    previewPositionTransform.position,
                    previewPositionTransform.rotation
                );
                return;
            }

            if(!_hasCachedPreviewAnchorPose) {
                _cachedPreviewAnchorPosition = _previewPlayerModel.transform.position;
                _cachedPreviewAnchorRotation = _previewPlayerModel.transform.rotation;
                _hasCachedPreviewAnchorPose = true;
            }

            _previewPlayerModel.transform.SetPositionAndRotation(_cachedPreviewAnchorPosition, _cachedPreviewAnchorRotation);
        }

        private void DisablePreviewModelRootMotion() {
            if(_previewPlayerModel == null) return;

            var animators = _previewPlayerModel.GetComponentsInChildren<Animator>(true);
            foreach(var animator in animators) {
                if(animator == null) continue;
                animator.applyRootMotion = false;
            }
        }


        private void UpdatePlayerModel() {
            if(_previewPlayerModel == null) return;

            var skinnedRenderer = _previewSkinnedRenderer;
            if(skinnedRenderer == null) {
                skinnedRenderer = _previewPlayerModel.GetComponentInChildren<SkinnedMeshRenderer>(true);
                _previewSkinnedRenderer = skinnedRenderer;
            }
            if(skinnedRenderer == null) return;

            // Apply new material packet system to preview model
            var c = GameSettings.Data.player.customization;
            var packetIndex = c.materialPacketIndex;
            var baseColor = new Color(c.baseColor.x, c.baseColor.y, c.baseColor.z, c.baseColor.w);
            var smoothness = c.smoothness;
            var metallic = c.metallic;
            var specularColor = new Color(c.specularColor.x, c.specularColor.y, c.specularColor.z, c.specularColor.w);
            var heightStrength = c.heightStrength;
            var emissionEnabled = c.emissionEnabled;
            var emissionColor = new Color(c.emissionColor.x, c.emissionColor.y, c.emissionColor.z, c.emissionColor.w);

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

            var materials = skinnedRenderer.sharedMaterials;
            if(materials.Length > 1) {
                materials[1] = generatedMaterial;
                skinnedRenderer.sharedMaterials = materials;
            } else {
                Debug.LogWarning(
                    "[LoadoutManager] Preview player model does not have enough material slots for customization.");
            }
        }

        private void CachePreviewWeaponModels() {
            if(_previewWeaponsCached) return;
            CachePreviewPrimaryWeaponModels();
            CachePreviewSecondaryWeaponModels();
            _previewWeaponsCached = true;

            if(_previewWeaponPrewarmCoroutine != null) {
                StopCoroutine(_previewWeaponPrewarmCoroutine);
            }
            _previewWeaponPrewarmCoroutine = StartCoroutine(PrewarmPreviewWeapons());
        }

        private void CachePreviewPrimaryWeaponModels() {
            _previewWeaponModels.Clear();
            if(previewPrimaryWeapons is { Count: > 0 }) {
                foreach(var weapon in previewPrimaryWeapons) {
                    if(weapon == null) continue;
                    _previewWeaponModels.Add(weapon);
                    CachePreviewWeaponRenderers(weapon);
                }

                return;
            }

            if(_previewPlayerModel == null) return;

            var weaponSocket = FindChildRecursive(_previewPlayerModel.transform, "weaponsocket");
            if(weaponSocket == null) {
                Debug.LogWarning(
                    "[LoadoutManager] WeaponSocket not found on preview model, and no weapons assigned in inspector.");
                return;
            }

            foreach(Transform child in weaponSocket) {
                var o = child.gameObject;
                _previewWeaponModels.Add(o);
                CachePreviewWeaponRenderers(o);
            }
        }

        private void CachePreviewSecondaryWeaponModels() {
            _previewSecondaryWeaponModels.Clear();

            if(previewSecondaryWeapons is { Count: > 0 }) {
                foreach(var weapon in previewSecondaryWeapons) {
                    if(weapon == null) continue;
                    _previewSecondaryWeaponModels.Add(weapon);
                    CachePreviewWeaponRenderers(weapon);
                }

                return;
            }

            if(_previewPlayerModel == null) return;

            var parent = secondaryWeaponParent != null
                ? secondaryWeaponParent
                : FindChildRecursive(_previewPlayerModel.transform, "hip");

            if(parent == null) {
                Debug.LogWarning(
                    "[LoadoutManager] Secondary weapon parent not found on preview model. Assign it in inspector for accurate holster previews.");
                return;
            }

            var secondaryLookup = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            foreach(Transform child in parent) {
                if(child == null) continue;
                var o = child.gameObject;
                secondaryLookup[child.name] = o;
                CachePreviewWeaponRenderers(o);
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

        private void UpdateWeaponModelSet(List<GameObject> models, int selectedIndex) {
            if(models == null || models.Count == 0) return;

            var safeIndex = Mathf.Clamp(selectedIndex, 0, models.Count - 1);
            for(var i = 0; i < models.Count; i++) {
                var weapon = models[i];
                if(weapon == null) continue;
                var shouldShow = i == safeIndex;
                SetPreviewWeaponVisible(weapon, shouldShow);
            }
        }

        /// <summary>
        /// Public method to update the preview model material. Called by CharacterCustomizationManager when settings change.
        /// </summary>
        public void UpdatePreviewModelMaterial(int packetIndex, Color baseColor, float smoothness, float metallic,
            Color specularColor, float heightStrength, bool emissionEnabled, Color emissionColor) {
            if(_previewPlayerModel == null) return;

            var skinnedRenderer = _previewSkinnedRenderer;
            if(skinnedRenderer == null) {
                skinnedRenderer = _previewPlayerModel.GetComponentInChildren<SkinnedMeshRenderer>(true);
                _previewSkinnedRenderer = skinnedRenderer;
            }
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

            var materials = skinnedRenderer.sharedMaterials;
            if(materials.Length > 1) {
                materials[1] = generatedMaterial;
                skinnedRenderer.sharedMaterials = materials;
            } else {
                Debug.LogWarning(
                    "[LoadoutManager] Preview player model does not have enough material slots for customization.");
            }
        }

        private void CachePreviewWeaponRenderers(GameObject weapon) {
            if(weapon == null || _previewWeaponRenderers.ContainsKey(weapon)) return;
            _previewWeaponRenderers[weapon] = weapon.GetComponentsInChildren<Renderer>(true);
        }

        private IEnumerator PrewarmPreviewWeapons() {
            var warmupList = new List<GameObject>(_previewWeaponModels.Count + _previewSecondaryWeaponModels.Count);
            warmupList.AddRange(_previewWeaponModels);
            warmupList.AddRange(_previewSecondaryWeaponModels);

            foreach(var weapon in warmupList) {
                if(weapon == null || _prewarmedPreviewWeapons.Contains(weapon)) continue;

                if(!weapon.activeSelf) {
                    weapon.SetActive(true);
                }

                CachePreviewWeaponRenderers(weapon);
                if(_previewWeaponRenderers.TryGetValue(weapon, out var renderers)) {
                    foreach(var r in renderers) {
                        if(r != null) {
                            r.enabled = false;
                        }
                    }
                }

                _prewarmedPreviewWeapons.Add(weapon);
                yield return null;
            }

            UpdatePreviewWeaponModel();
            _previewWeaponPrewarmCoroutine = null;
        }

        private static void SetRendererVisibility(Renderer[] renderers, bool visible) {
            if(renderers == null) return;
            foreach(var renderer in renderers) {
                if(renderer != null) {
                    renderer.enabled = visible;
                }
            }
        }

        private void SetPreviewWeaponVisible(GameObject weapon, bool visible) {
            if(weapon == null) return;
            CachePreviewWeaponRenderers(weapon);

            if(visible) {
                if(!weapon.activeSelf) {
                    weapon.SetActive(true);
                }

                _prewarmedPreviewWeapons.Add(weapon);
                if(_previewWeaponRenderers.TryGetValue(weapon, out var showRenderers)) {
                    SetRendererVisibility(showRenderers, true);
                }
                return;
            }

            if(_prewarmedPreviewWeapons.Contains(weapon) &&
               _previewWeaponRenderers.TryGetValue(weapon, out var hideRenderers)) {
                SetRendererVisibility(hideRenderers, false);
                return;
            }

            if(weapon.activeSelf) {
                weapon.SetActive(false);
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
            if(_previewPlayerModel == null) {
                Debug.LogWarning("[LoadoutManager] HandleViewportPointerDown called but model is null!");
                return;
            }

            if(!IsPreviewRotationInputEnabled()) {
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
            if(!_isDragging || _previewPlayerModel == null || !IsPreviewRotationInputEnabled()) return;

            var deltaX = position.x - _lastMousePosition.x;

            // Add sample
            var now = Time.time;
            _movementSamples.Add(new MovementSample { Time = now, X = position.x });

            // Prune old samples
            for(var i = _movementSamples.Count - 1; i >= 0; i--) {
                if(now - _movementSamples[i].Time > VelocitySampleWindow) {
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
            if(!IsPreviewRotationInputEnabled()) {
                _isDragging = false;
                _currentRotationVelocity = 0f;
                return;
            }

            if(!_isDragging) return;
            _isDragging = false;

            // Calculate velocity from samples
            var now = Time.time;

            // Prune old samples first
            for(var i = _movementSamples.Count - 1; i >= 0; i--) {
                if(now - _movementSamples[i].Time > VelocitySampleWindow) {
                    _movementSamples.RemoveAt(i);
                }
            }

            if(_movementSamples.Count >= 2) {
                var first = _movementSamples[0];
                var last = _movementSamples[^1];
                var timeDelta = last.Time - first.Time;

                if(timeDelta > 0.001f) {
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
            if(!_previewActive) {
                if(previewCamera != null && previewCamera.enabled) {
                    previewCamera.enabled = false;
                }
                return;
            }

            if(!IsPreviewRotationInputEnabled()) return;

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
        /// Small warmup render pass to avoid first-frame empty captures in some builds.
        /// </summary>
        private IEnumerator BruteForceInitialRendering() {
            for(var i = 0; i < PreviewWarmupFrames; i++) {
                yield return null;

                if(!_previewActive || previewCamera == null || !previewCamera.enabled ||
                   _previewPlayerModel == null) break;

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
            if(previewCamera != null) {
                previewCamera.enabled = false;
            }
            ReleasePreviewRenderTexture();

            // Start slide-out animation immediately
            StopSlideAnimations();
            FadeBackground(false);
            StartSlideOut();

            // Show main menu panel immediately
            var loadoutPanel = QOptional<VisualElement>("loadout-panel");
            if(mainMenuManager != null) {
                mainMenuManager.ShowPanel(mainMenuManager.MainMenuPanel);
            } else {
                var mainMenuPanel = QOptional<VisualElement>("main-menu-panel");
                if(mainMenuPanel != null) {
                    mainMenuPanel.RemoveFromClassList("hidden");
                    mainMenuPanel.style.display = DisplayStyle.Flex;
                }
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
            var primaryDirty = _selectedPrimaryIndex != _savedPrimaryIndex;
            var secondaryDirty = _selectedSecondaryIndex != _savedSecondaryIndex;
            var tertiaryDirty = _selectedTertiaryIndex != _savedTertiaryIndex;

            _hasUnsavedChanges = primaryDirty || secondaryDirty || tertiaryDirty || _customizationDirty;
        }

        private void ShowLoadoutUnsavedModal() {
            SetPreviewRotationBlocked(true);
            if(_loadoutUnsavedModal == null) return;
            _loadoutUnsavedModal.RemoveFromClassList("hidden");
            _loadoutUnsavedModal.style.display = DisplayStyle.Flex;
            _loadoutUnsavedModal.BringToFront();
        }

        private void HideLoadoutUnsavedModal() {
            SetPreviewRotationBlocked(false);
            if(_loadoutUnsavedModal == null) return;
            _loadoutUnsavedModal.AddToClassList("hidden");
            _loadoutUnsavedModal.style.display = StyleKeyword.Null;
        }

        private void OnLoadoutUnsavedYes() {
            if(mainMenuManager != null) {
                MainMenuManager.OnButtonClicked();
            }

            OnApplyLoadoutClicked();
            HideLoadoutUnsavedModal();
            StartCoroutine(HideLoadoutAndSwitchPanel());
        }

        private void OnLoadoutUnsavedNo() {
            if(mainMenuManager != null) {
                MainMenuManager.OnButtonClicked(true);
            }

            RevertLoadoutChanges();
            HideLoadoutUnsavedModal();
            StartCoroutine(HideLoadoutAndSwitchPanel());
        }

        private void OnLoadoutUnsavedCancel() {
            if(mainMenuManager != null) {
                MainMenuManager.OnButtonClicked(true);
            }

            HideLoadoutUnsavedModal();
        }

        private void RevertLoadoutChanges() {
            if(_playerNameLabel != null) _playerNameLabel.text = Social.StreamerMode.GetLocalDisplayName();

            _selectedPrimaryIndex = _savedPrimaryIndex;
            _selectedSecondaryIndex = _savedSecondaryIndex;
            _selectedTertiaryIndex = _savedTertiaryIndex;
            UpdateWeaponImages();

            OnReloadCustomizationRequested?.Invoke();

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

        private bool IsPreviewRotationInputEnabled() {
            return _rotationEnabled && !_previewRotationBlockedByUnsavedModal;
        }

        /// <summary>Blocks or unblocks preview rotation when an unsaved-changes modal is open.</summary>
        private void SetPreviewRotationBlocked(bool blocked) {
            _previewRotationBlockedByUnsavedModal = blocked;
            if(!blocked) return;

            _isDragging = false;
            _currentRotationVelocity = 0f;
            _movementSamples.Clear();
        }

        private IEnumerator AnimateContainersSlideIn() {
            // Get current positions (in case we're interrupting a slide-out)
            var weaponStart = GetCurrentTranslatePercent(_weaponContainer);
            var customizationStart = GetCurrentTranslatePercent(_customizationContainer);
            var nameStart = GetCurrentTranslatePercent(_nameContainer);
            var statsStart = GetCurrentTranslatePercent(_statsContainer);
            var challengesStart = GetCurrentTranslatePercent(_challengesContainer);

            // Target positions (on-screen)
            var weaponTarget = Vector2.zero;
            var customizationTarget = Vector2.zero;
            var nameTarget = Vector2.zero;
            var statsTarget = Vector2.zero;
            var challengesTarget = Vector2.zero;

            var elapsed = 0f;

            while(elapsed < SlideAnimationDuration) {
                elapsed += Time.deltaTime;
                var t = Mathf.SmoothStep(0f, 1f, elapsed / SlideAnimationDuration);

                // Interpolate positions
                if(_weaponContainer != null) {
                    _weaponContainer.style.translate =
                        new StyleTranslate(PercentToTranslate(Vector2.Lerp(weaponStart, weaponTarget, t)));
                }

                if(_customizationContainer != null) {
                    _customizationContainer.style.translate =
                        new StyleTranslate(
                            PercentToTranslate(Vector2.Lerp(customizationStart, customizationTarget, t)));
                }

                if(_nameContainer != null) {
                    _nameContainer.style.translate =
                        new StyleTranslate(PercentToTranslate(Vector2.Lerp(nameStart, nameTarget, t)));
                }

                if(_statsContainer != null) {
                    _statsContainer.style.translate =
                        new StyleTranslate(PercentToTranslate(Vector2.Lerp(statsStart, statsTarget, t)));
                }

                if(_challengesContainer != null) {
                    _challengesContainer.style.translate =
                        new StyleTranslate(PercentToTranslate(Vector2.Lerp(challengesStart, challengesTarget, t)));
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

            if(_statsContainer != null) {
                _statsContainer.style.translate = new StyleTranslate(PercentToTranslate(statsTarget));
            }

            if(_challengesContainer != null) {
                _challengesContainer.style.translate = new StyleTranslate(PercentToTranslate(challengesTarget));
            }

            _slideInCoroutine = null;
        }

        private IEnumerator AnimateContainersSlideOut() {
            // Get current positions (in case we're interrupting a slide-in)
            var weaponStart = GetCurrentTranslatePercent(_weaponContainer);
            var customizationStart = GetCurrentTranslatePercent(_customizationContainer);
            var nameStart = GetCurrentTranslatePercent(_nameContainer);
            var statsStart = GetCurrentTranslatePercent(_statsContainer);
            var challengesStart = GetCurrentTranslatePercent(_challengesContainer);

            // Target positions (off-screen)
            var weaponTarget = WeaponOffscreenPercent;
            var customizationTarget = CustomizationOffscreenPercent;
            var nameTarget = NameOffscreenPercent;
            // Stats slides out to left
            var statsTarget = WeaponOffscreenPercent;
            // Challenges slides out to right
            var challengesTarget = CustomizationOffscreenPercent;

            var elapsed = 0f;

            while(elapsed < SlideAnimationDuration) {
                elapsed += Time.deltaTime;
                var t = Mathf.SmoothStep(0f, 1f, elapsed / SlideAnimationDuration);

                // Interpolate positions
                if(_weaponContainer != null) {
                    _weaponContainer.style.translate =
                        new StyleTranslate(PercentToTranslate(Vector2.Lerp(weaponStart, weaponTarget, t)));
                }

                if(_customizationContainer != null) {
                    _customizationContainer.style.translate =
                        new StyleTranslate(
                            PercentToTranslate(Vector2.Lerp(customizationStart, customizationTarget, t)));
                }

                if(_nameContainer != null) {
                    _nameContainer.style.translate =
                        new StyleTranslate(PercentToTranslate(Vector2.Lerp(nameStart, nameTarget, t)));
                }

                if(_statsContainer != null) {
                    _statsContainer.style.translate =
                        new StyleTranslate(PercentToTranslate(Vector2.Lerp(statsStart, statsTarget, t)));
                }

                if(_challengesContainer != null) {
                    _challengesContainer.style.translate =
                        new StyleTranslate(PercentToTranslate(Vector2.Lerp(challengesStart, challengesTarget, t)));
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

            if(_statsContainer != null) {
                _statsContainer.style.translate = new StyleTranslate(PercentToTranslate(statsTarget));
            }

            if(_challengesContainer != null) {
                _challengesContainer.style.translate = new StyleTranslate(PercentToTranslate(challengesTarget));
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

            // Use cached renderers if available, otherwise fallback (lazy init or expensive call)
            if(_cachedPreviewRenderers == null) {
                _cachedPreviewRenderers = _previewPlayerModel.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            }

            foreach(var smr in _cachedPreviewRenderers) {
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

        // --- Stats UI Methods ---

        private void BuildStatsUI() {
            _statsContainer = QRequired<VisualElement>("stats-container");
            _challengesContainer = QRequired<VisualElement>("challenges-container");
        }

        private void ToggleStats() {
            if(_statsContainer == null || _challengesContainer == null || _statsButton == null) {
                Debug.LogError("[LoadoutManager] Career UI is not initialized correctly. Check loadout UXML assignments.", this);
                return;
            }

            _showingStats = !_showingStats;

            if(_showingStats) {
                UpdateStatsUI();
                _statsContainer.style.display = DisplayStyle.Flex;
                _challengesContainer.style.display = DisplayStyle.Flex;
                if(_customizationContainer != null) _customizationContainer.style.display = DisplayStyle.None;
                if(_weaponContainer != null) _weaponContainer.style.display = DisplayStyle.None;
                _statsButton.text = "LOADOUT";
            } else {
                _statsContainer.style.display = DisplayStyle.None;
                _challengesContainer.style.display = DisplayStyle.None;
                if(_customizationContainer != null) _customizationContainer.style.display = DisplayStyle.Flex;
                if(_weaponContainer != null) _weaponContainer.style.display = DisplayStyle.Flex;
                _statsButton.text = "CAREER";
            }
        }

        private void UpdateStatsUI() {
            if(ProgressionManager.Instance == null) return;
            var pm = ProgressionManager.Instance;
            var data = pm.Data;

            // Level
            var levelLabel = _statsContainer.Q<Label>("stats-level-label");
            if(levelLabel != null) levelLabel.text = $"LEVEL {data.level}";

            // XP
            var maxXp = pm.GetXpForLevel(data.level);
            var bar = _statsContainer.Q<ProgressBar>("stats-xp-bar");
            if(bar != null) {
                bar.lowValue = 0;
                bar.highValue = maxXp;
                bar.value = data.currentXp;
            }

            var xpText = _statsContainer.Q<Label>("stats-xp-text");
            if(xpText != null) xpText.text = $"{data.currentXp} / {maxXp} XP";

            // Basic Stats
            SetLabelText("stats-kills", data.stats.kills.ToString());
            SetLabelText("stats-deaths", data.stats.deaths.ToString());

            var kdr = data.stats.deaths > 0 ? (float)data.stats.kills / data.stats.deaths : data.stats.kills;
            SetLabelText("stats-kdr", kdr.ToString("F2"));

            SetLabelText("stats-wins", data.stats.wins.ToString());
            SetLabelText("stats-losses", data.stats.losses.ToString());

            var accuracy = data.stats.shotsFired > 0
                ? (float)data.stats.shotsHit / data.stats.shotsFired * 100f
                : 0f;
            SetLabelText("stats-accuracy", $"{accuracy:F1}%");

            // New Stats
            SetLabelText("stats-streak", data.stats.highestKillStreak.ToString());
            SetLabelText("stats-oob", data.stats.oobDeaths.ToString());

            SetLabelText("stats-grapples", data.stats.grapplesUsed.ToString());
            SetLabelText("stats-jumppads", data.stats.jumpPadsUsed.ToString());
            SetLabelText("stats-airtime", FormatTime(data.stats.totalAirTime));

            SetLabelText("stats-playtime", FormatTime(data.stats.totalPlayTimeSeconds));

            // Prefer rolling per-match average speed for accuracy, then fall back to lifetime average.
            double avgSpeed = 0f;
            if(ProgressionManager.Instance != null) {
                avgSpeed = ProgressionManager.Instance.GetAverageMatchSpeed();
            }
            if(avgSpeed <= 0f) {
                avgSpeed = data.stats.totalPlayTimeSeconds > 0f
                    ? data.stats.totalDistanceTraveled / data.stats.totalPlayTimeSeconds
                    : 0f;
            }
            SetLabelText("stats-speed", $"{avgSpeed:F1} m/s");

            SetLabelText("stats-balltime", FormatTime(data.stats.timeHoldingHopball));
            SetLabelText("stats-hilltime", FormatTime(data.stats.timeAsKing));
            SetLabelText("stats-taggedtime", FormatTime(data.stats.timeTagged));

            // Daily Challenges - WIP is shown, no need to update
            // Weekly Challenges - WIP is shown, no need to update
        }

        private void SetLabelText(string labelName, string text) {
            var l = _statsContainer.Q<Label>(labelName);
            if(l != null) l.text = text;
        }

        private static string FormatTime(float seconds) {
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        // Helper methods added for Profile View Refactor
        private static void UpdateDropdownSelection(VisualElement dropdown, int selectedIndex, WeaponData[] data) {
            if(dropdown == null || data == null || selectedIndex < 0 || selectedIndex >= data.Length) return;

            var label = dropdown.Q<Label>("selected-label");
            if(label != null) label.text = data[selectedIndex].weaponName;

            var icon = dropdown.Q<Image>("selected-icon");
            if(icon != null) icon.sprite = data[selectedIndex].icon;
        }

        private void UpdateWeaponPreview() {
            UpdateWeaponImages();
            UpdatePlayerModel();
        }

        [Serializable]
        public class WeaponData {
            public string weaponName;
            public Sprite icon;
            public GameObject weaponPrefab;
        }
    }
}

using System;
using System.Collections;
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

        [SerializeField] private Transform originalCameraTransform;
        [SerializeField] private Transform previewCameraTransform;
        [SerializeField] private GameObject playerModelPrefab;
        [SerializeField] private RenderTexture previewRenderTexture;
        [SerializeField] private float cameraTransitionDuration = 1f;

        private UIDocument _uiDocument;
        private VisualElement _root;

        // UI Elements
        private VisualElement _colorOptions;
        private TextField _playerNameInput;
        private Button _applyNameButton;
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

        // Current selections
        private int _selectedColorIndex;
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

        [SerializeField] private MainMenuManager mainMenuManager;

        private void Awake() {
            _uiDocument = mainMenuManager.uiDocument;
        }

        private void OnEnable() {
            _root = _uiDocument.rootVisualElement;
            SetupUIReferences();
            SetupEventHandlers();
            RegisterOutsideClickHandler();
            LoadSavedLoadout();
        }

        private void OnDisable() {
            if(_root != null && _outsideClickHandlerRegistered) {
                _root.UnregisterCallback<PointerDownEvent>(OnRootPointerDown, TrickleDown.TrickleDown);
                _outsideClickHandlerRegistered = false;
            }
        }

        public void ShowLoadout() {
            SelectColor(PlayerPrefs.GetInt("PlayerColorIndex", 0));
            Setup3DPreview();
            StopCoroutine(TransitionCameraToOriginal());
            StartCoroutine(TransitionCameraToPreview());
        }

        private void HideLoadout() {
            StopCoroutine(TransitionCameraToPreview());
            StartCoroutine(TransitionCameraToOriginal());
        }

        private void SetupUIReferences() {
            // Color picker
            _colorOptions = _root.Q<VisualElement>("color-options");

            // Name input
            _playerNameInput = _root.Q<TextField>("player-name-input");
            _applyNameButton = _root.Q<Button>("apply-name-button");

            _applyNameButton.clicked += () => mainMenuManager.OnButtonClicked();
            _applyNameButton.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);

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
            _backLoadoutButton.clicked += () => {
                mainMenuManager.OnButtonClicked(true);
                OnBackClicked();
            };
            _backLoadoutButton.RegisterCallback<MouseEnterEvent>(MainMenuManager.MouseEnter);
        }

        private void SetupEventHandlers() {
            // Current color display - clicking any visible circle opens dropdown
            for(var i = 0; i < 7; i++) {
                var currentCircle = _root.Q<VisualElement>($"current-color-{i}");
                if(currentCircle is not null) {
                    currentCircle.RegisterCallback<ClickEvent>(_ => ToggleColorPicker());
                    currentCircle.RegisterCallback<ClickEvent>(_ => mainMenuManager.OnButtonClicked());
                    currentCircle.RegisterCallback<MouseEnterEvent>(evt => MainMenuManager.MouseEnter(evt));
                }
            }

            // Color option clicks
            for(var i = 0; i < 7; i++) {
                var optionCircle = _root.Q<VisualElement>($"option-color-{i}");
                if(optionCircle != null) {
                    var index = i;
                    optionCircle.RegisterCallback<ClickEvent>(_ => SelectColor(index));
                    optionCircle.RegisterCallback<ClickEvent>(_ => mainMenuManager.OnButtonClicked());
                    optionCircle.RegisterCallback<MouseEnterEvent>(evt => MainMenuManager.MouseEnter(evt));
                }
            }

            // Name input change
            _playerNameInput.RegisterValueChangedCallback(evt => OnNameChanged(evt.newValue));
            _applyNameButton.clicked += OnApplyNameClicked;

            // Weapon slot clicks (main equipped slot - opens dropdown)
            _primarySlot.RegisterCallback<ClickEvent>(_ => ToggleWeaponDropdown(_primaryDropdown));
            _primarySlot.RegisterCallback<ClickEvent>(_ => mainMenuManager.OnButtonClicked());
            _primarySlot.RegisterCallback<MouseEnterEvent>(evt => MainMenuManager.MouseEnter(evt));

            _secondarySlot.RegisterCallback<ClickEvent>(_ => ToggleWeaponDropdown(_secondaryDropdown));
            _secondarySlot.RegisterCallback<ClickEvent>(_ => mainMenuManager.OnButtonClicked());
            _secondarySlot.RegisterCallback<MouseEnterEvent>(evt => MainMenuManager.MouseEnter(evt));

            _tertiarySlot.RegisterCallback<ClickEvent>(_ => ToggleWeaponDropdown(_tertiaryDropdown));
            _tertiarySlot.RegisterCallback<ClickEvent>(_ => mainMenuManager.OnButtonClicked());
            _tertiarySlot.RegisterCallback<MouseEnterEvent>(evt => MainMenuManager.MouseEnter(evt));

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
            // Load color
            _selectedColorIndex = PlayerPrefs.GetInt("PlayerColorIndex", 0);
            UpdateColorDisplay(_selectedColorIndex);

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

            UpdateWeaponImages();
            UpdatePlayerModel();
        }

        private void UpdateColorDisplay(int colorIndex) {
            // Hide all current color circles
            for(int i = 0; i < 7; i++) {
                var currentCircle = _root.Q<VisualElement>($"current-color-{i}");
                currentCircle?.AddToClassList("hidden");
            }

            // Show only the selected color
            var selectedCircle = _root.Q<VisualElement>($"current-color-{colorIndex}");
            selectedCircle?.RemoveFromClassList("hidden");

            // Update options dropdown - hide selected, show others
            for(var i = 0; i < 7; i++) {
                var optionCircle = _root.Q<VisualElement>($"option-color-{i}");
                if(optionCircle != null) {
                    if(i == colorIndex) {
                        optionCircle.AddToClassList("hidden");
                    } else {
                        optionCircle.RemoveFromClassList("hidden");
                    }
                }
            }
        }

        private void ToggleColorPicker() {
            _colorOptions.ToggleInClassList("hidden");
        }

        private void SelectColor(int index) {
            _selectedColorIndex = index;
            UpdateColorDisplay(index);
            _colorOptions.AddToClassList("hidden");

            PlayerPrefs.SetInt("PlayerColorIndex", index);
            PlayerPrefs.Save();

            UpdatePlayerModel();
        }

        private void OnNameChanged(string newName) {
            _currentPlayerName = newName;

            if(_currentPlayerName != _savedPlayerName) {
                _applyNameButton.RemoveFromClassList("hidden");
            } else {
                _applyNameButton.AddToClassList("hidden");
            }
        }

        private void OnApplyNameClicked() {
            _savedPlayerName = _currentPlayerName;
            PlayerPrefs.SetString("PlayerName", _savedPlayerName);
            PlayerPrefs.Save();

            _applyNameButton.AddToClassList("hidden");

            Debug.Log($"Player name saved: {_savedPlayerName}");
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
                PopulateWeaponDropdown(_primaryDropdownScroll, primaryWeapons, _selectedPrimaryIndex, SelectPrimaryWeapon);
            } else if(dropdown == _secondaryDropdown) {
                PopulateWeaponDropdown(_secondaryDropdownScroll, secondaryWeapons, _selectedSecondaryIndex, SelectSecondaryWeapon);
            } else if(dropdown == _tertiaryDropdown) {
                PopulateWeaponDropdown(_tertiaryDropdownScroll, tertiaryWeapons, _selectedTertiaryIndex, SelectTertiaryWeapon);
            }
        }

        private void PopulateWeaponDropdown(ScrollView scroll, WeaponData[] weapons, int selectedIndex,
            Action<int> onSelect) {
            if(scroll == null) return;

            var container = scroll.contentContainer;
            container.Clear();

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
            PlayerPrefs.SetInt("PrimaryWeaponIndex", index);
            PlayerPrefs.Save();
            UpdateWeaponImages();
        }

        private void SelectSecondaryWeapon(int index) {
            _selectedSecondaryIndex = index;
            PlayerPrefs.SetInt("SecondaryWeaponIndex", index);
            PlayerPrefs.Save();
            UpdateWeaponImages();
        }

        private void SelectTertiaryWeapon(int index) {
            _selectedTertiaryIndex = index;
            PlayerPrefs.SetInt("TertiaryWeaponIndex", index);
            PlayerPrefs.Save();
            UpdateWeaponImages();
        }

        private void UpdateWeaponImages() {
            UpdateWeaponSlot(primaryWeapons, ref _selectedPrimaryIndex, _primaryWeaponImage, _primarySlot,
                ref _currentPrimarySlotClass, "weapon-primary");
            UpdateWeaponSlot(secondaryWeapons, ref _selectedSecondaryIndex, _secondaryWeaponImage, _secondarySlot,
                ref _currentSecondarySlotClass, "weapon-secondary");
            UpdateWeaponSlot(tertiaryWeapons, ref _selectedTertiaryIndex, _tertiaryWeaponImage, _tertiarySlot,
                ref _currentTertiarySlotClass, "weapon-tertiary");
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

        private void UpdateWeaponSlot(WeaponData[] weapons, ref int selectedIndex, Image targetImage,
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
            if(previewCamera == null || playerModelPrefab == null) return;

            // Set camera depth higher than UI camera to render above HUD background
            // UI camera typically has depth 0, so we'll use a higher value
            if(previewCamera.depth <= 0) {
                previewCamera.depth = 1; // Render above UI (which is typically depth 0)
            }

            if(_previewPlayerModel != null) {
                Destroy(_previewPlayerModel);
            }

            var modelPosition = previewCameraTransform.position + previewCameraTransform.transform.forward * 3f;
            modelPosition.y -= 0.5f;

            _previewPlayerModel = Instantiate(playerModelPrefab, modelPosition, Quaternion.Euler(0, 180, 0));
            _previewPlayerModel.GetComponentInChildren<SkinnedMeshRenderer>().materials[1] =
                playerMaterials[PlayerPrefs.GetInt("PlayerSkinIndex", 0)];

            var viewport = _root.Q<VisualElement>("player-model-viewport");
            if(viewport != null && previewRenderTexture != null) {
                viewport.style.backgroundImage =
                    new StyleBackground(Background.FromRenderTexture(previewRenderTexture));
                // Bring viewport to front of its parent to ensure it renders above other elements
                viewport.BringToFront();

                // Also ensure the loadout panel is brought to front
                var loadoutPanel = _root.Q<VisualElement>("loadout-panel");
                loadoutPanel?.BringToFront();
            }

            UpdatePlayerModel();
        }

        private void UpdatePlayerModel() {
            if(_previewPlayerModel == null) return;

            var skinnedRenderer = _previewPlayerModel.GetComponentInChildren<SkinnedMeshRenderer>();
            if(skinnedRenderer == null || _selectedColorIndex >= playerMaterials.Length) return;
            var materials = skinnedRenderer.materials;
            if(materials.Length <= 0) return;
            materials[1] = playerMaterials[_selectedColorIndex];
            skinnedRenderer.materials = materials;
        }

        private void OnBackClicked() {
            HideLoadout();

            // Use MainMenuManager's ShowPanel to ensure proper display handling
            if(mainMenuManager != null) {
                mainMenuManager.ShowPanel(mainMenuManager.MainMenuPanel);
            } else {
                // Fallback if MainMenuManager is not available
                var loadoutPanel = _root.Q<VisualElement>("loadout-panel");
                var mainMenuPanel = _root.Q<VisualElement>("main-menu-panel");

                loadoutPanel.AddToClassList("hidden");
                loadoutPanel.style.display = StyleKeyword.Null;
                mainMenuPanel.RemoveFromClassList("hidden");
                mainMenuPanel.style.display = DisplayStyle.Flex;
            }
        }

        private IEnumerator TransitionCameraToPreview() {
            var startPosition = previewCamera.transform.position;
            var startRotation = previewCamera.transform.rotation;

            var elapsed = 0f;

            while(elapsed < cameraTransitionDuration) {
                elapsed += Time.deltaTime;
                var t = Mathf.SmoothStep(0f, 1f, elapsed / cameraTransitionDuration);

                previewCamera.transform.position = Vector3.Lerp(startPosition, previewCameraTransform.position, t);
                previewCamera.transform.rotation = Quaternion.Slerp(startRotation, previewCameraTransform.rotation, t);

                yield return null;
            }
        }

        private IEnumerator TransitionCameraToOriginal() {
            if(previewCamera == null) yield break;

            var startPosition = previewCamera.transform.position;
            var startRotation = previewCamera.transform.rotation;

            var elapsed = 0f;

            while(elapsed < cameraTransitionDuration) {
                elapsed += Time.deltaTime;
                var t = Mathf.SmoothStep(0f, 1f, elapsed / cameraTransitionDuration);

                previewCamera.transform.position = Vector3.Lerp(startPosition, originalCameraTransform.position, t);
                previewCamera.transform.rotation = Quaternion.Slerp(startRotation, originalCameraTransform.rotation, t);

                yield return null;
            }

            if(previewCamera.transform.position != originalCameraTransform.position ||
               previewCamera.transform.rotation != originalCameraTransform.rotation) yield break;
            if(_previewPlayerModel != null) {
                Destroy(_previewPlayerModel);
            }
        }
    }

    [Serializable]
    public class WeaponData {
        public string weaponName;
        public Sprite icon;
        public GameObject weaponPrefab;
    }
}
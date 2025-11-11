using System;
using System.Collections;
using Network.Singletons;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UIElements;

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
        
    private Image _primaryWeaponImage;
    private Image _secondaryWeaponImage;
    private Image _tertiaryWeaponImage;
        
    private GameObject _previewPlayerModel;
        
    // Current selections
    private int _selectedColorIndex;
    private int _selectedPrimaryIndex;
    private int _selectedSecondaryIndex;
    private int _selectedTertiaryIndex;
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
        LoadSavedLoadout();
    }

    public void ShowLoadout() {
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
        _applyNameButton.RegisterCallback<MouseOverEvent>(mainMenuManager.MouseHover);
        
        // Weapon slots
        _primarySlot = _root.Q<VisualElement>("primary-weapon-slot");
        _secondarySlot = _root.Q<VisualElement>("secondary-weapon-slot");
        _tertiarySlot = _root.Q<VisualElement>("tertiary-weapon-slot");
            
        _primaryDropdown = _root.Q<VisualElement>("primary-dropdown");
        _secondaryDropdown = _root.Q<VisualElement>("secondary-dropdown");
        _tertiaryDropdown = _root.Q<VisualElement>("tertiary-dropdown");
            
        _primaryWeaponImage = _root.Q<Image>("primary-weapon-image");
        _secondaryWeaponImage = _root.Q<Image>("secondary-weapon-image");
        _tertiaryWeaponImage = _root.Q<Image>("tertiary-weapon-image");
            
        // Back button
        _backLoadoutButton = _root.Q<Button>("back-to-main-from-loadout");
        _backLoadoutButton.clicked += () => {
            mainMenuManager.OnButtonClicked(true);
            OnBackClicked();
        };
        _backLoadoutButton.RegisterCallback<MouseOverEvent>(mainMenuManager.MouseHover);
    }
        
    private void SetupEventHandlers() {
        // Current color display - clicking any visible circle opens dropdown
        for (int i = 0; i < 7; i++) {
            var currentCircle = _root.Q<VisualElement>($"current-color-{i}");
            if (currentCircle != null) {
                currentCircle.RegisterCallback<ClickEvent>(evt => ToggleColorPicker());
                currentCircle.RegisterCallback<ClickEvent>(evt => mainMenuManager.OnButtonClicked());
                currentCircle.RegisterCallback<MouseOverEvent>(evt => mainMenuManager.MouseHover(evt));
            }
        }
        
        // Color option clicks
        for(var i = 0; i < 7; i++) {
            var optionCircle = _root.Q<VisualElement>($"option-color-{i}");
            if(optionCircle != null) {
                var index = i;
                optionCircle.RegisterCallback<ClickEvent>(evt => SelectColor(index));
                optionCircle.RegisterCallback<ClickEvent>(evt => mainMenuManager.OnButtonClicked());
                optionCircle.RegisterCallback<MouseOverEvent>(evt => mainMenuManager.MouseHover(evt));
            }
        }
            
        // Name input change
        _playerNameInput.RegisterValueChangedCallback(evt => OnNameChanged(evt.newValue));
        _applyNameButton.clicked += OnApplyNameClicked;
            
        // Weapon slot clicks
        _primarySlot.RegisterCallback<ClickEvent>(evt => ToggleWeaponDropdown(_primaryDropdown));
        _primarySlot.RegisterCallback<ClickEvent>(evt => mainMenuManager.OnButtonClicked());
        _primarySlot.RegisterCallback<MouseOverEvent>(evt => mainMenuManager.MouseHover(evt));
        
        _secondarySlot.RegisterCallback<ClickEvent>(evt => ToggleWeaponDropdown(_secondaryDropdown));
        _secondarySlot.RegisterCallback<ClickEvent>(evt => mainMenuManager.OnButtonClicked());
        _secondarySlot.RegisterCallback<MouseOverEvent>(evt => mainMenuManager.MouseHover(evt));
        
        _tertiarySlot.RegisterCallback<ClickEvent>(evt => ToggleWeaponDropdown(_tertiaryDropdown));
        _tertiarySlot.RegisterCallback<ClickEvent>(evt => mainMenuManager.OnButtonClicked());
        _tertiarySlot.RegisterCallback<MouseOverEvent>(evt => mainMenuManager.MouseHover(evt));
            
        // Populate weapon dropdowns
        PopulateWeaponDropdown(_primaryDropdown.Q<ScrollView>("primary-scroll"), primaryWeapons, SelectPrimaryWeapon);
        PopulateWeaponDropdown(_secondaryDropdown.Q<ScrollView>("secondary-scroll"), secondaryWeapons, SelectSecondaryWeapon);
        PopulateWeaponDropdown(_tertiaryDropdown.Q<ScrollView>("tertiary-scroll"), tertiaryWeapons, SelectTertiaryWeapon);
    }
        
    private void LoadSavedLoadout() {
        // Load color
        _selectedColorIndex = PlayerPrefs.GetInt("PlayerColorIndex", 0);
        UpdateColorDisplay(_selectedColorIndex);
            
        // Load name
        _savedPlayerName = PlayerPrefs.GetString("PlayerName", "Player");
        _currentPlayerName = _savedPlayerName;
            
        if (_playerNameInput != null) {
            _playerNameInput.value = _currentPlayerName;
        }
            
        // Load weapons
        _selectedPrimaryIndex = PlayerPrefs.GetInt("PrimaryWeaponIndex", 0);
        _selectedSecondaryIndex = PlayerPrefs.GetInt("SecondaryWeaponIndex", 0);
        _selectedTertiaryIndex = PlayerPrefs.GetInt("TertiaryWeaponIndex", 0);
            
        UpdateWeaponImages();
        UpdatePlayerModel();
    }
    
    private void UpdateColorDisplay(int colorIndex) {
        // Hide all current color circles
        for (int i = 0; i < 7; i++) {
            var currentCircle = _root.Q<VisualElement>($"current-color-{i}");
            currentCircle?.AddToClassList("hidden");
        }
        
        // Show only the selected color
        var selectedCircle = _root.Q<VisualElement>($"current-color-{colorIndex}");
        selectedCircle?.RemoveFromClassList("hidden");
        
        // Update options dropdown - hide selected, show others
        for (var i = 0; i < 7; i++) {
            var optionCircle = _root.Q<VisualElement>($"option-color-{i}");
            if (optionCircle != null) {
                if (i == colorIndex) {
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
            
        if (_currentPlayerName != _savedPlayerName) {
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
        // Close all dropdowns
        _primaryDropdown.AddToClassList("hidden");
        _secondaryDropdown.AddToClassList("hidden");
        _tertiaryDropdown.AddToClassList("hidden");
            
        // Toggle the clicked one
        if (dropdown.ClassListContains("hidden")) {
            dropdown.RemoveFromClassList("hidden");
        }
    }
        
    private void PopulateWeaponDropdown(ScrollView scroll, WeaponData[] weapons, Action<int> onSelect) {
        var container = scroll.contentContainer;
        container.Clear();
            
        for (var i = 0; i < weapons.Length; i++) {
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
            });
                
            container.Add(weaponOption);
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
        if (primaryWeapons.Length > _selectedPrimaryIndex)
            _primaryWeaponImage.sprite = primaryWeapons[_selectedPrimaryIndex].icon;
            
        if (secondaryWeapons.Length > _selectedSecondaryIndex)
            _secondaryWeaponImage.sprite = secondaryWeapons[_selectedSecondaryIndex].icon;
            
        if (tertiaryWeapons.Length > _selectedTertiaryIndex)
            _tertiaryWeaponImage.sprite = tertiaryWeapons[_selectedTertiaryIndex].icon;
    }
        
    private void Setup3DPreview() {
        if (previewCamera == null || playerModelPrefab == null) return;
            
        if (_previewPlayerModel != null) {
            Destroy(_previewPlayerModel);
        }
        
        var modelPosition = previewCameraTransform.position + previewCameraTransform.transform.forward * 3f;
        modelPosition.y -= 0.5f;
        
        _previewPlayerModel = Instantiate(playerModelPrefab, modelPosition, Quaternion.Euler(0, 180, 0));
            
        var viewport = _root.Q<VisualElement>("player-model-viewport");
        if (viewport != null && previewRenderTexture != null) {
            viewport.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(previewRenderTexture));
        }
            
        UpdatePlayerModel();
    }
        
    private void UpdatePlayerModel() {
        if (_previewPlayerModel == null) return;
            
        var skinnedRenderer = _previewPlayerModel.GetComponentInChildren<SkinnedMeshRenderer>();
        if (skinnedRenderer != null && _selectedColorIndex < playerMaterials.Length) {
            var materials = skinnedRenderer.materials;
            if (materials.Length > 0) {
                materials[0] = playerMaterials[_selectedColorIndex];
                skinnedRenderer.materials = materials;
            }
        }
    }
        
    private void OnBackClicked() {
        HideLoadout();
        
        var loadoutPanel = _root.Q<VisualElement>("loadout-panel");
        var mainMenuPanel = _root.Q<VisualElement>("main-menu-panel");
            
        loadoutPanel?.AddToClassList("hidden");
        mainMenuPanel?.RemoveFromClassList("hidden");
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

        if(previewCamera.transform.position == originalCameraTransform.position && previewCamera.transform.rotation == originalCameraTransform.rotation) {
            if (_previewPlayerModel != null) {
                Destroy(_previewPlayerModel);
            }
        }
    }
}
    
[Serializable]
public class WeaponData {
    public string weaponName;
    public Sprite icon;
    public GameObject weaponPrefab;
}
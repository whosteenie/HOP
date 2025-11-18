using UnityEngine;
using UnityEngine.UIElements;

namespace Network.Singletons {
    public class HUDManager : MonoBehaviour
    {
        [SerializeField] private UIDocument uiDocument;

        private VisualElement _healthContainer;
        private ProgressBar _healthBar;
        private Label _healthValue;
    
        private VisualElement _multiplierContainer;
        private ProgressBar _multiplierBar;
        private Label _multiplierValue;
    
        private VisualElement _ammoContainer;
        private Label _ammoCurrent;
        private Label _ammoTotal;
    
        private VisualElement _crosshairContainer;

        public static HUDManager Instance;
    
        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }
        
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnEnable() {
            var root = uiDocument.rootVisualElement;

            _healthContainer = root.Q<VisualElement>("health-container");
            _healthBar = root.Q<ProgressBar>("health-bar");
            _healthValue = root.Q<Label>("health-value");
        
            _multiplierContainer = root.Q<VisualElement>("multiplier-container");
            _multiplierBar = root.Q<ProgressBar>("multiplier-bar");
            _multiplierValue = root.Q<Label>("multiplier-value");
        
            _ammoContainer = root.Q<VisualElement>("ammo-container");
            _ammoCurrent = root.Q<Label>("ammo-current");
            _ammoTotal = root.Q<Label>("ammo-total");
        
            _crosshairContainer = root.Q<VisualElement>("crosshair-container");
        }

        // Call from Player
        public void UpdateHealth(float current, float max) {
            // Check if we're in Tag mode
            var matchSettings = MatchSettings.Instance;
            bool isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Tag";
            
            if(isTagMode) {
                // Tag mode: don't update health bar (will be updated via UpdateTagStatus)
                return;
            }
            
            var percent = (current / max) * 100f;
            _healthBar.value = percent;
            _healthValue.text = Mathf.CeilToInt(current).ToString();
        }
        
        /// <summary>
        /// Updates the health bar to show tag status in Tag mode.
        /// </summary>
        public void UpdateTagStatus(bool isTagged) {
            var matchSettings = MatchSettings.Instance;
            bool isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Tag";
            
            if(!isTagMode) {
                // Not in Tag mode, don't update
                return;
            }
            
            // Hide health bar, show text status
            _healthBar.style.display = DisplayStyle.None;
            
            if(isTagged) {
                _healthValue.text = "You're it!";
            } else {
                _healthValue.text = "Not it...";
            }
        }
    
        public void UpdateMultiplier(float current, float max) {
            var percent = ((current - 1f) / (max - 1f)) * 100f;
            _multiplierBar.value = percent;
            _multiplierValue.text = current.ToString("0.00") + "x";
        }

        // Call from Weapon
        public void UpdateAmmo(int current, int total) {
            _ammoCurrent.text = current.ToString();
            _ammoTotal.text = total.ToString();
        }

        public void DisableHUD() {
            uiDocument.rootVisualElement.style.display = DisplayStyle.None;
        }

        public void HideHUD() {
            _healthContainer.style.visibility = Visibility.Hidden;
            _multiplierContainer.style.visibility = Visibility.Hidden;
            _ammoContainer.style.visibility = Visibility.Hidden;
            _crosshairContainer.style.visibility = Visibility.Hidden;
        }

        public void ShowHUD() {
            _healthContainer.style.visibility = Visibility.Visible;
            _multiplierContainer.style.visibility = Visibility.Visible;
            _ammoContainer.style.visibility = Visibility.Visible;
            _crosshairContainer.style.visibility = Visibility.Visible;
        }
    }
}

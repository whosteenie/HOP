using UnityEngine;
using UnityEngine.UIElements;

namespace Network.Singletons {
    public class HUDManager : MonoBehaviour {
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

        // Cached values to avoid unnecessary string allocations and UI updates
        private float _cachedHealthValue = -1f;
        private float _cachedHealthPercent = -1f;
        private string _cachedHealthText = "";
        private float _cachedMultiplierValue = -1f;
        private float _cachedMultiplierPercent = -1f;
        private string _cachedMultiplierText = "";
        private int _cachedAmmoCurrent = -1;
        private int _cachedAmmoTotal = -1;
        private string _cachedAmmoCurrentText = "";
        private string _cachedAmmoTotalText = "";

        // Cache MatchSettingsManager.Instance to avoid repeated lookups
        private MatchSettingsManager _cachedMatchSettings;

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

            // Cache MatchSettingsManager.Instance (but don't cache game mode - check it fresh each time)
            _cachedMatchSettings = MatchSettingsManager.Instance;
        }
        
        /// <summary>
        /// Checks if we're in Gun Tag mode. Always checks fresh to handle build initialization order issues.
        /// </summary>
        private bool IsTagMode() {
            // Refresh MatchSettingsManager cache if needed
            if(_cachedMatchSettings == null) {
                _cachedMatchSettings = MatchSettingsManager.Instance;
            }
            
            // Always check fresh - don't cache game mode as it may not be set yet during initialization
            return _cachedMatchSettings != null && _cachedMatchSettings.selectedGameModeId == "Gun Tag";
        }

        // Call from Player
        public void UpdateHealth(float current, float max) {
            // Check if we're in Tag mode (always check fresh)
            if(IsTagMode()) {
                // Tag mode: don't update health bar (will be updated via UpdateTagStatus)
                return;
            }

            // Only update if values have changed
            var percent = (current / max) * 100f;
            var healthText = Mathf.CeilToInt(current).ToString();

            if(Mathf.Abs(_healthBar.value - percent) > 0.01f) {
                _healthBar.value = percent;
                _cachedHealthPercent = percent;
            }

            if(_cachedHealthText != healthText) {
                _healthValue.text = healthText;
                _cachedHealthText = healthText;
                _cachedHealthValue = current;
            }
        }

        /// <summary>
        /// Updates the health bar to show tag status in Tag mode.
        /// </summary>
        public void UpdateTagStatus(bool isTagged) {
            // Check if we're in Tag mode (always check fresh)
            if(!IsTagMode()) {
                // Not in Tag mode, don't update
                return;
            }

            // Hide health bar, show text status
            _healthBar.style.display = DisplayStyle.None;

            var tagText = isTagged ? "You're it!" : "Not it...";
            if(_cachedHealthText != tagText) {
                _healthValue.text = tagText;
                _cachedHealthText = tagText;
            }
        }

        public void UpdateMultiplier(float current, float max) {
            // Only update if values have changed
            var percent = ((current - 1f) / (max - 1f)) * 100f;
            var multiplierText = current.ToString("0.00") + "x";

            if(Mathf.Abs(_multiplierBar.value - percent) > 0.01f) {
                _multiplierBar.value = percent;
                _cachedMultiplierPercent = percent;
            }

            if(_cachedMultiplierText != multiplierText) {
                _multiplierValue.text = multiplierText;
                _cachedMultiplierText = multiplierText;
                _cachedMultiplierValue = current;
            }
        }

        // Call from Weapon
        public void UpdateAmmo(int current, int total) {
            // Only update if values have changed
            if(_cachedAmmoCurrent != current) {
                var ammoCurrentText = current.ToString();
                _ammoCurrent.text = ammoCurrentText;
                _cachedAmmoCurrent = current;
                _cachedAmmoCurrentText = ammoCurrentText;
            }

            if(_cachedAmmoTotal != total) {
                var ammoTotalText = total.ToString();
                _ammoTotal.text = ammoTotalText;
                _cachedAmmoTotal = total;
                _cachedAmmoTotalText = ammoTotalText;
            }
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
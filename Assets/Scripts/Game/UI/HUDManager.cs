using System.Collections.Generic;
using Game.Match;
using Game.Player;
using Network.Events;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI {
    public class HUDManager : UIElementBase {

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
        private string _cachedHealthText = "";
        private string _cachedMultiplierText = "";
        private int _cachedAmmoCurrent = -1;
        private int _cachedAmmoTotal = -1;

        // Cache MatchSettingsManager.Instance to avoid repeated lookups
        private MatchSettingsManager _cachedMatchSettings;

        protected override void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            base.Awake();
        }

        protected override void OnEnable() {
            base.OnEnable();
            // Cache MatchSettingsManager.Instance (but don't cache game mode - check it fresh each time)
            _cachedMatchSettings = MatchSettingsManager.Instance;

            // Subscribe to UI events
            EventBus.Unsubscribe<UpdateHealthEvent>(OnUpdateHealth);
            EventBus.Unsubscribe<UpdateAmmoEvent>(OnUpdateAmmo);
            EventBus.Unsubscribe<UpdateTagStatusEvent>(OnUpdateTagStatus);
            EventBus.Unsubscribe<UpdateMultiplierEvent>(OnUpdateMultiplier);
            EventBus.Unsubscribe<ShowHUDEvent>(OnShowHUD);
            EventBus.Unsubscribe<HideHUDEvent>(OnHideHUD);
            EventBus.Subscribe<UpdateHealthEvent>(OnUpdateHealth);
            EventBus.Subscribe<UpdateAmmoEvent>(OnUpdateAmmo);
            EventBus.Subscribe<UpdateTagStatusEvent>(OnUpdateTagStatus);
            EventBus.Subscribe<UpdateMultiplierEvent>(OnUpdateMultiplier);
            EventBus.Subscribe<ShowHUDEvent>(OnShowHUD);
            EventBus.Subscribe<HideHUDEvent>(OnHideHUD);
        }

        protected override void OnDisable() {
            // Unsubscribe from UI events
            EventBus.Unsubscribe<UpdateHealthEvent>(OnUpdateHealth);
            EventBus.Unsubscribe<UpdateAmmoEvent>(OnUpdateAmmo);
            EventBus.Unsubscribe<UpdateTagStatusEvent>(OnUpdateTagStatus);
            EventBus.Unsubscribe<UpdateMultiplierEvent>(OnUpdateMultiplier);
            EventBus.Unsubscribe<ShowHUDEvent>(OnShowHUD);
            EventBus.Unsubscribe<HideHUDEvent>(OnHideHUD);
            base.OnDisable();
        }

        protected override void OnInitialize() {
            _healthContainer = QOptional<VisualElement>("health-container");
            _healthBar = QOptional<ProgressBar>("health-bar");
            _healthValue = QOptional<Label>("health-value");

            _multiplierContainer = QOptional<VisualElement>("multiplier-container");
            _multiplierBar = QOptional<ProgressBar>("multiplier-bar");
            _multiplierValue = QOptional<Label>("multiplier-value");

            _ammoContainer = QOptional<VisualElement>("ammo-container");
            _ammoCurrent = QOptional<Label>("ammo-current");
            _ammoTotal = QOptional<Label>("ammo-total");

            _crosshairContainer = QOptional<VisualElement>("crosshair-container");
        }

        protected override Dictionary<string, System.Type> GetRequiredElements() {
            return new Dictionary<string, System.Type> {
                { "health-container", typeof(VisualElement) },
                { "health-bar", typeof(ProgressBar) },
                { "ammo-container", typeof(VisualElement) },
                { "crosshair-container", typeof(VisualElement) }
            };
        }

        #region Event Handlers

        private void OnUpdateHealth(UpdateHealthEvent evt) {
            UpdateHealth(evt.Current, evt.Max);
        }

        private void OnUpdateAmmo(UpdateAmmoEvent evt) {
            UpdateAmmo(evt.Current, evt.Max);
        }

        private void OnUpdateTagStatus(UpdateTagStatusEvent evt) {
            UpdateTagStatus(evt.IsTagged);
        }

        private void OnUpdateMultiplier(UpdateMultiplierEvent evt) {
            UpdateMultiplier(evt.Current, evt.Max);
        }

        private void OnShowHUD(ShowHUDEvent evt) {
            ShowHUD();
        }

        private void OnHideHUD(HideHUDEvent evt) {
            HideHUD();
        }

        #endregion

        /// <summary>
        /// Checks if we're in Gun Tag mode. Always checks fresh to handle build initialization order issues.
        /// </summary>
        private bool IsTagMode() {
            // Refresh MatchSettingsManager cache if needed
            if(_cachedMatchSettings == null) _cachedMatchSettings = MatchSettingsManager.Instance;

            // Always check fresh - don't cache game mode as it may not be set yet during initialization
            return _cachedMatchSettings != null && _cachedMatchSettings.selectedGameModeId == "Gun Tag";
        }

        // Event handler - called via EventBus
        private void UpdateHealth(float current, float max) {
            // Check if we're in Tag mode (always check fresh)
            if(IsTagMode()) {
                // Tag mode: don't update health bar (will be updated via UpdateTagStatus)
                return;
            }

            // Ensure healthbar is visible for health-based modes
            // This resets it if it was hidden from Tag mode
            if(_healthBar != null && _healthBar.style.display == DisplayStyle.None) {
                _healthBar.style.display = DisplayStyle.Flex;
            }

            // If cached text is still showing tag status, clear it to force update
            if(_cachedHealthText == "You're it!" || _cachedHealthText == "Not it...") {
                _cachedHealthText = "";
            }

            // Only update if values have changed
            var percent = (current / max) * 100f;
            var healthText = Mathf.CeilToInt(current).ToString();

            if(_healthBar != null && Mathf.Abs(_healthBar.value - percent) > 0.01f) {
                _healthBar.value = percent;
            }

            if(_cachedHealthText == healthText) return;
            _healthValue.text = healthText;
            _cachedHealthText = healthText;
        }

        /// <summary>
        /// Updates the health bar to show tag status in Tag mode.
        /// Event handler - called via EventBus
        /// </summary>
        private void UpdateTagStatus(bool isTagged) {
            // Check if we're in Tag mode (always check fresh)
            if(!IsTagMode()) {
                // Ensure we restore numeric health if tag text was previously shown.
                TryRestoreHealthDisplayFromLocalPlayer();
                return;
            }

            // Hide health bar, show text status
            _healthBar.style.display = DisplayStyle.None;

            var tagText = isTagged ? "You're it!" : "Not it...";
            if(_cachedHealthText == tagText) return;
            _healthValue.text = tagText;
            _cachedHealthText = tagText;
        }

        // Event handler - called via EventBus
        private void UpdateMultiplier(float current, float max) {
            // Only update if values have changed
            var percent = ((current - 1f) / (max - 1f)) * 100f;
            var multiplierText = current.ToString("0.00") + "x";

            if(Mathf.Abs(_multiplierBar.value - percent) > 0.01f) {
                _multiplierBar.value = percent;
            }

            if(_cachedMultiplierText == multiplierText) return;
            _multiplierValue.text = multiplierText;
            _cachedMultiplierText = multiplierText;
        }

        // Event handler - called via EventBus
        private void UpdateAmmo(int current, int total) {
            // Only update if values have changed
            if(_cachedAmmoCurrent != current) {
                var ammoCurrentText = current.ToString();
                _ammoCurrent.text = ammoCurrentText;
                _cachedAmmoCurrent = current;
            }

            if(_cachedAmmoTotal == total) return;
            var ammoTotalText = total.ToString();
            _ammoTotal.text = ammoTotalText;
            _cachedAmmoTotal = total;
        }

        public void DisableHUD() {
            if(Root != null) {
                Root.style.display = DisplayStyle.None;
            }
        }

        // Event handler - called via EventBus
        private void HideHUD() {
            _healthContainer.style.visibility = Visibility.Hidden;
            _multiplierContainer.style.visibility = Visibility.Hidden;
            _ammoContainer.style.visibility = Visibility.Hidden;
            _crosshairContainer.style.visibility = Visibility.Hidden;
        }

        // Event handler - called via EventBus
        private void ShowHUD() {
            _healthContainer.style.visibility = Visibility.Visible;
            _multiplierContainer.style.visibility = Visibility.Visible;
            _ammoContainer.style.visibility = Visibility.Visible;
            _crosshairContainer.style.visibility = Visibility.Visible;
            
            // Reset healthbar display mode based on current game mode
            ResetHealthbarDisplayMode();

            // If we're not in tag mode, immediately restore numeric health text/value.
            if(!IsTagMode()) {
                TryRestoreHealthDisplayFromLocalPlayer();
            }
        }

        /// <summary>
        /// Resets the healthbar display mode based on the current game mode.
        /// Should be called when game mode changes or when HUD is shown.
        /// </summary>
        private void ResetHealthbarDisplayMode() {
            if(_healthBar == null) return;
            
            // Check current game mode and set display accordingly
            // Tag mode: healthbar should be hidden (will be shown via UpdateTagStatus)
            _healthBar.style.display = IsTagMode() ? DisplayStyle.None :
                // Health-based mode: ensure healthbar is visible
                DisplayStyle.Flex;
        }

        /// <summary>
        /// Restores numeric health text/value using local player's current health when not in Gun Tag.
        /// This prevents stale "You're it!" text persisting across mode transitions.
        /// </summary>
        private void TryRestoreHealthDisplayFromLocalPlayer() {
            if(_healthBar == null || _healthValue == null) return;
            if(IsTagMode()) return;

            var localPlayer = PlayerController.LocalPlayer;
            var current = localPlayer != null ? localPlayer.netHealth.Value : 100f;
            var max = 100f;

            _healthBar.style.display = DisplayStyle.Flex;
            var percent = (current / max) * 100f;
            _healthBar.value = percent;

            var healthText = Mathf.CeilToInt(current).ToString();
            _healthValue.text = healthText;
            _cachedHealthText = healthText;
        }
    }
}

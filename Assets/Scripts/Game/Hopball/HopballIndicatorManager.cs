using Game.Match;
using Game.Player;
using UnityEngine;

namespace Game.Hopball {
    /// <summary>
    /// Manages the hopball indicator instance and coordinates with the hopball system.
    /// Creates/destroys indicator, tracks hopball holder, and updates indicator state.
    /// </summary>
    public class HopballIndicatorManager : MonoBehaviour {
        private static HopballIndicatorManager Instance { get; set; }

        [Header("Indicator Prefab")]
        [SerializeField] private GameObject indicatorPrefab;

        private HopballIndicator _currentIndicator;
        private HopballController _currentHopballController;
        private bool _wasEquipped;
        private bool _wasDropped;

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        private void Update() {
            // Only update in Game scene and if hopball mode is active
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null || matchSettings.selectedGameModeId != "Hopball") {
                if(_currentIndicator != null) {
                    DestroyIndicator();
                }

                return;
            }

            // Get current hopball reference
            if(HopballSpawnManager.Instance == null) {
                if(_currentIndicator != null) {
                    DestroyIndicator();
                }

                return;
            }

            var hopball = GetCurrentHopball();
            if(hopball == null) {
                if(_currentIndicator != null) {
                    DestroyIndicator();
                }

                return;
            }

            _currentHopballController = hopball;

            // Check if state changed
            var isEquipped = hopball.IsEquipped;
            var isDropped = hopball.IsDropped;

            if(isEquipped != _wasEquipped || isDropped != _wasDropped || _currentIndicator == null) {
                UpdateIndicator();
                _wasEquipped = isEquipped;
                _wasDropped = isDropped;
            }

            // Update indicator if it exists
            if(_currentIndicator != null) {
                UpdateIndicatorState();
            }
        }

        /// <summary>
        /// Gets the current hopball instance from HopballSpawnManager.
        /// </summary>
        private static HopballController GetCurrentHopball() {
            return HopballSpawnManager.Instance != null ? HopballSpawnManager.Instance.CurrentHopballController : null;
        }

        /// <summary>
        /// Updates or creates the indicator based on hopball state.
        /// </summary>
        private void UpdateIndicator() {
            if(_currentHopballController == null) {
                DestroyIndicator();
                return;
            }

            // Create indicator if it doesn't exist
            if(_currentIndicator == null) {
                CreateIndicator();
            }

            if(_currentIndicator == null) return;

            // Determine target transform
            Transform targetTransform = null;
            var isDropped = _currentHopballController.IsDropped;

            if(_currentHopballController.IsEquipped) {
                // Get holder's transform
                var holderController = GetHolderController();
                if(holderController != null) {
                    targetTransform = holderController.transform;
                }
            } else if(isDropped) {
                // Use hopball transform
                targetTransform = _currentHopballController.transform;
            }

            if(targetTransform != null) {
                _currentIndicator.SetTarget(targetTransform, isDropped);
            }
        }

        /// <summary>
        /// Updates the indicator state (team color, etc.).
        /// </summary>
        private void UpdateIndicatorState() {
            if(_currentIndicator == null || _currentHopballController == null) return;

            if(_currentHopballController.IsEquipped) {
                var holderController = GetHolderController();
                if(holderController != null) {
                    _currentIndicator.UpdateTeamColor(holderController);
                }
            } else {
                // Dropped state - purple color
                _currentIndicator.UpdateTeamColor(null);
            }
        }

        /// <summary>
        /// Gets the PlayerController of the current hopball holder.
        /// </summary>
        private PlayerController GetHolderController() {
            return _currentHopballController == null ? null : _currentHopballController.HolderController;
        }

        /// <summary>
        /// Creates a new indicator instance.
        /// </summary>
        private void CreateIndicator() {
            if(indicatorPrefab == null) {
                Debug.LogWarning("[HopballIndicatorManager] Indicator prefab is not assigned!");
                return;
            }

            var indicatorObj = Instantiate(indicatorPrefab);
            _currentIndicator = indicatorObj.GetComponent<HopballIndicator>();

            if(_currentIndicator == null) {
                Debug.LogError("[HopballIndicatorManager] Indicator prefab does not have HopballIndicator component!");
                Destroy(indicatorObj);
                return;
            }

            // Setup icon
            _currentIndicator.SetupIcon();
        }

        /// <summary>
        /// Destroys the current indicator instance.
        /// </summary>
        private void DestroyIndicator() {
            if(_currentIndicator == null) return;
            Destroy(_currentIndicator.gameObject);
            _currentIndicator = null;
        }

        private void OnDestroy() {
            DestroyIndicator();
        }
    }
}
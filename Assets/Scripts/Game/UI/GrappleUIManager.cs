using Game.Player;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Game.UI {
    public class GrappleUIManager : MonoBehaviour {
        [Header("References")]
        [SerializeField] private UIDocument uiDocument;

        [Header("Settings")]
        [SerializeField] private float maxGrappleDistance = 50f;
        [SerializeField] private LayerMask grappleableLayers;

        [Header("Visual Settings")]
        [SerializeField] private Color readyColor = new(1f, 0.2f, 0.2f, 0.8f);
        [SerializeField] private Color cooldownColor = new(0f, 0f, 0f, 0.3f);
        [SerializeField] private int segments = 20; // Number of segments for the horseshoe
        [SerializeField] private float colorTransitionSpeed = 25f;

        private VisualElement _grappleIndicator;
        private VisualElement[] _segments;
        private bool _isLookingAtGrapplePoint;
        private Color _currentColor;
        private GrappleController _grappleController;
        private CinemachineCamera _fpCamera;
        
        // Bottom indicator (square)
        private VisualElement _grappleIndicatorBottom;
        private VisualElement _grappleIndicatorFill;
        private float _currentFillOpacity = 1f;
        
        // Cache scene name to avoid string allocations
        private string _cachedSceneName;

        public static GrappleUIManager Instance;

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            // Removed DontDestroyOnLoad - GrappleUIManager should be in Game scene only
            
            // Cache scene name to avoid allocations
            UpdateCachedSceneName();
        }

        private void OnEnable() {
            if(uiDocument == null) {
                Debug.LogError("[GrappleUIManager] UIDocument is not assigned!");
                return;
            }

            var root = uiDocument.rootVisualElement;
            if(root == null) {
                Debug.LogError("[GrappleUIManager] UIDocument rootVisualElement is null! UI may not be loaded yet.");
                return;
            }

            _grappleIndicator = root.Q<VisualElement>("grapple-indicator");
            if(_grappleIndicator == null) {
                Debug.LogError("[GrappleUIManager] Could not find grapple-indicator element in UI!");
                return;
            }
            
            // Find bottom indicator elements
            _grappleIndicatorBottom = root.Q<VisualElement>("grapple-indicator-bottom");
            _grappleIndicatorFill = root.Q<VisualElement>("grapple-indicator-fill");

            _currentColor = cooldownColor;
            CreateHorseshoeSegments();
            
            // Set initial visibility based on grapple indicator type setting
            var grappleIndicatorType = PlayerPrefs.GetInt("GrappleIndicator", 0);
            switch(grappleIndicatorType) {
                case 0: // Crosshair
                    ShowCrosshairIndicator();
                    HideBottomIndicator();
                    break;
                case 1: // Bottom
                    HideCrosshairIndicator();
                    ShowBottomIndicator();
                    break;
                case 2: // None
                    HideCrosshairIndicator();
                    HideBottomIndicator();
                    break;
            }
            
            // Subscribe to scene changes to update cache
            SceneManager.sceneLoaded += OnSceneLoaded;
        }
        
        private void OnDisable() {
            // Unsubscribe from scene changes
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }
        
        private void UpdateCachedSceneName() {
            var activeScene = SceneManager.GetActiveScene();
            if(activeScene.IsValid()) {
                _cachedSceneName = activeScene.name;
            }
        }
        
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            UpdateCachedSceneName();
        }

        private void Update() {
            // Validate references are not null and not destroyed
            if(_grappleController == null || _fpCamera == null) return;
            if(_cachedSceneName?.Contains("Game") != true) return;
            
            CheckGrapplePoint();
            
            // Check grapple indicator type setting
            var grappleIndicatorType = PlayerPrefs.GetInt("GrappleIndicator", 0);
            
            // Update appropriate indicator based on setting
            switch(grappleIndicatorType) {
                case 0: // Crosshair (horseshoe)
                    ShowCrosshairIndicator();
                    HideBottomIndicator();
                    UpdateIndicatorVisual();
                    break;
                case 1: // Bottom (square)
                    HideCrosshairIndicator();
                    ShowBottomIndicator();
                    UpdateBottomIndicatorVisual();
                    break;
                case 2: // None
                    HideCrosshairIndicator();
                    HideBottomIndicator();
                    break;
            }
        }
        
        private void ShowCrosshairIndicator() {
            if(_grappleIndicator != null) {
                _grappleIndicator.style.display = DisplayStyle.Flex;
            }
        }
        
        private void HideCrosshairIndicator() {
            if(_grappleIndicator != null) {
                _grappleIndicator.style.display = DisplayStyle.None;
            }
        }
        
        private void ShowBottomIndicator() {
            if(_grappleIndicatorBottom != null) {
                _grappleIndicatorBottom.style.display = DisplayStyle.Flex;
            }
        }
        
        private void HideBottomIndicator() {
            if(_grappleIndicatorBottom != null) {
                _grappleIndicatorBottom.style.display = DisplayStyle.None;
            }
        }

        public void RegisterLocalPlayer(PlayerController player) {
            if(player == null) return;
            _grappleController = player.GetComponentInChildren<GrappleController>();
            _fpCamera = player.GetComponentInChildren<CinemachineCamera>();
        }

        private void CreateHorseshoeSegments() {
            // Clear any existing children
            _grappleIndicator.Clear();

            // Create segments arranged in a horseshoe
            _segments = new VisualElement[segments];

            const float ringRadius = 20f; // Radius in pixels
            const float segmentWidth = 3f;
            const float segmentHeight = 8f;

            // Define the gap at the top (in degrees)
            const float gapDegrees = 108f; // 20% of 360

            // Gap at bottom: don't draw last 20% of segments
            var segmentsToDraw = Mathf.RoundToInt(segments * 0.8f);

            const float arcDegrees = 360f - gapDegrees;

            const float startAngle = 360f + (gapDegrees / 2f);

            for(var i = 0; i < segmentsToDraw; i++) {
                // Calculate angle for this segment (start from bottom, go clockwise)
                // Skip the bottom 20% (72 degrees) to create horseshoe gap
                var progress = segmentsToDraw > 1 ? i / (float)(segmentsToDraw - 1) : 0f;
                var angleDegrees = startAngle + (progress * arcDegrees);
                var angle = angleDegrees * Mathf.Deg2Rad;

                // Create segment
                var segment = new VisualElement {
                    style = {
                        width = segmentWidth,
                        height = segmentHeight,
                        position = Position.Absolute,
                        backgroundColor = cooldownColor
                    }
                };

                // Position around circle
                var x = 25f + Mathf.Sin(angle) * ringRadius - segmentWidth / 2f;
                var y = 25f - Mathf.Cos(angle) * ringRadius - segmentHeight / 2f;

                segment.style.left = x;
                segment.style.top = y;

                // Rotate segment to point toward center
                segment.style.rotate = new Rotate(new Angle(angleDegrees));

                _grappleIndicator.Add(segment);
                _segments[i] = segment;
            }
        }

        private void CheckGrapplePoint() {
            if(!_fpCamera) return;

            var fpCameraTransform = _fpCamera.transform;
            var ray = new Ray(fpCameraTransform.position, fpCameraTransform.forward);
            _isLookingAtGrapplePoint = Physics.Raycast(ray, maxGrappleDistance, grappleableLayers);
        }

        private void UpdateIndicatorVisual() {
            // Validate references before accessing
            if(!_grappleController || _grappleIndicator == null || _segments == null) {
                // Clear references if they're invalid (helps with cleanup)
                if(_grappleController == null) {
                    _fpCamera = null;
                }
                return;
            }

            if(_grappleController.IsGrappling) {
                _grappleIndicator.style.opacity = 0f;
                return;
            }

            _grappleIndicator.style.opacity = 1f;

            // Determine state
            Color targetColor;
            float fillAmount;

            if(!_grappleController.CanGrapple) {
                // Cooldown - show progress
                targetColor = cooldownColor;
                fillAmount = _grappleController.CooldownProgress;
            } else if(_isLookingAtGrapplePoint) {
                // Ready and targeting
                targetColor = readyColor;
                fillAmount = 1f;
            } else {
                // Ready but not targeting
                targetColor = new Color(cooldownColor.r, cooldownColor.g, cooldownColor.b, cooldownColor.a * 0.5f);
                fillAmount = 1f;
            }

            _currentColor = Color.Lerp(_currentColor, targetColor, colorTransitionSpeed * Time.deltaTime);

            // Update segment colors based on fill amount
            var segmentsToShow = Mathf.RoundToInt(_segments.Length * fillAmount);

            for(var i = 0; i < _segments.Length; i++) {
                if(_segments[i] == null) continue;

                if(i < segmentsToShow) {
                    _segments[i].style.backgroundColor = _currentColor;
                    _segments[i].style.opacity = 1f;
                } else {
                    _segments[i].style.opacity = 0f;
                }
            }
        }

        /// <summary>
        /// Hides the grapple UI indicator (e.g., during post-match podium).
        /// </summary>
        public void HideGrappleUI() {
            if(_grappleIndicator != null) {
                _grappleIndicator.style.display = DisplayStyle.None;
            }
            if(_grappleIndicatorBottom != null) {
                _grappleIndicatorBottom.style.display = DisplayStyle.None;
            }
        }

        /// <summary>
        /// Shows the grapple UI indicator (e.g., when starting a new game).
        /// </summary>
        public void ShowGrappleUI() {
            // Show appropriate indicator based on setting
            var grappleIndicatorType = PlayerPrefs.GetInt("GrappleIndicator", 0);
            switch(grappleIndicatorType) {
                case 0: // Crosshair
                    ShowCrosshairIndicator();
                    HideBottomIndicator();
                    break;
                case 1: // Bottom
                    HideCrosshairIndicator();
                    ShowBottomIndicator();
                    break;
                case 2: // None
                    HideCrosshairIndicator();
                    HideBottomIndicator();
                    break;
            }
        }
        
        /// <summary>
        /// Updates the bottom grapple indicator (square fill).
        /// Fill height = cooldown progress (0-100% bottom-to-top).
        /// Color opacity = target validity (red when valid, transparent when invalid).
        /// </summary>
        private void UpdateBottomIndicatorVisual() {
            if(_grappleIndicatorBottom == null || _grappleIndicatorFill == null) return;
            
            // Determine fill height based on cooldown/grappling state
            float fillHeight;
            
            if(_grappleController.IsGrappling) {
                // Grappling - no fill
                fillHeight = 0f;
            } else if(!_grappleController.CanGrapple) {
                // Cooldown - show progress
                fillHeight = _grappleController.CooldownProgress;
            } else {
                // Ready - full fill
                fillHeight = 1f;
            }
            
            // Determine color opacity based on target validity
            float targetOpacity;
            
            if(_grappleController.IsGrappling) {
                // Grappling - transparent
                targetOpacity = 0f;
            } else if(_isLookingAtGrapplePoint && _grappleController.CanGrapple) {
                // Ready and valid target - full red
                targetOpacity = 1f;
            } else if(!_grappleController.CanGrapple) {
                // Cooldown - show based on target (dim red if valid, very dim if not)
                targetOpacity = _isLookingAtGrapplePoint ? 0.5f : 0.2f;
            } else {
                // Ready but invalid target - dim
                targetOpacity = 0.3f;
            }
            
            // Smooth color opacity transition
            _currentFillOpacity = Mathf.Lerp(_currentFillOpacity, targetOpacity, colorTransitionSpeed * Time.deltaTime);
            
            // Update fill visuals
            _grappleIndicatorFill.style.height = Length.Percent(fillHeight * 100f);
            _grappleIndicatorFill.style.opacity = _currentFillOpacity;
        }
    }
}
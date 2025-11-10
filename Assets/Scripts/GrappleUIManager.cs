using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using UnityEngine.UIElements;

public class GrappleUIManager : MonoBehaviour {
    [Header("References")]
    [SerializeField] private UIDocument uiDocument;
    // [SerializeField] private GrappleController grappleController;
    // [SerializeField] private CinemachineCamera fpCamera;
    
    [Header("Settings")]
    [SerializeField] private float maxGrappleDistance = 50f;
    [SerializeField] private LayerMask grappleableLayers;
    
    [Header("Visual Settings")]
    [SerializeField] private Color readyColor = new Color(1f, 0.2f, 0.2f, 0.8f);
    [SerializeField] private Color cooldownColor = new Color(0f, 0f, 0f, 0.3f);
    [SerializeField] private int segments = 20; // Number of segments for the horseshoe
    [SerializeField] private float colorTransitionSpeed = 25f;
    
    private VisualElement _grappleIndicator;
    private VisualElement[] _segments;
    private bool _isLookingAtGrapplePoint;
    private Color _currentColor;
    public GrappleController grappleController;
    private CinemachineCamera _fpCamera;
    
    public static GrappleUIManager Instance;

    private void Awake() {
        if(Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }
        
        Instance = this;
        DontDestroyOnLoad(gameObject);
            
        var root = uiDocument.rootVisualElement;
        _grappleIndicator = root.Q<VisualElement>("grapple-indicator");
        
        _currentColor = cooldownColor;
        CreateHorseshoeSegments();
    }
    
    private void Update() {
        if(!grappleController && SceneManager.GetActiveScene().name == "Game") {
            FindLocalPlayerGrappleController();
        }

        if(grappleController != null) {
            CheckGrapplePoint();
            UpdateIndicatorVisual();
        }
    }

    private void FindLocalPlayerGrappleController() {
        var allGrappleControllers = FindObjectsByType<GrappleController>(FindObjectsSortMode.None);
        foreach(var controller in allGrappleControllers) {
            if(controller.IsOwner) {
                grappleController = controller;
                _fpCamera = controller.GetComponentInChildren<CinemachineCamera>();
                return;
            }
        }
    
        if(!grappleController) {
            Debug.LogError("No GrappleController found");
            Invoke(nameof(FindLocalPlayerGrappleController), 0.1f);
        }
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
        
        var arcDegrees = 360f - gapDegrees;

        var startAngle = 360f + (gapDegrees / 2f);
        
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
        var ray = new Ray(_fpCamera.transform.position, _fpCamera.transform.forward);
        _isLookingAtGrapplePoint = Physics.Raycast(ray, maxGrappleDistance, grappleableLayers);
    }
    
    private void UpdateIndicatorVisual() {
        if(grappleController.IsGrappling) {
            _grappleIndicator.style.opacity = 0f;
            return;
        }
        
        _grappleIndicator.style.opacity = 1f;
        
        // Determine state
        Color targetColor;
        float fillAmount;
        
        if(!grappleController.CanGrapple) {
            // Cooldown - show progress
            targetColor = cooldownColor;
            fillAmount = grappleController.CooldownProgress;
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
}
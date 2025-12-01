using Game.Player;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Hopball {
    /// <summary>
    /// Manages the 3D world-space UI indicator for the hopball.
    /// Shows above the holder's head or the dropped hopball, always faces the camera,
    /// displays team-based colors, distance, and handles off-screen clamping with arrow.
    /// </summary>
    public class HopballIndicator : MonoBehaviour {
    [Header("References")]
    [SerializeField] private Canvas canvas;
    [SerializeField] private Image circleIndicator;
    [SerializeField] private Image diamondIndicator;
    [SerializeField] private Image iconImage;
    [SerializeField] private TextMeshProUGUI iconText; // Fallback "HB" text
    [SerializeField] private TextMeshProUGUI distanceText;
    [SerializeField] private RectTransform arrowContainer;
    [SerializeField] private Image arrowImage;

    [Header("Settings")]
    [SerializeField] private float heightOffset = 2.5f; // Height above player head or hopball
    [SerializeField] private float screenEdgeMargin = 50f; // Margin from screen edge when off-screen
    [SerializeField] private float arrowDistance = 30f; // Distance between indicator and arrow when off-screen
    [SerializeField] private Sprite logoSprite; // Optional logo sprite

    [Header("Colors")]
    [SerializeField] private Color teamColor = new(0.392f, 0.588f, 1f); // #6496FF - Blue
    [SerializeField] private Color enemyColor = new(1f, 0.392f, 0.392f); // #FF6464 - Red
    [SerializeField] private Color droppedColor = new(0.549f, 0.094f, 0.933f); // #8C18EE - Purple

    private Camera _localCamera;
    private Transform _targetTransform; // Player head or hopball transform
    private Vector3 _targetWorldPosition;
    private bool _isDropped;
    private bool _isOffScreen;
    private Color _currentColor;

    private void Awake() {
        // Find local player's camera
        FindLocalCamera();
    }

    private void Start() {
        // Initialize with dropped state (purple diamond)
        SetDroppedState(true);
    }

    private void LateUpdate() {
        if(_localCamera == null) {
            FindLocalCamera();
            if(_localCamera == null) return;
        }

        if(_targetTransform == null) return;

        UpdateTargetPosition();
        UpdateBillboard();
        UpdateDistanceDisplay();
        HandleOffScreen();
    }

    /// <summary>
    /// Finds the local player's camera for billboard behavior.
    /// </summary>
    private void FindLocalCamera() {
        if(NetworkManager.Singleton == null) return;
        if(NetworkManager.Singleton.LocalClient == null) return;
        
        var localPlayer = NetworkManager.Singleton.LocalClient.PlayerObject;

        if(localPlayer == null) return;
        var playerController = localPlayer.GetComponent<PlayerController>();
        if(playerController == null) return;

        // Get the CinemachineCamera and extract the actual Camera component
        var fpCamera = playerController.FpCamera;
        if(fpCamera != null) {
            // Try to find camera in children
            _localCamera = fpCamera.GetComponent<Camera>();
            if(_localCamera == null) {
                _localCamera = fpCamera.GetComponentInChildren<Camera>();
            }
        }

        // Fallback to main camera
        if(_localCamera == null) _localCamera = Camera.main;
    }

    /// <summary>
    /// Sets the target transform (player or hopball) and updates state.
    /// </summary>
    public void SetTarget(Transform target, bool isDropped) {
        _targetTransform = target;
        _isDropped = isDropped;
        SetDroppedState(isDropped);
    }

    /// <summary>
    /// Updates the team color based on holder's team vs local player's team.
    /// </summary>
    public void UpdateTeamColor(PlayerController holderController) {
        if(_isDropped) {
            _currentColor = droppedColor;
            return;
        }

        if(holderController == null) {
            _currentColor = droppedColor;
            return;
        }

        // Get local player's team
        if(NetworkManager.Singleton == null) return;
        if(NetworkManager.Singleton.LocalClient == null) return;
        var localPlayer = NetworkManager.Singleton.LocalClient.PlayerObject;
        if(localPlayer == null) {
            _currentColor = enemyColor;
            return;
        }

        var localController = localPlayer.GetComponent<PlayerController>();
        if(localController == null) return;
        var localTeamMgr = localController.TeamManager;
        var holderTeamMgr = holderController.TeamManager;

        if(localTeamMgr == null || holderTeamMgr == null) {
            _currentColor = enemyColor;
            return;
        }

        // Determine if same team
        var isTeammate = localTeamMgr.netTeam.Value == holderTeamMgr.netTeam.Value;
        _currentColor = isTeammate ? teamColor : enemyColor;

        // Apply color to indicators
        if(circleIndicator != null) {
            circleIndicator.color = _currentColor;
        }

        if(diamondIndicator != null) {
            diamondIndicator.color = _currentColor;
        }
    }

    /// <summary>
    /// Sets the dropped state (purple diamond) or equipped state (colored circle).
    /// </summary>
    private void SetDroppedState(bool dropped) {
        _isDropped = dropped;

        if(circleIndicator != null) {
            circleIndicator.gameObject.SetActive(!dropped);
        }

        if(diamondIndicator == null) return;
        diamondIndicator.gameObject.SetActive(dropped);
        if(dropped) {
            diamondIndicator.color = droppedColor;
        }
    }

    /// <summary>
    /// Updates the target world position (above head or hopball).
    /// </summary>
    private void UpdateTargetPosition() {
        if(_targetTransform == null) return;

        // Get base position (player head or hopball center)
        var basePosition = _targetTransform.position;

        // For players, try to get head position (approximate with character controller height)
        var characterController = _targetTransform.GetComponent<CharacterController>();
        if(characterController != null) {
            basePosition.y += characterController.height * 0.5f + characterController.center.y;
        }

        _targetWorldPosition = basePosition + Vector3.up * heightOffset;
    }

    /// <summary>
    /// Makes the indicator always face the camera (billboard behavior).
    /// </summary>
    private void UpdateBillboard() {
        if(_localCamera == null || canvas == null) return;

        // Make canvas face camera (billboard effect)
        // For World Space Canvas, we rotate the canvas transform
        var directionToCamera = _localCamera.transform.position - canvas.transform.position;

        if(directionToCamera != Vector3.zero) {
            // Face camera but keep upright (only rotate around Y axis for horizontal billboard)
            // Or use full billboard (rotate to face camera completely)
            canvas.transform.rotation = Quaternion.LookRotation(-directionToCamera);
        }
    }

    /// <summary>
    /// Calculates and displays distance from local camera to target.
    /// </summary>
    private void UpdateDistanceDisplay() {
        if(_localCamera == null || distanceText == null) return;

        var distance = Vector3.Distance(_localCamera.transform.position, _targetWorldPosition);
        distanceText.text = $"{Mathf.RoundToInt(distance)}m";
    }

    /// <summary>
    /// Handles off-screen detection and clamping with arrow.
    /// </summary>
    private void HandleOffScreen() {
        if(_localCamera == null || canvas == null) return;

        // Convert world position to screen space
        var screenPos = _localCamera.WorldToScreenPoint(_targetWorldPosition);

        // Check if on screen (with margin)
        // Also check if behind camera (z < 0)
        var isBehindCamera = screenPos.z < 0;
        var screenRect = new Rect(screenEdgeMargin, screenEdgeMargin,
            Screen.width - screenEdgeMargin * 2,
            Screen.height - screenEdgeMargin * 2);

        _isOffScreen = isBehindCamera || !screenRect.Contains(new Vector2(screenPos.x, screenPos.y));

        if(_isOffScreen) {
            // Clamp to screen edge
            var clampedScreenPos = screenPos;

            // If behind camera, flip to opposite side
            if(isBehindCamera) {
                clampedScreenPos.x = Screen.width - clampedScreenPos.x;
                clampedScreenPos.y = Screen.height - clampedScreenPos.y;
            }

            clampedScreenPos.x = Mathf.Clamp(clampedScreenPos.x, screenEdgeMargin, Screen.width - screenEdgeMargin);
            clampedScreenPos.y = Mathf.Clamp(clampedScreenPos.y, screenEdgeMargin, Screen.height - screenEdgeMargin);

            // Convert back to world space
            // Use the distance from camera to target to maintain proper scale
            var distance = Mathf.Max(1f, Vector3.Distance(_localCamera.transform.position, _targetWorldPosition));
            var worldPos = _localCamera.ScreenToWorldPoint(
                new Vector3(clampedScreenPos.x, clampedScreenPos.y, distance));

            // Update canvas position
            canvas.transform.position = worldPos;

            // Show and position arrow
            if(arrowContainer == null || arrowImage == null) return;
            arrowContainer.gameObject.SetActive(true);

            // Calculate direction from clamped position to actual target (in screen space)
            var targetScreenPos = _localCamera.WorldToScreenPoint(_targetWorldPosition);
            var screenDirection = (new Vector2(targetScreenPos.x, targetScreenPos.y) -
                                   new Vector2(clampedScreenPos.x, clampedScreenPos.y)).normalized;

            if(screenDirection.magnitude > 0.01f) {
                // Rotate arrow to point toward target (in screen space)
                var angle = Mathf.Atan2(screenDirection.y, screenDirection.x) * Mathf.Rad2Deg - 90f;
                arrowContainer.rotation = canvas.transform.rotation * Quaternion.Euler(0, 0, angle);
            }

            // Position arrow between indicator and screen edge (in world space)
            // Convert screen direction to world direction
            var localCameraTransform = _localCamera.transform;
            var worldDirection = localCameraTransform.right * screenDirection.x +
                                 localCameraTransform.up * screenDirection.y;
            worldDirection.Normalize();

            // Set world position (Unity will convert to local space for Canvas child)
            var arrowWorldPos = worldPos + worldDirection * arrowDistance;
            arrowContainer.position = arrowWorldPos;
        } else {
            // On screen - use actual target position
            canvas.transform.position = _targetWorldPosition;

            // Hide arrow
            if(arrowContainer != null) {
                arrowContainer.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>
    /// Sets up the icon (sprite or text fallback).
    /// </summary>
    public void SetupIcon() {
        if(logoSprite != null && iconImage != null) {
            iconImage.sprite = logoSprite;
            iconImage.gameObject.SetActive(true);
            if(iconText != null) {
                iconText.gameObject.SetActive(false);
            }
        } else if(iconText != null) {
            iconText.text = "HB";
            iconText.gameObject.SetActive(true);
            if(iconImage != null) {
                iconImage.gameObject.SetActive(false);
            }
        }
    }
    }
}
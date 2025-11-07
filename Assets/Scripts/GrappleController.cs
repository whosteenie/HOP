using System.Collections;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

public class GrappleController : NetworkBehaviour
{
    [Header("Grapple Settings")]
    [SerializeField] private float maxGrappleDistance = 50f;
    [SerializeField] private float grappleSpeed = 30f;
    [SerializeField] private float grappleDuration = 0.5f;
    [SerializeField] private float grappleCooldown = 1.3f;
    [SerializeField] private LayerMask grappleableLayers;
    
    [Header("Momentum Settings")]
    [SerializeField] private bool preserveMomentum = true;
    [SerializeField] private float momentumBoost = 1.2f; // Multiplier for final velocity
    
    [Header("Components")]
    [SerializeField] private CinemachineCamera fpCamera;
    [SerializeField] private CharacterController characterController;
    [SerializeField] private PlayerController playerController;
    [SerializeField] private LineRenderer grappleLine;
    [SerializeField] private AudioClip[] grappleSounds;
    [SerializeField] private NetworkSoundRelay soundRelay;
    
    [Header("Visual Settings")]
    [SerializeField] private float lineWidth = 0.05f;
    [SerializeField] private Color grappleColor = new Color(0.2f, 0.8f, 1f);
    [SerializeField] private Material lineMaterial;
    
    #region Private Fields

    private Vector3 _grapplePoint;
    private float _grappleStartTime;
    private Vector3 _grappleStartPosition;
    private float _cooldownStartTime;
    private LayerMask _playerLayer;
    
    #endregion
    
    #region Properties
    
    public bool IsGrappling { get; private set; }

    public bool CanGrapple { get; private set; } = true;

    public float CooldownProgress {
        get {
            if(CanGrapple) return 1f;
            var elapsed = Time.time - _cooldownStartTime;
            return Mathf.Clamp01(elapsed / grappleCooldown);
        }
    }
    
    #endregion
    
    #region Unity Lifecycle
    
    private void Start() {
        _playerLayer = LayerMask.GetMask("Player");
        
        SetupGrappleLine();
    }
    
    private void Update() {
        if(!IsOwner) return;
        
        if(IsGrappling) {
            UpdateGrapple();
        }
        UpdateGrappleLine();
    }
    
    #endregion
    
    #region Setup
    
    private void SetupGrappleLine() {
        if(grappleLine == null) {
            var lineObj = new GameObject("GrappleLine");
            lineObj.transform.SetParent(transform);
            grappleLine = lineObj.AddComponent<LineRenderer>();
        }
        
        grappleLine.startWidth = lineWidth;
        grappleLine.endWidth = lineWidth;
        grappleLine.positionCount = 2;
        grappleLine.useWorldSpace = true;
        grappleLine.enabled = false;
        
        // Setup material
        grappleLine.material = lineMaterial != null ? lineMaterial : new Material(Shader.Find("Sprites/Default"));
        
        grappleLine.startColor = grappleColor;
        grappleLine.endColor = grappleColor;
    }
    
    #endregion
    
    #region Public Methods
    
    public void TryGrapple() {
        if(!CanGrapple || IsGrappling) return;
        
        // Raycast from camera to find grapple point
        var ray = new Ray(fpCamera.transform.position, fpCamera.transform.forward);
        
        if(Physics.Raycast(ray, out var hit, maxGrappleDistance, grappleableLayers)) {
            StartGrapple(hit.point);
        }
    }
    
    public void CancelGrapple() {
        if(!IsGrappling) return;
        
        EndGrapple(true);
    }
    
    #endregion
    
    #region Private Methods - Grapple Logic
    
    private void StartGrapple(Vector3 targetPoint) {
        IsGrappling = true;
        _grapplePoint = targetPoint;
        _grappleStartTime = Time.time;
        _grappleStartPosition = transform.position;
        
        // Enable visual
        grappleLine.enabled = true;
        
        if (soundRelay != null && IsOwner)
        {
            int idx = (grappleSounds != null && grappleSounds.Length > 0) ? Random.Range(0, grappleSounds.Length) : 0;
            soundRelay?.RequestWorldSfx(SFXKey.Grapple, attachToSelf: true, true);
        }
        // SoundFXManager.Instance.PlayRandomSoundFX(grappleSounds, transform, true, "grapple");
    }
    
    private void UpdateGrapple() {
        var elapsed = Time.time - _grappleStartTime;
        
        // Check if grapple duration exceeded
        if(elapsed >= grappleDuration) {
            EndGrapple(true);
            return;
        }
        
        // Calculate pull direction and velocity
        var directionToPoint = (_grapplePoint - transform.position).normalized;
        var distanceToPoint = Vector3.Distance(transform.position, _grapplePoint);
        
        // If we're very close, end the grapple
        if(distanceToPoint < 1f) {
            EndGrapple(true);
            return;
        }
        
        // Check for walls in the direction we're moving
        var pullVelocity = directionToPoint * grappleSpeed;
        var checkDistance = pullVelocity.magnitude * Time.deltaTime * 3f; // Check slightly ahead
        if(Physics.SphereCast(transform.position, characterController.radius, directionToPoint, out var hit, checkDistance, ~_playerLayer)) {
            // We're about to hit something, end grapple early
            EndGrapple(true);
            return;
        }
        
        // Apply movement
        characterController.Move(pullVelocity * Time.deltaTime);
    }
    
    private void EndGrapple(bool applyMomentum) {
        IsGrappling = false;
        
        StartCoroutine(DisableLineAfterDelay(0.1f));
        
        // grappleLine.enabled = false;
        
        if(applyMomentum && preserveMomentum) {
            // Calculate final momentum direction
            var directionToPoint = (_grapplePoint - transform.position).normalized;
            var finalVelocity = grappleSpeed * momentumBoost * directionToPoint;
            
            // Apply momentum to FpController
            if(playerController != null) {
                // Set horizontal velocity (preserve some existing momentum)
                var horizontalVelocity = new Vector3(finalVelocity.x, 0f, finalVelocity.z);
                playerController.SetVelocity(horizontalVelocity);
                
                // Add upward boost if grappling upward
                if(finalVelocity.y > 0) {
                    playerController.AddVerticalVelocity(finalVelocity.y);
                }
            }
        }
        
        // Start cooldown
        StartCoroutine(GrappleCooldown());
    }

    private IEnumerator DisableLineAfterDelay(float delay) {
        yield return new WaitForSeconds(delay);
        
        grappleLine.enabled = false;
    }
    
    private IEnumerator GrappleCooldown()
    {
        CanGrapple = false;
        _cooldownStartTime = Time.time;
        yield return new WaitForSeconds(grappleCooldown);
        CanGrapple = true;
    }
    
    private void UpdateGrappleLine()
    {
        if(!grappleLine.enabled) return;
        
        // Update line positions (from hand/weapon to grapple point)
        var handPosition = fpCamera.transform.position - fpCamera.transform.right * 0.3f - fpCamera.transform.up * 0.2f;
        
        grappleLine.SetPosition(0, handPosition);
        grappleLine.SetPosition(1, _grapplePoint);
    }
    
    #endregion
}

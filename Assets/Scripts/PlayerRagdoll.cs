using System.Runtime.InteropServices.WindowsRuntime;
using Unity.Netcode;
using UnityEngine;

public class PlayerRagdoll : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private Animator animator;
    [SerializeField] private CharacterController characterController;
    
    [Header("Ragdoll Settings")]
    [SerializeField] private float ragdollForce = 500f;
    [SerializeField] private float despawnDelay = 5f;
    
    private Rigidbody[] _ragdollRigidbodies;
    private Collider[] _ragdollColliders;
    private bool _isRagdoll;
    
    private void Awake() {
        // Get all rigidbodies and colliders in children (the ragdoll bones)
        _ragdollRigidbodies = GetComponentsInChildren<Rigidbody>();
        _ragdollColliders = GetComponentsInChildren<Collider>();
        
        if(!IsOwner) return;
        // Disable ragdoll by default
        SetRagdollActive(false);
    }

    public override void OnNetworkSpawn() {
        _ragdollRigidbodies = GetComponentsInChildren<Rigidbody>();
        _ragdollColliders = GetComponentsInChildren<Collider>();
        
        if(!IsOwner) return;
        SetRagdollActive(false);
    }
    
    public void EnableRagdoll(Vector3? hitPoint = null, Vector3? hitDirection = null) {
        if(_isRagdoll) {
            return;
        }
        
        _isRagdoll = true;
        
        // Disable character controller and animator
        if(characterController != null) {
            characterController.enabled = false;
        }
        
        if(animator != null) {
            animator.enabled = false;
        }
        
        // Enable ragdoll physics
        SetRagdollActive(true);
        
        // Apply force if we have hit info
        if(hitPoint.HasValue && hitDirection.HasValue) {
            ApplyRagdollForce(hitPoint.Value, hitDirection.Value);
        }
    }

    public void DisableRagdoll() {
        if(!_isRagdoll) {
            return;
        }
        
        _isRagdoll = false;
        
        // Disable ragdoll physics
        SetRagdollActive(false);
        
        // Enable character controller and animator
        if(characterController != null) {
            characterController.enabled = true;
        }
        
        if(animator != null) {
            animator.enabled = true;
        }
    }
    
    private void SetRagdollActive(bool active) {
        if(!IsOwner) return;
        
        foreach(var rb in _ragdollRigidbodies) {
            if(rb == null) continue;
            
            rb.isKinematic = !active;
            rb.detectCollisions = active;
            
            if(active) {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
        
        
        foreach(var col in _ragdollColliders) {
            if(col == null) continue;
            
            // Skip the CharacterController's collider
            if(col == characterController) continue;
            
            col.enabled = active;
        }
    }
    
    private void ApplyRagdollForce(Vector3 hitPoint, Vector3 direction) {
        // Find the closest rigidbody to the hit point
        Rigidbody closestRb = null;
        var closestDistance = float.MaxValue;
        
        foreach(var rb in _ragdollRigidbodies) {
            if(rb == null) continue;
            
            var distance = Vector3.Distance(rb.position, hitPoint);
            if(distance < closestDistance) {
                closestDistance = distance;
                closestRb = rb;
            }
        }
        
        // Apply force to the closest bone
        if(closestRb != null) {
            closestRb.AddForceAtPosition(direction.normalized * ragdollForce, hitPoint, ForceMode.Impulse);
        }
    }
}
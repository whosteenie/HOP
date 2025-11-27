using System.Linq;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class PlayerRagdoll : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private CharacterController _characterController;
        private Animator _playerAnimator;

        [Header("Ragdoll Settings")]
        private const float RagdollForce = 60f;

        [Header("Ragdoll Force Target")]
        [Tooltip("Rigidbody to apply ragdoll force to (typically the chest/spine/torso).")]
        [SerializeField] private Rigidbody chestRigidbody;

        [Header("Body Part Tags")]
        [Tooltip("Tag used for head body part (for headshot detection).")]
        [SerializeField] private string headTag = "Head";

        private Rigidbody[] _ragdollRigidbodies;
        private CharacterJoint[] _ragdollJoints;
        private Collider[] _ragdollColliders;
        private Vector3 _hitPoint;
        private Vector3 _hitDir;

        /// <summary>
        /// Returns whether the player is currently in ragdoll state.
        /// </summary>
        public bool IsRagdoll { get; private set; }

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[PlayerRagdoll] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_characterController == null) _characterController = playerController.CharacterController;
            if(_playerAnimator == null) _playerAnimator = playerController.PlayerAnimator;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            _ragdollRigidbodies = GetComponentsInChildren<Rigidbody>(true);
            _ragdollJoints = GetComponentsInChildren<CharacterJoint>(true);
            _ragdollColliders = GetComponentsInChildren<Collider>(true);
            Debug.LogWarning(
                $"[PlayerRagdoll] Cached ragdoll components. Rigidbodies={_ragdollRigidbodies?.Length ?? 0}, Colliders={_ragdollColliders?.Length ?? 0}, Joints={_ragdollJoints?.Length ?? 0}");

            // Validate chest rigidbody is assigned
            if(chestRigidbody == null) {
                Debug.LogError("[PlayerRagdoll] Chest rigidbody not assigned in inspector! Ragdoll force will not work correctly.");
            }

            // Set ragdoll components to Enemy layer (excluding base GameObject)
            SetRagdollLayersToEnemy();

            DisableRagdoll();
            
            // Enable colliders for hit detection (even when ragdoll is disabled)
            EnableCollidersForHitDetection();
        }

        public void EnableRagdoll(Vector3? hitPoint = null, Vector3? hitDirection = null, string bodyPartTag = null) {
            if(IsRagdoll) return;
            IsRagdoll = true;

            _characterController.enabled = false;
            _playerAnimator.enabled = false;
            EnableRagdollPhysics();

            if(!hitPoint.HasValue || !hitDirection.HasValue) return;
            _hitPoint = hitPoint.Value;
            _hitDir = hitDirection.Value;
            ApplyHitForce(bodyPartTag);
        }

        private void ApplyHitForce(string bodyPartTag = null) {
            // Always apply force to chest/torso for consistent, predictable ragdoll behavior
            // bodyPartTag is still used for headshot damage calculation, just not for force direction
            if(chestRigidbody != null) {
                chestRigidbody.AddForce(_hitDir * RagdollForce, ForceMode.Impulse);
            } else {
                // Fallback if not assigned (shouldn't happen if inspector is set up correctly)
                var fallback = GetClosestRigidbody(_hitPoint);
                fallback?.AddForce(_hitDir * RagdollForce, ForceMode.Impulse);
            }
        }

        /// <summary>
        /// Gets a rigidbody by its GameObject tag (e.g., "Head" for headshots).
        /// </summary>
        private Rigidbody GetRigidbodyByTag(string rbTag) {
            return _ragdollRigidbodies.FirstOrDefault(rb => rb != null && rb.CompareTag(rbTag));
        }

        private Rigidbody GetClosestRigidbody(Vector3 point) {
            Rigidbody closest = null;
            var bestDist = float.MaxValue;
            foreach(var rb in _ragdollRigidbodies) {
                if(rb == null) continue;
                var d = Vector3.Distance(rb.worldCenterOfMass, point);
                if(!(d < bestDist)) continue;
                bestDist = d;
                closest = rb;
            }

            return closest;
        }

        public void DisableRagdoll() {
            if(!IsRagdoll) return;
            IsRagdoll = false;

            // CancelInvoke();
            DisableRagdollPhysics();

            _characterController.enabled = true;
            _playerAnimator.enabled = true;
            
            // Ensure colliders are enabled for hit detection after disabling ragdoll
            EnableCollidersForHitDetection();
        }

        private void EnableRagdollPhysics() {
            Debug.LogWarning("[PlayerRagdoll] Enabling ragdoll physics");
            foreach(var rb in _ragdollRigidbodies) {
                if(rb == null) continue;
                rb.isKinematic = false; // Make non-kinematic for physics interactions
                rb.linearVelocity = Vector3.zero;
                rb.detectCollisions = true; // Keep true for raycast hit detection
                rb.useGravity = true;
            }

            foreach(var col in _ragdollColliders) {
                if(col == null) continue;
                col.enabled = true;
            }

            foreach(var joint in _ragdollJoints) {
                if(joint == null) continue;
                joint.enableCollision = true;
            }
        }

        private void DisableRagdollPhysics() {
            Debug.LogWarning("[PlayerRagdoll] Disabling ragdoll physics");
            foreach(var rb in _ragdollRigidbodies) {
                if(rb == null) continue;
                // Set velocities BEFORE making kinematic (can't set velocity on kinematic bodies)
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true; // Make kinematic to prevent physics interactions
                rb.detectCollisions = true; // Keep true for raycast hit detection
                rb.useGravity = false;
            }

            // Keep colliders enabled for hit detection, but disable physics collisions via kinematic
            // Colliders are enabled in EnableCollidersForHitDetection()
            // Rigidbodies are kinematic so they don't interact with physics, but raycasts still work

            foreach(var joint in _ragdollJoints) {
                if(joint == null) continue;
                joint.enableCollision = false;
            }
        }

        /// <summary>
        /// Enables ragdoll colliders for hit detection (raycasts).
        /// Colliders remain enabled even when ragdoll is disabled so bullets can hit them.
        /// </summary>
        private void EnableCollidersForHitDetection() {
            if(_ragdollColliders == null) return;
            
            foreach(var col in _ragdollColliders) {
                if(col == null) continue;
                col.enabled = true; // Enable for raycast hit detection
                col.isTrigger = false; // Ensure it's not a trigger (triggers don't block raycasts)
            }
        }

        /// <summary>
        /// Sets all ragdoll components (rigidbodies and colliders) to Enemy layer for OTHER players.
        /// Excludes the base GameObject (the one with CharacterController).
        /// Only sets layers for non-owner players (other players are enemies).
        /// </summary>
        private void SetRagdollLayersToEnemy() {
            // Only set Enemy layer for other players, not the local player
            if(IsOwner) {
                return;
            }

            var enemyLayer = LayerMask.NameToLayer("Enemy");
            if(enemyLayer == -1) {
                Debug.LogWarning("[PlayerRagdoll] Enemy layer not found. Make sure 'Enemy' layer exists in project settings.");
                return;
            }

            // Get base GameObject (the one with CharacterController)
            var baseGameObject = _characterController != null ? _characterController.gameObject : gameObject;

            // Set rigidbody GameObjects to Enemy layer (excluding base GameObject)
            foreach(var rb in _ragdollRigidbodies) {
                if(rb != null && rb.gameObject != baseGameObject) {
                    rb.gameObject.layer = enemyLayer;
                }
            }

            // Set collider GameObjects to Enemy layer (excluding base GameObject)
            foreach(var col in _ragdollColliders) {
                if(col != null && col.gameObject != baseGameObject) {
                    col.gameObject.layer = enemyLayer;
                }
            }
        }
    }
}
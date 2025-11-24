using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class PlayerRagdoll : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private Animator animator;

        [SerializeField] private CharacterController characterController;

        [Header("Ragdoll Settings")]
        [SerializeField] private float ragdollForce = 60f;

        [Header("Ragdoll Force Target")]
        [Tooltip("Rigidbody to apply ragdoll force to (typically the chest/spine/torso).")]
        [SerializeField] private Rigidbody chestRigidbody;

        [Header("Body Part Tags")]
        [Tooltip("Tag used for head body part (for headshot detection).")]
        [SerializeField] private string headTag = "Head";

        private Rigidbody[] _ragdollRigidbodies;
        private CharacterJoint[] _ragdollJoints;
        private Collider[] _ragdollColliders;
        private bool _isRagdoll;
        private Vector3 _hitPoint;
        private Vector3 _hitDir;

        /// <summary>
        /// Returns whether the player is currently in ragdoll state.
        /// </summary>
        public bool IsRagdoll => _isRagdoll;

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            _ragdollRigidbodies = GetComponentsInChildren<Rigidbody>(true);
            _ragdollJoints = GetComponentsInChildren<CharacterJoint>(true);
            _ragdollColliders = GetComponentsInChildren<Collider>(true);

            // Validate chest rigidbody is assigned
            if(chestRigidbody == null) {
                Debug.LogError("[PlayerRagdoll] Chest rigidbody not assigned in inspector! Ragdoll force will not work correctly.");
            }

            // Set ragdoll components to Enemy layer (excluding base GameObject)
            SetRagdollLayersToEnemy();

            // foreach(var rb in _ragdollRigidbodies) {
            //     rb.interpolation = RigidbodyInterpolation.Interpolate;
            //     rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            // }

            DisableRagdoll();
            
            // Enable colliders for hit detection (even when ragdoll is disabled)
            EnableCollidersForHitDetection();
        }

        public void EnableRagdoll(Vector3? hitPoint = null, Vector3? hitDirection = null, string bodyPartTag = null) {
            if(_isRagdoll) return;
            _isRagdoll = true;

            characterController.enabled = false;
            animator.enabled = false;
            EnableRagdollPhysics();

            if(hitPoint.HasValue && hitDirection.HasValue) {
                _hitPoint = hitPoint.Value;
                _hitDir = hitDirection.Value;
                ApplyHitForce(bodyPartTag);
            }
        }

        private void ApplyHitForce(string bodyPartTag = null) {
            // Always apply force to chest/torso for consistent, predictable ragdoll behavior
            // bodyPartTag is still used for headshot damage calculation, just not for force direction
            if(chestRigidbody != null) {
                chestRigidbody.AddForce(_hitDir * ragdollForce, ForceMode.Impulse);
            } else {
                // Fallback if not assigned (shouldn't happen if inspector is set up correctly)
                Debug.LogWarning("[PlayerRagdoll] Chest rigidbody not assigned, using closest rigidbody as fallback.");
                var fallback = GetClosestRigidbody(_hitPoint);
                if(fallback != null) {
                    fallback.AddForce(_hitDir * ragdollForce, ForceMode.Impulse);
                }
            }
        }

        /// <summary>
        /// Gets a rigidbody by its GameObject tag (e.g., "Head" for headshots).
        /// </summary>
        private Rigidbody GetRigidbodyByTag(string tag) {
            foreach(var rb in _ragdollRigidbodies) {
                if(rb != null && rb.CompareTag(tag)) {
                    return rb;
                }
            }
            return null;
        }

        private Rigidbody GetClosestRigidbody(Vector3 point) {
            Rigidbody closest = null;
            var bestDist = float.MaxValue;
            foreach(var rb in _ragdollRigidbodies) {
                if(rb == null) continue;
                var d = Vector3.Distance(rb.worldCenterOfMass, point);
                if(d < bestDist) {
                    bestDist = d;
                    closest = rb;
                }
            }

            return closest;
        }

        public void DisableRagdoll() {
            if(!_isRagdoll) return;
            _isRagdoll = false;

            // CancelInvoke();
            DisableRagdollPhysics();

            characterController.enabled = true;
            animator.enabled = true;
            
            // Ensure colliders are enabled for hit detection after disabling ragdoll
            EnableCollidersForHitDetection();
        }

        private void EnableRagdollPhysics() {
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
        public void EnableCollidersForHitDetection() {
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

            int enemyLayer = LayerMask.NameToLayer("Enemy");
            if(enemyLayer == -1) {
                Debug.LogWarning("[PlayerRagdoll] Enemy layer not found. Make sure 'Enemy' layer exists in project settings.");
                return;
            }

            // Get base GameObject (the one with CharacterController)
            GameObject baseGameObject = characterController != null ? characterController.gameObject : gameObject;

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
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class PlayerRagdoll : NetworkBehaviour {
        [Header("References")] [SerializeField]
        private Animator animator;

        [SerializeField] private CharacterController characterController;

        [Header("Ragdoll Settings")] [SerializeField]
        private float ragdollForce = 150f;

        [SerializeField] private float maxRagdollVelocity = 7f;

        private Rigidbody[] _ragdollRigidbodies;
        private Collider[] _ragdollColliders;
        private bool _isRagdoll;

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            _ragdollRigidbodies = GetComponentsInChildren<Rigidbody>(true);
            _ragdollColliders = GetComponentsInChildren<Collider>(true);

            // Configure rigidbodies for stability
            foreach(var rb in _ragdollRigidbodies) {
                if(!rb) continue;

                // CRITICAL: Set these for stable ragdolls
                rb.maxAngularVelocity = 7f; // Prevent spinning too fast
                rb.interpolation = RigidbodyInterpolation.Interpolate; // Smooth movement
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous; // Better collision
            }

            SetRagdollActive(false);
        }

        public void EnableRagdoll(Vector3? hitPoint = null, Vector3? hitDirection = null) {
            if(_isRagdoll) return;
            _isRagdoll = true;

            if(characterController) characterController.enabled = false;
            if(animator) animator.enabled = false;

            SetRagdollActive(true);

            // Apply force AFTER a single physics frame to ensure rigidbodies are ready
            if(hitPoint.HasValue && hitDirection.HasValue) {
                // Small delay ensures physics is ready
                Invoke(nameof(ApplyDelayedForce), 0.02f);
                _pendingHitPoint = hitPoint.Value;
                _pendingHitDirection = hitDirection.Value;
            }
        }

        private Vector3 _pendingHitPoint;
        private Vector3 _pendingHitDirection;

        private void ApplyDelayedForce() {
            ApplyRagdollForce(_pendingHitPoint, _pendingHitDirection);
        }

        public void DisableRagdoll() {
            if(!_isRagdoll) return;
            _isRagdoll = false;

            // Cancel any pending force applications
            CancelInvoke(nameof(ApplyDelayedForce));

            SetRagdollActive(false);

            if(characterController) characterController.enabled = true;
            if(animator) animator.enabled = true;
        }

        private void SetRagdollActive(bool active) {
            foreach(var rb in _ragdollRigidbodies) {
                if(!rb) continue;

                if(!active) {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                rb.isKinematic = !active;
                rb.detectCollisions = active;
            }

            foreach(var col in _ragdollColliders) {
                if(!col) continue;
                if(col == characterController) continue;

                col.enabled = active;
            }
        }

        private void ApplyRagdollForce(Vector3 hitPoint, Vector3 direction) {
            if(_ragdollRigidbodies == null || _ragdollRigidbodies.Length == 0)
                return;

            // 1) Find body closest to hit
            Rigidbody closestRb = null;
            float closestDistance = float.MaxValue;

            foreach(var rb in _ragdollRigidbodies) {
                if(!rb) continue;

                float distance = Vector3.Distance(rb.worldCenterOfMass, hitPoint);
                if(distance < closestDistance) {
                    closestDistance = distance;
                    closestRb = rb;
                }
            }

            if(closestRb) {
                Vector3 dir = direction.normalized;
                if(dir.sqrMagnitude < 0.0001f)
                    return;

                // Much softer force to start. Tune this.
                float impulse = ragdollForce; // e.g. 75–150 instead of 300
                Vector3 force = dir * impulse;

                closestRb.AddForce(force, ForceMode.Impulse);
            }

            // 2) Clamp ALL bodies’ velocities & spin so nothing goes nuts
            foreach(var rb in _ragdollRigidbodies) {
                if(!rb) continue;

                if(rb.linearVelocity.magnitude > maxRagdollVelocity)
                    rb.linearVelocity = rb.linearVelocity.normalized * maxRagdollVelocity;

                if(rb.angularVelocity.magnitude > maxRagdollVelocity)
                    rb.angularVelocity = rb.angularVelocity.normalized * maxRagdollVelocity;
            }
        }
    }
}
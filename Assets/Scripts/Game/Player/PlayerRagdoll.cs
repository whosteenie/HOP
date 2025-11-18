using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class PlayerRagdoll : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private Animator animator;
        [SerializeField] private CharacterController characterController;

        [Header("Ragdoll Settings")]
        [SerializeField] private float ragdollForce = 120f;

        private Rigidbody[] _ragdollRigidbodies;
        private Collider[] _ragdollColliders;
        private bool _isRagdoll;

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            _ragdollRigidbodies = GetComponentsInChildren<Rigidbody>(true);
            _ragdollColliders = GetComponentsInChildren<Collider>(true);

            foreach (var rb in _ragdollRigidbodies) {
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            }

            DisableRagdoll();
        }

        public void EnableRagdoll(Vector3? hitPoint = null, Vector3? hitDirection = null) {
            if (_isRagdoll) return;
            _isRagdoll = true;

            characterController.enabled = false;
            if (animator) Invoke(nameof(DisableAnimator), 0.05f);
            EnableRagdollPhysics();

            if (hitPoint.HasValue && hitDirection.HasValue) {
                _hitPoint = hitPoint.Value;
                _hitDir = hitDirection.Value;
                // Invoke(nameof(ApplyHitForce), 0.03f);
            }
        }

        private Vector3 _hitPoint;
        private Vector3 _hitDir;

        private void DisableAnimator() {
            if (animator) animator.enabled = false;
        }

        private void ApplyHitForce() {
            var closest = GetClosestRigidbody(_hitPoint);
            if (closest) {
                closest.AddForce(_hitDir * ragdollForce, ForceMode.Impulse);
            }
        }

        private Rigidbody GetClosestRigidbody(Vector3 point) {
            Rigidbody closest = null;
            var bestDist = float.MaxValue;
            foreach (var rb in _ragdollRigidbodies) {
                var d = Vector3.Distance(rb.worldCenterOfMass, point);
                if (d < bestDist) {
                    bestDist = d;
                    closest = rb;
                }
            }
            return closest;
        }

        public void DisableRagdoll() {
            if (!_isRagdoll) return;
            _isRagdoll = false;

            CancelInvoke();
            DisableRagdollPhysics();

            characterController.enabled = true;
            if (animator) animator.enabled = true;
        }

        private void EnableRagdollPhysics() {
            foreach (var rb in _ragdollRigidbodies) {
                rb.isKinematic = false;
                rb.detectCollisions = true;
            }
            foreach (var col in _ragdollColliders) {
                if (col && col != characterController) col.enabled = true;
            }
        }

        private void DisableRagdollPhysics() {
            foreach (var rb in _ragdollRigidbodies) {
                // Set velocities BEFORE making kinematic (can't set velocity on kinematic bodies)
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }
            foreach (var col in _ragdollColliders) {
                if (col && col != characterController) col.enabled = false;
            }
        }
    }
}
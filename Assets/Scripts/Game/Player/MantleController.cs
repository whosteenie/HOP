using System;
using System.Collections;
using UnityEngine;

namespace Game.Player {
    public class MantleController : MonoBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private CharacterController _characterController;
        private Transform _cameraTransform;

        [Header("Mantle Detection")]
        private const float DetectionRadius = 0.4f;
        private const float DetectionDistance = 1f;
        private const float MantleCheckHeightMin = 0.5f;
        private const float MantleCheckHeightMax = 1.8f;
        private const float MinMantleHeight = 0.8f;
        private const float MaxMantleHeight = 2.5f;
        private const float LedgeSearchHeight = 3f;
        private const float ForwardPushDistance = 0.8f;
        private const float HeightBoost = 0.1f;

        [Header("Mantle Movement")]
        private const float MantleDuration = 0.3f;

        private const float RotationClampAngle = 60f;
        [SerializeField] private AnimationCurve mantleHeightCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
        [SerializeField] private AnimationCurve mantleForwardCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Range(0f, 1f)]
        private const float ForwardMovementStartRatio = 0.4f; // When forward movement starts (0 = immediately, 1 = at end)

        [Tooltip("Delay after mantle ends before allowing jump (seconds). Prevents accidental jumps when holding jump during mantle.")]
        private const float PostMantleJumpDelay = 0.15f;

        [Header("Layers")]
        private LayerMask _mantleableLayers;

        public bool IsMantling { get; private set; }
        public bool CanJump => !IsMantling && _postMantleJumpCooldown <= 0f;

        private Vector3 _mantleStartPosition;
        private Vector3 _mantleTargetPosition;
        private Vector3 _mantleDirection;
        private float _mantleTimer;
        private float _postMantleJumpCooldown;

        private void Awake() {
            playerController ??= GetComponent<PlayerController>();
            _characterController ??= playerController.CharacterController;
            _cameraTransform ??= playerController.FpCamera.transform;
            _mantleableLayers = playerController.WorldLayer;
        }

        public void TryMantle() {
            if(IsMantling) return;
            if(playerController.IsGrounded) return;

            var playerForward = transform.forward;
            playerForward.y = 0;
            playerForward.Normalize();

            RaycastHit wallHit = default;
            var foundWall = false;

            for(var checkHeight = MantleCheckHeightMin; checkHeight <= MantleCheckHeightMax; checkHeight += 0.3f) {
                var sphereCheckOrigin = transform.position + Vector3.up * checkHeight;

                if(!Physics.SphereCast(sphereCheckOrigin, DetectionRadius, playerForward, out wallHit,
                       DetectionDistance,
                       _mantleableLayers)) continue;
                foundWall = true;
                Debug.DrawLine(sphereCheckOrigin, wallHit.point, Color.red, 2f);
                break;
            }

            if(!foundWall) {
                return;
            }

            var wallNormalHorizontal = wallHit.normal;
            wallNormalHorizontal.y = 0;
            wallNormalHorizontal.Normalize();

            var dotProduct = Vector3.Dot(playerForward, -wallNormalHorizontal);
            if(dotProduct < 0.5f) {
                return;
            }

            var ledgeSearchStart = wallHit.point + Vector3.up * LedgeSearchHeight - wallNormalHorizontal * 0.2f;
            Debug.DrawLine(wallHit.point, ledgeSearchStart, Color.cyan, 2f);

            if(!Physics.Raycast(ledgeSearchStart, Vector3.down, out RaycastHit ledgeHit,
                   LedgeSearchHeight + MaxMantleHeight, _mantleableLayers)) {
                return;
            }

            Debug.DrawLine(ledgeSearchStart, ledgeHit.point, Color.green, 2f);
            Debug.DrawRay(ledgeHit.point, Vector3.up * 0.5f, Color.magenta, 2f);

            if(ledgeHit.point.y <= wallHit.point.y + 0.1f) {
                return;
            }

            var ledgeHeight = ledgeHit.point.y - transform.position.y;
            if(ledgeHeight < MinMantleHeight || ledgeHeight > MaxMantleHeight) {
                return;
            }

            var mantleDirection = -wallNormalHorizontal;
            var targetPosition = ledgeHit.point + mantleDirection * ForwardPushDistance;
            targetPosition.y = ledgeHit.point.y + HeightBoost;

            Debug.DrawRay(targetPosition, Vector3.up * _characterController.height, Color.cyan, 2f);
            Debug.DrawLine(ledgeHit.point, targetPosition, Color.yellow, 2f);

            if(Physics.Raycast(targetPosition, Vector3.up, _characterController.height + 0.2f, _mantleableLayers)) {
                return;
            }

            if(Physics.CheckCapsule(
                   targetPosition + Vector3.up * _characterController.radius,
                   targetPosition + Vector3.up * (_characterController.height - _characterController.radius),
                   _characterController.radius * 0.8f,
                   _mantleableLayers)) {
                return;
            }

            StartMantle(targetPosition, mantleDirection);
        }

        private void StartMantle(Vector3 targetPosition, Vector3 mantleDirection) {
            IsMantling = true;
            _mantleTimer = 0f;
            _postMantleJumpCooldown = 0f; // Reset cooldown when starting new mantle

            _mantleStartPosition = transform.position;
            _mantleTargetPosition = targetPosition;
            _mantleDirection = mantleDirection;

            // Zero out player velocity before starting mantle
            if(playerController != null) {
                playerController.ResetVelocity();
            }

            _characterController.enabled = false;

            StartCoroutine(MantleCoroutine());
        }

        private IEnumerator MantleCoroutine() {
            while(_mantleTimer < MantleDuration) {
                _mantleTimer += Time.deltaTime;
                var t = _mantleTimer / MantleDuration;

                // Height progresses throughout the entire mantle
                var heightProgress = mantleHeightCurve.Evaluate(t);

                // Forward movement starts later and accelerates as we near the end
                // This creates the "pull up then push forward" feel
                var forwardT = Mathf.Clamp01((t - ForwardMovementStartRatio) / (1f - ForwardMovementStartRatio));
                var forwardProgress = mantleForwardCurve.Evaluate(forwardT);

                // Calculate horizontal target (target position at start height)
                var horizontalTarget =
                    new Vector3(_mantleTargetPosition.x, _mantleStartPosition.y, _mantleTargetPosition.z);

                // Interpolate horizontal position based on forward progress
                var currentPos = Vector3.Lerp(_mantleStartPosition, horizontalTarget, forwardProgress);

                // Interpolate vertical position based on height progress
                currentPos.y = Mathf.Lerp(_mantleStartPosition.y, _mantleTargetPosition.y, heightProgress);

                transform.position = currentPos;

                yield return null;
            }

            transform.position = _mantleTargetPosition;
            EndMantle();
        }

        private void EndMantle() {
            IsMantling = false;
            
            // Reset velocity again before re-enabling the controller
            // This ensures no stored velocity from before the mantle gets applied
            if(playerController != null) {
                playerController.ResetVelocity();
            }
            
            _characterController.enabled = true;
            
            // Start post-mantle jump cooldown
            _postMantleJumpCooldown = PostMantleJumpDelay;
        }

        private void Update() {
            // Update post-mantle jump cooldown
            if(!(_postMantleJumpCooldown > 0f)) return;
            _postMantleJumpCooldown -= Time.deltaTime;
            if(_postMantleJumpCooldown < 0f) {
                _postMantleJumpCooldown = 0f;
            }
        }

        #region Debug Visualization

        private void OnDrawGizmosSelected() {
            if(transform == null) return;

            var forward = transform.forward;
            forward.y = 0;
            forward.Normalize();

            Gizmos.color = Color.yellow;
            var minCheckPos = transform.position + Vector3.up * MantleCheckHeightMin;
            var maxCheckPos = transform.position + Vector3.up * MantleCheckHeightMax;

            Gizmos.DrawWireSphere(minCheckPos + forward * DetectionDistance, DetectionRadius);
            Gizmos.DrawWireSphere(maxCheckPos + forward * DetectionDistance, DetectionRadius);
            Gizmos.DrawLine(minCheckPos, minCheckPos + forward * DetectionDistance);
            Gizmos.DrawLine(maxCheckPos, maxCheckPos + forward * DetectionDistance);

            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(minCheckPos + forward * DetectionDistance, maxCheckPos + forward * DetectionDistance);

            Gizmos.color = Color.green;
            var minHeightPos = transform.position + Vector3.up * MinMantleHeight;
            var maxHeightPos = transform.position + Vector3.up * MaxMantleHeight;
            Gizmos.DrawWireSphere(minHeightPos, 0.2f);
            Gizmos.DrawWireSphere(maxHeightPos, 0.2f);
            Gizmos.DrawLine(minHeightPos, maxHeightPos);

            if(!IsMantling) return;
            Gizmos.color = Color.red;
            var origin = transform.position + Vector3.up * 1.5f;

            // Show clamp boundaries relative to mantle direction
            var leftBound = Quaternion.Euler(0, -RotationClampAngle, 0) * _mantleDirection * 2f;
            var rightBound = Quaternion.Euler(0, RotationClampAngle, 0) * _mantleDirection * 2f;

            Gizmos.DrawLine(origin, origin + leftBound);
            Gizmos.DrawLine(origin, origin + rightBound);
            Gizmos.DrawLine(origin, origin + _mantleDirection * 2f);
        }

        #endregion
    }
}
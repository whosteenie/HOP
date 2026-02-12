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

        [SerializeField] private AnimationCurve mantleHeightCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
        [SerializeField] private AnimationCurve mantleForwardCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Range(0f, 1f)]
        private const float
            ForwardMovementStartRatio = 0.4f; // When forward movement starts (0 = immediately, 1 = at end)

        [Tooltip(
            "Delay after mantle ends before allowing jump (seconds). Prevents accidental jumps when holding jump during mantle.")]
        private const float PostMantleJumpDelay = 0.15f;

        [Header("Layers")]
        private LayerMask _mantleableLayers;

        public bool IsMantling { get; private set; }
        public bool CanJump => !IsMantling && _postMantleJumpCooldown <= 0f;

        private Vector3 _mantleStartPosition;
        private Vector3 _mantleTargetPosition;
        private float _mantleTimer;
        private float _postMantleJumpCooldown;

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[MantleController] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_characterController == null) _characterController = playerController.CharacterController;
            if(_cameraTransform == null) _cameraTransform = playerController.FpCamera.transform;
            _mantleableLayers = playerController.WorldLayer;
        }

        /// <summary>
        /// Attempts to initiate a mantle if the player is facing a ledge and in the air.
        /// </summary>
        /// <param name="overrideForward">Optional forward vector to use for detection.</param>
        /// <returns>True if a mantle was started.</returns>
        public bool TryMantle(Vector3? overrideForward = null) {
            if(IsMantling) return false;
            if(playerController.IsGrounded) return false;

            var forwardVector = overrideForward ?? transform.forward;
            var playerForward = forwardVector;
            playerForward.y = 0;
            playerForward.Normalize();

            RaycastHit wallHit = default;
            var foundWall = false;

            for(var checkHeight = MantleCheckHeightMin; checkHeight <= MantleCheckHeightMax; checkHeight += 0.3f) {
                var sphereCheckOrigin = playerController.Position + Vector3.up * checkHeight;

                if(!Physics.SphereCast(sphereCheckOrigin, DetectionRadius, playerForward, out wallHit,
                       DetectionDistance,
                       _mantleableLayers)) continue;
                foundWall = true;
                Debug.DrawLine(sphereCheckOrigin, wallHit.point, Color.red, 2f);
                break;
            }

            if(!foundWall) {
                return false;
            }

            var wallNormalHorizontal = wallHit.normal;
            wallNormalHorizontal.y = 0;
            wallNormalHorizontal.Normalize();

            var dotProduct = Vector3.Dot(playerForward, -wallNormalHorizontal);
            if(dotProduct < 0.5f) {
                return false;
            }

            var ledgeSearchStart = wallHit.point + Vector3.up * LedgeSearchHeight - wallNormalHorizontal * 0.2f;
            Debug.DrawLine(wallHit.point, ledgeSearchStart, Color.cyan, 2f);

            if(!Physics.Raycast(ledgeSearchStart, Vector3.down, out var ledgeHit,
                   LedgeSearchHeight + MaxMantleHeight, _mantleableLayers)) {
                return false;
            }

            Debug.DrawLine(ledgeSearchStart, ledgeHit.point, Color.green, 2f);
            Debug.DrawRay(ledgeHit.point, Vector3.up * 0.5f, Color.magenta, 2f);

            if(ledgeHit.point.y <= wallHit.point.y + 0.1f) {
                return false;
            }

            var ledgeHeight = ledgeHit.point.y - playerController.Position.y;
            if(ledgeHeight is < MinMantleHeight or > MaxMantleHeight) {
                return false;
            }

            var mantleDirection = -wallNormalHorizontal;
            var targetPosition = ledgeHit.point + mantleDirection * ForwardPushDistance;
            targetPosition.y = ledgeHit.point.y + HeightBoost;

            Debug.DrawRay(targetPosition, Vector3.up * _characterController.height, Color.cyan, 2f);
            Debug.DrawLine(ledgeHit.point, targetPosition, Color.yellow, 2f);

            if(Physics.Raycast(targetPosition, Vector3.up, _characterController.height + 0.2f, _mantleableLayers)) {
                return false;
            }

            if(Physics.CheckCapsule(
                   targetPosition + Vector3.up * _characterController.radius,
                   targetPosition + Vector3.up * (_characterController.height - _characterController.radius),
                   _characterController.radius * 0.8f, _mantleableLayers)) {
                return false;
            }

            StartMantle(targetPosition);
            return true;
        }

        private void StartMantle(Vector3 targetPosition) {
            IsMantling = true;
            _mantleTimer = 0f;
            _postMantleJumpCooldown = 0f;

            _mantleStartPosition = playerController.Position;
            _mantleTargetPosition = targetPosition;

            if(playerController != null && playerController.AnimationController != null) {
                playerController.AnimationController.PlayMantleAnimationServerRpc();
            }

            playerController.ResetVelocity();

            _characterController.enabled = false;

            StartCoroutine(MantleCoroutine());
        }

        private IEnumerator MantleCoroutine() {
            while(_mantleTimer < MantleDuration) {
                _mantleTimer += Time.deltaTime;
                var t = _mantleTimer / MantleDuration;

                // Height progresses throughout the entire mantle
                var heightProgress = mantleHeightCurve.Evaluate(t);

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

            playerController.ResetVelocity();

            _characterController.enabled = true;

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

    }
}

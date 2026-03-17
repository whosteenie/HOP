using System.Collections;
using Game.Player.Contracts;
using UnityEngine;

namespace Game.Player.Movement {
    public class MantleController : MonoBehaviour {
        [Header("References")]
        [HideInInspector, SerializeField] private MonoBehaviour playerContextSource;

        private IPlayerMovementContext _playerContext;
        private PlayerMovementController _movementController;

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
        private const float TargetBackoffStep = 0.2f;
        private const int TargetBackoffAttempts = 6;

        [Header("Mantle Movement")]
        private const float MantleDuration = 0.3f;
        private const int DepenetrationIterations = 3;
        private const float CapsuleShrinkFactor = 0.95f;

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
        private Coroutine _mantleRoutine;
        private readonly Collider[] _overlapBuffer = new Collider[16];

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(!PlayerContractResolver.TryResolve(this, ref playerContextSource, out _playerContext)) {
                Debug.LogError("[MantleController] IPlayerMovementContext not found!");
                enabled = false;
                return;
            }

            if(_characterController == null) _characterController = _playerContext.CharacterController;
            if(_cameraTransform == null) _cameraTransform = _playerContext.FpCamera.transform;
            if(_movementController == null) _movementController = GetComponent<PlayerMovementController>();
            _mantleableLayers = _playerContext.WorldLayer;
        }

        /// <summary>
        /// Attempts to initiate a mantle if the player is facing a ledge and in the air.
        /// </summary>
        /// <param name="overrideForward">Optional forward vector to use for detection.</param>
        /// <returns>True if a mantle was started.</returns>
        public bool TryMantle(Vector3? overrideForward = null) {
            if(IsMantling) return false;
            if(_playerContext.IsGrounded) return false;

            var forwardVector = overrideForward ?? transform.forward;
            var playerForward = forwardVector;
            playerForward.y = 0;
            playerForward.Normalize();

            RaycastHit wallHit = default;
            var foundWall = false;

            for(var checkHeight = MantleCheckHeightMin; checkHeight <= MantleCheckHeightMax; checkHeight += 0.3f) {
                var sphereCheckOrigin = _playerContext.Position + Vector3.up * checkHeight;

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

            var ledgeHeight = ledgeHit.point.y - _playerContext.Position.y;
            if(ledgeHeight is < MinMantleHeight or > MaxMantleHeight) {
                return false;
            }

            var mantleDirection = -wallNormalHorizontal;
            var targetPosition = ledgeHit.point + mantleDirection * ForwardPushDistance;
            targetPosition.y = ledgeHit.point.y + HeightBoost;
            targetPosition = FindBestReachableMantleTarget(targetPosition, wallNormalHorizontal);

            Debug.DrawRay(targetPosition, Vector3.up * _characterController.height, Color.cyan, 2f);
            Debug.DrawLine(ledgeHit.point, targetPosition, Color.yellow, 2f);

            StartMantle(targetPosition);
            return true;
        }

        private void StartMantle(Vector3 targetPosition) {
            IsMantling = true;
            _mantleTimer = 0f;
            _postMantleJumpCooldown = 0f;

            _mantleStartPosition = _playerContext.Position;
            _mantleTargetPosition = targetPosition;

            _playerContext.TriggerMantleAnimation();

            if(_movementController != null) _movementController.ResetVelocity();

            if(_movementController != null) _movementController.SetMantling(true);

            if(_mantleRoutine != null) {
                StopCoroutine(_mantleRoutine);
            }
            _mantleRoutine = StartCoroutine(MantleCoroutine());
        }

        public void CancelMantleForJumpPad() {
            if(!IsMantling) return;

            if(_mantleRoutine != null) {
                StopCoroutine(_mantleRoutine);
                _mantleRoutine = null;
            }

            EndMantle(applyJumpCooldown: false);
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

                MoveMantleTowards(currentPos);

                yield return null;
            }

            _mantleRoutine = null;
            MoveMantleTowards(_mantleTargetPosition);
            ResolveMantleOverlaps();
            EndMantle(applyJumpCooldown: true);
        }

        private void EndMantle(bool applyJumpCooldown) {
            if(!IsMantling) return;
            IsMantling = false;

            if(_movementController != null) _movementController.ResetVelocity();

            if(_movementController != null) _movementController.SetMantling(false);

            _postMantleJumpCooldown = applyJumpCooldown ? PostMantleJumpDelay : 0f;
        }

        private void Update() {
            // Update post-mantle jump cooldown
            if(!(_postMantleJumpCooldown > 0f)) return;
            _postMantleJumpCooldown -= Time.deltaTime;
            if(_postMantleJumpCooldown < 0f) {
                _postMantleJumpCooldown = 0f;
            }
        }

        private Vector3 FindBestReachableMantleTarget(Vector3 initialTarget, Vector3 wallNormalHorizontal) {
            if(!IsCapsuleBlockedAtPosition(initialTarget)) {
                return initialTarget;
            }

            for(var i = 1; i <= TargetBackoffAttempts; i++) {
                var offset = wallNormalHorizontal * (TargetBackoffStep * i);
                var probe = initialTarget + offset;
                if(!IsCapsuleBlockedAtPosition(probe)) {
                    return probe;
                }
            }

            // Still return original target; collision-aware mantle movement will slide/settle into the closest valid space.
            return initialTarget;
        }

        private bool IsCapsuleBlockedAtPosition(Vector3 position) {
            var up = transform.up;
            var height = Mathf.Max(_characterController.height, _characterController.radius * 2f);
            var halfSegment = Mathf.Max(0f, height * 0.5f - _characterController.radius);
            var center = position + _characterController.center;
            var p1 = center + up * halfSegment;
            var p2 = center - up * halfSegment;
            var radius = _characterController.radius * CapsuleShrinkFactor;

            return Physics.CheckCapsule(p1, p2, radius, _mantleableLayers, QueryTriggerInteraction.Ignore);
        }

        private void MoveMantleTowards(Vector3 targetPosition) {
            if(_characterController == null || !_characterController.enabled) {
                transform.position = targetPosition;
                return;
            }

            var delta = targetPosition - transform.position;
            if(delta.sqrMagnitude <= 0.000001f) return;

            _characterController.Move(delta);
            ResolveMantleOverlaps();
        }

        private void ResolveMantleOverlaps() {
            if(_characterController == null || !_characterController.enabled) return;

            for(var iteration = 0; iteration < DepenetrationIterations; iteration++) {
                var overlapCount = GetMantleOverlaps();
                if(overlapCount == 0) return;

                var totalPush = Vector3.zero;
                for(var i = 0; i < overlapCount; i++) {
                    var overlap = _overlapBuffer[i];
                    if(overlap == null || overlap.isTrigger) continue;
                    if(overlap == _characterController) continue;

                    if(!Physics.ComputePenetration(
                           _characterController,
                           transform.position,
                           transform.rotation,
                           overlap,
                           overlap.transform.position,
                           overlap.transform.rotation,
                           out var direction,
                           out var distance)) {
                        continue;
                    }

                    if(distance <= 0.0001f) continue;
                    totalPush += direction * (distance + 0.002f);
                }

                if(totalPush.sqrMagnitude <= 0.000001f) return;
                _characterController.Move(totalPush);
            }
        }

        private int GetMantleOverlaps() {
            var up = transform.up;
            var height = Mathf.Max(_characterController.height, _characterController.radius * 2f);
            var halfSegment = Mathf.Max(0f, height * 0.5f - _characterController.radius);
            var center = transform.position + _characterController.center;
            var capsuleStart = center + up * halfSegment;
            var capsuleEnd = center - up * halfSegment;
            var radius = _characterController.radius * CapsuleShrinkFactor;

            return Physics.OverlapCapsuleNonAlloc(
                capsuleStart,
                capsuleEnd,
                radius,
                _overlapBuffer,
                _mantleableLayers,
                QueryTriggerInteraction.Ignore);
        }

    }
}


using System.Collections;
using Game.Player;
using UnityEngine;

public class MantleController : MonoBehaviour {
    [Header("References")]
    [SerializeField] private PlayerController playerController;
    [SerializeField] private CharacterController characterController;
    [SerializeField] private Transform cameraTransform;

    [Header("Mantle Detection")]
    [SerializeField] private float detectionRadius = 0.4f;
    [SerializeField] private float detectionDistance = 1f;
    [SerializeField] private float mantleCheckHeightMin = 0.5f;
    [SerializeField] private float mantleCheckHeightMax = 1.8f;
    [SerializeField] private float minMantleHeight = 0.8f;
    [SerializeField] private float maxMantleHeight = 2.5f;
    [SerializeField] private float ledgeSearchHeight = 3f;
    [SerializeField] private float forwardPushDistance = 0.8f;
    [SerializeField] private float heightBoost = 0.1f;

    [Header("Mantle Movement")]
    [SerializeField] private float mantleDuration = 0.1f; // Even faster!
    [SerializeField] private float rotationClampAngle = 60f;
    [SerializeField] private AnimationCurve mantleHeightCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
    [SerializeField] private AnimationCurve mantleForwardCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Header("Layers")]
    [SerializeField] private LayerMask mantleableLayers;

    public bool IsMantling { get; private set; }

    private Vector3 _mantleStartPosition;
    private Vector3 _mantleTargetPosition;
    private Vector3 _mantleDirection;
    private float _mantleDirectionYaw; // Yaw angle of mantle direction
    private float _mantleTimer;

    public void TryMantle() {
        if(IsMantling) return;
        if(playerController.IsGrounded) return;

        var playerForward = transform.forward;
        playerForward.y = 0;
        playerForward.Normalize();

        RaycastHit wallHit = default;
        var foundWall = false;

        for(var checkHeight = mantleCheckHeightMin; checkHeight <= mantleCheckHeightMax; checkHeight += 0.3f) {
            var sphereCheckOrigin = transform.position + Vector3.up * checkHeight;

            if(Physics.SphereCast(sphereCheckOrigin, detectionRadius, playerForward, out wallHit, detectionDistance, mantleableLayers)) {
                foundWall = true;
                Debug.DrawLine(sphereCheckOrigin, wallHit.point, Color.red, 2f);
                break;
            }
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

        var ledgeSearchStart = wallHit.point + Vector3.up * ledgeSearchHeight - wallNormalHorizontal * 0.2f;
        Debug.DrawLine(wallHit.point, ledgeSearchStart, Color.cyan, 2f);

        if(!Physics.Raycast(ledgeSearchStart, Vector3.down, out RaycastHit ledgeHit, ledgeSearchHeight + maxMantleHeight, mantleableLayers)) {
            return;
        }

        Debug.DrawLine(ledgeSearchStart, ledgeHit.point, Color.green, 2f);
        Debug.DrawRay(ledgeHit.point, Vector3.up * 0.5f, Color.magenta, 2f);

        if(ledgeHit.point.y <= wallHit.point.y + 0.1f) {
            return;
        }

        var ledgeHeight = ledgeHit.point.y - transform.position.y;
        if(ledgeHeight < minMantleHeight || ledgeHeight > maxMantleHeight) {
            return;
        }

        var mantleDirection = -wallNormalHorizontal;
        var targetPosition = ledgeHit.point + mantleDirection * forwardPushDistance;
        targetPosition.y = ledgeHit.point.y + heightBoost;

        Debug.DrawRay(targetPosition, Vector3.up * characterController.height, Color.cyan, 2f);
        Debug.DrawLine(ledgeHit.point, targetPosition, Color.yellow, 2f);

        if(Physics.Raycast(targetPosition, Vector3.up, characterController.height + 0.2f, mantleableLayers)) {
            return;
        }

        if(Physics.CheckCapsule(
            targetPosition + Vector3.up * characterController.radius,
            targetPosition + Vector3.up * (characterController.height - characterController.radius),
            characterController.radius * 0.8f,
            mantleableLayers)) {
            return;
        }

        StartMantle(targetPosition, mantleDirection);
    }

    private void StartMantle(Vector3 targetPosition, Vector3 mantleDirection) {
        IsMantling = true;
        _mantleTimer = 0f;

        _mantleStartPosition = transform.position;
        _mantleTargetPosition = targetPosition;
        _mantleDirection = mantleDirection;
        
        // Store mantle direction as yaw angle
        _mantleDirectionYaw = Mathf.Atan2(mantleDirection.x, mantleDirection.z) * Mathf.Rad2Deg;

        playerController.SetMantling(true);
        characterController.enabled = false;

        StartCoroutine(MantleCoroutine());
    }

    private IEnumerator MantleCoroutine() {
        while(_mantleTimer < mantleDuration) {
            _mantleTimer += Time.deltaTime;
            var t = _mantleTimer / mantleDuration;

            var heightProgress = mantleHeightCurve.Evaluate(t);
            var forwardProgress = mantleForwardCurve.Evaluate(t);

            var horizontalTarget = new Vector3(_mantleTargetPosition.x, _mantleStartPosition.y, _mantleTargetPosition.z);
            var currentPos = Vector3.Lerp(_mantleStartPosition, horizontalTarget, forwardProgress);
            currentPos.y = Mathf.Lerp(_mantleStartPosition.y, _mantleTargetPosition.y, heightProgress);
            transform.position = currentPos;

            yield return null;
        }

        transform.position = _mantleTargetPosition;
        EndMantle();
    }

    public void ClampMantleYaw(Transform characterRoot)
    {
        if (!IsMantling) return;

        // Current yaw of the character
        var euler = characterRoot.eulerAngles;
        var currentYaw = euler.y;

        // How far are we from the mantle direction?
        var delta = Mathf.DeltaAngle(_mantleDirectionYaw, currentYaw);

        // Clamp that offset
        var clampedDelta = Mathf.Clamp(delta, -rotationClampAngle, rotationClampAngle);

        // If we're already within the cone, do nothing
        if (Mathf.Approximately(delta, clampedDelta))
            return;

        // Otherwise, pull yaw back to the boundary
        var newYaw = _mantleDirectionYaw + clampedDelta;
        euler.y = newYaw;
        characterRoot.eulerAngles = euler;
    }

    private void EndMantle() {
        IsMantling = false;

        characterController.enabled = true;
        playerController.SetMantling(false);

        playerController.ResetVelocity();
    }

    #region Debug Visualization

    private void OnDrawGizmosSelected() {
        if(transform == null) return;

        var forward = transform.forward;
        forward.y = 0;
        forward.Normalize();

        Gizmos.color = Color.yellow;
        var minCheckPos = transform.position + Vector3.up * mantleCheckHeightMin;
        var maxCheckPos = transform.position + Vector3.up * mantleCheckHeightMax;

        Gizmos.DrawWireSphere(minCheckPos + forward * detectionDistance, detectionRadius);
        Gizmos.DrawWireSphere(maxCheckPos + forward * detectionDistance, detectionRadius);
        Gizmos.DrawLine(minCheckPos, minCheckPos + forward * detectionDistance);
        Gizmos.DrawLine(maxCheckPos, maxCheckPos + forward * detectionDistance);

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(minCheckPos + forward * detectionDistance, maxCheckPos + forward * detectionDistance);

        Gizmos.color = Color.green;
        var minHeightPos = transform.position + Vector3.up * minMantleHeight;
        var maxHeightPos = transform.position + Vector3.up * maxMantleHeight;
        Gizmos.DrawWireSphere(minHeightPos, 0.2f);
        Gizmos.DrawWireSphere(maxHeightPos, 0.2f);
        Gizmos.DrawLine(minHeightPos, maxHeightPos);
        
        if(IsMantling) {
            Gizmos.color = Color.red;
            var origin = transform.position + Vector3.up * 1.5f;
            
            // Show clamp boundaries relative to mantle direction
            var leftBound = Quaternion.Euler(0, -rotationClampAngle, 0) * _mantleDirection * 2f;
            var rightBound = Quaternion.Euler(0, rotationClampAngle, 0) * _mantleDirection * 2f;
            
            Gizmos.DrawLine(origin, origin + leftBound);
            Gizmos.DrawLine(origin, origin + rightBound);
            Gizmos.DrawLine(origin, origin + _mantleDirection * 2f);
        }
    }

    #endregion
}
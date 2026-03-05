using Audio.Networking;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Shelved movement prototype.
    /// Not part of current production gameplay paths; retained for future experimentation.
    /// </summary>
    [AddComponentMenu("Shelved/Movement/Swing Grapple (Unused Prototype)")]
    public class SwingGrapple : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        [SerializeField] private CharacterController characterController;
        [SerializeField] private CinemachineCamera fpCamera;
        [SerializeField] private LineRenderer ropeRenderer;
        [SerializeField] private GrappleController pullGrapple;
        [SerializeField] private NetworkAudioRelay audioRelay;

        [Header("Swing Settings")]
        [SerializeField] private float maxSwingDistance = 50f;

        [Header("Rope Visuals")]
        [SerializeField] private float lineWidth = 0.05f;

        [SerializeField] private Color ropeColor = new(0.2f, 0.8f, 1f);
        [SerializeField] private Material ropeMaterial;

        [Header("Layers")]
        [SerializeField] private LayerMask grappleableLayers;

        [SerializeField] private LayerMask groundLayers;

        public bool IsSwinging { get; private set; }

        private Vector3 _swingPoint;
        private float _ropeLength;
        private Vector3 _currentVelocity;

        // Networked for visuals
        private readonly NetworkVariable<bool> _netIsSwinging = new();
        private readonly NetworkVariable<Vector3> _netSwingPoint = new();

        private void Awake() {
            if(audioRelay == null) {
                audioRelay = GetComponent<NetworkAudioRelay>();
            }

            if(!ropeRenderer) {
                var go = new GameObject("SwingRope");
                go.transform.SetParent(transform);
                ropeRenderer = go.AddComponent<LineRenderer>();
            }

            ropeRenderer.startWidth = ropeRenderer.endWidth = lineWidth;
            ropeRenderer.positionCount = 2;
            ropeRenderer.useWorldSpace = true;
            ropeRenderer.material = ropeMaterial ? ropeMaterial : new Material(Shader.Find("Sprites/Default"));
            ropeRenderer.startColor = ropeRenderer.endColor = ropeColor;
            ropeRenderer.enabled = false;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            _netIsSwinging.OnValueChanged += OnSwingChanged;
            _netSwingPoint.OnValueChanged += OnPointChanged;
        }

        public override void OnNetworkDespawn() {
            _netIsSwinging.OnValueChanged -= OnSwingChanged;
            _netSwingPoint.OnValueChanged -= OnPointChanged;
        }

        [Rpc(SendTo.Server)]
        private void UpdateSwingRpc(bool swinging, Vector3 point) {
            _netIsSwinging.Value = swinging;
            _netSwingPoint.Value = point;
        }

        private void OnSwingChanged(bool _, bool newVal) => UpdateRope();
        private void OnPointChanged(Vector3 _, Vector3 __) => UpdateRope();

        private void UpdateRope() {
            if(!ropeRenderer) return;
            ropeRenderer.enabled = _netIsSwinging.Value;
        }

        /// <summary>
        /// Attempts to start a swing grapple if looking at a grappleable surface.
        /// </summary>
        public void TryStartSwing() {
            if(!IsOwner || IsSwinging || characterController.isGrounded) return;
            if(pullGrapple && !pullGrapple.CanGrapple) return;

            if(Physics.Raycast(fpCamera.transform.position, fpCamera.transform.forward, out var hit, maxSwingDistance,
                   grappleableLayers)) {
                StartSwing(hit.point);
            }
        }

        private void CancelSwing() {
            if(!IsSwinging) return;

            IsSwinging = false;
            ropeRenderer.enabled = false;
            UpdateSwingRpc(false, Vector3.zero);

            // Keep momentum when releasing. During a jumppad launch, do not apply vertical velocity
            // from the swing so the launch's upward velocity is preserved.
            playerController.SetVelocity(new Vector3(_currentVelocity.x, 0f, _currentVelocity.z));
            var inJumpPadLaunch = playerController.MovementController != null && playerController.MovementController.IsInJumpPadLaunch;
            if(!inJumpPadLaunch) {
                playerController.AddVerticalVelocity(_currentVelocity.y);
            }
        }

        private void StartSwing(Vector3 point) {
            _swingPoint = point;
            _ropeLength = Vector3.Distance(transform.position, _swingPoint);
            _currentVelocity = playerController.GetFullVelocity;

            IsSwinging = true;
            ropeRenderer.enabled = true;
            UpdateSwingRpc(true, _swingPoint);

            if(pullGrapple != null) {
                pullGrapple.TriggerCooldown();
            }
            if(audioRelay != null) {
                audioRelay.RequestPlayAttached("gameplay.grapple", new NetworkObjectReference(NetworkObject), allowOverlap: true);
            }
        }

        private void Update() {
            if(!IsOwner) {
                if(_netIsSwinging.Value) DrawRopeVisuals();
                return;
            }

            if(!IsSwinging) return;

            // Auto-cancel on ground
            if(Physics.Raycast(transform.position + Vector3.up * 0.1f, Vector3.down, 1.6f, groundLayers)) {
                CancelSwing();
                return;
            }

            SwingUpdate();
            DrawRopeVisuals();
        }

        private void SwingUpdate() {
            var position = transform.position;
            var toAnchor = _swingPoint - position;
            var currentDist = toAnchor.magnitude;

            // Direction along rope
            var ropeDir = toAnchor / currentDist;

            // Gravity component tangent to swing arc
            var gravityTangent = Vector3.ProjectOnPlane(Physics.gravity, ropeDir);

            // Apply tangential gravity only
            _currentVelocity += gravityTangent * Time.deltaTime;

            // Predict next position
            var predicted = position + _currentVelocity * Time.deltaTime;

            // Enforce rope length (project back onto sphere)
            var fromAnchor = predicted - _swingPoint;
            predicted = _swingPoint + fromAnchor.normalized * _ropeLength;

            // Correct velocity: remove any radial component
            var delta = predicted - position;
            var radialVel = Vector3.Project(_currentVelocity, ropeDir);
            _currentVelocity -= radialVel;

            // Move with CharacterController
            characterController.Move(delta);

            // Optional: very light air damping (feels better)
            _currentVelocity *= 0.99f;
        }

        private void DrawRopeVisuals() {
            if(!ropeRenderer || !ropeRenderer.enabled) return;

            var fpCameraTransform = fpCamera.transform;
            var handPos = fpCameraTransform.position
                          - fpCameraTransform.right * 0.3f
                          - fpCameraTransform.up * 0.2f;

            var anchor = IsOwner ? _swingPoint : _netSwingPoint.Value;

            ropeRenderer.SetPosition(0, handPos);
            ropeRenderer.SetPosition(1, anchor);
        }

        private void OnDrawGizmosSelected() {
            if(!IsSwinging) return;
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(_swingPoint, 0.3f);
            Gizmos.DrawWireSphere(_swingPoint, _ropeLength);
        }
    }
}

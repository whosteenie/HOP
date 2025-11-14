using System.Collections;
using Network.Rpc;
using Network.Singletons;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Player {
    public class GrappleController : NetworkBehaviour {
        [Header("Grapple Settings")] [SerializeField]
        private float maxGrappleDistance = 50f;

        [SerializeField] private float grappleSpeed = 30f;
        [SerializeField] private float grappleDuration = 0.5f;
        [SerializeField] private float grappleCooldown = 1.3f;
        [SerializeField] private LayerMask grappleableLayers;

        [Header("Momentum Settings")] [SerializeField]
        private bool preserveMomentum = true;

        [SerializeField] private float momentumBoost = 1.2f; // Multiplier for final velocity

        [Header("Components")] [SerializeField]
        private CinemachineCamera fpCamera;

        [SerializeField] private Transform grappleOriginTp;
        [SerializeField] private CharacterController characterController;
        [SerializeField] private PlayerController playerController;
        [SerializeField] private LineRenderer grappleLine;
        [SerializeField] private NetworkSfxRelay sfxRelay;
        [SerializeField] private LayerMask playerLayer;
        [SerializeField] private SwingGrapple swingGrapple;

        [Header("Visual Settings")] [SerializeField]
        private float lineWidth = 0.05f;

        [SerializeField] private Color grappleColor = new Color(0.2f, 0.8f, 1f);
        [SerializeField] private Material lineMaterial;

        #region Private Fields

        private Vector3 _grapplePoint;
        private float _grappleStartTime;
        private Vector3 _grappleStartPosition;
        private float _cooldownStartTime;

        #endregion

        #region Properties

        public bool IsGrappling { get; private set; }

        public bool CanGrapple { get; private set; } = true;

        public float CooldownProgress {
            get {
                if(CanGrapple) return 1f;
                var elapsed = Time.time - _cooldownStartTime;
                return Mathf.Clamp01(elapsed / grappleCooldown);
            }
        }

        #endregion

        private NetworkVariable<bool> _netIsGrappling = new();
        private NetworkVariable<Vector3> _netGrapplePoint = new();

        #region Unity Lifecycle

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            _netIsGrappling.OnValueChanged += OnGrappleStateChanged;
            _netGrapplePoint.OnValueChanged += OnGrapplePointChanged;

            // Apply initial state
            if(!IsOwner) {
                UpdateGrappleVisuals(_netIsGrappling.Value, _netGrapplePoint.Value);
            }
        }

        private void Start() {
            // if(IsOwner && GrappleUIManager.Instance != null) {
            //     GrappleUIManager.Instance.GrappleController = this;
            // }

            SetupGrappleLine();
        }

        private void Update() {
            if(!IsOwner && _netIsGrappling.Value) {
                // Non-owners: update line position every frame while grappling
                if(grappleLine != null) {
                    grappleLine.SetPosition(0, grappleOriginTp.position);
                    grappleLine.SetPosition(1, _netGrapplePoint.Value);
                }

                return;
            }

            if(!IsOwner) return;

            if(IsGrappling) {
                UpdateGrapple();
            }

            UpdateGrappleLine();
        }

        #endregion

        public void TriggerCooldown() {
            if(!CanGrapple) return; // Already on cooldown
            StartCoroutine(GrappleCooldown());
        }

        [Rpc(SendTo.Server)]
        private void UpdateGrappleServerRpc(bool isGrappling, Vector3 grapplePoint) {
            _netIsGrappling.Value = isGrappling;
            _netGrapplePoint.Value = grapplePoint;
        }

        // Called on all clients when grapple state changes
        private void OnGrappleStateChanged(bool previousValue, bool newValue) {
            if(IsOwner) return; // Owner already has their own visuals

            UpdateGrappleVisuals(newValue, _netGrapplePoint.Value);
        }

        private void OnGrapplePointChanged(Vector3 previousValue, Vector3 newValue) {
            if(IsOwner) return;

            UpdateGrappleVisuals(_netIsGrappling.Value, newValue);
        }

        private void UpdateGrappleVisuals(bool isGrappling, Vector3 targetPoint) {
            if(grappleLine == null) return;

            grappleLine.enabled = isGrappling;

            if(isGrappling) {
                grappleLine.SetPosition(0, _grappleStartPosition);
                grappleLine.SetPosition(1, targetPoint);
            }
        }

        #region Setup

        private void SetupGrappleLine() {
            if(grappleLine == null) {
                var lineObj = new GameObject("GrappleLine");
                lineObj.transform.SetParent(transform);
                grappleLine = lineObj.AddComponent<LineRenderer>();
            }

            grappleLine.startWidth = lineWidth;
            grappleLine.endWidth = lineWidth;
            grappleLine.positionCount = 2;
            grappleLine.useWorldSpace = true;
            grappleLine.enabled = false;

            // Setup material
            grappleLine.material = lineMaterial != null ? lineMaterial : new Material(Shader.Find("Sprites/Default"));

            grappleLine.startColor = grappleColor;
            grappleLine.endColor = grappleColor;
        }

        #endregion

        #region Public Methods

        public void TryGrapple() {
            if(!CanGrapple || IsGrappling) return;

            // Raycast from camera to find grapple point
            var ray = new Ray(fpCamera.transform.position, fpCamera.transform.forward);

            if(Physics.Raycast(ray, out var hit, maxGrappleDistance, grappleableLayers)) {
                StartGrapple(hit.point);
            }
        }

        public void CancelGrapple() {
            if(!IsGrappling) return;

            // UpdateGrappleServerRpc(false, Vector3.zero);
            EndGrapple(true);
        }

        #endregion

        #region Private Methods - Grapple Logic

        private void StartGrapple(Vector3 targetPoint) {
            if(swingGrapple.IsSwinging) swingGrapple.CancelSwing();
            UpdateGrappleServerRpc(true, targetPoint);
            IsGrappling = true;
            _grapplePoint = targetPoint;
            _grappleStartTime = Time.time;
            _grappleStartPosition = transform.position;

            // Enable visual
            grappleLine.enabled = true;

            if(sfxRelay != null && IsOwner) {
                sfxRelay?.RequestWorldSfx(SfxKey.Grapple, attachToSelf: true, true);
            }
        }

        private void UpdateGrapple() {
            var elapsed = Time.time - _grappleStartTime;

            // Check if grapple duration exceeded
            if(elapsed >= grappleDuration) {
                EndGrapple(true);
                return;
            }

            // Calculate pull direction and velocity
            var directionToPoint = (_grapplePoint - transform.position).normalized;
            var distanceToPoint = Vector3.Distance(transform.position, _grapplePoint);

            // If we're very close, end the grapple
            if(distanceToPoint < 1f) {
                EndGrapple(true);
                return;
            }

            // Check for walls in the direction we're moving
            var pullVelocity = directionToPoint * grappleSpeed;
            var checkDistance = pullVelocity.magnitude * Time.deltaTime * 3f; // Check slightly ahead
            if(Physics.SphereCast(transform.position, characterController.radius, directionToPoint, out var hit,
                   checkDistance, ~playerLayer)) {
                // We're about to hit something, end grapple early
                EndGrapple(true);
                return;
            }

            // Apply movement
            characterController.Move(pullVelocity * Time.deltaTime);
        }

        private void EndGrapple(bool applyMomentum) {
            IsGrappling = false;

            StartCoroutine(DisableLineAfterDelay(0.1f));

            // grappleLine.enabled = false;

            if(applyMomentum && preserveMomentum) {
                // Calculate final momentum direction
                var directionToPoint = (_grapplePoint - transform.position).normalized;
                var finalVelocity = grappleSpeed * momentumBoost * directionToPoint;

                // Apply momentum to FpController
                if(playerController != null) {
                    // Set horizontal velocity (preserve some existing momentum)
                    var horizontalVelocity = new Vector3(finalVelocity.x, 0f, finalVelocity.z);
                    playerController.SetVelocity(horizontalVelocity);

                    // Add upward boost if grappling upward
                    if(finalVelocity.y > 0) {
                        playerController.AddVerticalVelocity(finalVelocity.y);
                    }
                }
            }

            // Start cooldown
            StartCoroutine(GrappleCooldown());
        }

        private IEnumerator DisableLineAfterDelay(float delay) {
            yield return new WaitForSeconds(delay);

            UpdateGrappleServerRpc(false, Vector3.zero);
            grappleLine.enabled = false;
        }

        private IEnumerator GrappleCooldown() {
            CanGrapple = false;
            _cooldownStartTime = Time.time;
            yield return new WaitForSeconds(grappleCooldown);
            CanGrapple = true;
        }

        private void UpdateGrappleLine() {
            if(!grappleLine.enabled) return;

            // Update line positions (from hand/weapon to grapple point)
            var handPosition = fpCamera.transform.position - fpCamera.transform.right * 0.3f -
                               fpCamera.transform.up * 0.2f;

            grappleLine.SetPosition(0, handPosition);
            grappleLine.SetPosition(1, _grapplePoint);
        }

        #endregion
    }
}
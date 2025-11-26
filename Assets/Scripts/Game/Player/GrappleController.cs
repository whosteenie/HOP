using System.Collections;
using Network.Rpc;
using Network.Singletons;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class GrappleController : NetworkBehaviour {
        [Header("Components")]
        [SerializeField] private PlayerController playerController;
        private CinemachineCamera _fpCamera;
        private CharacterController _characterController;
        private PlayerTagController _tagController; // For checking if player is tagged in Gun Tag mode
        private NetworkSfxRelay _sfxRelay;
        private LayerMask _playerLayer;
        [SerializeField] private Transform grappleOriginTp;
        [SerializeField] private LineRenderer grappleLine;
        // [SerializeField] private SwingGrapple swingGrapple;

        [Header("Grapple Settings")]
        private const float MaxGrappleDistance = 50f;

        private const float GrappleSpeed = 30f;
        private const float GrappleDuration = 0.5f;
        private const float GrappleCooldown = 1.3f;
        private const float TaggedPlayerCooldown = 1.0f; // Lower cooldown for tagged players in Gun Tag mode
        private LayerMask _grappleableLayers;

        [Header("Momentum Settings")]
        private const bool PreserveMomentum = true;
        private const float MomentumBoost = 1.2f; // Multiplier for final velocity

        [Header("Visual Settings")]
        [SerializeField] private float lineWidth = 0.05f;
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
                var currentCooldown = GetCurrentCooldown();
                return Mathf.Clamp01(elapsed / currentCooldown);
            }
        }

        /// <summary>
        /// Gets the current grapple cooldown based on whether the player is tagged in Gun Tag mode.
        /// </summary>
        private float GetCurrentCooldown() {
            // Check if we're in Gun Tag mode and player is tagged
            var matchSettings = MatchSettingsManager.Instance;
            var isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag";

            if(isTagMode && _tagController != null && _tagController.isTagged.Value) {
                return TaggedPlayerCooldown;
            }

            return GrappleCooldown;
        }

        #endregion

        private readonly NetworkVariable<bool> _netIsGrappling = new();
        private readonly NetworkVariable<Vector3> _netGrapplePoint = new();

        // Throttling for network updates (at 90Hz: 3 ticks = ~33ms)
        private float _lastGrappleUpdateTime;
        private const float GrappleUpdateInterval = 0.033f; // ~3 ticks at 90Hz

        #region Unity Lifecycle

        private void Awake() {
            playerController ??= GetComponent<PlayerController>();

            _fpCamera ??= playerController.FpCamera;
            _characterController ??= playerController.CharacterController;
            _tagController ??= playerController.TagController;
            _sfxRelay ??= playerController.SfxRelay;
            _playerLayer = playerController.PlayerLayer;
            _grappleableLayers = playerController.WorldLayer;
        }

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
            SetupGrappleLine();
        }

        private void Update() {
            switch(IsOwner) {
                case false when _netIsGrappling.Value: {
                    // Non-owners: update line position every frame while grappling
                    if(grappleLine == null) return;
                    grappleLine.SetPosition(0, grappleOriginTp.position);
                    grappleLine.SetPosition(1, _netGrapplePoint.Value);

                    return;
                }
                case false:
                    return;
            }

            if(IsGrappling) {
                UpdateGrapple();
            }

            UpdateGrappleLine();
        }

        #endregion

        public void TriggerCooldown() {
            if(!CanGrapple) return; // Already on cooldown
            StartCoroutine(StartGrappleCooldown());
        }

        [Rpc(SendTo.Server)]
        private void UpdateGrappleServerRpc(bool isGrappling, Vector3 grapplePoint) {
            // Throttle network updates - only send if enough time has passed or state changed
            var shouldUpdate = Time.time - _lastGrappleUpdateTime >= GrappleUpdateInterval ||
                               _netIsGrappling.Value != isGrappling ||
                               Vector3.Distance(_netGrapplePoint.Value, grapplePoint) > 0.1f;

            if(!shouldUpdate) return;
            _netIsGrappling.Value = isGrappling;
            _netGrapplePoint.Value = grapplePoint;
            _lastGrappleUpdateTime = Time.time;
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

            if(!isGrappling) return;
            grappleLine.SetPosition(0, _grappleStartPosition);
            grappleLine.SetPosition(1, targetPoint);
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
            var ray = new Ray(_fpCamera.transform.position, _fpCamera.transform.forward);

            if(Physics.Raycast(ray, out var hit, MaxGrappleDistance, _grappleableLayers)) {
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
            // TODO: Reimplement when swing grapple is implemented
            // if(swingGrapple.IsSwinging) swingGrapple.CancelSwing();
            UpdateGrappleServerRpc(true, targetPoint);
            IsGrappling = true;
            _grapplePoint = targetPoint;
            _grappleStartTime = Time.time;
            _grappleStartPosition = transform.position;

            // Enable visual
            grappleLine.enabled = true;

            if(_sfxRelay != null && IsOwner) {
                _sfxRelay?.RequestWorldSfx(SfxKey.Grapple, attachToSelf: true, true);
            }
        }

        private void UpdateGrapple() {
            var elapsed = Time.time - _grappleStartTime;

            // Check if grapple duration exceeded
            if(elapsed >= GrappleDuration) {
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

            // Check if character controller is active (prevents errors during mantling, respawn, etc.)
            if(_characterController == null || !_characterController.enabled) {
                EndGrapple(false);
                return;
            }

            // Check for walls in the direction we're moving
            var pullVelocity = directionToPoint * GrappleSpeed;
            var checkDistance = pullVelocity.magnitude * Time.deltaTime * 3f; // Check slightly ahead
            if(Physics.SphereCast(transform.position, _characterController.radius, directionToPoint, out _,
                   checkDistance, ~_playerLayer)) {
                // We're about to hit something, end grapple early
                EndGrapple(true);
                return;
            }

            // Apply movement
            _characterController.Move(pullVelocity * Time.deltaTime);
        }

        private void EndGrapple(bool applyMomentum) {
            IsGrappling = false;

            StartCoroutine(DisableLineAfterDelay(0.1f));

            // grappleLine.enabled = false;

            if(applyMomentum && PreserveMomentum) {
                // Calculate final momentum direction
                var directionToPoint = (_grapplePoint - transform.position).normalized;
                var finalVelocity = GrappleSpeed * MomentumBoost * directionToPoint;

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
            StartCoroutine(StartGrappleCooldown());
        }

        private IEnumerator DisableLineAfterDelay(float delay) {
            yield return new WaitForSeconds(delay);

            UpdateGrappleServerRpc(false, Vector3.zero);
            grappleLine.enabled = false;
        }

        private IEnumerator StartGrappleCooldown() {
            CanGrapple = false;
            _cooldownStartTime = Time.time;
            var currentCooldown = GetCurrentCooldown();
            yield return new WaitForSeconds(currentCooldown);
            CanGrapple = true;
        }

        private void UpdateGrappleLine() {
            if(!grappleLine.enabled) return;

            // Update line positions (from hand/weapon to grapple point)
            var handPosition = _fpCamera.transform.position - _fpCamera.transform.right * 0.3f -
                               _fpCamera.transform.up * 0.2f;

            grappleLine.SetPosition(0, handPosition);
            grappleLine.SetPosition(1, _grapplePoint);
        }

        #endregion
    }
}
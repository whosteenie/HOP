using System.Collections;
using Audio.Networking;
using Game.Match;
using Game.Progression;
using Game.Weapons;
using Network.Events;
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
        private NetworkAudioRelay _audioRelay;
        private LayerMask _playerLayer;
        [SerializeField] private Transform grappleOriginTp;

        [Header("Grapple Settings")]
        private const float MaxGrappleDistance = 50f;

        private const float GrappleSpeed = 30f;
        private const float GrappleDuration = 0.5f;
        private const float GrappleCooldown = 1.3f;
        private const float TaggedPlayerCooldown = 1.0f; // Lower cooldown for tagged players in Gun Tag mode
        private const float GrappleEstablishGrace = 0.05f; // Ignore spherecast/collision cancel until mesh can show

        [Header("Momentum Settings")]
        private const bool PreserveMomentum = true;

        private const float MomentumBoost = 1.2f; // Multiplier for final velocity

        [Header("Visual Settings")]
        [SerializeField] private Material lineMaterial;

        [Header("Mesh Settings")]
        [SerializeField] private int meshSegments = 8;
        [SerializeField] private float meshRadius = 0.02f;
        [SerializeField] private Color grappleColor = new(0.2f, 0.8f, 1f);

        #region Private Fields

        private Vector3 _grapplePoint;
        private float _grappleStartTime;
        private float _cooldownStartTime;

        // Mesh system fields
        private GameObject _grappleMeshObject;
        private MeshFilter _grappleMeshFilter;
        private MeshRenderer _grappleMeshRenderer;
        private Mesh _grappleMesh;
        private bool _pendingGrappleMeshEnable; // Defer mesh until grapple anim first frame

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
        
        /// <summary>
        /// Gets the grappleable layers for external use (e.g., AI bots for raycasting).
        /// </summary>
        private LayerMask GrappleableLayers { get; set; }

        /// <summary>
        /// Gets the max grapple distance for external use (e.g., AI bots).
        /// </summary>
        public float MaxGrappleDistanceValue => MaxGrappleDistance;

        #endregion

        private readonly NetworkVariable<bool> _netIsGrappling = new();
        private readonly NetworkVariable<Vector3> _netGrapplePoint = new();

        // Throttling for network updates (at 90Hz: 3 ticks = ~33ms)
        private float _lastGrappleUpdateTime;
        private const float GrappleUpdateInterval = 0.033f; // ~3 ticks at 90Hz

        #region Unity Lifecycle

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }
            
            if(_fpCamera == null) {
                _fpCamera = playerController.FpCamera;
            }
            
            if(_characterController == null) {
                _characterController = playerController.CharacterController;
            }
            
            if(_tagController == null) {
                _tagController = playerController.TagController;
            }
            
            if(_audioRelay == null) {
                _audioRelay = playerController.AudioRelay;
            }

            _playerLayer = playerController.PlayerLayer;
            GrappleableLayers = playerController.WorldLayer;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            _netIsGrappling.OnValueChanged += OnGrappleStateChanged;
            _netGrapplePoint.OnValueChanged += OnGrapplePointChanged;

            if(IsOwner) {
                EventBus.Subscribe<GrappleAnimFirstFrameEvent>(OnGrappleAnimFirstFrame);
                EventBus.Subscribe<GrappleAnimHideEvent>(OnGrappleAnimHide);
            }

            // Apply initial state
            if(!IsOwner) {
                UpdateGrappleVisuals(_netIsGrappling.Value, _netGrapplePoint.Value);
            }
        }

        private void Start() {
            SetupGrappleLine();
        }

        public override void OnDestroy() {
            if(IsOwner) {
                EventBus.Unsubscribe<GrappleAnimFirstFrameEvent>(OnGrappleAnimFirstFrame);
                EventBus.Unsubscribe<GrappleAnimHideEvent>(OnGrappleAnimHide);
            }

            base.OnDestroy();
            // Clean up unparented mesh object
            if(_grappleMeshObject != null) {
                Destroy(_grappleMeshObject);
            }
        }

        private void Update() {
            switch(IsOwner) {
                case false when _netIsGrappling.Value: {
                    // Non-owners: update visual position every frame while grappling
                    if(_grappleMeshRenderer == null || grappleOriginTp == null) return;
                    UpdateGrappleMesh(grappleOriginTp.position, _netGrapplePoint.Value);
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

        /// <summary>
        /// Triggers the grapple cooldown.
        /// </summary>
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

        /// <summary>
        /// Called when the grapple point is updated on the network.
        /// </summary>
        private void OnGrapplePointChanged(Vector3 previousValue, Vector3 newValue) {
            if(IsOwner) return;

            UpdateGrappleVisuals(_netIsGrappling.Value, newValue);
        }

        private void UpdateGrappleVisuals(bool isGrappling, Vector3 targetPoint) {
            if(_grappleMeshRenderer == null) return;
            _grappleMeshRenderer.enabled = isGrappling;
            if(!isGrappling) return;
            if(grappleOriginTp != null) {
                UpdateGrappleMesh(grappleOriginTp.position, targetPoint);
            }
        }

        #region Setup

        private void SetupGrappleLine() {
            SetupGrappleMesh();
        }

        private void SetupGrappleMesh() {
            if(_grappleMeshObject == null) {
                _grappleMeshObject = new GameObject("GrappleCable");
                _grappleMeshObject.transform.SetParent(null);

                _grappleMeshFilter = _grappleMeshObject.AddComponent<MeshFilter>();
                _grappleMeshRenderer = _grappleMeshObject.AddComponent<MeshRenderer>();

                _grappleMesh = new Mesh {
                    name = "GrappleCableMesh"
                };
                _grappleMeshFilter.mesh = _grappleMesh;
            } else {
                if(_grappleMeshObject.transform.parent != null) {
                    _grappleMeshObject.transform.SetParent(null);
                }
            }

            if(lineMaterial != null) {
                _grappleMeshRenderer.material = lineMaterial;
            } else {
                _grappleMeshRenderer.material = new Material(Shader.Find("Universal Render Pipeline/Lit"))
                    {
                        color = grappleColor
                    };
            }

            _grappleMeshRenderer.enabled = false;
        }

        private void UpdateGrappleMesh(Vector3 startPos, Vector3 endPos) {
            if(_grappleMesh == null || _grappleMeshFilter == null) return;

            var direction = (endPos - startPos).normalized;
            var distance = Vector3.Distance(startPos, endPos);

            // Generate cylinder mesh between two points
            var vertices = new Vector3[meshSegments * 2];
            var triangles = new int[meshSegments * 6];
            var uvs = new Vector2[vertices.Length];
            var normals = new Vector3[vertices.Length];

            // Calculate perpendicular vectors for cylinder cross-section
            var right = Vector3.Cross(direction, Vector3.up);
            if(right.magnitude < 0.1f) {
                right = Vector3.Cross(direction, Vector3.forward);
            }

            right.Normalize();
            var up = Vector3.Cross(right, direction).normalized;

            // Generate vertices for start and end circles
            for(var i = 0; i < meshSegments; i++) {
                var angle = i / (float)meshSegments * Mathf.PI * 2f;
                var offset = right * (Mathf.Cos(angle) * meshRadius) + up * (Mathf.Sin(angle) * meshRadius);

                // Start circle
                vertices[i] = startPos + offset;
                uvs[i] = new Vector2(i / (float)meshSegments, 0f);
                normals[i] = offset.normalized;

                // End circle
                vertices[i + meshSegments] = endPos + offset;
                uvs[i + meshSegments] = new Vector2(i / (float)meshSegments, distance);
                normals[i + meshSegments] = offset.normalized;
            }

            // Generate triangles (quads made of two triangles)
            var triIndex = 0;
            for(var i = 0; i < meshSegments; i++) {
                var next = (i + 1) % meshSegments;

                // First triangle
                triangles[triIndex++] = i;
                triangles[triIndex++] = i + meshSegments;
                triangles[triIndex++] = next;

                // Second triangle
                triangles[triIndex++] = next;
                triangles[triIndex++] = i + meshSegments;
                triangles[triIndex++] = next + meshSegments;
            }

            // Update mesh
            _grappleMesh.Clear();
            _grappleMesh.vertices = vertices;
            _grappleMesh.triangles = triangles;
            _grappleMesh.uv = uvs;
            _grappleMesh.normals = normals;
            _grappleMesh.RecalculateBounds();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Attempts to start a grapple if looking at a grappleable surface.
        /// </summary>
        public void TryGrapple() {
            if(!CanGrapple || IsGrappling) return;

            var ray = new Ray(playerController.FpCameraTransform.position, playerController.FpCameraTransform.forward);

            if(Physics.Raycast(ray, out var hit, MaxGrappleDistance, GrappleableLayers)) {
                StartGrapple(hit.point);
            }
        }

        /// <summary>
        /// Cancels the active grapple.
        /// </summary>
        /// <param name="fromCollision">If true, ignored during grace period (avoids cancel on first-frame contact).</param>
        public void CancelGrapple(bool fromCollision = false) {
            if(!IsGrappling) return;

            var elapsed = Time.time - _grappleStartTime;
            if(fromCollision && elapsed < GrappleEstablishGrace)
                return; // Defer collision cancel until mesh can show

            EndGrapple(true);
        }

        #endregion

        #region Private Methods - Grapple Logic

        private void StartGrapple(Vector3 targetPoint) {
            
            // Cancel any active slide - grapple takes full control
            if(playerController != null && playerController.MovementController != null && playerController.MovementController.IsSliding) {
                playerController.MovementController.CancelSlideForJump();
            }
            
            UpdateGrappleServerRpc(true, targetPoint);
            IsGrappling = true;
            _grapplePoint = targetPoint;
            _grappleStartTime = Time.time;

            if(_grappleMeshObject == null) {
                SetupGrappleLine();
            }

            // Defer mesh until animation event fires (avoids flash from hand still in idle pose)
            if(_grappleMeshRenderer != null) {
                _pendingGrappleMeshEnable = true;
                StartCoroutine(GrappleMeshEnableFailsafe());
            } else {
                Debug.LogError("[GrappleController] Grapple started but grapple mesh is null!");
            }

            if(_audioRelay != null && IsOwner) {
                _audioRelay.RequestPlayAttached("gameplay.grapple", new NetworkObjectReference(playerController.NetworkObject),
                    allowOverlap: true);
            }

            // Publish grapple started event
            EventBus.Publish(new GrappleStartedEvent(targetPoint));
            
            if (IsOwner && ProgressionManager.Instance != null) {
                ProgressionManager.Instance.RecordGrappleUsed();
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
            var directionToPoint = (_grapplePoint - playerController.Position).normalized;
            var distanceToPoint = Vector3.Distance(playerController.Position, _grapplePoint);

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

            // Check for walls in the direction we're moving (defer until after grace - avoids hitting grapple target on frame 0)
            var pullVelocity = directionToPoint * GrappleSpeed;
            if(elapsed >= GrappleEstablishGrace) {
                var checkDistance = pullVelocity.magnitude * Time.deltaTime * 3f;
                if(Physics.SphereCast(playerController.Position, _characterController.radius, directionToPoint, out _,
                       checkDistance, ~_playerLayer)) {
                    EndGrapple(true);
                    return;
                }
            }

            // Apply movement
            _characterController.Move(pullVelocity * Time.deltaTime);
        }

        private void EndGrapple(bool applyMomentum) {
            // Publish grapple ended event
            EventBus.Publish(new GrappleEndedEvent());
            IsGrappling = false;

            // Mesh hide: only via HideGrapple anim event (anim authority for both show and hide)
            UpdateGrappleServerRpc(false, Vector3.zero);

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
                    
                    // Try to initiate slide if grounded and crouching at speed
                    if(playerController.MovementController != null) {
                        playerController.MovementController.TryInitiateSlideFromGrapple();
                    }
                }
            }

            // Start cooldown
            StartCoroutine(StartGrappleCooldown());
        }

        private IEnumerator StartGrappleCooldown() {
            CanGrapple = false;
            _cooldownStartTime = Time.time;
            var currentCooldown = GetCurrentCooldown();
            yield return new WaitForSeconds(currentCooldown);
            CanGrapple = true;
        }

        private void OnGrappleAnimFirstFrame(GrappleAnimFirstFrameEvent _) {
            TryEnablePendingGrappleMesh();
        }

        private void OnGrappleAnimHide(GrappleAnimHideEvent _) {
            if(_grappleMeshRenderer != null && _grappleMeshRenderer.enabled) {
                _grappleMeshRenderer.enabled = false;
            }
        }

        private void TryEnablePendingGrappleMesh() {
            if(!_pendingGrappleMeshEnable) return;

            // Show mesh when anim says to; don't require IsGrappling (cancelled grapples still play anim).
            _pendingGrappleMeshEnable = false;
            if(_grappleMeshRenderer != null) {
                _grappleMeshRenderer.enabled = true;
                if(_fpCamera != null) {
                    UpdateGrappleMesh(GetFpGrappleOriginPosition(), _grapplePoint);
                }
            }
        }

        private IEnumerator GrappleMeshEnableFailsafe() {
            yield return new WaitForSeconds(0.15f);
            TryEnablePendingGrappleMesh();
        }

        private void UpdateGrappleLine() {
            if(_grappleMeshObject == null) {
                SetupGrappleLine();
            }

            if(_grappleMeshRenderer == null || !_grappleMeshRenderer.enabled) return;

            if(_fpCamera == null) {
                Debug.LogError("[GrappleController] UpdateGrappleLine: _fpCamera == null!");
                return;
            }

            // Update mesh positions (from hand/weapon to grapple point)
            UpdateGrappleMesh(GetFpGrappleOriginPosition(), _grapplePoint);
        }

        private Vector3 GetFpGrappleOriginPosition() {
            var fpWeapon = playerController.WeaponManager != null ? playerController.WeaponManager.GetCurrentFpWeapon() : null;

            var driver = fpWeapon != null ? fpWeapon.GetComponent<KinemationFpWeaponDriver>() : null;

            var handTransform = driver != null ? driver.GetGrappleOriginFpTransform() : null;

            if(handTransform != null)
                return handTransform.position;

            // Fallback: camera offset when FP hand bone unavailable (no weapon, hopball, etc.)
            var cam = playerController.FpCameraTransform;
            return cam.position - cam.right * 0.3f - cam.up * 0.2f;
        }

        #endregion
    }
}
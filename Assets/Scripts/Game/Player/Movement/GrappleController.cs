using System.Collections;
using Diagnostics;
using Events;
using Game.Audio.System;
using Game.Player.Contracts;
using Game.Weapon.Kinemation;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Movement {
    public class GrappleController : NetworkBehaviour {
        [Header("Components")]
        [HideInInspector, SerializeField] private MonoBehaviour playerContextSource;

        private IPlayerMovementContext _playerContext;
        private PlayerMovementController _movementController;

        private CinemachineCamera _fpCamera;
        private CharacterController _characterController;
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
        private const float GrappleMeshMinimumVisibleSeconds = 0.08f;
        private const float GrappleAnimHideFailsafeDelay = 0.35f;

        [Header("Momentum Settings")]
        private const bool PreserveMomentum = true;

        private const float MomentumBoost = 1.2f; // Multiplier for final velocity
        private const float UpwardReleaseDirectionThreshold = 0.05f;

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
        private float _cooldownDuration = GrappleCooldown;
        private float _cooldownEndTime;

        // Mesh system fields
        private GameObject _grappleMeshObject;
        private MeshFilter _grappleMeshFilter;
        private MeshRenderer _grappleMeshRenderer;
        private Mesh _grappleMesh;
        private Vector3[] _meshVertices;
        private Vector2[] _meshUvs;
        private Vector3[] _meshNormals;
        private int[] _meshTriangles;
        private int _meshBufferSegments = -1;
        private bool _pendingGrappleMeshEnable; // Defer mesh until grapple anim first frame
        private bool _useAnimatedFirstPersonGrappleVisuals = true;
        private bool _forceCameraOffsetOriginForCurrentCable;
        private float _grappleMeshFirstShownTime = -1f;
        private Coroutine _grappleMeshHideCoroutine;
        private Coroutine _grappleMeshEnableFailsafeCoroutine;
        private Coroutine _grappleMeshAnimatedHideFailsafeCoroutine;

        #endregion

        #region Properties

        public bool IsGrappling { get; private set; }

        public bool CanGrapple { get; private set; } = true;

        private Vector3 CurrentPullVelocity {
            get {
                if(!IsGrappling || _playerContext == null) {
                    return Vector3.zero;
                }

                var toPoint = _grapplePoint - _playerContext.Position;
                if(toPoint.sqrMagnitude <= 0.0001f) {
                    return Vector3.zero;
                }

                return toPoint.normalized * GrappleSpeed;
            }
        }

        public Vector3 CurrentHorizontalPullVelocity {
            get {
                var pullVelocity = CurrentPullVelocity;
                pullVelocity.y = 0f;
                return pullVelocity;
            }
        }

        public float CooldownProgress {
            get {
                if(CanGrapple) return 1f;
                var elapsed = Time.time - _cooldownStartTime;
                return Mathf.Clamp01(elapsed / Mathf.Max(_cooldownDuration, 0.0001f));
            }
        }

        /// <summary>
        /// Gets the current grapple cooldown based on whether the player is tagged in Gun Tag mode.
        /// </summary>
        private float GetCurrentCooldown() {
            return _playerContext is { IsGunTagMode: true, IsTagged: true } ? TaggedPlayerCooldown : GrappleCooldown;
        }
        
        /// <summary>
        /// Gets the grappleable layers for external use (e.g., AI bots for raycasting).
        /// </summary>
        private LayerMask GrappleableLayers { get; set; }

        #endregion

        private readonly NetworkVariable<bool> _netIsGrappling = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<Vector3> _netGrapplePoint = new(Vector3.zero,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        // Throttling for network updates (at 90Hz: 3 ticks = ~33ms)
        private float _lastGrappleUpdateTime;
        private const float GrappleUpdateInterval = 0.033f; // ~3 ticks at 90Hz
        private const float JumpPadAnchorProbeRadius = 0.75f;
        private readonly Collider[] _jumpPadAnchorProbeResults = new Collider[8];

        #region Unity Lifecycle

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(!PlayerContractResolver.TryResolve(this, ref playerContextSource, out _playerContext)) {
                DevLog.LogError("[GrappleController] IPlayerMovementContext not found!");
                enabled = false;
                return;
            }

            if(_fpCamera == null) {
                _fpCamera = _playerContext.FpCamera;
            }

            if(_characterController == null) {
                _characterController = _playerContext.CharacterController;
            }

            if(_audioRelay == null) {
                _audioRelay = _playerContext.AudioRelay;
            }

            if(_movementController == null) {
                _movementController = GetComponent<PlayerMovementController>();
            }

            _playerLayer = _playerContext.PlayerLayer;
            GrappleableLayers = _playerContext.WorldLayer;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            if(_grappleMeshObject == null) {
                SetupGrappleLine();
            }

            _netIsGrappling.OnValueChanged += OnGrappleStateChanged;
            _netGrapplePoint.OnValueChanged += OnGrapplePointChanged;

            if(IsOwner) {
                EventBus.Unsubscribe<PlayerTagStateChangedEvent>(OnPlayerTagStateChanged);
                EventBus.Subscribe<GrappleAnimFirstFrameEvent>(OnGrappleAnimFirstFrame);
                EventBus.Subscribe<GrappleAnimHideEvent>(OnGrappleAnimHide);
                EventBus.Subscribe<PlayerTagStateChangedEvent>(OnPlayerTagStateChanged);
            }

            // Apply initial state
            if(!IsOwner) {
                UpdateGrappleVisuals(_netIsGrappling.Value, _netGrapplePoint.Value);
            }
        }

        public override void OnNetworkDespawn() {
            _netIsGrappling.OnValueChanged -= OnGrappleStateChanged;
            _netGrapplePoint.OnValueChanged -= OnGrapplePointChanged;

            if(IsOwner) {
                EventBus.Unsubscribe<GrappleAnimFirstFrameEvent>(OnGrappleAnimFirstFrame);
                EventBus.Unsubscribe<GrappleAnimHideEvent>(OnGrappleAnimHide);
                EventBus.Unsubscribe<PlayerTagStateChangedEvent>(OnPlayerTagStateChanged);
            }

            if(_grappleMeshHideCoroutine != null) {
                StopCoroutine(_grappleMeshHideCoroutine);
                _grappleMeshHideCoroutine = null;
            }
            if(_grappleMeshEnableFailsafeCoroutine != null) {
                StopCoroutine(_grappleMeshEnableFailsafeCoroutine);
                _grappleMeshEnableFailsafeCoroutine = null;
            }
            if(_grappleMeshAnimatedHideFailsafeCoroutine != null) {
                StopCoroutine(_grappleMeshAnimatedHideFailsafeCoroutine);
                _grappleMeshAnimatedHideFailsafeCoroutine = null;
            }

            base.OnNetworkDespawn();
        }

        private void Start() {
            if(_grappleMeshObject == null) {
                SetupGrappleLine();
            }
        }

        public override void OnDestroy() {
            _netIsGrappling.OnValueChanged -= OnGrappleStateChanged;
            _netGrapplePoint.OnValueChanged -= OnGrapplePointChanged;

            if(IsOwner) {
                EventBus.Unsubscribe<GrappleAnimFirstFrameEvent>(OnGrappleAnimFirstFrame);
                EventBus.Unsubscribe<GrappleAnimHideEvent>(OnGrappleAnimHide);
                EventBus.Unsubscribe<PlayerTagStateChangedEvent>(OnPlayerTagStateChanged);
            }

            if(_grappleMeshHideCoroutine != null) {
                StopCoroutine(_grappleMeshHideCoroutine);
                _grappleMeshHideCoroutine = null;
            }
            if(_grappleMeshEnableFailsafeCoroutine != null) {
                StopCoroutine(_grappleMeshEnableFailsafeCoroutine);
                _grappleMeshEnableFailsafeCoroutine = null;
            }
            if(_grappleMeshAnimatedHideFailsafeCoroutine != null) {
                StopCoroutine(_grappleMeshAnimatedHideFailsafeCoroutine);
                _grappleMeshAnimatedHideFailsafeCoroutine = null;
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
                    if(_grappleMeshRenderer == null) {
                        SetupGrappleLine();
                    }
                    if(_grappleMeshRenderer == null || grappleOriginTp == null) return;
                    UpdateGrappleMesh(grappleOriginTp.position, _netGrapplePoint.Value);
                    return;
                }
                case false:
                    return;
            }

            UpdateCooldownState();

            if(IsGrappling) {
                UpdateGrapple();
            }

            UpdateGrappleLine();
        }

        #endregion

        private void PublishGrappleState(bool isGrappling, Vector3 grapplePoint) {
            if(!IsOwner) return;

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
            if(_grappleMeshRenderer == null) {
                SetupGrappleLine();
            }
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
                _grappleMesh.MarkDynamic();
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

        private void EnsureMeshBuffers(int segments) {
            if(_meshBufferSegments == segments &&
               _meshVertices != null &&
               _meshUvs != null &&
               _meshNormals != null &&
               _meshTriangles != null) {
                return;
            }

            _meshBufferSegments = segments;
            _meshVertices = new Vector3[segments * 2];
            _meshUvs = new Vector2[segments * 2];
            _meshNormals = new Vector3[segments * 2];
            _meshTriangles = new int[segments * 6];

            var triIndex = 0;
            for(var i = 0; i < segments; i++) {
                var next = (i + 1) % segments;

                // First triangle
                _meshTriangles[triIndex++] = i;
                _meshTriangles[triIndex++] = i + segments;
                _meshTriangles[triIndex++] = next;

                // Second triangle
                _meshTriangles[triIndex++] = next;
                _meshTriangles[triIndex++] = i + segments;
                _meshTriangles[triIndex++] = next + segments;
            }
        }

        private void UpdateGrappleMesh(Vector3 startPos, Vector3 endPos) {
            if(_grappleMesh == null || _grappleMeshFilter == null) return;

            var segments = Mathf.Max(3, meshSegments);
            EnsureMeshBuffers(segments);

            var direction = (endPos - startPos).normalized;
            var distance = Vector3.Distance(startPos, endPos);

            // Calculate perpendicular vectors for cylinder cross-section
            var right = Vector3.Cross(direction, Vector3.up);
            if(right.magnitude < 0.1f) {
                right = Vector3.Cross(direction, Vector3.forward);
            }

            right.Normalize();
            var up = Vector3.Cross(right, direction).normalized;

            // Generate vertices for start and end circles
            for(var i = 0; i < segments; i++) {
                var angle = i / (float)segments * Mathf.PI * 2f;
                var offset = right * (Mathf.Cos(angle) * meshRadius) + up * (Mathf.Sin(angle) * meshRadius);

                // Start circle
                _meshVertices[i] = startPos + offset;
                _meshUvs[i] = new Vector2(i / (float)segments, 0f);
                _meshNormals[i] = offset.normalized;

                // End circle
                _meshVertices[i + segments] = endPos + offset;
                _meshUvs[i + segments] = new Vector2(i / (float)segments, distance);
                _meshNormals[i + segments] = offset.normalized;
            }

            // Update mesh
            _grappleMesh.Clear();
            _grappleMesh.vertices = _meshVertices;
            _grappleMesh.triangles = _meshTriangles;
            _grappleMesh.uv = _meshUvs;
            _grappleMesh.normals = _meshNormals;
            _grappleMesh.RecalculateBounds();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Attempts to start a grapple if looking at a grappleable surface.
        /// </summary>
        public void TryGrapple() {
            if(!IsOwner) return;
            if(!CanGrapple || IsGrappling) return;

            var ray = new Ray(_playerContext.FpCameraTransform.position, _playerContext.FpCameraTransform.forward);

            if(Physics.Raycast(ray, out var hit, MaxGrappleDistance, GrappleableLayers)) {
                StartGrapple(hit.point);
            }
        }

        /// <summary>
        /// Cancels the active grapple.
        /// </summary>
        /// <param name="fromCollision">If true, ignored during grace period (avoids cancel on first-frame contact).</param>
        /// <param name="forJumpPadLaunch">
        /// True only when cancelling because the player hit a jump pad while grappling during an active jump-pad launch chain.
        /// Enables launch compensation behavior on this grapple end.
        /// </param>
        public void CancelGrapple(bool fromCollision = false, bool forJumpPadLaunch = false) {
            if(!IsGrappling) return;

            var elapsed = Time.time - _grappleStartTime;
            if(fromCollision && elapsed < GrappleEstablishGrace)
                return; // Defer collision cancel until mesh can show

            EndGrapple(true, applyJumpPadLaunchCompensation: forJumpPadLaunch);
        }

        #endregion

        #region Private Methods - Grapple Logic

        private void StartGrapple(Vector3 targetPoint) {
            var isReloading = IsOwner &&
                              _playerContext?.CurrentWeapon != null &&
                              _playerContext.CurrentWeapon.IsReloadInProgress;
            var isHoldingHopball = IsOwner &&
                                   _playerContext is { IsHoldingHopball: true };
            var useFallbackFirstPersonGrappleVisuals = isReloading || isHoldingHopball;

            _useAnimatedFirstPersonGrappleVisuals = !useFallbackFirstPersonGrappleVisuals;
            _forceCameraOffsetOriginForCurrentCable = useFallbackFirstPersonGrappleVisuals;

            // Cancel any active slide - grapple takes full control
            if(_movementController != null && _movementController.IsSliding) {
                _movementController.CancelSlideForJump();
            }

            PublishGrappleState(true, targetPoint);
            IsGrappling = true;
            _grapplePoint = targetPoint;
            _grappleStartTime = Time.time;
            _grappleMeshFirstShownTime = -1f;
            if(_grappleMeshHideCoroutine != null) {
                StopCoroutine(_grappleMeshHideCoroutine);
                _grappleMeshHideCoroutine = null;
            }
            if(_grappleMeshEnableFailsafeCoroutine != null) {
                StopCoroutine(_grappleMeshEnableFailsafeCoroutine);
                _grappleMeshEnableFailsafeCoroutine = null;
            }
            if(_grappleMeshAnimatedHideFailsafeCoroutine != null) {
                StopCoroutine(_grappleMeshAnimatedHideFailsafeCoroutine);
                _grappleMeshAnimatedHideFailsafeCoroutine = null;
            }

            if(_grappleMeshObject == null) {
                SetupGrappleLine();
            }

            // Use anim-timed cable show for normal grapples; instant show for reload-conflict fallback visuals.
            if(_grappleMeshRenderer != null) {
                _pendingGrappleMeshEnable = _useAnimatedFirstPersonGrappleVisuals;
                if(_useAnimatedFirstPersonGrappleVisuals) {
                    _grappleMeshEnableFailsafeCoroutine = StartCoroutine(GrappleMeshEnableFailsafe());
                } else {
                    ShowGrappleMeshNow();
                }
            } else {
                DevLog.LogError("[GrappleController] Grapple started but grapple mesh is null!");
            }

            if(_audioRelay != null && IsOwner) {
                if(_playerContext != null)
                    _audioRelay.RequestPlayAttached("gameplay.grapple",
                        new NetworkObjectReference(_playerContext.NetworkObject),
                        allowOverlap: true);
            }

            // Publish grapple started event
            EventBus.Publish(new GrappleStartedEvent(targetPoint, _useAnimatedFirstPersonGrappleVisuals));
            
            if(IsOwner) {
                EventBus.Publish(new PlayerGrappleUsedProgressionEvent(OwnerClientId));
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
            var directionToPoint = (_grapplePoint - _playerContext.Position).normalized;
            var distanceToPoint = Vector3.Distance(_playerContext.Position, _grapplePoint);

            // If we're very close, end the grapple
            if(distanceToPoint < 1f) {
                if(TryHandleJumpPadAnchor()) {
                    return;
                }

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
                if(Physics.SphereCast(_playerContext.Position, _characterController.radius, directionToPoint, out var sphereHit,
                       checkDistance, ~_playerLayer)) {
                    if(IsJumpPadCollider(sphereHit.collider, out var isMegaPad)) {
                        HandleJumpPadSweepHit(sphereHit, isMegaPad);
                        return;
                    }

                    EndGrapple(true);
                    return;
                }
            }

            // Apply movement
            var moveDelta = pullVelocity * Time.deltaTime;
            _characterController.Move(moveDelta);
        }

        private void EndGrapple(bool applyMomentum, bool applyJumpPadLaunchCompensation = false) {
            var movementController = _movementController;
            var toAnchor = _grapplePoint - transform.position;
            var distanceToAnchor = toAnchor.magnitude;
            var directionToAnchor = distanceToAnchor > 0.0001f ? toAnchor / distanceToAnchor : Vector3.zero;
            var allowUpwardReleaseMomentum = directionToAnchor.y > UpwardReleaseDirectionThreshold;

            // Prevent stale upward velocity from resurfacing after downward/flat grapples.
            if(movementController != null && !allowUpwardReleaseMomentum && movementController.VerticalVelocity > 0f) {
                movementController.VerticalVelocity = 0f;
            }

            // Publish grapple ended event
            EventBus.Publish(new GrappleEndedEvent());
            IsGrappling = false;

            // Guarantee cable visibility at least once for initiated grapples, even if cancellation is immediate.
            if(_pendingGrappleMeshEnable) {
                TryEnablePendingGrappleMesh();
            }

            // Mesh hide remains animation-authoritative for animated path, but fallback path hides after minimum visibility.
            if(_useAnimatedFirstPersonGrappleVisuals) {
                ScheduleAnimatedHideFailsafe();
            } else {
                RequestHideGrappleMesh();
            }
            PublishGrappleState(false, Vector3.zero);
            _useAnimatedFirstPersonGrappleVisuals = true;

            if(applyMomentum && PreserveMomentum) {
                // Calculate final momentum direction
                var finalVelocity = GrappleSpeed * MomentumBoost * directionToAnchor;

                // Apply momentum to FpController
                if(_movementController != null) {
                    var ownerMovementController = _movementController;
                    if(ownerMovementController == null) {
                        return;
                    }

                    // Set horizontal velocity (preserve some existing momentum)
                    var horizontalVelocity = new Vector3(finalVelocity.x, 0f, finalVelocity.z);
                    ownerMovementController.SetVelocity(horizontalVelocity);

                    // During JP handoff, suppress grapple upward boost so pad launch fully owns vertical.
                    // For downward/flat release directions, only horizontal carry is allowed.
                    var addVerticalImpulse = finalVelocity.y > 0f &&
                                             allowUpwardReleaseMomentum &&
                                             !applyJumpPadLaunchCompensation;
                    if(addVerticalImpulse) {
                        ownerMovementController.AddVerticalVelocity(finalVelocity.y);
                    }

                    // Try to initiate slide if grounded and crouching at speed
                    ownerMovementController.TryInitiateSlideFromGrapple();
                }
            }

            // Start cooldown
            StartGrappleCooldown();
        }

        private static bool IsJumpPadCollider(Collider collider, out bool isMegaPad) {
            isMegaPad = false;
            if(collider == null) return false;

            if(collider.CompareTag("JumpPad")) {
                return true;
            }

            if(!collider.CompareTag("MegaPad")) return false;
            isMegaPad = true;
            return true;
        }

        private void HandleJumpPadSweepHit(RaycastHit sphereHit, bool isMegaPad) {
            HandleJumpPadHandoff(sphereHit.collider, isMegaPad);
        }

        private bool TryHandleJumpPadAnchor() {
            var hitCount = Physics.OverlapSphereNonAlloc(_grapplePoint,
                JumpPadAnchorProbeRadius,
                _jumpPadAnchorProbeResults,
                ~_playerLayer,
                QueryTriggerInteraction.Ignore);
            if(hitCount <= 0) {
                return false;
            }

            var bestCollider = default(Collider);
            var bestIsMegaPad = false;
            var bestDistanceSqr = float.MaxValue;
            for(var i = 0; i < hitCount; i++) {
                var candidate = _jumpPadAnchorProbeResults[i];
                _jumpPadAnchorProbeResults[i] = null;
                if(!IsJumpPadCollider(candidate, out var candidateIsMegaPad)) {
                    continue;
                }

                var nearestPoint = candidate.ClosestPoint(_grapplePoint);
                var distanceSqr = (nearestPoint - _grapplePoint).sqrMagnitude;
                if(!(distanceSqr < bestDistanceSqr)) {
                    continue;
                }

                bestDistanceSqr = distanceSqr;
                bestCollider = candidate;
                bestIsMegaPad = candidateIsMegaPad;
            }

            if(bestCollider == null) {
                return false;
            }

            HandleJumpPadHandoff(bestCollider, bestIsMegaPad);
            return true;
        }

        /// <summary>Handles jump pad handoff from the given collider.</summary>
        private void HandleJumpPadHandoff(Collider padCollider, bool isMegaPad) {
            if(_movementController == null) {
                EndGrapple(true);
                return;
            }

            var movementController = _movementController;
            if(movementController == null) {
                EndGrapple(true);
                return;
            }

            var applyJumpPadLaunchCompensation = movementController.IsInJumpPadLaunch;
            EndGrapple(true, applyJumpPadLaunchCompensation: applyJumpPadLaunchCompensation);

            movementController.CancelMantleForJumpPad();

            var padNormal = padCollider != null ? padCollider.transform.up : Vector3.up;
            var launchForce = isMegaPad ? 30f : 15f;
            movementController.LaunchFromJumpPad(padNormal,
                force: launchForce,
                ignoreGroundedRequirement: true);
        }

        private void StartGrappleCooldown() {
            CanGrapple = false;
            _cooldownStartTime = Time.time;
            _cooldownDuration = GetCurrentCooldown();
            _cooldownEndTime = _cooldownStartTime + _cooldownDuration;
        }

        private void UpdateCooldownState() {
            if(CanGrapple) return;
            if(Time.time < _cooldownEndTime) return;
            CanGrapple = true;
        }

        private void OnPlayerTagStateChanged(PlayerTagStateChangedEvent evt) {
            if(evt == null || _playerContext == null || evt.PlayerId != OwnerClientId) return;
            if(CanGrapple) return;
            // Snap progress/remaining time to current rules when tagged state changes mid-cooldown.
            _cooldownDuration = GetCurrentCooldown();
            _cooldownEndTime = _cooldownStartTime + _cooldownDuration;
            if(Time.time >= _cooldownEndTime) {
                CanGrapple = true;
            }
        }

        private void OnGrappleAnimFirstFrame(GrappleAnimFirstFrameEvent _) {
            TryEnablePendingGrappleMesh();
        }

        private void OnGrappleAnimHide(GrappleAnimHideEvent _) {
            if(_grappleMeshAnimatedHideFailsafeCoroutine != null) {
                StopCoroutine(_grappleMeshAnimatedHideFailsafeCoroutine);
                _grappleMeshAnimatedHideFailsafeCoroutine = null;
            }
            RequestHideGrappleMesh();
        }

        private void TryEnablePendingGrappleMesh() {
            if(!_pendingGrappleMeshEnable) return;

            // Show mesh when anim says to; don't require IsGrappling (cancelled grapples still play anim).
            _pendingGrappleMeshEnable = false;
            ShowGrappleMeshNow();
        }

        private IEnumerator GrappleMeshEnableFailsafe() {
            yield return new WaitForSeconds(0.15f);
            _grappleMeshEnableFailsafeCoroutine = null;
            TryEnablePendingGrappleMesh();
        }

        private void ScheduleAnimatedHideFailsafe() {
            if(_grappleMeshAnimatedHideFailsafeCoroutine != null) {
                StopCoroutine(_grappleMeshAnimatedHideFailsafeCoroutine);
            }

            _grappleMeshAnimatedHideFailsafeCoroutine = StartCoroutine(AnimatedHideFailsafeCoroutine());
        }

        private IEnumerator AnimatedHideFailsafeCoroutine() {
            yield return new WaitForSeconds(GrappleAnimHideFailsafeDelay);
            _grappleMeshAnimatedHideFailsafeCoroutine = null;
            if(IsGrappling) yield break;
            RequestHideGrappleMesh();
        }

        private void ShowGrappleMeshNow() {
            if(_grappleMeshRenderer == null) return;

            if(!_grappleMeshRenderer.enabled) {
                _grappleMeshRenderer.enabled = true;
            }

            if(_grappleMeshFirstShownTime < 0f) {
                _grappleMeshFirstShownTime = Time.time;
            }

            if(_fpCamera != null) {
                UpdateGrappleMesh(GetFpGrappleOriginPosition(), _grapplePoint);
            }
        }

        private void RequestHideGrappleMesh() {
            if(_grappleMeshRenderer == null) return;

            if(_pendingGrappleMeshEnable) {
                TryEnablePendingGrappleMesh();
            }

            if(!_grappleMeshRenderer.enabled) return;

            var elapsedVisible = _grappleMeshFirstShownTime >= 0f ? Time.time - _grappleMeshFirstShownTime : float.MaxValue;
            var remainingVisible = GrappleMeshMinimumVisibleSeconds - elapsedVisible;
            if(remainingVisible <= 0f) {
                HideGrappleMeshImmediately();
                return;
            }

            if(_grappleMeshHideCoroutine != null) {
                StopCoroutine(_grappleMeshHideCoroutine);
            }

            _grappleMeshHideCoroutine = StartCoroutine(HideGrappleMeshAfterDelay(remainingVisible));
        }

        private IEnumerator HideGrappleMeshAfterDelay(float delay) {
            yield return new WaitForSeconds(delay);
            _grappleMeshHideCoroutine = null;
            HideGrappleMeshImmediately();
        }

        private void HideGrappleMeshImmediately() {
            if(_grappleMeshRenderer != null && _grappleMeshRenderer.enabled) {
                _grappleMeshRenderer.enabled = false;
            }
            _pendingGrappleMeshEnable = false;
            _forceCameraOffsetOriginForCurrentCable = false;
            _grappleMeshFirstShownTime = -1f;
        }

        private void UpdateGrappleLine() {
            if(_grappleMeshObject == null) {
                SetupGrappleLine();
            }

            if(_grappleMeshRenderer == null || !_grappleMeshRenderer.enabled) return;

            if(_fpCamera == null) {
                DevLog.LogError("[GrappleController] UpdateGrappleLine: _fpCamera == null!");
                return;
            }

            // Update mesh positions (from hand/weapon to grapple point)
            UpdateGrappleMesh(GetFpGrappleOriginPosition(), _grapplePoint);
        }

        private Vector3 GetFpGrappleOriginPosition() {
            if(IsOwner && _forceCameraOffsetOriginForCurrentCable) {
                var fallbackCam = _playerContext.FpCameraTransform;
                return fallbackCam.position - fallbackCam.right * 0.3f - fallbackCam.up * 0.2f;
            }

            var fpWeapon = _playerContext.GetCurrentFpWeapon();

            var driver = fpWeapon != null ? fpWeapon.GetComponent<KinFpWeaponDriver>() : null;

            var handTransform = driver != null ? driver.GetGrappleOriginFpTransform() : null;

            if(handTransform != null)
                return handTransform.position;

            // Fallback: camera offset when FP hand bone unavailable (no weapon, hopball, etc.)
            var cam = _playerContext.FpCameraTransform;
            return cam.position - cam.right * 0.3f - cam.up * 0.2f;
        }

        #endregion
    }
}


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
        [SerializeField] private bool useLegacyLineRenderer = false;

        [SerializeField] private float lineWidth = 0.05f;
        [SerializeField] private Color grappleColor = new(0.2f, 0.8f, 1f);
        [SerializeField] private Material lineMaterial;

        [Header("Mesh Settings (when not using legacy)")]
        [SerializeField] private int meshSegments = 8;

        [SerializeField] private float meshRadius = 0.02f;

        #region Private Fields

        private Vector3 _grapplePoint;
        private float _grappleStartTime;
        private Vector3 _grappleStartPosition;
        private float _cooldownStartTime;

        // Mesh system fields (only used when not using legacy LineRenderer)
        private GameObject _grappleMeshObject;
        private MeshFilter _grappleMeshFilter;
        private MeshRenderer _grappleMeshRenderer;
        private Mesh _grappleMesh;

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
            
            if(_sfxRelay == null) {
                _sfxRelay = playerController.SfxRelay;
            }

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

        public override void OnDestroy() {
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
                    if(useLegacyLineRenderer) {
                        if(grappleLine == null) return;
                        grappleLine.SetPosition(0, grappleOriginTp.position);
                        grappleLine.SetPosition(1, _netGrapplePoint.Value);
                    } else {
                        if(_grappleMeshRenderer == null || grappleOriginTp == null) return;
                        UpdateGrappleMesh(grappleOriginTp.position, _netGrapplePoint.Value);
                    }

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
            if(useLegacyLineRenderer) {
                if(grappleLine == null) return;
                grappleLine.enabled = isGrappling;
                if(!isGrappling) return;
                grappleLine.SetPosition(0, _grappleStartPosition);
                grappleLine.SetPosition(1, targetPoint);
            } else {
                if(_grappleMeshRenderer == null) return;
                _grappleMeshRenderer.enabled = isGrappling;
                if(!isGrappling) return;
                if(grappleOriginTp != null) {
                    UpdateGrappleMesh(grappleOriginTp.position, targetPoint);
                }
            }
        }

        #region Setup

        private void SetupGrappleLine() {
            if(useLegacyLineRenderer) {
                SetupLegacyLineRenderer();
            } else {
                SetupGrappleMesh();
            }
        }

        private void SetupLegacyLineRenderer() {
            if(grappleLine == null) {
                var lineObj = new GameObject("GrappleLine");
                lineObj.transform.SetParent(transform);
                grappleLine = lineObj.AddComponent<LineRenderer>();
                Debug.Log("[GrappleController] Created new LineRenderer GameObject");
            } else {
                Debug.Log("[GrappleController] Using existing LineRenderer from inspector");
            }

            grappleLine.startWidth = lineWidth;
            grappleLine.endWidth = lineWidth;
            grappleLine.positionCount = 2;
            grappleLine.useWorldSpace = true;
            grappleLine.enabled = false;

            // Setup material
            grappleLine.material = lineMaterial ?? new Material(Shader.Find("Sprites/Default"));
            var materialName = grappleLine.sharedMaterial != null
                ? grappleLine.sharedMaterial.name
                : (grappleLine.material != null ? grappleLine.material.name : "null");
            Debug.Log(
                $"[GrappleController] SetupLegacyLineRenderer - Material: {materialName}, Width: {lineWidth}, Color: {grappleColor}");

            grappleLine.startColor = grappleColor;
            grappleLine.endColor = grappleColor;
        }

        private void SetupGrappleMesh() {
            // Create mesh object if it doesn't exist
            if(_grappleMeshObject == null) {
                _grappleMeshObject = new GameObject("GrappleCable");
                // Don't parent to transform - mesh vertices are in world space
                _grappleMeshObject.transform.SetParent(null);

                _grappleMeshFilter = _grappleMeshObject.AddComponent<MeshFilter>();
                _grappleMeshRenderer = _grappleMeshObject.AddComponent<MeshRenderer>();

                // Create the mesh
                _grappleMesh = new Mesh();
                _grappleMesh.name = "GrappleCableMesh";
                _grappleMeshFilter.mesh = _grappleMesh;

                Debug.Log("[GrappleController] Created new grapple mesh GameObject");
            } else {
                Debug.Log("[GrappleController] Using existing grapple mesh from inspector");
                // Ensure it's not parented if it was assigned in inspector
                if(_grappleMeshObject.transform.parent != null) {
                    _grappleMeshObject.transform.SetParent(null);
                }
            }

            // Set material (supports complex materials with normals, AO, height, etc.)
            if(lineMaterial != null) {
                _grappleMeshRenderer.material = lineMaterial;
            } else {
                // Fallback to a simple material if none assigned
                _grappleMeshRenderer.material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                _grappleMeshRenderer.material.color = grappleColor;
            }

            _grappleMeshRenderer.enabled = false;

            var materialName = _grappleMeshRenderer.sharedMaterial != null
                ? _grappleMeshRenderer.sharedMaterial.name
                : "null";
            Debug.Log(
                $"[GrappleController] SetupGrappleMesh - Material: {materialName}, Radius: {meshRadius}, Segments: {meshSegments}");
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
                var angle = (i / (float)meshSegments) * Mathf.PI * 2f;
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

            if(useLegacyLineRenderer) {
                if(grappleLine == null) {
                    SetupGrappleLine();
                }
            } else {
                if(_grappleMeshObject == null) {
                    SetupGrappleLine();
                }
            }

            // Enable visual
            if(useLegacyLineRenderer) {
                if(grappleLine != null) {
                    grappleLine.enabled = true;
                    // Set initial positions immediately
                    if(_fpCamera != null) {
                        var handPosition = _fpCamera.transform.position - _fpCamera.transform.right * 0.3f -
                                           _fpCamera.transform.up * 0.2f;
                        grappleLine.SetPosition(0, handPosition);
                        grappleLine.SetPosition(1, _grapplePoint);
                    }

                    var matName = grappleLine.sharedMaterial != null
                        ? grappleLine.sharedMaterial.name
                        : (grappleLine.material != null ? grappleLine.material.name : "null");
                    Debug.Log(
                        $"[GrappleController] Grapple started - Line enabled: {grappleLine.enabled}, Material: {matName}, Position 0: {grappleLine.GetPosition(0)}, Position 1: {grappleLine.GetPosition(1)}");
                } else {
                    Debug.LogError("[GrappleController] Grapple started but grappleLine == null!");
                }
            } else {
                if(_grappleMeshRenderer != null) {
                    _grappleMeshRenderer.enabled = true;
                    // Set initial mesh
                    if(_fpCamera != null) {
                        var handPosition = _fpCamera.transform.position - _fpCamera.transform.right * 0.3f -
                                           _fpCamera.transform.up * 0.2f;
                        UpdateGrappleMesh(handPosition, _grapplePoint);
                    }

                    var matName = _grappleMeshRenderer.sharedMaterial != null
                        ? _grappleMeshRenderer.sharedMaterial.name
                        : "null";
                    Debug.Log(
                        $"[GrappleController] Grapple started - Mesh enabled: {_grappleMeshRenderer.enabled}, Material: {matName}");
                } else {
                    Debug.LogError("[GrappleController] Grapple started but grapple mesh is null!");
                }
            }

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
            if(useLegacyLineRenderer) {
                if(grappleLine != null) {
                    grappleLine.enabled = false;
                }
            } else {
                if(_grappleMeshRenderer != null) {
                    _grappleMeshRenderer.enabled = false;
                }
            }
        }

        private IEnumerator StartGrappleCooldown() {
            CanGrapple = false;
            _cooldownStartTime = Time.time;
            var currentCooldown = GetCurrentCooldown();
            yield return new WaitForSeconds(currentCooldown);
            CanGrapple = true;
        }

        private void UpdateGrappleLine() {
            if(useLegacyLineRenderer) {
                if(grappleLine == null) {
                    SetupGrappleLine();
                }

                if(grappleLine == null) {
                    if(IsGrappling) {
                        Debug.LogWarning(
                            "[GrappleController] UpdateGrappleLine: grappleLine == null but IsGrappling is true!");
                    }

                    return;
                }

                if(!grappleLine.enabled) {
                    if(IsGrappling) {
                        Debug.LogWarning(
                            $"[GrappleController] UpdateGrappleLine: grappleLine.enabled is false but IsGrappling is true! Line was disabled unexpectedly.");
                    }

                    return;
                }

                if(_fpCamera == null) {
                    Debug.LogError("[GrappleController] UpdateGrappleLine: _fpCamera == null!");
                    return;
                }

                // Update line positions (from hand/weapon to grapple point)
                var handPosition = _fpCamera.transform.position - _fpCamera.transform.right * 0.3f -
                                   _fpCamera.transform.up * 0.2f;

                grappleLine.SetPosition(0, handPosition);
                grappleLine.SetPosition(1, _grapplePoint);

                // Debug first few frames to see if positions are being set
                if(Time.frameCount % 60 == 0 && IsGrappling) {
                    Debug.Log(
                        $"[GrappleController] Line update - Enabled: {grappleLine.enabled}, Pos0: {handPosition}, Pos1: {_grapplePoint}, Distance: {Vector3.Distance(handPosition, _grapplePoint)}");
                }
            } else {
                if(_grappleMeshObject == null) {
                    SetupGrappleLine();
                }

                if(_grappleMeshRenderer == null || !_grappleMeshRenderer.enabled) {
                    if(IsGrappling) {
                        Debug.LogWarning(
                            $"[GrappleController] UpdateGrappleLine: mesh renderer is disabled but IsGrappling is true!");
                    }

                    return;
                }

                if(_fpCamera == null) {
                    Debug.LogError("[GrappleController] UpdateGrappleLine: _fpCamera == null!");
                    return;
                }

                // Update mesh positions (from hand/weapon to grapple point)
                var handPosition = _fpCamera.transform.position - _fpCamera.transform.right * 0.3f -
                                   _fpCamera.transform.up * 0.2f;

                UpdateGrappleMesh(handPosition, _grapplePoint);
            }
        }

        #endregion
    }
}
using System.Collections;
using Cysharp.Threading.Tasks;
using Game.Weapons;
using Network;
using Network.Rpc;
using Network.Singletons;
using Unity.Cinemachine;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace Game.Player {
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerController : NetworkBehaviour {
        #region Constants

        private const float WalkSpeed = 5f;
        private const float SprintSpeed = 10f;
        private const float JumpHeight = 2f;
        private const float CrouchSpeed = 2.5f;
        private const float StandHeight = 1.7f;
        private const float CrouchHeight = 1.1f;
        private const float StandCollider = 1.9f;
        private const float CrouchCollider = 1.3f;
        private const float StandCheckHeight = StandCollider - CrouchCollider;
        private const float PitchLimit = 90f;
        private const float GravityScale = 3f;

        #endregion

        #region Animation Parameter Hashes

        private static readonly int MoveXHash = Animator.StringToHash("moveX");
        private static readonly int MoveYHash = Animator.StringToHash("moveY");
        private static readonly int LookXHash = Animator.StringToHash("lookX");
        private static readonly int IsSprintingHash = Animator.StringToHash("IsSprinting");
        private static readonly int IsCrouchingHash = Animator.StringToHash("IsCrouching");
        private static readonly int JumpTriggerHash = Animator.StringToHash("JumpTrigger");
        private static readonly int LandTriggerHash = Animator.StringToHash("LandTrigger");
        private static readonly int DamageTriggerHash = Animator.StringToHash("DamageTrigger");
        private static readonly int IsJumpingHash = Animator.StringToHash("IsJumping");
        private static readonly int IsFallingHash = Animator.StringToHash("IsFalling");
        private static readonly int IsGroundedHash = Animator.StringToHash("IsGrounded");

        #endregion

        #region Health Constants

        private const float RegenDelay = 3f;
        private const float RegenRate = 10f;
        private const float MaxHealth = 100f;

        #endregion

        #region Serialized Fields

        [Header("Movement Parameters")]
        [SerializeField] private float acceleration = 15f;

        [SerializeField] private float airAcceleration = 50f;
        [SerializeField] private float maxAirSpeed = 5f;
        [SerializeField] private float friction = 8f;

        [Header("Look Parameters")]
        [SerializeField] public Vector2 lookSensitivity;

        [Header("Components")]
        [SerializeField] private CinemachineCamera fpCamera;

        [SerializeField] private SwingGrapple swingGrapple;
        [SerializeField] private CharacterController characterController;
        [SerializeField] private PlayerInput playerInput;
        [SerializeField] private Animator characterAnimator;
        [SerializeField] private WeaponManager weaponManager;
        [SerializeField] private GrappleController grappleController;
        [SerializeField] private PlayerRagdoll playerRagdoll;
        [SerializeField] private DeathCamera deathCamera;
        [SerializeField] private NetworkSfxRelay sfxRelay;
        [SerializeField] private AudioClip hurtSound;
        [SerializeField] private AudioClip taggedSound; // UI sound for when this player gets tagged
        [SerializeField] private AudioClip taggingSound; // UI sound for when this player tags someone
        [SerializeField] private CinemachineImpulseSource impulseSource;
        [SerializeField] private MeshRenderer worldWeapon;
        [SerializeField] private LayerMask worldLayer;
        [SerializeField] private LayerMask enemyLayer;
        [SerializeField] private Transform tr;
        [SerializeField] private GameObject[] worldWeaponPrefabs;
        [SerializeField] private ClientNetworkTransform networkTransform;

        [Header("FOV (speed boost)")]
        [SerializeField] private float baseFov = 80f;

        [SerializeField] private float sprintStartSpeed = 9f;
        [SerializeField] private float maxSpeedForFov = 30f;
        [SerializeField] private float maxFov = 100f;
        [SerializeField] private float fovSmoothTime = 0.12f;


        #endregion

        #region Public Input Fields

        public Vector2 moveInput;
        public Vector2 lookInput;
        public bool sprintInput;
        public bool crouchInput;

        #endregion

        #region Private Fields

        private float _currentPitch;
        private float _maxSpeed = WalkSpeed;
        private float _verticalVelocity;
        private Vector3 _horizontalVelocity;
        private bool _isJumping;
        private bool _isFalling;
        private float _crouchTransition;
        private Vector3? _lastHitPoint;
        private Vector3? _lastHitNormal;
        private bool _wasGrounded;
        private float _lastDeathTime;
        private float _fallStartHeight;
        private const float MinFallDistance = 1.5f; // Minimum distance to trigger fall animation
        private float _lastSpawnTime;
        private const float LandingSoundCooldown = 0.5f; // Block landing sounds for 0.5s after spawn/respawn

        private float _fovVel;
        private float _targetFov;

        private float _lastDamageTime;
        private bool _isRegenerating;

        #endregion

        #region Podium Fields

        [Header("Podium Fix")]
        [SerializeField] private Transform rootBone;

        [SerializeField] private float podiumSnapDelay = 0.05f;
        private Animator _podiumAnimator;
        private SkinnedMeshRenderer _podiumSkinned;
        private ClientNetworkTransform _cnt;
        private bool _awaitingPodiumSnap;

        #endregion

        #region Private Properties

        public Vector3 CurrentFullVelocity {
            get {
                _cachedFullVelocity.x = _horizontalVelocity.x;
                _cachedFullVelocity.y = _verticalVelocity;
                _cachedFullVelocity.z = _horizontalVelocity.z;
                return _cachedFullVelocity;
            }
        }

        public bool IsGrounded => characterController.isGrounded;

        private float CurrentPitch {
            get => _currentPitch;

            set => _currentPitch = Mathf.Clamp(value, -PitchLimit, PitchLimit);
        }

        #endregion

        #region Network Variables

        public NetworkVariable<float> netHealth = new(100f);
        public NetworkVariable<bool> netIsDead = new();
        public NetworkVariable<int> kills = new();
        public NetworkVariable<int> deaths = new();
        public NetworkVariable<int> assists = new();
        public NetworkVariable<float> damageDealt = new();
        public NetworkVariable<float> averageVelocity = new();
        public NetworkVariable<int> pingMs = new();
        
        // Tag mode stats
        public NetworkVariable<int> tags = new();
        public NetworkVariable<int> tagged = new();
        public NetworkVariable<int> timeTagged = new(); // Time tagged in seconds
        public NetworkVariable<bool> isTagged = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public NetworkVariable<int> playerMaterialIndex = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<FixedString64Bytes> playerName = new("Player",
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<bool> netIsCrouching = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        #endregion

        #region Additional Serialized Fields

        [SerializeField] private Material[] playerMaterials;
        [SerializeField] private CinemachineCamera worldCamera;
        [SerializeField] private GameObject worldModelRoot;
        [SerializeField] private MantleController mantleController;
        [SerializeField] private UpperBodyPitch upperBodyPitch;

        #endregion

        #region Additional Private Fields

        private float _timer;
        private float _totalVelocitySampled;
        private int _velocitySampleCount;
        private float _velSampleAccum;
        private int _velSampleCount;
        private float _velSampleTimer;
        private int _obstacleMask;
        private bool _isMantling;

        private SkinnedMeshRenderer _cachedSkinnedMeshRenderer;
        private PlayerTeamManager _cachedPlayerTeamManager;
        private Renderer[] _cachedRenderers;
        private SkinnedMeshRenderer[] _cachedSkinnedRenderers;
        private bool _renderersCacheValid;
        private int _playerLayer;
        private int _enemyLayer;
        private float _gravityY;
        private float _cachedHorizontalSpeedSqr;
        private Vector3 _cachedFullVelocity;
        private Vector3 _moveVelocity;
        private const string GrappleLineName = "GrappleLine";

        private MaterialPropertyBlock _materialPropertyBlock;
        private Material[] _cachedMaterialsArray;
        private int _lastMaterialIndex = -1;
        private bool _hasLoggedEmissionProperties;

        #endregion

        #region Unity Lifecycle

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            _obstacleMask = worldLayer | enemyLayer;

            _playerLayer = LayerMask.NameToLayer("Player");
            _enemyLayer = LayerMask.NameToLayer("Enemy");
            _gravityY = Physics.gravity.y;

            _cachedSkinnedMeshRenderer = GetComponentInChildren<SkinnedMeshRenderer>();
            _cachedPlayerTeamManager = GetComponent<PlayerTeamManager>();

            _materialPropertyBlock = new MaterialPropertyBlock();

            if(_cachedSkinnedMeshRenderer != null) {
                _cachedMaterialsArray = _cachedSkinnedMeshRenderer.materials;
            }

            _renderersCacheValid = false;

            playerMaterialIndex.OnValueChanged -= OnMatChanged;
            playerMaterialIndex.OnValueChanged += OnMatChanged;
            netHealth.OnValueChanged -= OnHealthChanged;
            netHealth.OnValueChanged += OnHealthChanged;
            netIsCrouching.OnValueChanged -= OnCrouchStateChanged;
            netIsCrouching.OnValueChanged += OnCrouchStateChanged;
            isTagged.OnValueChanged -= OnTaggedStateChanged;
            isTagged.OnValueChanged += OnTaggedStateChanged;

            ApplyPlayerMaterial(playerMaterialIndex.Value);

            if(characterController.enabled == false)
                characterController.enabled = true;

            // Set others to enemy layer
            if(!IsOwner)
                gameObject.layer = _enemyLayer;

            var gameMenu = GameMenuManager.Instance;
            if(gameMenu && gameMenu.TryGetComponent(out UIDocument doc)) {
                var root = doc.rootVisualElement;
                var rootContainer = root?.Q<VisualElement>("root-container");
                if(rootContainer != null)
                    rootContainer.style.display = DisplayStyle.Flex;
            }

            HUDManager.Instance.ShowHUD();
            if(IsOwner && fpCamera) fpCamera.Lens.FieldOfView = baseFov;
            GameMenuManager.Instance.IsPostMatch = false;
            GameMenuManager.Instance.ShowInGameHudAfterPostMatch();
            
            // Track spawn time to prevent landing sounds on initial spawn
            _lastSpawnTime = Time.time;


            if(GameMenuManager.Instance.IsPaused) {
                GameMenuManager.Instance.TogglePause();
            }

            // Load single sensitivity value, defaulting to 0.1 if not set
            // If old separate X/Y values exist, use X as the new unified value
            float sensitivityValue;
            if(PlayerPrefs.HasKey("Sensitivity")) {
                sensitivityValue = PlayerPrefs.GetFloat("Sensitivity", 0.1f);
            } else if(PlayerPrefs.HasKey("SensitivityX")) {
                sensitivityValue = PlayerPrefs.GetFloat("SensitivityX", 0.1f);
                // Migrate to new unified key
                PlayerPrefs.SetFloat("Sensitivity", sensitivityValue);
            } else {
                sensitivityValue = 0.1f;
            }
            lookSensitivity = new Vector2(sensitivityValue, sensitivityValue);

            if(IsOwner) {
                playerName.Value = PlayerPrefs.GetString("PlayerName", "Unknown Player");
                var savedColorIndex = PlayerPrefs.GetInt("PlayerColorIndex", 0);
                playerMaterialIndex.Value = savedColorIndex;
                GrappleUIManager.Instance.RegisterLocalPlayer(this);
                
                // Initialize tag status for HUD in Tag mode
                var matchSettings = MatchSettings.Instance;
                if(matchSettings != null && matchSettings.selectedGameModeId == "Tag") {
                    HUDManager.Instance.UpdateTagStatus(isTagged.Value);
                }
                
                // Initialize glow effect for tagged status
                UpdateTaggedGlow();

                SetWorldWeaponPrefabsShadowMode(ShadowCastingMode.ShadowsOnly);

                _podiumAnimator = characterAnimator;
                _podiumSkinned = GetComponentInChildren<SkinnedMeshRenderer>();
                _cnt = GetComponent<ClientNetworkTransform>();

                if(rootBone == null && _podiumAnimator != null) {
                    rootBone = _podiumAnimator.GetBoneTransform(HumanBodyBones.Hips);
                }
            } else {
                if(worldModelRoot != null && !worldModelRoot.activeSelf) {
                    worldModelRoot.SetActive(true);
                }

                SetSkinnedMeshRenderersShadowMode(ShadowCastingMode.On, null, true);

                if(worldWeapon != null) {
                    if(!worldWeapon.gameObject.activeSelf) {
                        worldWeapon.gameObject.SetActive(true);
                    }

                    SetWorldWeaponRenderersShadowMode(ShadowCastingMode.On, true);
                }
            }
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();

            playerMaterialIndex.OnValueChanged -= OnMatChanged;
            netHealth.OnValueChanged -= OnHealthChanged;
            netIsCrouching.OnValueChanged -= OnCrouchStateChanged;
            isTagged.OnValueChanged -= OnTaggedStateChanged;
            
        }

        private void OnMatChanged(int _, int newIdx) => ApplyPlayerMaterial(newIdx);

        private void OnHealthChanged(float oldV, float newV) {
            if(IsOwner) HUDManager.Instance.UpdateHealth(newV, 100f);
        }

        private void OnCrouchStateChanged(bool oldValue, bool newValue) {
            UpdateCharacterControllerCrouch(newValue);
        }
        
        private void OnTaggedStateChanged(bool oldValue, bool newValue) {
            // Update HUD for Tag mode
            if(IsOwner) {
                var matchSettings = MatchSettings.Instance;
                if(matchSettings != null && matchSettings.selectedGameModeId == "Tag") {
                    HUDManager.Instance.UpdateTagStatus(newValue);
                }
            }
            
            // Update visual glow effect for tagged status
            UpdateTaggedGlow();
        }

        private void Update() {
            if(IsServer) {
                Vector3 authPos = networkTransform.transform.position;
                if(authPos.y <= 600f) {
                    if(!netIsDead.Value && Time.time - _lastDeathTime >= 4f) {
                        _lastDeathTime = Time.time;
                        netHealth.Value = 0f;
                        BroadcastKillClientRpc("HOP", playerName.Value.ToString(), ulong.MaxValue, OwnerClientId);
                        DieServer();
                    }
                }

                HandleHealthRegeneration();
                
                // Tag mode: increment time tagged every second while tagged
                var matchSettings = MatchSettings.Instance;
                bool isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Tag";
                
                _timer += Time.deltaTime;
                if(_timer >= 1f) {
                    _timer = 0f;
                    
                    if(isTagMode && isTagged.Value) {
                        // Tag mode: increment time tagged
                        timeTagged.Value++;
                    } else {
                        // Normal mode: update ping
                        UpdatePing();
                    }
                }
            }

            if(netIsDead.Value || characterController.enabled == false) return;

            if(IsOwner) {
                _cachedHorizontalSpeedSqr = _horizontalVelocity.sqrMagnitude;

                UpdateFallingState();
                UpdateSpeedFov();
                HandleMovement();
                HandleCrouch();
                UpdateAnimator();
                TrackVelocity();
            } else {
                UpdateCharacterControllerCrouch(netIsCrouching.Value);
                characterAnimator.SetBool(IsCrouchingHash, netIsCrouching.Value);
            }
            
            // Update tagged glow (for Tag mode)
            // This needs to run on all clients to see other players' glow
            var matchSettingsUpdate = MatchSettings.Instance;
            bool isTagModeUpdate = matchSettingsUpdate != null && matchSettingsUpdate.selectedGameModeId == "Tag";
            if(isTagModeUpdate) {
                // Update glow for tagged players (always visible at full intensity)
                if(isTagged.Value) {
                    UpdateTaggedGlow();
                }
            }
        }

        private void LateUpdate() {
            if(!IsOwner || netIsDead.Value) return;

            HandleLook();

            upperBodyPitch.SetLocalPitchFromCamera(CurrentPitch);
        }

        #endregion

        public void SetGameplayCameraActive(bool active) {
            if(fpCamera != null) {
                fpCamera.gameObject.SetActive(active);
            }

            if(worldCamera != null) {
                worldCamera.gameObject.SetActive(active);
            }
        }

        [Rpc(SendTo.Everyone)]
        public void SetWorldModelVisibleRpc(bool visible) {
            weaponManager.SwitchWeapon(0);
            InvalidateRendererCache();

            if(visible) {
                SetSkinnedMeshRenderersShadowMode(ShadowCastingMode.On);
                SetWorldWeaponRenderersShadowMode(ShadowCastingMode.On);
            } else {
                worldModelRoot.SetActive(false);
                worldWeapon.gameObject.SetActive(false);
            }
        }

        [Rpc(SendTo.Everyone)]
        public void ResetVelocityRpc() {
            ResetVelocity();
        }

        private void UpdatePing() {
            var transport = NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;
            if(!transport) return;

            var rtt = transport.GetCurrentRtt(OwnerClientId);

            pingMs.Value = (int)rtt;
        }

        private void HandleHealthRegeneration() {
            if(netIsDead.Value || netHealth.Value >= MaxHealth) {
                _isRegenerating = false;
                return;
            }

            var timeSinceDamage = Time.time - _lastDamageTime;

            if(timeSinceDamage >= RegenDelay) {
                if(!_isRegenerating) {
                    _isRegenerating = true;
                }

                netHealth.Value = Mathf.Min(MaxHealth, netHealth.Value + RegenRate * Time.deltaTime);
            } else {
                _isRegenerating = false;
            }
        }

        public void ResetVelocity() {
            _horizontalVelocity = Vector3.zero;
            _verticalVelocity = 0f;
        }

        private void TrackVelocity() {
            var speed = CurrentFullVelocity.sqrMagnitude;
            if(speed >= WalkSpeed * WalkSpeed) {
                _velSampleAccum += Mathf.Sqrt(speed);
                _velSampleCount++;
            }

            _velSampleTimer += Time.deltaTime;
            if(_velSampleTimer >= 0.1f && _velSampleCount > 0) {
                var avg = _velSampleAccum / _velSampleCount;
                SubmitVelocitySampleServerRpc(avg);
                _velSampleTimer = 0f;
                _velSampleAccum = 0f;
                _velSampleCount = 0;
            }
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void SubmitVelocitySampleServerRpc(float speed) {
            _totalVelocitySampled += speed;
            _velocitySampleCount++;
            averageVelocity.Value = _totalVelocitySampled / _velocitySampleCount;
        }

        private void ApplyPlayerMaterial(int index) {
            if(!_cachedSkinnedMeshRenderer) {
                _cachedSkinnedMeshRenderer = GetComponentInChildren<SkinnedMeshRenderer>();
                if(!_cachedSkinnedMeshRenderer) return;
            }

            if(index == _lastMaterialIndex && _cachedMaterialsArray != null) {
                // Still update glow even if material hasn't changed (tag status might have)
                UpdateTaggedGlow();
                return;
            }

            if(_cachedMaterialsArray == null || _cachedMaterialsArray.Length < 2) {
                _cachedMaterialsArray = _cachedSkinnedMeshRenderer.materials;
            }

            var selectedMaterial = playerMaterials[index % playerMaterials.Length];

            // Material array: [0] = outline, [1] = color, [2] = lit
            // Only modify [1] (color) for player color selection
            // Do NOT modify [0] (outline) - that's handled by UpdateTaggedGlow() via MaterialPropertyBlock
            
            if(_cachedMaterialsArray[1] != selectedMaterial) {
                _cachedMaterialsArray[1] = selectedMaterial;
                _cachedSkinnedMeshRenderer.materials = _cachedMaterialsArray;
                _lastMaterialIndex = index;
            }
            
            // Update glow effect after material change
            UpdateTaggedGlow();
        }
        
        /// <summary>
        /// Updates the visual glow effect for tagged players in Tag mode.
        /// Uses MaterialPropertyBlock on outline material (index 0) following PlayerTeamManager pattern.
        /// Does NOT modify the color material (index 1).
        /// Only modifies outline in Tag mode - leaves it alone in team-based modes so PlayerTeamManager can handle it.
        /// 
        /// Tagged players always glow at full intensity.
        /// </summary>
        private void UpdateTaggedGlow() {
            if(_cachedSkinnedMeshRenderer == null || _materialPropertyBlock == null) {
                return;
            }
            
            // Only apply in Tag mode
            var matchSettings = MatchSettings.Instance;
            bool isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Tag";
            
            // If not in Tag mode, don't modify outline (let PlayerTeamManager handle it for team-based modes)
            if(!isTagMode) {
                return;
            }
            
            // Get the outline material to check its properties
            if(_cachedMaterialsArray == null || _cachedMaterialsArray.Length < 1) {
                _cachedMaterialsArray = _cachedSkinnedMeshRenderer.materials;
            }
            
            var outlineMaterial = _cachedMaterialsArray[0];
            if(outlineMaterial == null) {
                return;
            }
            
            // Get existing property block for material index 0 (outline), following PlayerTeamManager pattern
            _cachedSkinnedMeshRenderer.GetPropertyBlock(_materialPropertyBlock, 0);
            
            if(isTagged.Value) {
                // Tagged players always glow at full intensity
                // Bright yellow/orange glow for tagged players (highly visible)
                // Using HDR color for bright glow (similar to PlayerTeamManager's outline colors)
                Color glowColor = new Color(5f, 4f, 1f, 1f); // Bright yellow-orange with HDR intensity
                
                // Debug: Log available properties (only once)
                if(!_hasLoggedEmissionProperties) {
                    Debug.Log($"[PlayerController] Outline material shader: {outlineMaterial.shader.name}");
                    Debug.Log($"[PlayerController] Checking properties on outline material...");
                    var hasOutlineColor = outlineMaterial.HasProperty("OutlineColor");
                    var hasOutlineColorUnderscore = outlineMaterial.HasProperty("_OutlineColor");
                    var hasEmission = outlineMaterial.HasProperty("Emission");
                    var hasEmissionColor = outlineMaterial.HasProperty("_EmissionColor");
                    Debug.Log($"[PlayerController] Has 'OutlineColor': {hasOutlineColor}");
                    Debug.Log($"[PlayerController] Has '_OutlineColor': {hasOutlineColorUnderscore}");
                    Debug.Log($"[PlayerController] Has 'Emission': {hasEmission}");
                    Debug.Log($"[PlayerController] Has '_EmissionColor': {hasEmissionColor}");
                    
                    // Try to get all property names from the shader
                    var shader = outlineMaterial.shader;
                    int propertyCount = shader.GetPropertyCount();
                    Debug.Log($"[PlayerController] Shader has {propertyCount} properties:");
                    for(int i = 0; i < propertyCount; i++) {
                        var propName = shader.GetPropertyName(i);
                        var propType = shader.GetPropertyType(i);
                        Debug.Log($"[PlayerController]   Property {i}: {propName} (type: {propType})");
                    }
                    _hasLoggedEmissionProperties = true;
                }
                
                // Set _OutlineColor via MaterialPropertyBlock (same pattern as PlayerTeamManager)
                // The _OutlineColor property feeds into Emission in the shader graph
                // Console shows the property is "_OutlineColor" (Color type)
                _materialPropertyBlock.SetColor("_OutlineColor", glowColor);
            } else {
                // Clear glow (not tagged) - set to black/default
                _materialPropertyBlock.SetColor("_OutlineColor", Color.black);
            }
            
            // Apply property block to material index 0 (outline), following PlayerTeamManager pattern
            _cachedSkinnedMeshRenderer.SetPropertyBlock(_materialPropertyBlock, 0);
        }
        

        #region Public Methods

        public void TryJump(float height = JumpHeight) {
            if(!IsGrounded) {
                return;
            }

            height = CheckForJumpPad() ? 15f : height;

            if(IsOwner) {
                var key = Mathf.Approximately(height, 15f) ? "jumpPad" : "jump";

                if(key == "jumpPad") {
                    sfxRelay.RequestWorldSfx(SfxKey.JumpPad, attachToSelf: true, true);
                }

                sfxRelay.RequestWorldSfx(SfxKey.Jump, attachToSelf: true, true);
            }

            // Calculate and apply vertical velocity for jump
            _verticalVelocity = Mathf.Sqrt(height * -2f * _gravityY * GravityScale);
            
            // Ensure velocity is positive (upward) before triggering jump animation
            // This guarantees the jump animation only triggers when velocity is actually applied upward
            if(_verticalVelocity > 0f) {
                PlayJumpAnimationServerRpc();
                _isJumping = true;
            }
        }

        [Rpc(SendTo.Everyone)]
        private void PlayJumpAnimationServerRpc() {
            characterAnimator.SetTrigger(JumpTriggerHash);
            characterAnimator.SetBool(IsJumpingHash, true);
        }

        public void PlayWalkSound() {
            if(!IsGrounded) return;

            if(_cachedHorizontalSpeedSqr < 0.5f * 0.5f) {
                return;
            }

            if(IsOwner) {
                sfxRelay.RequestWorldSfx(SfxKey.Walk, attachToSelf: true, true);
            }
        }

        public void PlayRunSound() {
            if(!IsGrounded) return;

            if(_cachedHorizontalSpeedSqr < 0.5f * 0.5f) {
                return;
            }

            if(IsOwner) {
                sfxRelay.RequestWorldSfx(SfxKey.Run, attachToSelf: true, true);
            }
        }

        #endregion

        #region Movement Methods

        private void OnControllerColliderHit(ControllerColliderHit hit) {
            if(hit.gameObject.CompareTag("JumpPad")) {
                TryJump(15f);
            }

            grappleController.CancelGrapple();
        }

        private bool CheckForJumpPad() {
            if(Physics.Raycast(tr.position, Vector3.down, out var hit, characterController.height * 0.6f)) {
                if(hit.collider.CompareTag("JumpPad")) {
                    return true;
                }
            }

            return false;
        }

        private void HandleMovement() {
            if(_isMantling || (swingGrapple && swingGrapple.IsSwinging)) {
                return;
            }

            UpdateMaxSpeed();
            CalculateHorizontalVelocity();
            CheckCeilingHit();
            ApplyGravity();
            MoveCharacter();
        }

        private void UpdateMaxSpeed() {
            if(crouchInput) {
                _maxSpeed = CrouchSpeed;
            } else if(sprintInput) {
                _maxSpeed = SprintSpeed;
            } else {
                _maxSpeed = WalkSpeed;
            }
        }

        private void CalculateHorizontalVelocity() {
            var motion = (tr.forward * moveInput.y + tr.right * moveInput.x).normalized;
            motion.y = 0f;

            if(IsGrounded) {
                ApplyFriction();
                ApplyDirectionChange(motion);

                var targetVelocity = motion.sqrMagnitude >= 0.1f ? motion * _maxSpeed : Vector3.zero;
                _horizontalVelocity =
                    Vector3.MoveTowards(_horizontalVelocity, targetVelocity, acceleration * Time.deltaTime);
            } else {
                AirStrafe(motion);
            }
        }

        private void ApplyFriction() {
            if(moveInput.sqrMagnitude >= 0.01f) return;

            var speed = _horizontalVelocity.magnitude;
            if(speed < 0.001f) return;

            var drop = speed * friction * Time.deltaTime;
            var newSpeed = Mathf.Max(speed - drop, 0f);
            _horizontalVelocity *= newSpeed / speed;
        }

        private void ApplyDirectionChange(Vector3 motion) {
            if(!(_horizontalVelocity.magnitude > 0.1f) || !(motion.magnitude > 0.1f)) return;

            var angle = Vector3.Angle(_horizontalVelocity, motion);

            if(!(angle > 90f)) return;

            var normalizedAngle = Mathf.InverseLerp(90f, 180f, angle);
            var reduction = Mathf.Lerp(0.85f, 0.2f, normalizedAngle * normalizedAngle);
            _horizontalVelocity *= reduction;
        }

        private void AirStrafe(Vector3 wishDir) {
            if(moveInput.sqrMagnitude < 0.01f) return;

            var currentSpeed = Vector3.Dot(_horizontalVelocity, wishDir);
            var addSpeed = maxAirSpeed - currentSpeed;

            if(addSpeed <= 0) return;

            var accelSpeed = airAcceleration * Time.deltaTime;
            accelSpeed = Mathf.Min(accelSpeed, addSpeed);

            _horizontalVelocity += wishDir * accelSpeed;
        }

        private void CheckCeilingHit() {
            var rayHit = Physics.Raycast(fpCamera.transform.position, Vector3.up, out var hit, 0.75f, _obstacleMask);
            if(rayHit && _verticalVelocity > 0f) {
                grappleController.CancelGrapple();
                _verticalVelocity = 0f;
            }
        }

        private void ApplyGravity() {
            if(IsGrounded && _verticalVelocity <= 0.01f) {
                _verticalVelocity = -3f;
            } else {
                _verticalVelocity += _gravityY * GravityScale * Time.deltaTime;
            }
        }

        private void MoveCharacter() {
            _moveVelocity.x = _horizontalVelocity.x;
            _moveVelocity.y = _verticalVelocity;
            _moveVelocity.z = _horizontalVelocity.z;
            characterController.Move(_moveVelocity * Time.deltaTime);
        }

        private void HandleCrouch() {
            float sphereRadius = characterController != null ? characterController.radius : 0.3f;
            bool headBlocked = Physics.SphereCast(
                fpCamera.transform.position,
                sphereRadius,
                Vector3.up,
                out _,
                StandCheckHeight,
                _obstacleMask
            );

            bool isCurrentlyCrouched = _crouchTransition > 0.5f || netIsCrouching.Value;

            bool targetCrouchState;
            if(crouchInput) {
                targetCrouchState = true;
            } else {
                targetCrouchState = headBlocked && isCurrentlyCrouched;
            }

            if(IsOwner && netIsCrouching.Value != targetCrouchState) {
                netIsCrouching.Value = targetCrouchState;
            }

            characterAnimator.SetBool(IsCrouchingHash, targetCrouchState);

            var targetTransition = targetCrouchState ? 1f : 0f;
            _crouchTransition = Mathf.Lerp(_crouchTransition, targetTransition, 10f * Time.deltaTime);

            var targetCameraHeight = Mathf.Lerp(StandHeight, CrouchHeight, _crouchTransition);
            var targetColliderHeight = Mathf.Lerp(StandCollider, CrouchCollider, _crouchTransition);

            if(IsOwner) {
                fpCamera.transform.localPosition = new Vector3(0f, targetCameraHeight, 0f);
            }

            UpdateCharacterControllerCrouch(targetCrouchState);
        }

        private void UpdateCharacterControllerCrouch(bool isCrouching) {
            var targetTransition = isCrouching ? 1f : 0f;
            if(!IsOwner) {
                _crouchTransition = Mathf.Lerp(_crouchTransition, targetTransition, 10f * Time.deltaTime);
            }

            var targetColliderHeight = Mathf.Lerp(StandCollider, CrouchCollider, _crouchTransition);
            var centerY = targetColliderHeight / 2f;
            characterController.height = targetColliderHeight;
            characterController.center = new Vector3(0f, centerY, 0f);
        }

        #endregion

        #region Look Methods

        private void HandleLook() {
            var lookDelta = new Vector2(lookInput.x * lookSensitivity.x, lookInput.y * lookSensitivity.y);

            UpdatePitch(lookDelta.y);
            UpdateYaw(lookDelta.x);
            UpdateTurnAnimation(lookDelta.x);
        }

        private void UpdatePitch(float pitchDelta) {
            CurrentPitch -= pitchDelta;
            fpCamera.transform.localRotation = Quaternion.Euler(CurrentPitch, 0f, 0f);
        }

        private void UpdateYaw(float yawDelta) {
            tr.Rotate(Vector3.up * yawDelta);
        }

        private void UpdateSpeedFov() {
            if(!IsOwner || !fpCamera) return;

            var speed = _horizontalVelocity.magnitude;
            var t = Mathf.InverseLerp(sprintStartSpeed, maxSpeedForFov, speed);
            t = Mathf.Pow(t, 0.65f);

            _targetFov = Mathf.Lerp(baseFov, maxFov, t);

            var current = fpCamera.Lens.FieldOfView;
            var next = Mathf.SmoothDamp(current, _targetFov, ref _fovVel, fovSmoothTime);
            fpCamera.Lens.FieldOfView = next;
        }

        #endregion

        #region Damage & Death Methods

        public bool ApplyDamageServer_Auth(float amount, Vector3 hitPoint, Vector3 hitNormal, ulong attackerId) {
            if(!IsServer || netIsDead.Value) return false;

            // Check if we're in Tag mode
            var matchSettings = MatchSettings.Instance;
            bool isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Tag";

            _lastHitPoint = hitPoint;
            _lastHitNormal = hitNormal;
            _lastDamageTime = Time.time;
            _isRegenerating = false;

            if(isTagMode) {
                // Tag mode: no damage, just tag on hit
                // Play hit effects (flinch animation)
                PlayHitEffectsClientRpc(hitPoint, amount);
                
                // Tag the player (1 bullet = tag) - only broadcast if tag is actually transferred
                bool wasTagged = isTagged.Value;
                if(!wasTagged) {
                    isTagged.Value = true;
                    tagged.Value++;
                    
                    // Play UI sound for getting tagged (on victim's client)
                    PlayTaggedSoundClientRpc();
                    
                    // Untag the attacker if they were tagged
                    bool attackerWasTagged = false;
                    if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var attackerClient)) {
                        var attacker = attackerClient.PlayerObject?.GetComponent<PlayerController>();
                        if(attacker != null && attacker.isTagged.Value) {
                            attackerWasTagged = true;
                            attacker.isTagged.Value = false;
                        }
                        if(attacker != null) {
                            attacker.tags.Value++;
                            // Play UI sound for tagging someone (on attacker's client)
                            attacker.PlayTaggingSoundClientRpc();
                        }
                    }
                    
                    // Broadcast tag transfer to kill feed (only on first bullet that tags)
                    BroadcastTagTransferClientRpc(attackerId, OwnerClientId);
                }
                
                return false; // No kill in tag mode (except OOB)
            } else {
                // Normal damage mode
                var pre = netHealth.Value;
                var newHp = Mathf.Max(0f, pre - amount);
                var actualDealt = pre - newHp;

                netHealth.Value = newHp;

                PlayHitEffectsClientRpc(hitPoint, amount);

                if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var attackerClient)) {
                    var attacker = attackerClient.PlayerObject?.GetComponent<PlayerController>();
                    if(attacker) {
                        attacker.damageDealt.Value += actualDealt;
                    }
                }

                if(newHp <= 0f && !netIsDead.Value && !PostMatchManager.Instance.PostMatchFlowStarted) {
                    netIsDead.Value = true;

                    if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var killerClient)) {
                        var killer = killerClient.PlayerObject?.GetComponent<PlayerController>();
                        if(killer) {
                            killer.kills.Value++;
                            BroadcastKillClientRpc(killer.playerName.Value.ToString(), playerName.Value.ToString(),
                                attackerId, OwnerClientId);
                        }
                    }

                    deaths.Value++;
                    DieClientRpc(_lastHitPoint ?? tr.position, _lastHitNormal ?? Vector3.up);
                    return true;
                }

                return false;
            }
        }

        [Rpc(SendTo.Everyone)]
        private void BroadcastKillClientRpc(string killerName, string victimName, ulong killerClientId,
            ulong victimClientId) {
            var isLocalKiller = NetworkManager.Singleton.LocalClientId == killerClientId;
            GameMenuManager.Instance.AddKillToFeed(killerName, victimName, isLocalKiller, killerClientId,
                victimClientId);
        }
        
        [Rpc(SendTo.Everyone)]
        private void BroadcastTagTransferClientRpc(ulong taggerClientId, ulong taggedClientId) {
            // Get player names
            string taggerName = "Unknown";
            string taggedName = "Unknown";
            
            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(taggerClientId, out var taggerClient)) {
                var tagger = taggerClient.PlayerObject?.GetComponent<PlayerController>();
                if(tagger != null) {
                    taggerName = tagger.playerName.Value.ToString();
                }
            }
            
            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(taggedClientId, out var taggedClient)) {
                var tagged = taggedClient.PlayerObject?.GetComponent<PlayerController>();
                if(tagged != null) {
                    taggedName = tagged.playerName.Value.ToString();
                }
            }
            
            var isLocalTagger = NetworkManager.Singleton.LocalClientId == taggerClientId;
            GameMenuManager.Instance.AddTagTransferToFeed(taggerName, taggedName, isLocalTagger, taggerClientId, taggedClientId);
        }
        
        /// <summary>
        /// Broadcasts a tag transfer from HOP (initial designation) to the kill feed.
        /// Similar to OOB kills, uses ulong.MaxValue as the tagger client ID.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        public void BroadcastTagTransferFromHOPClientRpc(ulong taggedClientId) {
            string taggedName = "Unknown";
            
            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(taggedClientId, out var taggedClient)) {
                var tagged = taggedClient.PlayerObject?.GetComponent<PlayerController>();
                if(tagged != null) {
                    taggedName = tagged.playerName.Value.ToString();
                }
            }
            
            // HOP is never the local player, so isLocalTagger is always false
            GameMenuManager.Instance.AddTagTransferToFeed("HOP", taggedName, false, ulong.MaxValue, taggedClientId);
        }

        [Rpc(SendTo.Everyone)]
        private void PlayHitEffectsClientRpc(Vector3 hitPoint, float amount) {
            if(IsOwner) {
                SoundFXManager.Instance.PlayUISound(hurtSound);
                impulseSource.GenerateImpulse();

                if(DamageVignetteUI.Instance && fpCamera) {
                    var intensity = Mathf.Clamp01(amount / 50f);
                    DamageVignetteUI.Instance.ShowHitFromWorldPoint(hitPoint, fpCamera.transform, intensity);
                }
            }

            characterAnimator.SetTrigger(DamageTriggerHash);
        }

        /// <summary>
        /// Plays UI sound when this player gets tagged (called on the victim's client).
        /// </summary>
        [Rpc(SendTo.Owner)]
        public void PlayTaggedSoundClientRpc() {
            if(SoundFXManager.Instance != null && taggedSound != null) {
                SoundFXManager.Instance.PlayUISound(taggedSound);
            }
        }

        /// <summary>
        /// Plays UI sound when this player tags someone (called on the attacker's client).
        /// </summary>
        [Rpc(SendTo.Owner)]
        public void PlayTaggingSoundClientRpc() {
            if(SoundFXManager.Instance != null && taggingSound != null) {
                SoundFXManager.Instance.PlayUISound(taggingSound);
            }
        }

        private void DieServer() {
            if(!IsServer) return;

            netIsDead.Value = true;
            deaths.Value++;
            DieClientRpc(_lastHitPoint ?? tr.position, _lastHitNormal ?? Vector3.up);
        }

        [Rpc(SendTo.Everyone)]
        private void DieClientRpc(Vector3 hitPoint, Vector3 hitNormal) {
            if(playerRagdoll) {
                if(_lastHitPoint.HasValue && _lastHitNormal.HasValue)
                    playerRagdoll.EnableRagdoll(_lastHitPoint, -_lastHitNormal);
                else
                    playerRagdoll.EnableRagdoll();
            }

            SetRenderersEnabled(true);
            SetSkinnedMeshRenderersShadowMode(ShadowCastingMode.On);

            if(IsOwner) {
                if(weaponManager != null && weaponManager.WeaponCameraController != null) {
                    weaponManager.WeaponCameraController.SetWeaponCameraEnabled(false);
                }

                HUDManager.Instance.HideHUD();
                deathCamera.EnableDeathCamera();
                worldWeapon.shadowCastingMode = ShadowCastingMode.On;
                if(IsOwner && fpCamera) {
                    fpCamera.Lens.FieldOfView = baseFov;
                }
            }

            StartCoroutine(RespawnTimer());
        }

        private IEnumerator RespawnTimer() {
            yield return new WaitForSeconds(3f);

            RequestRespawnServerRpc();
        }

        [Rpc(SendTo.Server)]
        private void RequestRespawnServerRpc() {
            if(!netIsDead.Value) return;

            DoRespawnServer();
        }

        private void DoRespawnServer() {
            PrepareRespawnClientRpc();

            Vector3 position;
            Quaternion rotation;

            var matchSettings = MatchSettings.Instance;
            bool isTeamBased = matchSettings != null && IsTeamBasedMode(matchSettings.selectedGameModeId);

            if(isTeamBased) {
                if(!_cachedPlayerTeamManager) {
                    _cachedPlayerTeamManager = GetComponent<PlayerTeamManager>();
                }

                var team = _cachedPlayerTeamManager?.netTeam.Value ?? SpawnPoint.Team.TeamA;
                (position, rotation) = GetSpawnPointForTeam(team);
            } else {
                (position, rotation) = GetSpawnPointFfa();
            }

            StartCoroutine(TeleportAfterPreparation(position, rotation));
        }

        private IEnumerator TeleportAfterPreparation(Vector3 position, Quaternion rotation) {
            const float fadeDuration = 0.5f;
            const float buffer = 0.15f;

            yield return new WaitForSeconds(fadeDuration + buffer);

            ResetHealthAndRegenerationState();

            DisableRagdollAndTeleportClientRpc(position, rotation);

            const float holdDuration = 0.5f;
            yield return new WaitForSeconds(holdDuration);

            SignalFadeInStartClientRpc();
            RestoreControlAfterFadeInClientRpc();
        }

        [Rpc(SendTo.Owner)]
        private void SignalFadeInStartClientRpc() {
            if(SceneTransitionManager.Instance != null) {
                SceneTransitionManager.Instance.SignalFadeInStart();
            }
        }

        [Rpc(SendTo.Owner)]
        private void RestoreControlAfterFadeInClientRpc() {
            if(characterController) characterController.enabled = true;

            CurrentPitch = 0f;
            _horizontalVelocity = Vector3.zero;
            _verticalVelocity = 0f;
            lookInput = Vector2.zero;
            fpCamera.transform.localRotation = Quaternion.identity;
            HUDManager.Instance.ShowHUD();

            ShowRespawnVisualsClientRpc(tr.position);
        }

        [Rpc(SendTo.Everyone)]
        private void ShowRespawnVisualsClientRpc(Vector3 expectedSpawnPosition) {
            if(IsOwner && weaponManager != null && weaponManager.WeaponCameraController != null) {
                weaponManager.WeaponCameraController.SetWeaponCameraEnabled(true);
            }

            if(!IsOwner) {
                StartCoroutine(ShowVisualsAfterPositionSync(expectedSpawnPosition));
                return;
            }

            ShowVisuals();
        }

        [Rpc(SendTo.Everyone)]
        private void DisableRagdollAndTeleportClientRpc(Vector3 position, Quaternion rotation) {
            if(playerRagdoll) {
                playerRagdoll.DisableRagdoll();
            }

            InvalidateRendererCache();
            HideVisuals();

            ResetAnimatorState(characterAnimator);

            if(IsOwner) {
                deathCamera.DisableDeathCamera();
                TeleportOwnerClientRpc(position, rotation);
            }
        }

        private Coroutine _respawnFadeCoroutine;

        [Rpc(SendTo.Everyone)]
        private void PrepareRespawnClientRpc() {
            if(IsOwner && SceneTransitionManager.Instance != null) {
                if(_respawnFadeCoroutine != null) {
                    StopCoroutine(_respawnFadeCoroutine);
                }

                _respawnFadeCoroutine = StartCoroutine(SceneTransitionManager.Instance.FadeRespawnTransition());
            }
        }

        private static bool IsTeamBasedMode(string modeId) => modeId switch {
            "Team Deathmatch" => true,
            "CTF" => true,
            "Oddball" => true,
            "KOTH" => true,
            _ => false
        };

        private (Vector3 pos, Quaternion rot) GetSpawnPointForTeam(SpawnPoint.Team team) {
            var point = SpawnManager.Instance.GetNextSpawnPoint(team);
            var pos = point.transform.position;
            var rot = point.transform.rotation;
            return (pos, rot);
        }

        private (Vector3 pos, Quaternion rot) GetSpawnPointFfa() {
            var point = SpawnManager.Instance.GetNextSpawnPoint();
            var pos = point.transform.position;
            var rot = point.transform.rotation;
            return (pos, rot);
        }

        [Rpc(SendTo.Owner)]
        private void TeleportOwnerClientRpc(Vector3 spawn, Quaternion rotation) {
            _ = TeleportAndNotifyAsync(spawn, rotation);
        }

        private async UniTaskVoid TeleportAndNotifyAsync(Vector3 spawn, Quaternion rotation) {
            if(characterController) characterController.enabled = false;

            if(_cnt) {
                _cnt.Teleport(spawn, rotation, Vector3.one);
            } else {
                tr.SetPositionAndRotation(spawn, rotation);
            }
            
            // Track respawn time to prevent landing sounds on respawn
            _lastSpawnTime = Time.time;

            await UniTask.WaitForFixedUpdate();
            await UniTask.WaitForFixedUpdate();

            var currentPos = tr.position;
            var distanceMoved = Vector3.Distance(currentPos, spawn);
            if(distanceMoved > 0.1f) {
                await UniTask.Delay(50);
            }
        }

        private void HideVisuals() {
            SetRenderersEnabled(false);
        }


        private IEnumerator ShowVisualsAfterPositionSync(Vector3 expectedPosition) {
            int maxWaitFrames = 10;
            int framesWaited = 0;

            while(framesWaited < maxWaitFrames) {
                var distance = Vector3.Distance(tr.position, expectedPosition);
                if(distance < 5f) {
                    break;
                }

                framesWaited++;
                yield return null;
            }

            ShowVisuals();
        }

        private void ShowVisuals() {
            SetRenderersEnabled(true);
            SetSkinnedMeshRenderersShadowMode(ShadowCastingMode.On, ShadowCastingMode.ShadowsOnly);

            gameObject.layer = IsOwner ? _playerLayer : _enemyLayer;

            if(IsOwner) {
                ResetWeaponState(resetAllAmmo: true, switchToWeapon0: true);
                StartCoroutine(UpdateHUDAfterWeaponSwitch());
            }
        }

        private IEnumerator UpdateHUDAfterWeaponSwitch() {
            yield return null;
            ResetWeaponState(updateHUD: true);
        }

        #endregion

        #region Podium Methods

        public void ForceRespawnForPodiumServer() {
            if(!IsServer) return;

            ResetHealthAndRegenerationState();

            ForceRespawnForPodiumClientRpc();
        }

        [Rpc(SendTo.Everyone)]
        private void ForceRespawnForPodiumClientRpc() {
            if(playerRagdoll) playerRagdoll.DisableRagdoll();

            ResetAnimatorState(_podiumAnimator);

            SetRenderersEnabled(true, true, ShadowCastingMode.On);

            if(_podiumSkinned) {
                _podiumSkinned.shadowCastingMode = ShadowCastingMode.On;
            }

            gameObject.layer = IsOwner ? _playerLayer : _enemyLayer;
            _awaitingPodiumSnap = true;
        }

        public void TeleportToPodiumFromServer(Vector3 position, Quaternion rotation) {
            if(!IsServer) return;
            TeleportToPodiumOwnerClientRpc(position, rotation);
        }

        [Rpc(SendTo.Owner)]
        private void TeleportToPodiumOwnerClientRpc(Vector3 position, Quaternion rotation) {
            StartCoroutine(TeleportAndSnapToPodium(position, rotation));
        }

        private IEnumerator TeleportAndSnapToPodium(Vector3 pos, Quaternion rot) {
            if(characterController) characterController.enabled = false;
            if(_cnt) _cnt.enabled = false;

            tr.SetPositionAndRotation(pos, rot);
            if(_cnt) _cnt.Teleport(pos, rot, Vector3.one);

            yield return new WaitForFixedUpdate();

            if(characterController) characterController.enabled = true;
            if(_cnt) _cnt.enabled = true;

            if(_awaitingPodiumSnap) {
                yield return new WaitForSeconds(podiumSnapDelay);
                SnapBonesToRoot();
                _awaitingPodiumSnap = false;
            }
        }

        private void SnapBonesToRoot() {
            if(!rootBone || !_podiumAnimator) return;

            rootBone.position = tr.position;
            rootBone.rotation = tr.rotation;

            _podiumAnimator.enabled = false;
            _podiumAnimator.enabled = true;

            if(_podiumSkinned) {
                _podiumSkinned.enabled = false;
                _podiumSkinned.enabled = true;
            }
        }

        [Rpc(SendTo.Everyone)]
        public void SnapPodiumVisualsClientRpc() {
            if(_awaitingPodiumSnap) {
                SnapBonesToRoot();
                _awaitingPodiumSnap = false;
            }
        }

        #endregion

        #region Utility Methods

        public void SetMantling(bool mantling) {
            _isMantling = mantling;
        }

        #endregion

        #region Animation Methods

        [Rpc(SendTo.Everyone)]
        private void PlayLandingAnimationServerRpc() {
            characterAnimator.SetTrigger(LandTriggerHash);
            characterAnimator.SetBool(IsJumpingHash, false);
            _isFalling = false;
            _isJumping = false;
            // Set IsFallingHash based on _isFalling state to ensure consistency
            characterAnimator.SetBool(IsFallingHash, _isFalling);
            characterAnimator.SetBool(IsGroundedHash, IsGrounded);
        }

        private void UpdateFallingState() {
            var grounded = IsGrounded;

            // Track when we leave the ground
            if(_wasGrounded && !grounded) {
                _fallStartHeight = tr.position.y;
            }

            // Initialize fall start height if we're in air and it hasn't been set (edge case)
            if(!grounded && _fallStartHeight == 0f) {
                _fallStartHeight = tr.position.y;
            }

            // Set falling to true whenever we're in air (both going up and down)
            // This allows jump->fall transitions to work in the animator
            if(!grounded) {
                _isFalling = true;
                
                // Track peak height while rising (for distance calculations)
                if(_verticalVelocity > 0f) {
                    if(tr.position.y > _fallStartHeight) {
                        _fallStartHeight = tr.position.y;
                    }
                }
            } else {
                // Reset when grounded
                _fallStartHeight = 0f;
                _isFalling = false;
            }

            // Landing: always trigger land animation when we hit the ground from air
            if(!_wasGrounded && grounded) {
                if(IsOwner) {
                    PlayLandingAnimationServerRpc();
                    // Only play landing sound if enough time has passed since spawn/respawn
                    if(Time.time - _lastSpawnTime >= LandingSoundCooldown) {
                        sfxRelay.RequestWorldSfx(SfxKey.Land, attachToSelf: true, allowOverlap: true);
                    }
                }

                _isJumping = false;
                _isFalling = false;
                _fallStartHeight = 0f;
            }

            _wasGrounded = grounded;
        }

        private void UpdateAnimator() {
            var localVelocity = tr.InverseTransformDirection(_horizontalVelocity);
            var isSprinting = _cachedHorizontalSpeedSqr > (WalkSpeed + 1f) * (WalkSpeed + 1f);

            characterAnimator.SetFloat(MoveXHash, localVelocity.x / _maxSpeed, 0.1f, Time.deltaTime);
            characterAnimator.SetFloat(MoveYHash, localVelocity.z / _maxSpeed, 0.1f, Time.deltaTime);
            characterAnimator.SetBool(IsSprintingHash, isSprinting);
            characterAnimator.SetBool(IsFallingHash, _isFalling);
            
            // Reset jump trigger when grounded and not jumping, or when in air (falling)
            // Since _isFalling is now true during entire jump (up and down), we reset it whenever in air
            // Note: Land trigger is never reset - let the animator consume it naturally
            if((IsGrounded && !_isJumping) || _isFalling) {
                // Grounded and not jumping, or in air - clear jump trigger
                characterAnimator.ResetTrigger(JumpTriggerHash);
            }
        }

        private void UpdateTurnAnimation(float yawDelta) {
            var turnSpeed = Mathf.Abs(yawDelta) > 0.001f ? Mathf.Clamp(yawDelta * 10f, -1f, 1f) : 0f;

            characterAnimator.SetFloat(LookXHash, turnSpeed, 0.1f, Time.deltaTime);
        }

        #endregion

        #region Grapple Support Methods

        public void SetVelocity(Vector3 horizontalVelocity) {
            _horizontalVelocity = new Vector3(horizontalVelocity.x, 0f, horizontalVelocity.z);
        }

        public void AddVerticalVelocity(float verticalBoost) {
            _verticalVelocity += verticalBoost;
        }

        #endregion

        #region Performance Optimization Helpers

        private void RefreshRendererCacheIfNeeded() {
            if(!_renderersCacheValid) {
                _cachedRenderers = GetComponentsInChildren<Renderer>(true);
                _cachedSkinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
                _renderersCacheValid = true;
            }
        }

        private void InvalidateRendererCache() {
            _renderersCacheValid = false;
        }

        private void SetWorldWeaponRenderersShadowMode(ShadowCastingMode mode, bool enabled = true) {
            if(worldWeapon != null) {
                var weaponRenderers = worldWeapon.GetComponentsInChildren<MeshRenderer>();
                foreach(var mr in weaponRenderers) {
                    if(mr != null) {
                        mr.shadowCastingMode = mode;
                        mr.enabled = enabled;
                    }
                }
            }
        }

        private void SetWorldWeaponPrefabsShadowMode(ShadowCastingMode mode) {
            if(worldWeaponPrefabs != null) {
                foreach(var w in worldWeaponPrefabs) {
                    var weaponRenderers = w.GetComponentsInChildren<MeshRenderer>();
                    foreach(var mr in weaponRenderers) {
                        if(mr != null) {
                            mr.shadowCastingMode = mode;
                        }
                    }
                }
            }
        }

        private void SetRenderersEnabled(bool enabled, bool excludeGrappleLine = true, ShadowCastingMode? shadowMode = null) {
            RefreshRendererCacheIfNeeded();
            foreach(var r in _cachedRenderers) {
                if(r != null && (!excludeGrappleLine || r.name != GrappleLineName)) {
                    r.enabled = enabled;
                    if(shadowMode.HasValue) {
                        r.shadowCastingMode = shadowMode.Value;
                    }
                }
            }
        }

        private void SetSkinnedMeshRenderersShadowMode(ShadowCastingMode mode, ShadowCastingMode? ownerMode = null, bool? enabled = null) {
            RefreshRendererCacheIfNeeded();
            foreach(var smr in _cachedSkinnedRenderers) {
                if(smr != null) {
                    if(enabled.HasValue) {
                        smr.enabled = enabled.Value;
                    }
                    if(ownerMode.HasValue) {
                        smr.shadowCastingMode = IsOwner ? ownerMode.Value : mode;
                    } else {
                        smr.shadowCastingMode = mode;
                    }
                }
            }
        }

        private void ResetHealthAndRegenerationState() {
            netIsDead.Value = false;
            netHealth.Value = 100f;
            _lastDamageTime = Time.time - RegenDelay;
            _isRegenerating = false;
            
            // Tag mode: reset tagged state on respawn
            var matchSettings = MatchSettings.Instance;
            if(matchSettings != null && matchSettings.selectedGameModeId == "Tag") {
                isTagged.Value = false;
            }
        }

        private void ResetAnimatorState(Animator animator) {
            if(animator != null) {
                animator.Rebind();
                animator.Update(0f);
            }
        }

        private void ResetWeaponState(bool resetAllAmmo = false, bool switchToWeapon0 = false, bool updateHUD = false) {
            if(!IsOwner || weaponManager == null) return;

            if(resetAllAmmo) {
                weaponManager.ResetAllWeaponAmmo();
            }

            var currentWeapon = weaponManager.CurrentWeapon;
            if(currentWeapon != null) {
                currentWeapon.ResetWeapon();
                currentWeapon.CurrentDamageMultiplier = 1f;

                var weaponInstance = currentWeapon.GetWeaponPrefab();
                if(weaponInstance != null) {
                    weaponInstance.SetActive(true);
                    weaponInstance.transform.localPosition = currentWeapon.GetSpawnPosition();
                    weaponInstance.transform.localEulerAngles = currentWeapon.GetSpawnRotation();
                }

                if(worldWeapon != null) {
                    worldWeapon.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                }

                if(updateHUD) {
                    HUDManager.Instance.UpdateAmmo(currentWeapon.currentAmmo, currentWeapon.GetMagSize());
                    HUDManager.Instance.UpdateHealth(netHealth.Value, 100f);
                }
            }

            if(switchToWeapon0) {
                playerInput.SwitchWeapon(0);
            }
        }

        #endregion
    }
}
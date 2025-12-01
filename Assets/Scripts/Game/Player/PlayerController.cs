using System.Collections.Generic;
using System.Linq;
using Game.Weapons;
using Game.UI;
using Game.Menu;
using Game.Match;
using Network;
using Network.AntiCheat;
using Network.Components;
using Network.Events;
using Network.Rpc;
using Network.Singletons;
using OSI;
using Unity.Cinemachine;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace Game.Player {
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    [DefaultExecutionOrder(-100)] // Initialize before sub-controllers
    public partial class PlayerController : NetworkBehaviour {
        #region Serialized Fields

        [Header("Core Components")]
        [SerializeField] private Transform playerTransform;
        [SerializeField] private CharacterController characterController;
        [SerializeField] private PlayerInput playerInput;
        [SerializeField] private UnityEngine.InputSystem.PlayerInput unityPlayerInput;
        [SerializeField] private Animator playerAnimator;
        [SerializeField] private ClientNetworkTransform clientNetworkTransform;
        [SerializeField] private Target playerTarget;

        [Header("Cameras")]
        [SerializeField] private CinemachineCamera fpCamera;
        [SerializeField] private Camera weaponCamera;
        [SerializeField] private CinemachineCamera deathCamera;


        [Header("Player Model")]
        [SerializeField] private GameObject playerModelRoot;
        [SerializeField] private SkinnedMeshRenderer playerMesh;
        [SerializeField] private Material[] playerMaterials;
        [SerializeField] private PlayerVisualController visualController;
        [SerializeField] private PlayerShadow playerShadow;
        [SerializeField] private PlayerRenderer playerRenderer;
        [SerializeField] private UpperBodyPitch upperBodyPitch;
        [SerializeField] private PlayerRagdoll playerRagdoll;
        [SerializeField] private Transform deathCameraTarget;

        [Header("Movement Controllers")]
        [SerializeField] private PlayerMovementController movementController;
        [SerializeField] private PlayerLookController lookController;
        [SerializeField] private MantleController mantleController;
        // [SerializeField] private SwingGrapple swingGrapple;
        [SerializeField] private GrappleController grappleController;


        [Header("Gameplay Controllers")]
        [SerializeField] private PlayerStatsController statsController;
        [SerializeField] private PlayerHealthController healthController;
        [SerializeField] private PlayerAnimationController animationController;
        [SerializeField] private PlayerTagController tagController;
        [SerializeField] private PlayerPodiumController podiumController;
        [SerializeField] private PlayerHopballController playerHopballController;
        [SerializeField] private PlayerTeamManager playerTeamManager;
        [SerializeField] private WeaponCameraController weaponCameraController;
        [SerializeField] private DeathCameraController deathCameraController;


        [Header("Weapon System")]
        [SerializeField] private WeaponManager weaponManager;
        [SerializeField] private Weapon weaponComponent;
        // [SerializeField] private MeshRenderer worldWeapon;
        [SerializeField] private Transform worldWeaponSocket;
        [SerializeField] private GameObject[] worldWeaponPrefabs;


        [Header("Audio / Visual Effects")]
        [SerializeField] private AudioListener audioListener;
        [SerializeField] private NetworkDamageRelay damageRelay;
        [SerializeField] private NetworkFxRelay fxRelay;
        [SerializeField] private NetworkSfxRelay sfxRelay;
        [SerializeField] private CinemachineImpulseSource impulseSource;
        [SerializeField] private SpeedTrail speedTrail;


        [Header("Layers")]
        [SerializeField] private LayerMask worldLayer;
        [SerializeField] private LayerMask playerLayer;
        [SerializeField] private LayerMask enemyLayer;
        [SerializeField] private LayerMask weaponLayer;
        [SerializeField] private LayerMask hopballLayer;

        #endregion

        #region Public Input Fields

        public Vector2 moveInput;
        public Vector2 lookInput;
        public bool sprintInput;
        public bool crouchInput;

        #endregion

        #region Private Fields

        private float _lastDeathTime; // Used for OOB check in Update()
        private Vector3 _lastServerMovementPosition;
        private float _lastServerMovementTime;
        private bool _hasServerMovementSample;

        // Movement violation tracking
        private class MovementViolation {
            public float Time;
            public bool WasSpeedViolation;
        }

        private readonly List<MovementViolation> _movementViolations = new();

        private const float MinHeightStrength = 0.005f;
        private const float MaxHeightStrength = 0.08f;

        // Cache MeshRenderers per weapon instance to avoid repeated GetComponentsInChildren calls
        private readonly Dictionary<GameObject, MeshRenderer[]> _cachedWeaponRenderers = new();
        private readonly Dictionary<GameObject, Collider[]> _cachedWeaponColliders = new();

        #endregion

        #region Network Variables

        public NetworkVariable<float> netHealth = new(100f);
        public NetworkVariable<bool> netIsDead = new();
        public NetworkVariable<int> kills = new();
        public NetworkVariable<int> deaths = new();
        public NetworkVariable<int> assists = new();

        public NetworkVariable<float> damageDealt = new(0f,
            NetworkVariableReadPermission.Owner);

        public NetworkVariable<int> playerMaterialIndex = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        // New material packet system NetworkVariables
        [Header("Material Customization (New System)")]
        public NetworkVariable<int> playerMaterialPacketIndex = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);
        
        // Base color as Vector4 (RGBA) for network serialization
        public NetworkVariable<Vector4> playerBaseColor = new(new Vector4(1f, 1f, 1f, 1f),
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);
        
        public NetworkVariable<float> playerSmoothness = new(0.5f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);
        
        public NetworkVariable<float> playerMetallic = new(0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);
        
        // Specular color as Vector4 (RGBA) for network serialization
        public NetworkVariable<Vector4> playerSpecularColor = new(new Vector4(0.2f, 0.2f, 0.2f, 1f),
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);
        
        public NetworkVariable<float> playerHeightStrength = new(0.02f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<bool> playerEmissionEnabled = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<Vector4> playerEmissionColor = new(new Vector4(0f, 0f, 0f, 1f),
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<FixedString64Bytes> playerName = new("Player",
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<bool> netIsCrouching = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        // Weapon selection NetworkVariables (synced across all clients)
        public NetworkVariable<int> primaryWeaponIndex = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<int> secondaryWeaponIndex = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        #endregion

        #region Unity Lifecycle

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Network-dependent initialization
            SubscribeToNetworkVariables();

            // Apply new material system (will use defaults if not set)
            UpdatePlayerMaterialFromNetwork();

            // Only enable character controller if player is not dead
            if(characterController.enabled == false && !netIsDead.Value) {
                characterController.enabled = true;
            }

            // Note: Main player object should be set to Default layer in inspector
            // Ragdoll components are set to Enemy layer in PlayerRagdoll.OnNetworkSpawn()

            var gameMenu = GameMenuManager.Instance;
            if(gameMenu != null && gameMenu.TryGetComponent(out UIDocument doc)) {
                var root = doc.rootVisualElement;
                var rootContainer = root?.Q<VisualElement>("root-container");
                if(rootContainer != null)
                    rootContainer.style.display = DisplayStyle.Flex;
            }

            EventBus.Publish(new ShowHUDEvent());
            if(IsOwner && fpCamera && lookController != null) {
                fpCamera.Lens.FieldOfView = lookController.BaseFov;
            }

            GameMenuManager.Instance.IsPostMatch = false;
            PostMatchManager.Instance.ShowInGameHudAfterPostMatch();

            // Track spawn time to prevent landing sounds on initial spawn
            if(animationController != null)
                animationController.ResetSpawnTime();

            if(GameMenuManager.Instance.IsPaused) {
                GameMenuManager.Instance.TogglePause();
            }

            if(IsOwner) {
                playerName.Value = PlayerPrefs.GetString("PlayerName", "Unknown Player");
                
                // Load weapon selections from PlayerPrefs and sync via NetworkVariables
                primaryWeaponIndex.Value = PlayerPrefs.GetInt("PrimaryWeaponIndex", 0);
                secondaryWeaponIndex.Value = PlayerPrefs.GetInt("SecondaryWeaponIndex", 0);
                
                // Load new material customization system
                LoadMaterialCustomizationFromPrefs();
                
                GrappleUIManager.Instance.RegisterLocalPlayer(this);

                // Initialize tag status for HUD in Tag mode
                var matchSettings = MatchSettingsManager.Instance;
                if(matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag" && tagController != null) {
                    EventBus.Publish(new UpdateTagStatusEvent(tagController.isTagged.Value));
                }

                if(playerShadow != null)
                    playerShadow.ApplyOwnerDefaultShadowState();
            } else {
                // Ensure world model root and weapon are active for non-owner players
                if(playerModelRoot != null && !playerModelRoot.activeSelf) {
                    playerModelRoot.SetActive(true);
                }

                if(visualController != null) {
                    // Invalidate renderer cache to force refresh
                    visualController.InvalidateRendererCache();
                    // Enable all renderers and set proper shadow modes
                    visualController.SetRenderersEnabled(true);
                    // Force bounds update immediately
                    visualController.ForceRendererBoundsUpdate();
                }

                if(playerShadow != null)
                    playerShadow.ApplyVisibleShadowState();
            }
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            UnsubscribeFromNetworkVariables();
        }

        /// <summary>
        /// Subscribes to all NetworkVariable value change callbacks.
        /// </summary>
        private void SubscribeToNetworkVariables() {
            playerMaterialIndex.OnValueChanged -= OnMatChanged;
            playerMaterialIndex.OnValueChanged += OnMatChanged;
            
            playerMaterialPacketIndex.OnValueChanged -= OnMaterialPacketChanged;
            playerMaterialPacketIndex.OnValueChanged += OnMaterialPacketChanged;
            playerBaseColor.OnValueChanged -= OnMaterialCustomizationChanged;
            playerBaseColor.OnValueChanged += OnMaterialCustomizationChanged;
            playerSmoothness.OnValueChanged -= OnMaterialCustomizationChanged;
            playerSmoothness.OnValueChanged += OnMaterialCustomizationChanged;
            playerMetallic.OnValueChanged -= OnMaterialCustomizationChanged;
            playerMetallic.OnValueChanged += OnMaterialCustomizationChanged;
            playerSpecularColor.OnValueChanged -= OnMaterialCustomizationChanged;
            playerSpecularColor.OnValueChanged += OnMaterialCustomizationChanged;
            playerHeightStrength.OnValueChanged -= OnMaterialCustomizationChanged;
            playerHeightStrength.OnValueChanged += OnMaterialCustomizationChanged;
            playerEmissionEnabled.OnValueChanged -= OnMaterialCustomizationChanged;
            playerEmissionEnabled.OnValueChanged += OnMaterialCustomizationChanged;
            playerEmissionColor.OnValueChanged -= OnMaterialCustomizationChanged;
            playerEmissionColor.OnValueChanged += OnMaterialCustomizationChanged;
            netHealth.OnValueChanged -= OnHealthChanged;
            netHealth.OnValueChanged += OnHealthChanged;
            netIsCrouching.OnValueChanged -= OnCrouchStateChanged;
            netIsCrouching.OnValueChanged += OnCrouchStateChanged;
            netIsDead.OnValueChanged -= OnDeathStateChanged;
            netIsDead.OnValueChanged += OnDeathStateChanged;
        }

        /// <summary>
        /// Unsubscribes from all NetworkVariable value change callbacks.
        /// </summary>
        private void UnsubscribeFromNetworkVariables() {
            playerMaterialIndex.OnValueChanged -= OnMatChanged;
            playerMaterialPacketIndex.OnValueChanged -= OnMaterialPacketChanged;
            playerBaseColor.OnValueChanged -= OnMaterialCustomizationChanged;
            playerSmoothness.OnValueChanged -= OnMaterialCustomizationChanged;
            playerMetallic.OnValueChanged -= OnMaterialCustomizationChanged;
            playerSpecularColor.OnValueChanged -= OnMaterialCustomizationChanged;
            playerHeightStrength.OnValueChanged -= OnMaterialCustomizationChanged;
            playerEmissionEnabled.OnValueChanged -= OnMaterialCustomizationChanged;
            playerEmissionColor.OnValueChanged -= OnMaterialCustomizationChanged;
            netHealth.OnValueChanged -= OnHealthChanged;
            netIsCrouching.OnValueChanged -= OnCrouchStateChanged;
            netIsDead.OnValueChanged -= OnDeathStateChanged;
        }

        private void OnMatChanged(int _, int newIdx) {
            // Legacy material system - no longer used, new system handles materials via UpdatePlayerMaterialFromNetwork()
        }

        /// <summary>
        /// Called when material packet index changes. Triggers material update.
        /// </summary>
        private void OnMaterialPacketChanged(int _, int newIndex) {
            UpdatePlayerMaterialFromNetwork();
        }

        /// <summary>
        /// Called when any material customization value changes. Triggers material update.
        /// </summary>
        private void OnMaterialCustomizationChanged<T>(T _, T __) {
            UpdatePlayerMaterialFromNetwork();
        }

        /// <summary>
        /// Updates the player material using the new packet-based system from network values.
        /// </summary>
        private void UpdatePlayerMaterialFromNetwork() {
            if(visualController == null) return;

            var baseColor = new Color(
                playerBaseColor.Value.x,
                playerBaseColor.Value.y,
                playerBaseColor.Value.z,
                playerBaseColor.Value.w
            );

            var specularColor = new Color(
                playerSpecularColor.Value.x,
                playerSpecularColor.Value.y,
                playerSpecularColor.Value.z,
                playerSpecularColor.Value.w
            );

            var emissionColor = new Color(
                playerEmissionColor.Value.x,
                playerEmissionColor.Value.y,
                playerEmissionColor.Value.z,
                playerEmissionColor.Value.w
            );

            visualController.ApplyPlayerMaterialCustomization(
                playerMaterialPacketIndex.Value,
                baseColor,
                playerSmoothness.Value,
                playerMetallic.Value,
                specularColor,
                Mathf.Clamp(playerHeightStrength.Value, MinHeightStrength, MaxHeightStrength),
                playerEmissionEnabled.Value,
                emissionColor
            );
        }

        /// <summary>
        /// Loads material customization values from PlayerPrefs.
        /// Falls back to defaults if not found.
        /// </summary>
        private void LoadMaterialCustomizationFromPrefs() {
            // Packet index (0 = None, 1+ = loaded packets)
            var savedPacketIndex = PlayerPrefs.GetInt("PlayerMaterialPacketIndex", 0);
            playerMaterialPacketIndex.Value = savedPacketIndex;

            // Base color (RGBA)
            var baseColorR = PlayerPrefs.GetFloat("PlayerBaseColorR", 1f);
            var baseColorG = PlayerPrefs.GetFloat("PlayerBaseColorG", 1f);
            var baseColorB = PlayerPrefs.GetFloat("PlayerBaseColorB", 1f);
            var baseColorA = PlayerPrefs.GetFloat("PlayerBaseColorA", 1f);
            playerBaseColor.Value = new Vector4(baseColorR, baseColorG, baseColorB, baseColorA);

            // Smoothness
            playerSmoothness.Value = PlayerPrefs.GetFloat("PlayerSmoothness", 0f);

            // Metallic
            playerMetallic.Value = PlayerPrefs.GetFloat("PlayerMetallic", 0f);

            // Specular color (RGBA)
            var specularR = PlayerPrefs.GetFloat("PlayerSpecularColorR", 0.2f);
            var specularG = PlayerPrefs.GetFloat("PlayerSpecularColorG", 0.2f);
            var specularB = PlayerPrefs.GetFloat("PlayerSpecularColorB", 0.2f);
            var specularA = PlayerPrefs.GetFloat("PlayerSpecularColorA", 1f);
            playerSpecularColor.Value = new Vector4(specularR, specularG, specularB, specularA);

            // Height strength
            var savedHeight = PlayerPrefs.GetFloat("PlayerHeightStrength", 0.02f);
            playerHeightStrength.Value = Mathf.Clamp(savedHeight, MinHeightStrength, MaxHeightStrength);

            // Emission
            playerEmissionEnabled.Value = PlayerPrefs.GetInt("PlayerEmissionEnabled", 0) == 1;
            var emissionR = PlayerPrefs.GetFloat("PlayerEmissionColorR", 0f);
            var emissionG = PlayerPrefs.GetFloat("PlayerEmissionColorG", 0f);
            var emissionB = PlayerPrefs.GetFloat("PlayerEmissionColorB", 0f);
            var emissionA = PlayerPrefs.GetFloat("PlayerEmissionColorA", 1f);
            playerEmissionColor.Value = new Vector4(emissionR, emissionG, emissionB, emissionA);

            // Apply immediately
            UpdatePlayerMaterialFromNetwork();
        }

        /// <summary>
        /// Saves material customization values to PlayerPrefs.
        /// </summary>
        public void SaveMaterialCustomizationToPrefs() {
            PlayerPrefs.SetInt("PlayerMaterialPacketIndex", playerMaterialPacketIndex.Value);
            PlayerPrefs.SetFloat("PlayerBaseColorR", playerBaseColor.Value.x);
            PlayerPrefs.SetFloat("PlayerBaseColorG", playerBaseColor.Value.y);
            PlayerPrefs.SetFloat("PlayerBaseColorB", playerBaseColor.Value.z);
            PlayerPrefs.SetFloat("PlayerBaseColorA", playerBaseColor.Value.w);
            PlayerPrefs.SetFloat("PlayerSmoothness", playerSmoothness.Value);
            PlayerPrefs.SetFloat("PlayerMetallic", playerMetallic.Value);
            PlayerPrefs.SetFloat("PlayerSpecularColorR", playerSpecularColor.Value.x);
            PlayerPrefs.SetFloat("PlayerSpecularColorG", playerSpecularColor.Value.y);
            PlayerPrefs.SetFloat("PlayerSpecularColorB", playerSpecularColor.Value.z);
            PlayerPrefs.SetFloat("PlayerSpecularColorA", playerSpecularColor.Value.w);
            var clampedHeight = Mathf.Clamp(playerHeightStrength.Value, MinHeightStrength, MaxHeightStrength);
            PlayerPrefs.SetFloat("PlayerHeightStrength", clampedHeight);
            PlayerPrefs.SetInt("PlayerEmissionEnabled", playerEmissionEnabled.Value ? 1 : 0);
            PlayerPrefs.SetFloat("PlayerEmissionColorR", playerEmissionColor.Value.x);
            PlayerPrefs.SetFloat("PlayerEmissionColorG", playerEmissionColor.Value.y);
            PlayerPrefs.SetFloat("PlayerEmissionColorB", playerEmissionColor.Value.z);
            PlayerPrefs.SetFloat("PlayerEmissionColorA", playerEmissionColor.Value.w);
            PlayerPrefs.Save();
        }

        private void OnHealthChanged(float oldV, float newV) {
            if(IsOwner) EventBus.Publish(new UpdateHealthEvent(newV, 100f));
        }

        private void OnCrouchStateChanged(bool oldValue, bool newValue) {
            if(movementController != null)
                movementController.UpdateCrouch(fpCamera);
        }

        private void OnDeathStateChanged(bool oldValue, bool newValue) {
            // Disable character controller when player dies
            if(newValue && characterController != null) {
                characterController.enabled = false;
            }
            // Note: Character controller is re-enabled during respawn in PlayerHealthController.RestoreControlAfterFadeInClientRpc()
        }

        private void Update() {
            if(IsServer) {
                var authPos = clientNetworkTransform.transform.position;
                ValidateServerMovement(authPos);
                if(authPos.y <= 600f) {
                    // Only trigger OOB death if player is not already dead and cooldown has passed
                    // The health controller now resets netIsDead immediately when respawn starts,
                    // so this check prevents race conditions during respawn
                    if(!netIsDead.Value && Time.time - _lastDeathTime >= 4f) {
                        _lastDeathTime = Time.time;
                        // OOB death handled by health controller
                        if(healthController != null)
                            healthController.ApplyDamageServer_Auth(1000f, playerTransform.position, Vector3.up, ulong.MaxValue);
                    }
                }

                if(healthController != null)
                    healthController.UpdateHealthRegeneration();
            }

            if(netIsDead.Value || characterController.enabled == false) return;

            if(IsOwner) {
                if(movementController != null) {
                    movementController.UpdateMovement(fpCamera);
                    movementController.UpdateCrouch(fpCamera);

                    if(animationController != null) {
                        animationController.UpdateFallingState(movementController.IsGrounded,
                            movementController.VerticalVelocity, playerTransform.position);
                        animationController.UpdateAnimator(movementController.HorizontalVelocity,
                            movementController.MaxSpeed, movementController.CachedHorizontalSpeedSqr);
                    }
                }

                if(lookController != null)
                    lookController.UpdateSpeedFov();

                if(statsController != null)
                    statsController.TrackVelocity();
            } else {
                if(movementController != null)
                    movementController.UpdateCrouch(fpCamera);

                if(animationController != null)
                    animationController.SetCrouching(netIsCrouching.Value);

                // Periodic visibility check for non-owner players
                // This helps catch and fix cases where renderers become invisible due to bounds issues
                if(visualController == null || Time.frameCount % 60 != 0) return;
                visualController.VerifyAndFixVisibility();
            }
        }

        private void LateUpdate() {
            if(!IsOwner || netIsDead.Value) return;

            if(lookController != null)
                lookController.UpdateLook();
        }

        private void ValidateServerMovement(Vector3 position) {
            var config = AntiCheatConfig.Instance;
            if(config == null || clientNetworkTransform == null) return;

            var now = Time.time;
            if(!_hasServerMovementSample) {
                _lastServerMovementPosition = position;
                _lastServerMovementTime = now;
                _hasServerMovementSample = true;
                return;
            }

            // Clean up old violations outside the time window
            _movementViolations.RemoveAll(v => now - v.Time > config.movementViolationWindowSeconds);

            var delta = position - _lastServerMovementPosition;
            var distance = delta.magnitude;
            var dt = Mathf.Max(0.0001f, now - _lastServerMovementTime);
            var adjustedPosition = position;

            // Check for teleport violation
            if(distance > config.maxTeleportDistance) {
                _movementViolations.Add(new MovementViolation { Time = now, WasSpeedViolation = false });
                
                // Count violations in the time window
                var teleportViolations = _movementViolations.Count(v => !v.WasSpeedViolation);
                
                if(teleportViolations >= config.teleportViolationThreshold) {
                    AntiCheatLogger.LogMovementEnforcement(OwnerClientId,
                        $"teleport {distance:F1}m (limit {config.maxTeleportDistance:F1}) - {teleportViolations} violations in window");

                    if(delta.sqrMagnitude > 0.0001f) {
                        var clamped =
                            _lastServerMovementPosition + delta.normalized * config.maxTeleportDistance;
                        clientNetworkTransform.Teleport(clamped, playerTransform.rotation, Vector3.one);
                        adjustedPosition = clamped;
                        delta = clamped - _lastServerMovementPosition;
                        distance = delta.magnitude;
                    } else {
                        adjustedPosition = _lastServerMovementPosition;
                    }
                }
            }

            // Check for speed violation
            var speed = distance / dt;
            if(speed > config.maxSpeedMetersPerSecond && delta.sqrMagnitude > 0.0001f) {
                _movementViolations.Add(new MovementViolation { Time = now, WasSpeedViolation = true });
                
                // Count speed violations in the time window
                var speedViolations = _movementViolations.Count(v => v.WasSpeedViolation);
                
                if(speedViolations >= config.speedViolationThreshold) {
                    AntiCheatLogger.LogMovementEnforcement(OwnerClientId,
                        $"speed {speed:F1} m/s (limit {config.maxSpeedMetersPerSecond:F1}) - {speedViolations} violations in window");

                    var allowedDistance = config.maxSpeedMetersPerSecond * dt;
                    var clamped =
                        _lastServerMovementPosition + delta.normalized * allowedDistance;
                    clientNetworkTransform.Teleport(clamped, playerTransform.rotation, Vector3.one);
                    adjustedPosition = clamped;
                }
            } else {
                // Player is within speed limits - clear violation history if they've been good for a bit
                // This allows occasional spikes without penalty
                if(_movementViolations.Count == 0 || now - _movementViolations[^1].Time > config.movementViolationWindowSeconds * 0.5f) {
                    _movementViolations.Clear();
                }
            }

            _lastServerMovementPosition = adjustedPosition;
            _lastServerMovementTime = now;
            _hasServerMovementSample = true;
        }

        #endregion

        #region Collision Handling

        private void OnControllerColliderHit(ControllerColliderHit hit) {
            if(hit.gameObject.CompareTag("JumpPad")) {
                // Cancel any active grapple before launching
                grappleController.CancelGrapple();

                // Use the jump pad's own transform.up (surface normal) instead of collision normal
                // This preserves horizontal velocity (e.g., from grappling) while adding vertical boost
                if(movementController == null) {
                    Debug.LogError("[PlayerController] MovementController not found! Cannot launch from jump pad.");
                    return;
                }
                var padNormal = hit.gameObject.transform.up;
                movementController.LaunchFromJumpPad(padNormal);
            } else if(hit.gameObject.CompareTag("MegaPad")) {
                // Cancel any active grapple before launching
                grappleController.CancelGrapple();

                // Mega jump pad: provides greater boost
                // Use the jump pad's own transform.up (surface normal) instead of collision normal
                if(movementController == null) {
                    Debug.LogError("[PlayerController] MovementController not found! Cannot launch from mega pad.");
                    return;
                }
                var padNormal = hit.gameObject.transform.up;
                movementController.LaunchFromJumpPad(padNormal, force: 30f);
            } else {
                // Cancel grapple for other collisions (non-jump pad)
                grappleController.CancelGrapple();
            }
        }

        #endregion

        #region Damage & Death Methods

        public bool ApplyDamageServer_Auth(float amount, Vector3 hitPoint, Vector3 hitDirection, ulong attackerId,
            string bodyPartTag = null, bool isHeadshot = false) {
            if(healthController != null) {
                return healthController.ApplyDamageServer_Auth(amount, hitPoint, hitDirection, attackerId, bodyPartTag,
                    isHeadshot);
            }

            return false;
        }

        #endregion

        #region Health & Animation

        public void ResetHealthAndRegenerationState() {
            if(healthController != null)
                healthController.ResetHealthAndRegenerationState();
        }

        public Color CurrentBaseColor => new(
            playerBaseColor.Value.x,
            playerBaseColor.Value.y,
            playerBaseColor.Value.z,
            playerBaseColor.Value.w
        );

        public void ResetWeaponState(bool resetAllAmmo = false, bool switchToWeapon0 = false, bool updateHUD = false) {
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
                    EnsureWeaponHierarchyActive(weaponInstance);
                    EnsureWeaponShadowVisibility(weaponInstance);

                    // Set position and rotation
                    weaponInstance.transform.localPosition = currentWeapon.GetSpawnPosition();
                    weaponInstance.transform.localEulerAngles = currentWeapon.GetSpawnRotation();

                    // Get cached MeshRenderers or cache them if not found
                    if(!_cachedWeaponRenderers.TryGetValue(weaponInstance, out var meshRenderers)) {
                        meshRenderers = weaponInstance.GetComponentsInChildren<MeshRenderer>(true);
                        _cachedWeaponRenderers[weaponInstance] = meshRenderers;
                    }

                    if(playerRenderer == null) {
                        Debug.LogError("[PlayerController] PlayerRenderer not found! Cannot enable world weapon renderers.");
                        return;
                    }
                    playerRenderer.SetWorldWeaponRenderersEnabled(true);
                }

                // if(worldWeapon != null) {
                //     worldWeapon.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                // }

                if(updateHUD) {
                    EventBus.Publish(new UpdateAmmoEvent(currentWeapon.currentAmmo, currentWeapon.GetMagSize()));
                    EventBus.Publish(new UpdateHealthEvent(netHealth.Value, 100f));
                }
            }

            if(switchToWeapon0) {
                playerInput.SwitchWeapon(0);
            }
        }

        /// <summary>
        /// Ensures the FP weapon hierarchy (including parents and colliders) is active so it can render and cast shadows.
        /// </summary>
        private void EnsureWeaponHierarchyActive(GameObject weaponInstance) {
            if(weaponInstance == null) return;

            var parent = weaponInstance.transform;
            while(parent != null) {
                if(!parent.gameObject.activeSelf) {
                    parent.gameObject.SetActive(true);
                }

                parent = parent.parent;
            }

            weaponInstance.SetActive(true);

            if(!_cachedWeaponColliders.TryGetValue(weaponInstance, out var colliders)) {
                colliders = weaponInstance.GetComponentsInChildren<Collider>(true);
                _cachedWeaponColliders[weaponInstance] = colliders;
            }

            foreach(var col in colliders) {
                if(col != null && !col.enabled) {
                    col.enabled = true;
                }
            }
        }

        /// <summary>
        /// Forces all renderers in the FP weapon hierarchy to be enabled and casting shadows.
        /// </summary>
        private void EnsureWeaponShadowVisibility(GameObject weaponInstance) {
            if(weaponInstance == null) return;

            if(!_cachedWeaponRenderers.TryGetValue(weaponInstance, out var meshRenderers)) {
                meshRenderers = weaponInstance.GetComponentsInChildren<MeshRenderer>(true);
                _cachedWeaponRenderers[weaponInstance] = meshRenderers;
            }

            // Use PlayerRenderer for enabled state, shadow mode is handled by PlayerShadow
            if(playerRenderer == null) {
                Debug.LogError("[PlayerController] PlayerRenderer not found! Cannot enable world weapon renderers.");
                return;
            }
            playerRenderer.SetWorldWeaponRenderersEnabled(true);
            
            // Shadow mode is handled by PlayerShadow, but we set it here for initial setup
            foreach(var meshRenderer in meshRenderers) {
                if(meshRenderer == null) continue;
                meshRenderer.shadowCastingMode = ShadowCastingMode.On;
            }
        }

        #endregion
    }
}
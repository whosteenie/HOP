using System.Collections.Generic;
using System.Linq;
using Audio.Networking;
using Game.Weapons;
using Game.UI;
using Game.Menu;
using Game.Match;
using Network.Core;
using Network.AntiCheat;
using Network.Components;
using Network.Events;
using Network.Rpc;
using OSI;
using Steamworks;
using Unity.Cinemachine;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Game.Settings;

namespace Game.Player {
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(NetworkAudioRelay))]
    [DefaultExecutionOrder(-100)] // Initialize before sub-controllers
    public partial class PlayerController : NetworkBehaviour {
        public static PlayerController LocalPlayer { get; private set; }
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
        [SerializeField] private WallRunController wallRunController;


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
        [SerializeField] private NetworkAudioRelay audioRelay;
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
        public bool LockLook { get; set; }

        #endregion

        #region Private Fields

        private float _lastDeathTime; // Used for OOB check in Update()
        private float _ignoreOutOfBoundsUntilTime;
        [Header("Out Of Bounds")]
        [SerializeField] private string outOfBoundsMarkerName = "OOB";
        [SerializeField] private string outOfBoundsMarkerTag = "OOB";
        [SerializeField] private float defaultOutOfBoundsY = 600f;
        private int _cachedOobSceneHandle = -1;
        private float _cachedOutOfBoundsY;
        private bool _cachedUseYLevelOutOfBoundsKill = true;
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

        public NetworkVariable<ulong> steamId = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);
        
        public NetworkVariable<FixedString128Bytes> ugsId = new("",
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<FixedString64Bytes> playerName = new("Player",
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<bool> netIsCrouching = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<bool> netIsSliding = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        // Weapon selection NetworkVariables (synced across all clients)
        public NetworkVariable<int> primaryWeaponIndex = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<int> secondaryWeaponIndex = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);
        
        // Voice PTT state (synced so other players see speaking indicator)
        public NetworkVariable<bool> isPttActive = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        #endregion

        #region Public Properties

        public float CurrentPitch => lookController != null ? lookController.CurrentPitch : 0f;
        public WallRunController WallRunController => wallRunController;

        #endregion

        #region Unity Lifecycle

        private void Awake() {
            if(audioRelay == null) {
                audioRelay = GetComponent<NetworkAudioRelay>();
            }
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            if (IsOwner) {
                LocalPlayer = this;
            }

            SubscribeToNetworkVariables();
            UpdatePlayerMaterialFromNetwork();

            if(characterController.enabled == false && !netIsDead.Value) {
                characterController.enabled = true;
            }

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

            if(animationController != null)
                animationController.ResetSpawnTime();

            if(GameMenuManager.Instance.IsPaused) {
                GameMenuManager.Instance.TogglePause();
            }
            
            if (ScoreboardManager.Instance != null) {
                ScoreboardManager.Instance.RegisterPlayer(this);
            }

            if(IsOwner) {
                string pName;
                if(SteamClient.IsValid && SteamClient.IsLoggedOn) {
                    pName = Social.StreamerMode.GetLocalDisplayName();
                    steamId.Value = SteamClient.SteamId.Value;
                } else {
                    pName = Social.StreamerMode.LocalDisplayName;
                }
                playerName.Value = pName;

                var ugsPlayerId = LocalIdentity.GetUgsPlayerId();
                if(!string.IsNullOrEmpty(ugsPlayerId)) {
                    ugsId.Value = ugsPlayerId;
                }
                
                primaryWeaponIndex.Value = GameSettings.Data.player.primaryWeaponIndex;
                secondaryWeaponIndex.Value = GameSettings.Data.player.secondaryWeaponIndex;
                
                LoadMaterialCustomizationFromPrefs();
                
                GrappleUIManager.Instance.RegisterLocalPlayer(this);

                var matchSettings = MatchSettingsManager.Instance;
                if(matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag" && tagController != null) {
                    EventBus.Publish(new UpdateTagStatusEvent(tagController.isTagged.Value));
                }

                if(playerShadow != null)
                    playerShadow.ApplyOwnerDefaultShadowState();
            } else {
                if(playerModelRoot != null && !playerModelRoot.activeSelf) {
                    playerModelRoot.SetActive(true);
                }

                if(visualController != null) {
                    visualController.InvalidateRendererCache();
                    visualController.SetRenderersEnabled(true);
                    visualController.ForceRendererBoundsUpdate();
                }

                if(playerShadow != null)
                    playerShadow.ApplyVisibleShadowState();
            }
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            
            if (LocalPlayer == this) {
                LocalPlayer = null;
            }

            UnsubscribeFromNetworkVariables();
            
            // Unregister from ScoreboardManager
            if (ScoreboardManager.Instance != null) {
                ScoreboardManager.Instance.UnregisterPlayer(this);
            }
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

        private static void OnMatChanged(int _, int newIdx) {
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
        /// Loads material customization values from settings.json.
        /// </summary>
        private void LoadMaterialCustomizationFromPrefs() {
            var c = GameSettings.Data.player.customization;

            playerMaterialPacketIndex.Value = c.materialPacketIndex;
            playerBaseColor.Value = c.baseColor;
            playerSmoothness.Value = c.smoothness;
            playerMetallic.Value = c.metallic;
            playerSpecularColor.Value = c.specularColor;
            playerHeightStrength.Value = Mathf.Clamp(c.heightStrength, MinHeightStrength, MaxHeightStrength);
            playerEmissionEnabled.Value = c.emissionEnabled;
            playerEmissionColor.Value = c.emissionColor;

            UpdatePlayerMaterialFromNetwork();
        }

        /// <summary>
        /// Saves material customization values to settings.json.
        /// </summary>
        public void SaveMaterialCustomizationToPrefs() {
            var c = GameSettings.Data.player.customization;
            c.materialPacketIndex = playerMaterialPacketIndex.Value;
            c.baseColor = playerBaseColor.Value;
            c.smoothness = playerSmoothness.Value;
            c.metallic = playerMetallic.Value;
            c.specularColor = playerSpecularColor.Value;
            c.heightStrength = Mathf.Clamp(playerHeightStrength.Value, MinHeightStrength, MaxHeightStrength);
            c.emissionEnabled = playerEmissionEnabled.Value;
            c.emissionColor = playerEmissionColor.Value;
            GameSettings.Save();
        }

        private void OnHealthChanged(float oldV, float newV) {
            if(IsOwner) EventBus.Publish(new UpdateHealthEvent(newV, 100f));
        }

        private void OnCrouchStateChanged(bool oldValue, bool newValue) {
            if(movementController != null)
                movementController.UpdateCrouch(fpCamera);
        }

        private void OnDeathStateChanged(bool oldValue, bool newValue) {
            if(newValue && characterController != null) {
                characterController.enabled = false;
            }
        }
    
        /// <summary>
        /// Main update loop for core player logic, movement synchronization, and server validation.
        /// </summary>
        private void Update() {
            if(IsServer) {
                var authPos = clientNetworkTransform.transform.position;
                ValidateServerMovement(authPos);
                if(IsYLevelOutOfBoundsKillEnabled() &&
                   authPos.y <= GetOutOfBoundsKillY() &&
                   Time.time >= _ignoreOutOfBoundsUntilTime) {
                    if(!netIsDead.Value && Time.time - _lastDeathTime >= 4f) {
                        _lastDeathTime = Time.time;
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

                        var animHorizontal = movementController.HorizontalVelocity;
                        var animSpeedSqr = movementController.CachedHorizontalSpeedSqr;
                        if(characterController != null && movementController.IsGrounded) {
                            var actual = characterController.velocity;
                            actual.y = 0f;
                            var actualSpeed = actual.magnitude;
                            if(actualSpeed < 0.2f) {
                                animHorizontal = actual;
                                animSpeedSqr = actual.sqrMagnitude;
                            } else {
                                var intended = movementController.HorizontalVelocity;
                                intended.y = 0f;
                                var blendedSpeed = Mathf.Lerp(actualSpeed, intended.magnitude, 0.4f);
                                if(actualSpeed > 0.0001f) {
                                    animHorizontal = actual.normalized * blendedSpeed;
                                } else {
                                    animHorizontal = actual;
                                }
                                animSpeedSqr = animHorizontal.sqrMagnitude;
                            }
                        }

                        animationController.UpdateAnimator(animHorizontal,
                            movementController.MaxSpeed, animSpeedSqr);
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

                if(visualController == null || Time.frameCount % 60 != 0) return;
                visualController.VerifyAndFixVisibility();
            }
        }

        private void LateUpdate() {
            if(!IsOwner || netIsDead.Value) return;

            if(lookController != null)
                lookController.UpdateLook();
        }

        /// <summary>
        /// Validates client movement on the server to prevent cheating (teleporting/speed hacking).
        /// </summary>
        private void ValidateServerMovement(Vector3 position) {
            var config = AntiCheatConfig.Instance;
            if(config == null || clientNetworkTransform == null) return;

            var now = Time.time;
            if(netIsDead is { Value: true }) {
                _movementViolations.Clear();
                _lastServerMovementPosition = position;
                _lastServerMovementTime = now;
                _hasServerMovementSample = true;
                return;
            }

            if(!_hasServerMovementSample) {
                _lastServerMovementPosition = position;
                _lastServerMovementTime = now;
                _hasServerMovementSample = true;
                return;
            }

            _movementViolations.RemoveAll(v => now - v.Time > config.movementViolationWindowSeconds);

            var delta = position - _lastServerMovementPosition;
            var distance = delta.magnitude;
            var dt = Mathf.Max(0.0001f, now - _lastServerMovementTime);
            var adjustedPosition = position;

            if(distance > config.maxTeleportDistance) {
                _movementViolations.Add(new MovementViolation { Time = now, WasSpeedViolation = false });
                
                var teleportViolations = _movementViolations.Count(v => !v.WasSpeedViolation);
                
                if(teleportViolations >= config.teleportViolationThreshold) {
                    AntiCheatLogger.LogMovementEnforcement(OwnerClientId,
                        $"teleport {distance:F1}m (limit {config.maxTeleportDistance:F1}) - {teleportViolations} violations in window");

                    if(delta.sqrMagnitude > 0.0001f) {
                        var clamped =
                            _lastServerMovementPosition + delta.normalized * config.maxTeleportDistance;
                        ApplyServerMovementCorrectionOwnerRpc(clamped, playerTransform.rotation);
                        adjustedPosition = clamped;
                        delta = clamped - _lastServerMovementPosition;
                        distance = delta.magnitude;
                    } else {
                        adjustedPosition = _lastServerMovementPosition;
                    }
                }
            }

            var speed = distance / dt;
            if(speed > config.maxSpeedMetersPerSecond && delta.sqrMagnitude > 0.0001f) {
                _movementViolations.Add(new MovementViolation { Time = now, WasSpeedViolation = true });
                
                var speedViolations = _movementViolations.Count(v => v.WasSpeedViolation);
                
                if(speedViolations >= config.speedViolationThreshold) {
                    AntiCheatLogger.LogMovementEnforcement(OwnerClientId,
                        $"speed {speed:F1} m/s (limit {config.maxSpeedMetersPerSecond:F1}) - {speedViolations} violations in window");

                    var allowedDistance = config.maxSpeedMetersPerSecond * dt;
                    var clamped =
                        _lastServerMovementPosition + delta.normalized * allowedDistance;
                    ApplyServerMovementCorrectionOwnerRpc(clamped, playerTransform.rotation);
                    adjustedPosition = clamped;
                }
            } else {
                if(_movementViolations.Count == 0 || now - _movementViolations[^1].Time > config.movementViolationWindowSeconds * 0.5f) {
                    _movementViolations.Clear();
                }
            }

            _lastServerMovementPosition = adjustedPosition;
            _lastServerMovementTime = now;
            _hasServerMovementSample = true;
        }

        [Rpc(SendTo.Owner)]
        private void ApplyServerMovementCorrectionOwnerRpc(Vector3 correctedPosition, Quaternion correctedRotation) {
            if(netIsDead is { Value: true }) return;

            var shouldReEnableCharacterController = characterController != null && characterController.enabled;
            if(characterController != null) {
                characterController.enabled = false;
            }

            if(clientNetworkTransform != null) {
                clientNetworkTransform.Teleport(correctedPosition, correctedRotation, Vector3.one);
            } else if(playerTransform != null) {
                playerTransform.SetPositionAndRotation(correctedPosition, correctedRotation);
            }

            if(movementController != null) {
                movementController.ResetVelocity();
            }

            if(characterController != null && shouldReEnableCharacterController) {
                characterController.enabled = true;
            }
        }

        public void SetOutOfBoundsGraceWindow(float seconds) {
            if(!IsServer) return;

            var duration = Mathf.Max(0f, seconds);
            _ignoreOutOfBoundsUntilTime = Mathf.Max(_ignoreOutOfBoundsUntilTime, Time.time + duration);
        }

        public float GetOutOfBoundsKillY() {
            RefreshOutOfBoundsCacheIfNeeded();
            return _cachedOutOfBoundsY;
        }

        private bool IsYLevelOutOfBoundsKillEnabled() {
            RefreshOutOfBoundsCacheIfNeeded();
            return _cachedUseYLevelOutOfBoundsKill;
        }

        private void RefreshOutOfBoundsCacheIfNeeded() {
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if(_cachedOobSceneHandle == activeScene.handle) {
                return;
            }

            _cachedOobSceneHandle = activeScene.handle;
            _cachedOutOfBoundsY = defaultOutOfBoundsY;
            _cachedUseYLevelOutOfBoundsKill = MatchMapService.IsYLevelOutOfBoundsKillEnabled(activeScene.name);

            Transform marker = null;
            if(!string.IsNullOrWhiteSpace(outOfBoundsMarkerTag)) {
                try {
                    var taggedObject = GameObject.FindGameObjectWithTag(outOfBoundsMarkerTag);
                    if(taggedObject != null) {
                        marker = taggedObject.transform;
                    }
                } catch(UnityException) {
                    // Tag may be undefined in some projects/scenes; fallback to name lookup.
                }
            }

            if(marker == null && !string.IsNullOrWhiteSpace(outOfBoundsMarkerName)) {
                var namedObject = GameObject.Find(outOfBoundsMarkerName);
                if(namedObject != null) {
                    marker = namedObject.transform;
                }
            }

            if(marker != null) {
                _cachedOutOfBoundsY = marker.position.y;
            }
        }

        #endregion

        #region Collision Handling

        private void OnControllerColliderHit(ControllerColliderHit hit) {
            if(hit.gameObject.CompareTag("JumpPad")) {
                grappleController.CancelGrapple();

                if(movementController == null) {
                    Debug.LogError("[PlayerController] MovementController not found!");
                    return;
                }
                var padNormal = hit.gameObject.transform.up;
                movementController.LaunchFromJumpPad(padNormal);
            } else if(hit.gameObject.CompareTag("MegaPad")) {
                grappleController.CancelGrapple();

                if(movementController == null) {
                    Debug.LogError("[PlayerController] MovementController not found!");
                    return;
                }
                var padNormal = hit.gameObject.transform.up;
                movementController.LaunchFromJumpPad(padNormal, force: 30f);
            } else {
                grappleController.CancelGrapple();
            }
        }

        #endregion

        #region Damage & Death Methods

        public bool ApplyDamageServer_Auth(float amount, Vector3 hitPoint, Vector3 hitDirection, ulong attackerId,
            string bodyPartTag = null, bool isHeadshot = false, string weaponId = null) {
            if(healthController != null) {
                return healthController.ApplyDamageServer_Auth(amount, hitPoint, hitDirection, attackerId, bodyPartTag,
                    isHeadshot, weaponId);
            }

            return false;
        }

        #endregion

        #region Health & Animation

        /// <summary>
        /// Resets the player's health and regeneration state.
        /// </summary>
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

        /// <summary>
        /// Resets the player's weapon state, ammo, and HUD.
        /// </summary>
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

                    weaponInstance.transform.localPosition = currentWeapon.GetSpawnPosition();
                    weaponInstance.transform.localEulerAngles = currentWeapon.GetSpawnRotation();

                    if(!_cachedWeaponRenderers.TryGetValue(weaponInstance, out var meshRenderers)) {
                        meshRenderers = weaponInstance.GetComponentsInChildren<MeshRenderer>(true);
                        _cachedWeaponRenderers[weaponInstance] = meshRenderers;
                    }

                    if(playerRenderer == null) {
                        Debug.LogError("[PlayerController] PlayerRenderer not found!");
                        return;
                    }
                    playerRenderer.SetWorldWeaponRenderersEnabled(true);
                }

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

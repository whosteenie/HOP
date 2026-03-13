using System;
using System.Collections.Generic;
using Audio.Networking;
using Game.Match;
using Game.Player.Combat;
using Game.Player.Hopball;
using Game.Player.Look;
using Game.Player.Movement;
using Game.UI;
using Game.Weapons;
using Network;
using Network.Components;
using Network.Core;
using Network.Events;
using Network.Rpc;
using OSI;
using Unity.Cinemachine;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using SessionManager = Network.Session.SessionManager;

namespace Game.Player.Core {
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(NetworkAudioRelay))]
    [DefaultExecutionOrder(-100)] // Initialize before sub-controllers
    public class PlayerController : NetworkBehaviour {
        public static PlayerController LocalPlayer { get; private set; }
        public static event Action<PlayerController> PlayerSpawned;
        public static event Action<PlayerController> PlayerDespawned;
        private static readonly HashSet<PlayerController> SpawnedPlayersRegistry = new();
        public static IReadOnlyCollection<PlayerController> SpawnedPlayers => SpawnedPlayersRegistry;
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

        [Header("KINEMATION Safeguards")]
        [SerializeField] private bool disableKinemationFrameworkComponents = true;
        [SerializeField] private bool disableOnlyKinemationFrameworkCameraComponents;
        [SerializeField] private bool logKinemationFrameworkDisables;
        [SerializeField] private bool disableUnexpectedChildCameras = true;

        #endregion

        #region Public Input Fields

        public Vector2 moveInput;
        public Vector2 lookInput;
        public bool sprintInput;
        public bool crouchInput;
        public bool LockLook { get; set; }

        #endregion

        #region Private Fields

        [Header("Out Of Bounds")]
        [SerializeField] private string outOfBoundsMarkerName = "OOB";
        [SerializeField] private string outOfBoundsMarkerTag = "OOB";
        [SerializeField] private float defaultOutOfBoundsY = 600f;

        private static readonly NetworkVariable<float> MissingHealthState = new(100f);
        private static readonly NetworkVariable<bool> MissingDeathState = new();
        private static readonly NetworkVariable<int> MissingIntState = new();
        private static readonly NetworkVariable<float> MissingFloatState = new();
        private static readonly NetworkVariable<ulong> MissingSteamIdState = new();
        private static readonly NetworkVariable<FixedString128Bytes> MissingUgsIdState = new("");
        private static readonly NetworkVariable<FixedString64Bytes> MissingPlayerNameState = new("Player");

        private const float MinHeightStrength = 0.005f;
        private const float MaxHeightStrength = 0.08f;

        private PlayerNetworkStateCoordinator _networkStateCoordinator;
        private PlayerMaterialCustomizationCoordinator _materialCustomizationCoordinator;
        private PlayerRuntimeSafetyCoordinator _runtimeSafetyCoordinator;
        private PlayerOutOfBoundsCoordinator _outOfBoundsCoordinator;
        private PlayerMovementValidationCoordinator _movementValidationCoordinator;
        private PlayerWeaponPresentationCoordinator _weaponPresentationCoordinator;
        private PlayerSpawnPresentationCoordinator _spawnPresentationCoordinator;
        private PlayerUiEventBridge _uiEventBridge;

        #endregion

        #region Network Variables

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

        public NetworkVariable<bool> netIsCrouching = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<bool> netIsSliding = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<bool> netIsJumping = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<bool> netIsFalling = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<int> jumpAnimationSequence = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<int> landAnimationSequence = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<int> mantleAnimationSequence = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<bool> netIsWallRunning = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<bool> netIsRightWallRun = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<float> netWallRunDirection = new(1f,
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

        internal bool DisableKinemationFrameworkComponentsConfigured => disableKinemationFrameworkComponents;
        internal bool DisableOnlyKinemationFrameworkCameraComponents => disableOnlyKinemationFrameworkCameraComponents;
        internal bool LogKinemationFrameworkDisables => logKinemationFrameworkDisables;
        internal bool DisableUnexpectedChildCamerasConfigured => disableUnexpectedChildCameras;
        internal string OutOfBoundsMarkerName => outOfBoundsMarkerName;
        internal string OutOfBoundsMarkerTag => outOfBoundsMarkerTag;
        internal float DefaultOutOfBoundsY => defaultOutOfBoundsY;
        internal NetworkVariable<int> PlayerMaterialPacketIndexState => playerMaterialPacketIndex;
        internal NetworkVariable<Vector4> PlayerBaseColorState => playerBaseColor;
        internal NetworkVariable<float> PlayerSmoothnessState => playerSmoothness;
        internal NetworkVariable<float> PlayerMetallicState => playerMetallic;
        internal NetworkVariable<Vector4> PlayerSpecularColorState => playerSpecularColor;
        internal NetworkVariable<float> PlayerHeightStrengthState => playerHeightStrength;
        internal NetworkVariable<bool> PlayerEmissionEnabledState => playerEmissionEnabled;
        internal NetworkVariable<Vector4> PlayerEmissionColorState => playerEmissionColor;
        internal static float MinHeightStrengthValue => MinHeightStrength;
        internal static float MaxHeightStrengthValue => MaxHeightStrength;
        internal void AssignWeaponCamera(Camera assignedWeaponCamera) => weaponCamera = assignedWeaponCamera;
        internal void HandleResolvedHealthChanged(float oldValue, float newValue) => OnHealthChanged(oldValue, newValue);
        internal void HandleResolvedDeathChanged(bool oldValue, bool newValue) => OnDeathStateChanged(oldValue, newValue);
        internal void BeginIdentitySyncFromSpawn(ulong localSteamId, string ugsPlayerId, string playerDisplayName) =>
            BeginIdentitySync(localSteamId, ugsPlayerId, playerDisplayName);
        internal void LoadMaterialCustomizationFromPrefsForSpawn() => LoadMaterialCustomizationFromPrefs();

        #endregion

        #region Unity Lifecycle

        private void InitializeCoordinators() {
            _runtimeSafetyCoordinator ??= new PlayerRuntimeSafetyCoordinator(this);
            _outOfBoundsCoordinator ??= new PlayerOutOfBoundsCoordinator(this);
            _networkStateCoordinator ??= new PlayerNetworkStateCoordinator(this);
            _materialCustomizationCoordinator ??= new PlayerMaterialCustomizationCoordinator(this);
            _movementValidationCoordinator ??= new PlayerMovementValidationCoordinator(this);
            _weaponPresentationCoordinator ??= new PlayerWeaponPresentationCoordinator(this);
            _spawnPresentationCoordinator ??= new PlayerSpawnPresentationCoordinator(this);
            _uiEventBridge ??= new PlayerUiEventBridge();
        }

        private void Awake() {
            InitializeCoordinators();
            MarkChildComponentCachesDirty();
            DisableConflictingKinemationFrameworkComponents();
            DisableUnexpectedChildCamerasAndListeners();

            if(audioRelay == null) {
                audioRelay = GetComponent<NetworkAudioRelay>();
            }
        }

        private void OnTransformChildrenChanged() {
            InitializeCoordinators();
            MarkChildComponentCachesDirty();
        }

        private static void RegisterSpawnedPlayer(PlayerController player) {
            if(player == null || !SpawnedPlayersRegistry.Add(player)) return;
            PlayerSpawned?.Invoke(player);
            EventBus.Publish(new PlayerNetworkSpawnedEvent(player));
        }

        private static void UnregisterSpawnedPlayer(PlayerController player) {
            if(player == null || !SpawnedPlayersRegistry.Remove(player)) return;
            PlayerDespawned?.Invoke(player);
            EventBus.Publish(new PlayerNetworkDespawnedEvent(player));
        }

        public override void OnDestroy() {
            CancelPendingIdentitySync();
            UnsubscribeFromNetworkVariables();
            UnregisterSpawnedPlayer(this);
            if(LocalPlayer == this) {
                LocalPlayer = null;
            }
            base.OnDestroy();
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            InitializeCoordinators();
            MarkChildComponentCachesDirty();
            DisableConflictingKinemationFrameworkComponents();
            DisableUnexpectedChildCamerasAndListeners();

            if (IsOwner) {
                LocalPlayer = this;
            }
            RegisterSpawnedPlayer(this);

            SubscribeToNetworkVariables();
            TryBindPlayerStateSubscriptions();
            UpdatePlayerMaterialFromNetwork();
            _spawnPresentationCoordinator.HandleNetworkSpawnPresentation();
        }

        private void DisableConflictingKinemationFrameworkComponents() {
            _runtimeSafetyCoordinator.DisableConflictingKinemationFrameworkComponents();
        }

        private void DisableUnexpectedChildCamerasAndListeners() {
            _runtimeSafetyCoordinator.DisableUnexpectedChildCamerasAndListeners();
        }

        private void MarkChildComponentCachesDirty() {
            _runtimeSafetyCoordinator.MarkChildComponentCachesDirty();
        }

        public override void OnNetworkDespawn() {
            // Capture FP duplicate for unexpected disconnect *before* base/cleanup; player hierarchy still exists.
            if(IsOwner && SessionManager.Instance != null && !SessionManager.Instance.IsExpectedDisconnect) {
                if(DisconnectTransitionController.Instance != null) {
                    DisconnectTransitionController.Instance.CaptureAndShowDuplicateFpVisuals(this);
                }
            }

            base.OnNetworkDespawn();
            
            if (LocalPlayer == this) {
                LocalPlayer = null;
            }
            UnregisterSpawnedPlayer(this);
            CancelPendingIdentitySync();

            UnsubscribeFromNetworkVariables();
        }

        private void BeginIdentitySync(ulong localSteamId, string ugsPlayerId, string playerDisplayName) {
            _networkStateCoordinator.BeginIdentitySync(localSteamId, ugsPlayerId, playerDisplayName);
        }

        private void CancelPendingIdentitySync() {
            _networkStateCoordinator.CancelPendingIdentitySync();
        }

        /// <summary>
        /// Subscribes to all NetworkVariable value change callbacks.
        /// </summary>
        private void SubscribeToNetworkVariables() {
            _networkStateCoordinator.Subscribe();

            playerMaterialIndex.OnValueChanged -= OnMatChanged;
            playerMaterialIndex.OnValueChanged += OnMatChanged;
            _materialCustomizationCoordinator.Subscribe();
            netIsCrouching.OnValueChanged -= OnCrouchStateChanged;
            netIsCrouching.OnValueChanged += OnCrouchStateChanged;
            netIsSliding.OnValueChanged -= OnSlidingStateChanged;
            netIsSliding.OnValueChanged += OnSlidingStateChanged;
            netIsJumping.OnValueChanged -= OnJumpingStateChanged;
            netIsJumping.OnValueChanged += OnJumpingStateChanged;
            netIsFalling.OnValueChanged -= OnFallingStateChanged;
            netIsFalling.OnValueChanged += OnFallingStateChanged;
            jumpAnimationSequence.OnValueChanged -= OnJumpAnimationSequenceChanged;
            jumpAnimationSequence.OnValueChanged += OnJumpAnimationSequenceChanged;
            landAnimationSequence.OnValueChanged -= OnLandAnimationSequenceChanged;
            landAnimationSequence.OnValueChanged += OnLandAnimationSequenceChanged;
            mantleAnimationSequence.OnValueChanged -= OnMantleAnimationSequenceChanged;
            mantleAnimationSequence.OnValueChanged += OnMantleAnimationSequenceChanged;
            netIsWallRunning.OnValueChanged -= OnWallRunStateChanged;
            netIsWallRunning.OnValueChanged += OnWallRunStateChanged;
            netIsRightWallRun.OnValueChanged -= OnWallRunOrientationChanged;
            netIsRightWallRun.OnValueChanged += OnWallRunOrientationChanged;
            netWallRunDirection.OnValueChanged -= OnWallRunDirectionChanged;
            netWallRunDirection.OnValueChanged += OnWallRunDirectionChanged;
        }

        /// <summary>
        /// Unsubscribes from all NetworkVariable value change callbacks.
        /// </summary>
        private void UnsubscribeFromNetworkVariables() {
            _networkStateCoordinator.Unsubscribe();

            playerMaterialIndex.OnValueChanged -= OnMatChanged;
            _materialCustomizationCoordinator.Unsubscribe();
            netIsCrouching.OnValueChanged -= OnCrouchStateChanged;
            netIsSliding.OnValueChanged -= OnSlidingStateChanged;
            netIsJumping.OnValueChanged -= OnJumpingStateChanged;
            netIsFalling.OnValueChanged -= OnFallingStateChanged;
            jumpAnimationSequence.OnValueChanged -= OnJumpAnimationSequenceChanged;
            landAnimationSequence.OnValueChanged -= OnLandAnimationSequenceChanged;
            mantleAnimationSequence.OnValueChanged -= OnMantleAnimationSequenceChanged;
            netIsWallRunning.OnValueChanged -= OnWallRunStateChanged;
            netIsRightWallRun.OnValueChanged -= OnWallRunOrientationChanged;
            netWallRunDirection.OnValueChanged -= OnWallRunDirectionChanged;
        }

        public MatchPlayerStateProxy PlayerState => _networkStateCoordinator.PlayerState;

        private MatchPlayerStateProxy ResolvePlayerState() {
            return _networkStateCoordinator.ResolvePlayerState();
        }

        private void TryBindPlayerStateSubscriptions() {
            _networkStateCoordinator.TryBindPlayerStateSubscriptions();
        }

        private static void OnMatChanged(int _, int __) {
        }

        /// <summary>
        /// Called when material packet index changes. Triggers material update.
        /// </summary>
        private void UpdatePlayerMaterialFromNetwork() {
            _materialCustomizationCoordinator.UpdatePlayerMaterialFromNetwork();
        }

        /// <summary>
        /// Loads material customization values from settings.json.
        /// </summary>
        private void LoadMaterialCustomizationFromPrefs() {
            _materialCustomizationCoordinator.LoadMaterialCustomizationFromPrefs();
        }

        /// <summary>
        /// Saves material customization values to settings.json.
        /// </summary>
        public void SaveMaterialCustomizationToPrefs() {
            _materialCustomizationCoordinator.SaveMaterialCustomizationToPrefs();
        }

        private void OnHealthChanged(float _, float newV) {
            if(IsOwner) PlayerUiEventBridge.PublishHealthUpdated(newV, 100f);
        }

        private void OnCrouchStateChanged(bool oldValue, bool newValue) {
            if(movementController != null)
                movementController.UpdateCrouch(fpCamera);
        }

        private void OnSlidingStateChanged(bool oldValue, bool newValue) {
            if(IsOwner || animationController == null) return;
            animationController.ApplyRemoteSlidingState(newValue, playTrigger: newValue && !oldValue);
        }

        private void OnJumpingStateChanged(bool oldValue, bool newValue) {
            if(IsOwner || animationController == null) return;
            animationController.ApplyRemoteJumpingState(newValue);
        }

        private void OnFallingStateChanged(bool oldValue, bool newValue) {
            if(IsOwner || animationController == null) return;
            animationController.ApplyRemoteFallingState(newValue);
        }

        private void OnJumpAnimationSequenceChanged(int oldValue, int newValue) {
            if(IsOwner || animationController == null || newValue == oldValue) return;
            animationController.PlayRemoteJumpAnimation();
        }

        private void OnLandAnimationSequenceChanged(int oldValue, int newValue) {
            if(IsOwner || animationController == null || newValue == oldValue) return;
            animationController.PlayRemoteLandingAnimation();
        }

        private void OnMantleAnimationSequenceChanged(int oldValue, int newValue) {
            if(IsOwner || animationController == null || newValue == oldValue) return;
            animationController.PlayRemoteMantleAnimation();
        }

        private void OnWallRunStateChanged(bool oldValue, bool newValue) {
            RefreshRemoteWallRunState();
        }

        private void OnWallRunOrientationChanged(bool oldValue, bool newValue) {
            RefreshRemoteWallRunState();
        }

        private void OnWallRunDirectionChanged(float oldValue, float newValue) {
            RefreshRemoteWallRunState();
        }

        private void RefreshRemoteWallRunState() {
            if(IsOwner || animationController == null) return;
            animationController.ApplyRemoteWallRunState(netIsWallRunning.Value, netIsRightWallRun.Value,
                netWallRunDirection.Value);
        }

        private void OnDeathStateChanged(bool _, bool newValue) {
            switch(newValue) {
                case true when characterController != null:
                    characterController.enabled = false;
                    break;
                case false:
                    return;
            }

            ClearTriggerOutOfBoundsCountdownServer();
            if(IsOwner) {
                HideTriggerOutOfBoundsCountdownLocal();
            }
        }
    
        /// <summary>
        /// Main update loop for core player logic, movement synchronization, and server validation.
        /// </summary>
        private void Update() {
            if((disableKinemationFrameworkComponents || disableUnexpectedChildCameras) &&
               (Time.frameCount & 15) == 0) {
                DisableConflictingKinemationFrameworkComponents();
                DisableUnexpectedChildCamerasAndListeners();
            }

            if(NetworkAuthority.HasGlobalAuthority(this)) {
                var authPos = clientNetworkTransform.transform.position;
                ValidateServerMovement(authPos);
                HandleOutOfBoundsChecks(authPos);
                if(healthController != null) {
                    healthController.UpdateHealthRegeneration();
                }
                if(statsController != null) {
                    statsController.UpdateAuthorityStats();
                }
            }

            if(IsOwner) {
                UpdateTriggerOutOfBoundsCountdownUiOwner();
            }

            if(NetIsDead.Value || characterController.enabled == false) return;

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
            if(!IsOwner || NetIsDead.Value) return;

            if(lookController != null)
                lookController.UpdateLook();
        }

        /// <summary>
        /// Validates client movement on the server to prevent cheating (teleporting/speed hacking).
        /// </summary>
        private void ValidateServerMovement(Vector3 position) {
            _movementValidationCoordinator.ValidateServerMovement(position);
        }

        [Rpc(SendTo.Owner)]
        internal void ApplyServerMovementCorrectionOwnerRpc(Vector3 correctedPosition, Quaternion correctedRotation) {
            _movementValidationCoordinator.ApplyServerMovementCorrection(correctedPosition, correctedRotation);
        }

        public void SetOutOfBoundsGraceWindow(float seconds) {
            _outOfBoundsCoordinator.SetOutOfBoundsGraceWindow(seconds);
        }

        public float GetOutOfBoundsKillY() {
            return _outOfBoundsCoordinator.GetOutOfBoundsKillY();
        }

        public bool IsYLevelOutOfBoundsKillEnabled() {
            return _outOfBoundsCoordinator.IsYLevelOutOfBoundsKillEnabled();
        }

        private void HandleOutOfBoundsChecks(Vector3 authPos) {
            _outOfBoundsCoordinator.HandleOutOfBoundsChecks(authPos);
        }

        private void ClearTriggerOutOfBoundsCountdownServer() {
            _outOfBoundsCoordinator.ClearTriggerOutOfBoundsCountdownServer();
        }

        private void UpdateTriggerOutOfBoundsCountdownUiOwner() {
            _outOfBoundsCoordinator.UpdateTriggerOutOfBoundsCountdownUiOwner();
        }

        [Rpc(SendTo.Owner)]
        internal void ShowTriggerOutOfBoundsCountdownOwnerRpc(float countdownSeconds) {
            _outOfBoundsCoordinator.ShowTriggerOutOfBoundsCountdownOwner(countdownSeconds);
        }

        [Rpc(SendTo.Owner)]
        internal void HideTriggerOutOfBoundsCountdownOwnerRpc() {
            HideTriggerOutOfBoundsCountdownLocal();
        }

        private void HideTriggerOutOfBoundsCountdownLocal() {
            _outOfBoundsCoordinator.HideTriggerOutOfBoundsCountdownLocal();
        }

        #endregion

        #region Collision Handling

        private void OnControllerColliderHit(ControllerColliderHit hit) {
            if(hit.gameObject.CompareTag("JumpPad")) {
                var wasGrappling = grappleController != null && grappleController.IsGrappling;
                var applyJumpPadLaunchCompensation = wasGrappling &&
                                                     movementController != null &&
                                                     movementController.IsInJumpPadLaunch;

                grappleController.CancelGrapple(forJumpPadLaunch: applyJumpPadLaunchCompensation);
                var mantleWasActive = mantleController != null && mantleController.IsMantling;
                if(mantleWasActive) {
                    mantleController.CancelMantleForJumpPad();
                }

                if(movementController == null) {
                    Debug.LogError("[PlayerController] MovementController not found!");
                    return;
                }
                var padNormal = hit.gameObject.transform.up;
                var ignoreGrounded = mantleWasActive || wasGrappling;
                movementController.LaunchFromJumpPad(padNormal, ignoreGroundedRequirement: ignoreGrounded);
            } else if(hit.gameObject.CompareTag("MegaPad")) {
                var wasGrappling = grappleController != null && grappleController.IsGrappling;
                var applyJumpPadLaunchCompensation = wasGrappling &&
                                                     movementController != null &&
                                                     movementController.IsInJumpPadLaunch;

                grappleController.CancelGrapple(forJumpPadLaunch: applyJumpPadLaunchCompensation);
                var mantleWasActive = mantleController != null && mantleController.IsMantling;
                if(mantleWasActive) {
                    mantleController.CancelMantleForJumpPad();
                }

                if(movementController == null) {
                    Debug.LogError("[PlayerController] MovementController not found!");
                    return;
                }
                var padNormal = hit.gameObject.transform.up;
                var ignoreGrounded = mantleWasActive || wasGrappling;
                movementController.LaunchFromJumpPad(padNormal, force: 30f, ignoreGroundedRequirement: ignoreGrounded);
            } else {
                grappleController.CancelGrapple(fromCollision: true);
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
            _weaponPresentationCoordinator.ResetWeaponState(resetAllAmmo, switchToWeapon0, updateHUD);
        }

        #endregion

        #region Public API

        public void HideFpVisualsForDisconnectTransition() {
            if(!IsOwner) return;
            if(weaponManager != null) weaponManager.HideFpVisualsForDisconnectTransition();
            if(playerHopballController != null) playerHopballController.HideFpVisualsForDisconnectTransition();
        }

        public void SetGameplayCameraActive(bool active) {
            if(fpCamera != null) {
                fpCamera.enabled = active;
            }

            if(deathCamera != null) {
                deathCamera.enabled = active;
            }

            if(weaponCameraController != null) {
                weaponCameraController.SetWeaponCameraEnabled(active);
            } else if(weaponCamera != null) {
                weaponCamera.enabled = active;
            }
        }

        public void SetPostMatchControlLock(bool locked, bool lockLook = true, bool resetVelocity = true) {
            if(!IsOwner) return;

            if(locked) {
                moveInput = Vector2.zero;
                lookInput = Vector2.zero;
                sprintInput = false;
                crouchInput = false;
                if(resetVelocity && movementController != null) {
                    movementController.ResetVelocity();
                }
            }

            LockLook = locked && lockLook;
        }

        public void ResetVelocity() {
            if(movementController != null) {
                movementController.ResetVelocity();
            }
        }

        public void TryJump(float height = 2f) {
            if(movementController != null) {
                movementController.TryJump(height);
            }
        }

        public void PlayWalkSound() {
            if(!IsGrounded) return;
            if(movementController == null) return;

            if(characterController != null) {
                var actual = characterController.velocity;
                actual.y = 0f;
                if(actual.sqrMagnitude < 0.3f * 0.3f) {
                    return;
                }
            } else if(movementController.CachedHorizontalSpeedSqr < 0.5f * 0.5f) {
                return;
            }

            if(!IsOwner || audioRelay == null) return;
            audioRelay.RequestPlayAttached("foley.tile.walk", new NetworkObjectReference(NetworkObject), allowOverlap: true);
        }

        public void PlayRunSound() {
            var isWallRunning = wallRunController != null && wallRunController.IsWallRunning;
            if(!IsGrounded && !isWallRunning) return;
            if(movementController == null) return;

            if(characterController != null && IsGrounded) {
                var actual = characterController.velocity;
                actual.y = 0f;
                if(actual.sqrMagnitude < 0.5f * 0.5f) {
                    return;
                }
            } else if(movementController.CachedHorizontalSpeedSqr < 0.5f * 0.5f) {
                return;
            }

            if(!IsOwner || audioRelay == null) return;
            audioRelay.RequestPlayAttached("foley.tile.run", new NetworkObjectReference(NetworkObject), allowOverlap: true);
        }

        public void PickupHopball() {
            if(playerHopballController != null) {
                playerHopballController.TryPickupHopball();
            } else {
                Debug.LogWarning("HopballController == null, cannot pick up hopball.");
            }
        }

        public bool IsHoldingHopball => playerHopballController != null && playerHopballController.IsHoldingHopball;

        public void DropHopball() {
            if(playerHopballController != null) {
                playerHopballController.DropHopball();
            }
        }

        #endregion

        #region Core Components

        public Transform PlayerTransform => playerTransform != null ? playerTransform : transform;
        public CharacterController CharacterController => characterController;
        public PlayerInput PlayerInput => playerInput;
        public UnityEngine.InputSystem.PlayerInput UnityPlayerInput => unityPlayerInput;
        public AudioListener AudioListener => audioListener;
        public Target PlayerTarget => playerTarget;
        public LayerMask WorldLayer => worldLayer;
        public LayerMask PlayerLayer => playerLayer;
        public LayerMask EnemyLayer => enemyLayer;
        public LayerMask WeaponLayer => weaponLayer;
        public LayerMask HopballLayer => hopballLayer;

        #endregion

        #region Cameras

        public CinemachineCamera FpCamera => fpCamera;
        public Transform FpCameraTransform => fpCamera != null ? fpCamera.transform : null;
        public Camera WeaponCamera => weaponCamera;
        public CinemachineCamera DeathCamera => deathCamera;
        public WeaponCameraController WeaponCameraController => weaponCameraController;

        #endregion

        #region Player Model

        public GameObject PlayerModelRoot => playerModelRoot;
        public SkinnedMeshRenderer PlayerMesh => playerMesh;
        public Material[] PlayerMaterials => playerMaterials;
        public PlayerVisualController VisualController => visualController;
        public PlayerAnimationController AnimationController => animationController;
        public PlayerShadow PlayerShadow => playerShadow;
        public PlayerRenderer PlayerRenderer => playerRenderer;
        public UpperBodyPitch UpperBodyPitch => upperBodyPitch;
        public PlayerRagdoll PlayerRagdoll => playerRagdoll;
        public SpeedTrail SpeedTrail => speedTrail;
        public Transform DeathCameraTarget => deathCameraTarget;

        #endregion

        #region Gameplay Controllers

        public PlayerMovementController MovementController => movementController;
        public PlayerLookController LookController => lookController;
        public PlayerStatsController StatsController => statsController;
        public PlayerHealthController HealthController => healthController;
        public PlayerTagController TagController => tagController;
        public PlayerPodiumController PodiumController => podiumController;
        public PlayerHopballController PlayerHopballController => playerHopballController;
        public PlayerTeamManager TeamManager => playerTeamManager;
        public MantleController MantleController => mantleController;
        public DeathCameraController DeathCameraController => deathCameraController;

        #endregion

        #region Weapons

        public WeaponManager WeaponManager => weaponManager;
        public GrappleController GrappleController => grappleController;
        public NetworkDamageRelay DamageRelay => damageRelay;
        public NetworkFxRelay FxRelay => fxRelay;
        public NetworkAudioRelay AudioRelay => audioRelay;
        public CinemachineImpulseSource ImpulseSource => impulseSource;
        public GameObject[] WorldWeaponPrefabs => worldWeaponPrefabs;
        public Weapon WeaponComponent => weaponComponent;
        public Animator PlayerAnimator => playerAnimator;
        public Transform WorldWeaponSocket => worldWeaponSocket;

        #endregion

        #region Network Components

        public ClientNetworkTransform ClientNetworkTransform => clientNetworkTransform;
        private MatchPlayerStateProxy ResolvePlayerStateOrNull() => ResolvePlayerState();
        private NetworkVariable<int> KillsState => ResolvePlayerState() != null ? ResolvePlayerState().kills : MissingIntState;
        private NetworkVariable<int> DeathsState => ResolvePlayerState() != null ? ResolvePlayerState().deaths : MissingIntState;
        private NetworkVariable<int> AssistsState => ResolvePlayerState() != null ? ResolvePlayerState().assists : MissingIntState;

        public NetworkVariable<float> NetHealth => ResolvePlayerStateOrNull() != null ? ResolvePlayerStateOrNull().netHealth : MissingHealthState;
        public NetworkVariable<bool> NetIsDead => ResolvePlayerStateOrNull() != null ? ResolvePlayerStateOrNull().netIsDead : MissingDeathState;
        public NetworkVariable<bool> NetIsCrouching => netIsCrouching;
        public NetworkVariable<bool> NetIsSliding => netIsSliding;
        public NetworkVariable<bool> NetIsJumping => netIsJumping;
        public NetworkVariable<bool> NetIsFalling => netIsFalling;
        public NetworkVariable<bool> NetIsWallRunning => netIsWallRunning;
        public NetworkVariable<bool> NetIsRightWallRun => netIsRightWallRun;
        public NetworkVariable<float> NetWallRunDirection => netWallRunDirection;
        public NetworkVariable<int> Kills => KillsState;
        public NetworkVariable<int> Deaths => DeathsState;
        public NetworkVariable<int> Assists => AssistsState;
        public NetworkVariable<float> DamageDealt => ResolvePlayerStateOrNull() != null ? ResolvePlayerStateOrNull().damageDealt : MissingFloatState;
        public NetworkVariable<int> PlayerMaterialIndex => playerMaterialIndex;
        public NetworkVariable<ulong> SteamId => ResolvePlayerStateOrNull() != null ? ResolvePlayerStateOrNull().steamId : MissingSteamIdState;
        public NetworkVariable<FixedString128Bytes> UgsId => ResolvePlayerStateOrNull() != null ? ResolvePlayerStateOrNull().ugsId : MissingUgsIdState;
        public NetworkVariable<FixedString64Bytes> PlayerName => ResolvePlayerStateOrNull() != null ? ResolvePlayerStateOrNull().playerName : MissingPlayerNameState;
        public int PingMs => statsController != null ? statsController.PingMs.Value : 0;

        #endregion

        #region Player State

        public Vector3 Position => PlayerTransform.position;
        public Quaternion Rotation => PlayerTransform.rotation;
        public bool IsDead => NetIsDead is { Value: true };
        public bool IsCrouching => netIsCrouching is { Value: true };
        public bool IsGrounded => movementController != null && movementController.IsGrounded;

        #endregion

        #region Velocity Helpers

        public Vector3 GetHorizontalVelocity() {
            return movementController != null ? movementController.HorizontalVelocity : Vector3.zero;
        }

        public float GetVerticalVelocity() {
            return movementController != null ? movementController.VerticalVelocity : 0f;
        }

        public Vector3 GetFullVelocity => movementController != null ? movementController.FullVelocity : Vector3.zero;

        public float GetMaxSpeed() {
            return movementController != null ? movementController.MaxSpeed : 5f;
        }

        public float GetCachedHorizontalSpeedSqr() {
            return movementController != null ? movementController.CachedHorizontalSpeedSqr : 0f;
        }

        public float AverageVelocity => statsController != null ? statsController.AverageVelocity.Value : 0f;
        public float ObservedServerMovementSpeed => _movementValidationCoordinator.ObservedServerMovementSpeed;

        public void SetVelocity(Vector3 horizontalVelocity) {
            if(movementController != null) {
                movementController.SetVelocity(horizontalVelocity);
            }
        }

        public void AddVerticalVelocity(float verticalBoost) {
            if(movementController != null) {
                movementController.AddVerticalVelocity(verticalBoost);
            }
        }

        #endregion

        #region Gun Tag Stats

        public int Tags => tagController != null ? tagController.Tags.Value : 0;
        public int Tagged => tagController != null ? tagController.Tagged.Value : 0;
        public int TimeTagged => tagController != null ? tagController.TimeTagged.Value : 0;
        public bool IsTagged => tagController != null && tagController.IsTagged.Value;

        #endregion

        #region Podium Methods

        public void ForceRespawnForPodiumServer() {
            if(podiumController != null) {
                podiumController.ForceRespawnForPodiumServer();
            }
        }

        public void TeleportToPodiumFromServer(Vector3 position, Quaternion rotation) {
            if(podiumController != null) {
                podiumController.TeleportToPodiumFromServer(position, rotation);
            }
        }

        #endregion

        #region Network RPCs

        [Rpc(SendTo.Everyone)]
        public void SetWorldModelVisibleRpc(bool visible) {
            if(visualController != null) {
                visualController.SetWorldModelVisible(visible);
            }
        }

        [Rpc(SendTo.Everyone)]
        public void ResetVelocityRpc() {
            if(movementController != null) {
                movementController.ResetVelocity();
            }
        }

        [Rpc(SendTo.Everyone)]
        public void PlayHitEffectsClientRpc(Vector3 hitPoint, float amount) {
            if(IsOwner) {
                if(Audio2.AudioService.Instance != null) {
                    Audio2.AudioService.Instance.Play("ui.hit.hurt", Vector3.zero);
                }

                impulseSource.GenerateImpulse();

                if(DamageVignetteUIManager.Instance && fpCamera) {
                    var intensity = Mathf.Clamp01(amount / 50f);
                    DamageVignetteUIManager.Instance.ShowHitFromWorldPoint(hitPoint, fpCamera.transform, intensity);
                }
            }

            if(animationController != null) {
                animationController.PlayDamageAnimation();
            }
        }

        [Rpc(SendTo.Everyone)]
        public void SnapPodiumVisualsClientRpc() {
            if(podiumController != null) {
                podiumController.SnapPodiumVisualsClientRpc();
            }
        }

        #endregion
    }
}

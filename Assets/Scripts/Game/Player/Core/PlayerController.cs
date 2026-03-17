using System;
using System.Collections.Generic;
using Events;
using Game.Audio.System;
using Game.Player.Contracts;
using Game.Match;
using Game.Player.Combat;
using Game.Player.Input;
using Game.Player.Movement;
using Game.Player.Visual;
using Game.Weapon.Core;
using Game.Weapon.Manager;
using Game.Weapon.Presentation;
using Network.Components;
using Network.Core;
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
    public class PlayerController : NetworkBehaviour, IPlayerMovementContext, IPlayerVisualContext, IPlayerInputContext, IPlayerLookContext {
        public static PlayerController LocalPlayer { get; private set; }
        public static event Action<PlayerController> PlayerSpawned;
        public static event Action<PlayerController> PlayerDespawned;
        private static readonly HashSet<PlayerController> SpawnedPlayersRegistry = new();
        public static IReadOnlyCollection<PlayerController> SpawnedPlayers => SpawnedPlayersRegistry;

        #region Serialized Fields

        [Header("Core Components")]
        [SerializeField] private Transform playerTransform;

        [SerializeField] private CharacterController characterController;
        [SerializeField] private PlayerInputController playerInputController;
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

        [SerializeField] private PlayerCombatController combatController;
        [SerializeField] private PlayerAnimationController animationController;
        [SerializeField] private PlayerTagController tagController;
        [SerializeField] private PlayerPodiumController podiumController;
        [SerializeField] private PlayerTeamManager playerTeamManager;
        [SerializeField] private WeaponCameraController weaponCameraController;
        [SerializeField] private DeathCameraController deathCameraController;


        [Header("Weapon System")]
        [SerializeField] private WeaponManager weaponManager;

        [SerializeField] private Weapon.Core.Weapon weaponComponent;

        // [SerializeField] private MeshRenderer worldWeapon;
        [SerializeField] private Transform worldWeaponSocket;
        [SerializeField] private GameObject[] worldWeaponPrefabs;


        [Header("Audio / Visual Effects")]
        [SerializeField] private AudioListener audioListener;

        [SerializeField] private WeaponDamageRelay damageRelay;
        [SerializeField] private WeaponFxRelay fxRelay;
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

        private PlayerNetworkState _networkState;
        private PlayerMaterialCustomization _materialCustomization;
        private PlayerRuntimeSafety _runtimeSafety;
        private PlayerOutOfBounds _outOfBounds;
        private PlayerMovementValidation _movementValidation;
        private PlayerWeaponPresentation _weaponPresentation;
        private PlayerSpawnPresentation _spawnPresentation;
        private PlayerPresentationState _presentationState;

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
        public float BaseFov => lookController != null ? lookController.BaseFov : 80f;
        public bool IsJumpHeld => playerInputController != null && playerInputController.IsJumpHeld;
        public WallRunController WallRunController => wallRunController;
        public bool IsWallRunning => wallRunController != null && wallRunController.IsWallRunning;
        public bool IsRightWallRunning => wallRunController != null && wallRunController.IsRightWallRun;

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

        internal void HandleResolvedHealthChanged(float oldValue, float newValue) =>
            OnHealthChanged(oldValue, newValue);

        internal void HandleResolvedDeathChanged(bool oldValue, bool newValue) =>
            OnDeathStateChanged(oldValue, newValue);

        internal void BeginIdentitySyncFromSpawn(ulong localSteamId, string ugsPlayerId, string playerDisplayName) =>
            BeginIdentitySync(localSteamId, ugsPlayerId, playerDisplayName);

        internal void LoadMaterialCustomizationFromPrefsForSpawn() => LoadMaterialCustomizationFromPrefs();
        internal void ClearTriggerOobCountdownFromPresentation() => ClearTriggerOobCountdownServer();
        internal void HideTriggerOobCountdownLocalFromPresentation() => HideTriggerOobCountdownLocal();
        internal void ResetLookPitchFromRespawn() {
            if(lookController != null) {
                lookController.ResetPitch();
            }
        }

        internal void SetLookTilt(float tilt) {
            if(lookController != null) {
                lookController.SetTargetTilt(tilt);
            }
        }

        private void UpdateTurnAnimationFromLook(float yawDelta) {
            if(animationController != null) {
                animationController.UpdateTurnAnimation(yawDelta);
            }
        }

        internal Vector2 ResampleHeldMovementInputFromRespawn(string reason = "Unknown") =>
            playerInputController != null ? playerInputController.ResampleHeldMovementInput(reason) : Vector2.zero;

        #endregion

        #region Unity Lifecycle

        private void OnValidate() {
            ValidateSerializedReferences(logIssues: false);
        }

        private void ValidateSerializedReferences(bool logIssues) {
            playerTransform = playerTransform ? playerTransform : transform;
            characterController = characterController ? characterController : GetComponent<CharacterController>();
            playerInputController = playerInputController ? playerInputController : GetComponent<PlayerInputController>();
            unityPlayerInput = unityPlayerInput ? unityPlayerInput : GetComponent<UnityEngine.InputSystem.PlayerInput>();
            playerAnimator = playerAnimator ? playerAnimator : GetComponentInChildren<Animator>(true);
            clientNetworkTransform = clientNetworkTransform ? clientNetworkTransform : GetComponent<ClientNetworkTransform>();
            movementController = movementController ? movementController : GetComponent<PlayerMovementController>();
            lookController = lookController ? lookController : GetComponent<PlayerLookController>();
            mantleController = mantleController ? mantleController : GetComponent<MantleController>();
            grappleController = grappleController ? grappleController : GetComponent<GrappleController>();
            wallRunController = wallRunController ? wallRunController : GetComponent<WallRunController>();
            statsController = statsController ? statsController : GetComponent<PlayerStatsController>();
            combatController = combatController ? combatController : GetComponent<PlayerCombatController>();
            animationController = animationController ? animationController : GetComponent<PlayerAnimationController>();
            tagController = tagController ? tagController : GetComponent<PlayerTagController>();
            podiumController = podiumController ? podiumController : GetComponent<PlayerPodiumController>();
            playerTeamManager = playerTeamManager ? playerTeamManager : GetComponent<PlayerTeamManager>();
            weaponCameraController = weaponCameraController ? weaponCameraController : GetComponent<WeaponCameraController>();
            deathCameraController = deathCameraController ? deathCameraController : GetComponent<DeathCameraController>();
            weaponManager = weaponManager ? weaponManager : GetComponent<WeaponManager>();
            weaponComponent = weaponComponent ? weaponComponent : GetComponent<Weapon.Core.Weapon>();
            audioRelay = audioRelay ? audioRelay : GetComponent<NetworkAudioRelay>();
            audioListener = audioListener ? audioListener : GetComponentInChildren<AudioListener>(true);
            impulseSource = impulseSource ? impulseSource : GetComponentInChildren<CinemachineImpulseSource>(true);
            speedTrail = speedTrail ? speedTrail : GetComponentInChildren<SpeedTrail>(true);

            if(!logIssues) {
                return;
            }

            var missingRefs = new List<string>();

            if(characterController == null) missingRefs.Add(nameof(characterController));
            if(playerInputController == null) missingRefs.Add(nameof(playerInputController));
            if(unityPlayerInput == null) missingRefs.Add(nameof(unityPlayerInput));
            if(movementController == null) missingRefs.Add(nameof(movementController));
            if(lookController == null) missingRefs.Add(nameof(lookController));
            if(wallRunController == null) missingRefs.Add(nameof(wallRunController));
            if(combatController == null) missingRefs.Add(nameof(combatController));
            if(animationController == null) missingRefs.Add(nameof(animationController));
            if(weaponManager == null) missingRefs.Add(nameof(weaponManager));
            if(audioRelay == null) missingRefs.Add(nameof(audioRelay));

            if(missingRefs.Count > 0) {
                Debug.LogError(
                    $"[PlayerController] Missing critical serialized references on '{name}': {string.Join(", ", missingRefs)}",
                    this);
            }
        }

        private void InitializeCoordinators() {
            _runtimeSafety ??= new PlayerRuntimeSafety(this);
            _outOfBounds ??= new PlayerOutOfBounds(this);
            _networkState ??= new PlayerNetworkState(this);
            _materialCustomization ??= new PlayerMaterialCustomization(this);
            _movementValidation ??= new PlayerMovementValidation(this);
            _weaponPresentation ??= new PlayerWeaponPresentation(this);
            _spawnPresentation ??= new PlayerSpawnPresentation(this);
            _presentationState ??= new PlayerPresentationState(this);
        }

        private void Awake() {
            ValidateSerializedReferences(logIssues: true);
            InitializeCoordinators();
            MarkChildComponentCachesDirty();
            DisableConflictingKinemationComponents();
            DisableUnexpectedCamerasAndListeners();

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
            // Publish by owning client id so Events does not depend on PlayerController.
            EventBus.Publish(new PlayerNetworkSpawnedEvent(player.OwnerClientId));
        }

        private static void UnregisterSpawnedPlayer(PlayerController player) {
            if(player == null || !SpawnedPlayersRegistry.Remove(player)) return;
            PlayerDespawned?.Invoke(player);
            EventBus.Publish(new PlayerNetworkDespawnedEvent(player.OwnerClientId));
        }

        public override void OnDestroy() {
            CancelPendingIdentitySync();
            UnregisterLocalAudioRelay();
            UnsubscribeFromLocalVoiceEvents();
            UnsubscribeFromHopballStateEvents();
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
            DisableConflictingKinemationComponents();
            DisableUnexpectedCamerasAndListeners();

            if(IsOwner) {
                LocalPlayer = this;
                RegisterLocalAudioRelay();
            }

            RegisterSpawnedPlayer(this);

            SubscribeToNetworkVariables();
            SubscribeToLocalVoiceEvents();
            SubscribeToHopballStateEvents();
            TryBindStateSubscriptions();
            UpdatePlayerMaterialFromNetwork();
            _spawnPresentation.HandleNetworkSpawnPresentation();
        }

        private void SubscribeToHopballStateEvents() {
            EventBus.Unsubscribe<HopballHoldStateChangedEvent>(OnHopballHoldStateChanged);
            EventBus.Unsubscribe<HopballEquippedPresentationEvent>(OnHopballEquippedPresentation);
            EventBus.Unsubscribe<HopballDropPresentationEvent>(OnHopballDropPresentation);
            EventBus.Subscribe<HopballHoldStateChangedEvent>(OnHopballHoldStateChanged);
            EventBus.Subscribe<HopballEquippedPresentationEvent>(OnHopballEquippedPresentation);
            EventBus.Subscribe<HopballDropPresentationEvent>(OnHopballDropPresentation);
        }

        private void UnsubscribeFromHopballStateEvents() {
            EventBus.Unsubscribe<HopballHoldStateChangedEvent>(OnHopballHoldStateChanged);
            EventBus.Unsubscribe<HopballEquippedPresentationEvent>(OnHopballEquippedPresentation);
            EventBus.Unsubscribe<HopballDropPresentationEvent>(OnHopballDropPresentation);
        }

        private void OnHopballHoldStateChanged(HopballHoldStateChangedEvent evt) {
            if(evt == null || evt.PlayerOwnerClientId != OwnerClientId) return;
            IsHoldingHopball = evt.IsHoldingHopball;
        }

        private void OnHopballEquippedPresentation(HopballEquippedPresentationEvent evt) {
            if(evt == null || evt.HolderClientId != OwnerClientId) return;
            IsHoldingHopball = true;
        }

        private void OnHopballDropPresentation(HopballDropPresentationEvent evt) {
            if(evt == null || evt.HolderClientId != OwnerClientId) return;
            IsHoldingHopball = false;
        }

        private void DisableConflictingKinemationComponents() {
            _runtimeSafety.DisableConflictingKinemationComponents();
        }

        private void DisableUnexpectedCamerasAndListeners() {
            _runtimeSafety.DisableUnexpectedCamerasAndListeners();
        }

        private void MarkChildComponentCachesDirty() {
            _runtimeSafety.MarkChildComponentCachesDirty();
        }

        public override void OnNetworkDespawn() {
            // Capture FP duplicate for unexpected disconnect *before* base/cleanup; player hierarchy still exists.
            if(IsOwner && SessionManager.Instance != null && !SessionManager.Instance.IsExpectedDisconnect) {
                EventBus.Publish(new RequestDisconnectFpVisualCaptureEvent());
            }

            base.OnNetworkDespawn();

            if(LocalPlayer == this) {
                LocalPlayer = null;
            }

            UnregisterSpawnedPlayer(this);
            CancelPendingIdentitySync();
            UnregisterLocalAudioRelay();

            UnsubscribeFromLocalVoiceEvents();
            UnsubscribeFromNetworkVariables();
        }

        private void RegisterLocalAudioRelay() {
            if(!IsOwner || audioRelay == null) return;
            AudioServiceEventBusBridge.RegisterLocalRelay(audioRelay);
        }

        private void UnregisterLocalAudioRelay() {
            if(!IsOwner || audioRelay == null) return;
            AudioServiceEventBusBridge.UnregisterLocalRelay(audioRelay);
        }

        private void BeginIdentitySync(ulong localSteamId, string ugsPlayerId, string playerDisplayName) {
            _networkState.BeginIdentitySync(localSteamId, ugsPlayerId, playerDisplayName);
        }

        private void CancelPendingIdentitySync() {
            _networkState.CancelPendingIdentitySync();
        }

        /// <summary>
        /// Subscribes to all NetworkVariable value change callbacks.
        /// </summary>
        private void SubscribeToNetworkVariables() {
            _networkState.Subscribe();

            playerMaterialIndex.OnValueChanged -= OnMatChanged;
            playerMaterialIndex.OnValueChanged += OnMatChanged;
            _materialCustomization.Subscribe();
            _presentationState.Subscribe();
        }

        /// <summary>
        /// Unsubscribes from all NetworkVariable value change callbacks.
        /// </summary>
        private void UnsubscribeFromNetworkVariables() {
            _networkState.Unsubscribe();

            playerMaterialIndex.OnValueChanged -= OnMatChanged;
            _materialCustomization.Unsubscribe();
            _presentationState.Unsubscribe();
        }

        private void SubscribeToLocalVoiceEvents() {
            if(!IsOwner) return;
            EventBus.Unsubscribe<VoiceLocalPttStateChangedEvent>(OnVoiceLocalPttStateChanged);
            EventBus.Subscribe<VoiceLocalPttStateChangedEvent>(OnVoiceLocalPttStateChanged);
        }

        private void UnsubscribeFromLocalVoiceEvents() {
            if(!IsOwner) return;
            EventBus.Unsubscribe<VoiceLocalPttStateChangedEvent>(OnVoiceLocalPttStateChanged);
        }

        private void OnVoiceLocalPttStateChanged(VoiceLocalPttStateChangedEvent evt) {
            if(!IsOwner) return;
            isPttActive.Value = evt.IsActive;
        }

        public MatchPlayerStateProxy PlayerState => _networkState.PlayerState;

        private MatchPlayerStateProxy ResolvePlayerState() {
            return _networkState.ResolvePlayerState();
        }

        private void TryBindStateSubscriptions() {
            _networkState.TryBindStateSubscriptions();
        }

        private static void OnMatChanged(int _, int __) {
        }

        /// <summary>
        /// Called when material packet index changes. Triggers material update.
        /// </summary>
        private void UpdatePlayerMaterialFromNetwork() {
            _materialCustomization.UpdatePlayerMaterialFromNetwork();
        }

        /// <summary>
        /// Loads material customization values from settings.json.
        /// </summary>
        private void LoadMaterialCustomizationFromPrefs() {
            _materialCustomization.LoadMaterialCustomizationFromPrefs();
        }

        /// <summary>
        /// Saves material customization values to settings.json.
        /// </summary>
        public void SaveMaterialCustomizationToPrefs() {
            _materialCustomization.SaveMaterialCustomizationToPrefs();
        }

        private void OnHealthChanged(float _, float newV) => _presentationState.OnHealthChanged(newV);

        private void OnDeathStateChanged(bool _, bool newValue) => _presentationState.OnDeathStateChanged(newValue);

        /// <summary>
        /// Main update loop for core player logic, movement synchronization, and server validation.
        /// </summary>
        private void Update() {
            UpdateRuntimeSafetyMaintenance();
            UpdateAuthorityFrameState();

            if(IsOwner) {
                UpdateTriggerOobCountdownUi();
            }

            if(NetIsDead.Value || characterController.enabled == false) return;

            if(IsOwner) {
                UpdateOwnerFrameState();
            } else {
                UpdateRemoteFrameState();
            }
        }

        private void LateUpdate() {
            if(!IsOwner || NetIsDead.Value) return;

            if(lookController != null)
                lookController.UpdateLook();
        }

        private void UpdateRuntimeSafetyMaintenance() {
            if((!disableKinemationFrameworkComponents && !disableUnexpectedChildCameras) ||
               (Time.frameCount & 15) != 0) return;
            DisableConflictingKinemationComponents();
            DisableUnexpectedCamerasAndListeners();
        }

        private void UpdateAuthorityFrameState() {
            if(!NetworkAuthority.HasGlobalAuthority(this)) return;

            var authPos = clientNetworkTransform.transform.position;
            ValidateServerMovement(authPos);
            HandleOutOfBoundsChecks(authPos);

            if(combatController != null) {
                combatController.UpdateHealthRegeneration();
            }

            if(statsController != null) {
                statsController.UpdateAuthorityStats();
            }
        }

        private void UpdateOwnerFrameState() {
            UpdateOwnerMovementAndAnimation();

            if(lookController != null) {
                lookController.UpdateSpeedFov();
            }
        }

        private void UpdateOwnerMovementAndAnimation() {
            if(movementController == null) return;

            movementController.UpdateMovement(fpCamera);
            movementController.UpdateCrouch(fpCamera);

            if(animationController == null) return;

            animationController.UpdateFallingState(movementController.IsGrounded, movementController.VerticalVelocity,
                playerTransform.position);

            var (animHorizontal, animSpeedSqr) = GetOwnerAnimationMotion();
            animationController.UpdateAnimator(animHorizontal, movementController.MaxSpeed, animSpeedSqr);
        }

        private (Vector3 horizontalVelocity, float speedSqr) GetOwnerAnimationMotion() {
            if(movementController == null) {
                return (Vector3.zero, 0f);
            }

            var animHorizontal = movementController.HorizontalVelocity;
            var animSpeedSqr = movementController.CachedHorizontalSpeedSqr;

            if(characterController == null || !movementController.IsGrounded) {
                return (animHorizontal, animSpeedSqr);
            }

            var actual = characterController.velocity;
            actual.y = 0f;
            var actualSpeed = actual.magnitude;
            if(actualSpeed < 0.2f) {
                return (actual, actual.sqrMagnitude);
            }

            var intended = movementController.HorizontalVelocity;
            intended.y = 0f;
            var blendedSpeed = Mathf.Lerp(actualSpeed, intended.magnitude, 0.4f);
            if(actualSpeed > 0.0001f) {
                animHorizontal = actual.normalized * blendedSpeed;
            } else {
                animHorizontal = actual;
            }

            return (animHorizontal, animHorizontal.sqrMagnitude);
        }

        private void UpdateRemoteFrameState() {
            if(movementController != null) {
                movementController.UpdateCrouch(fpCamera);
            }

            if(animationController != null) {
                animationController.SetCrouching(netIsCrouching.Value);
            }

            if(visualController != null && Time.frameCount % 60 == 0) {
                visualController.VerifyAndFixVisibility();
            }
        }

        /// <summary>
        /// Validates client movement on the server to prevent cheating (teleporting/speed hacking).
        /// </summary>
        private void ValidateServerMovement(Vector3 position) {
            _movementValidation.ValidateServerMovement(position);
        }

        [Rpc(SendTo.Owner)]
        internal void ApplyServerMovementCorrectionOwnerRpc(Vector3 correctedPosition, Quaternion correctedRotation) {
            _movementValidation.ApplyServerMovementCorrection(correctedPosition, correctedRotation);
        }

        public void SetOutOfBoundsGraceWindow(float seconds) {
            _outOfBounds.SetOutOfBoundsGraceWindow(seconds);
        }

        public float GetOutOfBoundsKillY() {
            return _outOfBounds.GetOutOfBoundsKillY();
        }

        public bool IsYLevelOutOfBoundsKillEnabled() {
            return _outOfBounds.IsYLevelOutOfBoundsKillEnabled();
        }

        private void HandleOutOfBoundsChecks(Vector3 authPos) {
            _outOfBounds.HandleOutOfBoundsChecks(authPos);
        }

        private void ClearTriggerOobCountdownServer() {
            _outOfBounds.ClearTriggerOobCountdownServer();
        }

        private void UpdateTriggerOobCountdownUi() {
            _outOfBounds.UpdateTriggerOobCountdownUi();
        }

        [Rpc(SendTo.Owner)]
        internal void ShowTriggerOobCountdownOwnerRpc(float countdownSeconds) {
            _outOfBounds.ShowTriggerOobCountdownOwner(countdownSeconds);
        }

        [Rpc(SendTo.Owner)]
        internal void HideTriggerOobCountdownOwnerRpc() {
            HideTriggerOobCountdownLocal();
        }

        private void HideTriggerOobCountdownLocal() {
            _outOfBounds.HideTriggerOobCountdownLocal();
        }

        #endregion

        #region Collision Handling

        private void OnControllerColliderHit(ControllerColliderHit hit) {
            if(movementController != null) {
                movementController.HandleControllerColliderHit(hit);
            } else if(grappleController != null) {
                grappleController.CancelGrapple(fromCollision: true);
            }
        }

        #endregion

        #region Damage & Death Methods

        public bool ApplyDamageServer_Auth(float amount, Vector3 hitPoint, Vector3 hitDirection, ulong attackerId,
            string bodyPartTag = null, bool isHeadshot = false, string weaponId = null) {
            if(combatController != null) {
                return combatController.ApplyDamageServer_Auth(amount, hitPoint, hitDirection, attackerId, bodyPartTag,
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
            if(combatController != null)
                combatController.ResetHealthAndRegenerationState();
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
            _weaponPresentation.ResetWeaponState(resetAllAmmo, switchToWeapon0, updateHUD);
        }

        #endregion

        #region Public API

        public void HideFpVisualsForDisconnectTransition() {
            if(!IsOwner) return;
            if(weaponManager != null) weaponManager.HideFpVisualsForDisconnectTransition();
            if(NetworkObject != null) {
                EventBus.Publish(new PlayerDisconnectFpVisualHideRequestedEvent(NetworkObjectId));
            }
        }

        public void SetGameplayCameraActive(bool active) {
            if(fpCamera != null) {
                fpCamera.enabled = active;
            }

            if(deathCamera != null) {
                deathCamera.enabled = active;
                if(!active && deathCamera.gameObject.activeSelf) {
                    deathCamera.gameObject.SetActive(false);
                }
            }

            if(weaponCameraController != null) {
                weaponCameraController.SetWeaponCameraEnabled(active);
            } else if(weaponCamera != null) {
                weaponCamera.enabled = active;
            }
        }

        public void SetPostMatchControlLock(bool locked, bool lockLook = true, bool resetVelocity = true) {
            if(podiumController != null) {
                podiumController.SetPostMatchControlLock(locked, lockLook, resetVelocity);
            } else if(IsOwner) {
                LockLook = locked && lockLook;
            }
        }

        private void TryJump(float height = 2f) {
            if(movementController != null) {
                movementController.TryJump(height);
            }
        }

        public void PlayWalkSound() {
            if(movementController != null) movementController.PlayWalkSound();
        }

        public void PlayRunSound() {
            if(movementController != null) movementController.PlayRunSound();
        }

        private void PickupHopball() {
            if(NetworkObject != null) {
                EventBus.Publish(new PlayerHopballPickupRequestedEvent(NetworkObjectId));
            }
        }

        public bool IsHoldingHopball { get; private set; }

        public void DropHopball() {
            if(NetworkObject != null) {
                EventBus.Publish(new PlayerHopballManualDropRequestedEvent(NetworkObjectId));
            }
        }

        #endregion

        #region Core Components

        public Transform PlayerTransform => playerTransform != null ? playerTransform : transform;
        public CharacterController CharacterController => characterController;
        public PlayerInputController PlayerInputController => playerInputController;
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
        public PlayerCombatController CombatController => combatController;
        public PlayerTagController TagController => tagController;
        public PlayerPodiumController PodiumController => podiumController;
        public PlayerTeamManager TeamManager => playerTeamManager;
        public MantleController MantleController => mantleController;
        public DeathCameraController DeathCameraController => deathCameraController;

        #endregion

        #region Weapons

        public WeaponManager WeaponManager => weaponManager;
        public Weapon.Core.Weapon CurrentWeapon => weaponManager != null ? weaponManager.CurrentWeapon : null;
        public GrappleController GrappleController => grappleController;
        public WeaponDamageRelay DamageRelay => damageRelay;
        public WeaponFxRelay FxRelay => fxRelay;
        public NetworkAudioRelay AudioRelay => audioRelay;
        public CinemachineImpulseSource ImpulseSource => impulseSource;
        public GameObject[] WorldWeaponPrefabs => worldWeaponPrefabs;
        public Weapon.Core.Weapon WeaponComponent => weaponComponent;
        public Animator PlayerAnimator => playerAnimator;
        public Transform WorldWeaponSocket => worldWeaponSocket;

        #endregion

        #region Network Components

        public ClientNetworkTransform ClientNetworkTransform => clientNetworkTransform;

        private NetworkVariable<int> KillsState {
            get {
                var state = ResolvePlayerState();
                return state != null ? state.kills : MissingIntState;
            }
        }

        private NetworkVariable<int> DeathsState {
            get {
                var state = ResolvePlayerState();
                return state != null ? state.deaths : MissingIntState;
            }
        }

        private NetworkVariable<int> AssistsState {
            get {
                var state = ResolvePlayerState();
                return state != null ? state.assists : MissingIntState;
            }
        }

        public NetworkVariable<float> NetHealth {
            get {
                var state = ResolvePlayerState();
                return state != null ? state.netHealth : MissingHealthState;
            }
        }

        public NetworkVariable<bool> NetIsDead => ResolvePlayerState() != null
            ? ResolvePlayerState().netIsDead
            : MissingDeathState;

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

        public NetworkVariable<float> DamageDealt {
            get {
                var state = ResolvePlayerState();
                return state != null ? state.damageDealt : MissingFloatState;
            }
        }

        public NetworkVariable<int> PlayerMaterialIndex => playerMaterialIndex;

        public NetworkVariable<ulong> SteamId {
            get {
                var state = ResolvePlayerState();
                return state != null ? state.steamId : MissingSteamIdState;
            }
        }

        public NetworkVariable<FixedString128Bytes> UgsId {
            get {
                var state = ResolvePlayerState();
                return state != null ? state.ugsId : MissingUgsIdState;
            }
        }

        public NetworkVariable<FixedString64Bytes> PlayerName {
            get {
                var state = ResolvePlayerState();
                return state != null ? state.playerName : MissingPlayerNameState;
            }
        }

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
        public float ObservedServerMovementSpeed => _movementValidation.ObservedServerMovementSpeed;

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
                if(AudioService.Instance != null) {
                    AudioService.Instance.Play("ui.hit.hurt", Vector3.zero);
                }

                impulseSource.GenerateImpulse();

                if(fpCamera) {
                    var intensity = Mathf.Clamp01(amount / 50f);
                    var cameraTransform = fpCamera.transform;
                    EventBus.Publish(new ShowDamageVignetteFromWorldHitEvent(hitPoint, cameraTransform.position,
                        cameraTransform.forward, intensity));
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

        Vector2 IPlayerMovementContext.MoveInput => moveInput;
        bool IPlayerMovementContext.SprintInput => sprintInput;
        bool IPlayerMovementContext.CrouchInput => crouchInput;
        Vector3 IPlayerMovementContext.FullVelocity => GetFullVelocity;
        NetworkVariable<Vector4> IPlayerMovementContext.PlayerBaseColorNetwork => playerBaseColor;
        UnityEngine.InputSystem.PlayerInput IPlayerInputContext.UnityPlayerInput => unityPlayerInput;
        AudioListener IPlayerInputContext.AudioListener => audioListener;
        CinemachineCamera IPlayerInputContext.FpCamera => fpCamera;
        WeaponManager IPlayerInputContext.WeaponManager => weaponManager;
        bool IPlayerInputContext.IsWallRunning => wallRunController != null && wallRunController.IsWallRunning;
        bool IPlayerInputContext.IsMantling => mantleController != null && mantleController.IsMantling;
        bool IPlayerInputContext.CanMantleJump => mantleController != null && mantleController.CanJump;
        bool IPlayerInputContext.IsGrappling => grappleController != null && grappleController.IsGrappling;
        bool IPlayerInputContext.SprintInputState {
            get => sprintInput;
            set => sprintInput = value;
        }
        bool IPlayerInputContext.CrouchInputState {
            get => crouchInput;
            set => crouchInput = value;
        }
        bool IPlayerInputContext.IsSniperZoomActive => lookController != null && lookController.IsSniperZoomActive;
        void IPlayerMovementContext.SetLookTilt(float tilt) => SetLookTilt(tilt);
        void IPlayerMovementContext.SetCrouchingAnimation(bool isCrouching) {
            if(animationController != null) animationController.SetCrouching(isCrouching);
        }

        void IPlayerMovementContext.SetSlidingAnimationState(bool isSliding, bool playTrigger) {
            if(animationController != null) animationController.SetSlidingState(isSliding, playTrigger);
        }

        void IPlayerMovementContext.TriggerJumpAnimation() {
            if(animationController != null) animationController.TriggerJumpAnimation();
        }

        void IPlayerMovementContext.TriggerMantleAnimation() {
            if(animationController != null) animationController.TriggerMantleAnimation();
        }

        void IPlayerInputContext.SetMoveInput(Vector2 move) => moveInput = move;
        void IPlayerInputContext.SetLookInput(Vector2 look) => lookInput = look;
        void IPlayerInputContext.TryJump(float height) => TryJump(height);
        void IPlayerInputContext.PickupHopball() => PickupHopball();
        void IPlayerInputContext.TryMantle() {
            if(mantleController != null) mantleController.TryMantle();
        }

        void IPlayerInputContext.TryGrapple() {
            if(grappleController != null) grappleController.TryGrapple();
        }

        void IPlayerInputContext.CancelGrapple() {
            if(grappleController != null) grappleController.CancelGrapple();
        }

        void IPlayerInputContext.SetSniperZoomActive(bool active, float zoomFov) {
            if(lookController != null) lookController.SetSniperZoomActive(active, zoomFov);
        }

        Transform IPlayerLookContext.PlayerTransform => PlayerTransform;
        CinemachineCamera IPlayerLookContext.FpCamera => fpCamera;
        Vector2 IPlayerLookContext.LookInput => lookInput;
        Vector3 IPlayerLookContext.HorizontalVelocity => movementController != null ? movementController.HorizontalVelocity : Vector3.zero;
        bool IPlayerLookContext.IsRagdoll => playerRagdoll != null && playerRagdoll.IsRagdoll;
        void IPlayerLookContext.UpdateTurnAnimationFromLook(float yawDelta) => UpdateTurnAnimationFromLook(yawDelta);
        Color IPlayerVisualContext.TaggedGlowColor => playerTeamManager != null ? playerTeamManager.TaggedGlow : Color.white;
        NetworkVariable<int> IPlayerVisualContext.JumpAnimationSequence => jumpAnimationSequence;
        NetworkVariable<int> IPlayerVisualContext.LandAnimationSequence => landAnimationSequence;
        NetworkVariable<int> IPlayerVisualContext.MantleAnimationSequence => mantleAnimationSequence;

        GameObject IPlayerMovementContext.GetCurrentFpWeapon() {
            return weaponManager != null ? weaponManager.GetCurrentFpWeapon() : null;
        }

        #endregion
    }
}

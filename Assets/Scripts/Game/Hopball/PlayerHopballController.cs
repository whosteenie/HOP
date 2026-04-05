using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Diagnostics;
using Events;
using Game.Match;
using Game.Player.Combat;
using Game.Player.Core;
using Game.Player.Visual;
using OSI;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.VFX;
using Random = UnityEngine.Random;

namespace Game.Hopball {
    /// <summary>
    /// Handles hopball pickup, equipping, and dropping for the player.
    /// Manages weapon visibility and prevents shooting/reloading while holding the ball.
    /// </summary>
    public class PlayerHopballController : NetworkBehaviour {
        private enum HopballDropReason {
            Manual,
            WeaponSwitch,
            PlayerDeath
        }

        private const string WeaponLayerName = "Weapon";
        private const string MaskedLayerName = "Masked";
        private const string PooledArmVisualName = "PooledArmVisual";

        private static readonly int PutAwayHash = Animator.StringToHash("PutAway");

        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private PlayerCombatController _combatController; // For worldWeaponSocket reference
        private CinemachineCamera _fpCamera; // First-person camera (for FP weapon socket)
        private Transform _worldWeaponSocket;
        private Target _playerTarget; // OSI Target component on this player
        private CharacterController _characterController;
        private PlayerRenderer _playerRenderer;

        private LayerMask _hopballLayer;
        private const float PickupRange = 2.5f;

        [Header("Hopball Settings")]
        [SerializeField] private GameObject hopballVisualPrefab; // Visual-only FP hopball prefab (no state tracking)

        [SerializeField] private GameObject hopballArmPrefab; // FP hopball arm prefab (for PutAway animation)
        [SerializeField] private float hopballParticleWarmupSeconds = 1f;
        [SerializeField] private bool prewarmHopballVisualsOnSpawn = true;

        [Header("Hopball Float Motion")]
        [SerializeField] private bool enableHopballFloatMotion = true;

        [SerializeField] private float hopballFloatAmplitude = 0.008f;
        [SerializeField] private float hopballFloatCyclesPerSecond = 0.085f;
        [SerializeField, Range(0f, 1f)] private float hopballFloatApexDwell = 0.35f;

        [SerializeField] private Vector3 fpEquippedLocalPosition = Vector3.zero;
        [SerializeField] private Vector3 worldEquippedLocalPosition = Vector3.zero;

        [Header("Dissolve Settings")]
        [SerializeField] private float putAwayDissolveThreshold = 0.75f;

        [Header("Animation Layer Settings")]
        [SerializeField] private float layerTransitionDuration = 0.3f;

        [Tooltip("Layer index for 'Weapon Hold Layer' (both arms). Set in inspector or will auto-detect by name.")]
        [SerializeField]
        private int weaponHoldLayerIndex = -1;

        [Tooltip(
            "Layer index for 'Right Hand Hold Layer' (right arm only). Set in inspector or will auto-detect by name.")]
        [SerializeField]
        private int rightHandHoldLayerIndex = -1;

        // State
        private bool IsHoldingHopball => _currentHopballController != null;
        private static bool IsRestoringAfterDissolve => false; // Flag to allow weapon switch after dissolve
        private HopballController _currentHopballController;

        // Animation layer indices (cached for performance)
        private int _weaponHoldLayerIndex = -1;
        private int _rightHandHoldLayerIndex = -1;
        private Animator _playerAnimator;
        private Coroutine _layerTransitionCoroutine;
        private bool _putAwayAnimationTriggered;

        /// <summary>
        /// Clears the hopball reference. Called by Hopball when it dissolves/respawns.
        /// </summary>
        private void ClearHopballReference() {
            _currentHopballController = null;
            PublishHopballHoldStateChanged(false);
            // Unsubscribe from visual state changes
            HopballController.VisualStateChanged -= OnHopballVisualStateChanged;
        }

        // Hopball model references
        private GameObject _fpHopballVisualInstance; // Visual-only FP model (no state tracking)
        private GameObject _worldHopballVisualInstance; // Visual-only world model (parented to world weapon socket)
        private GameObject _fpHopballArmInstance; // FP hopball arm instance (for PutAway animation)
        private Vector3 _fpHopballBaseLocalPosition;
        private Vector3 _worldHopballBaseLocalPosition;
        private float _hopballFloatPhase;
        private float _heldHopballProgressionSeconds;
        private Collider PlayerCollider { get; set; }
        private bool _fpParticlesPrewarmed;
        private bool _worldParticlesPrewarmed;
        private Material _cachedHopballArmCustomMaterial;
        private EntityId _cachedHopballArmCustomSourceId = EntityId.None;
        private readonly Dictionary<EntityId, Material> _cachedHopballArmOutlineByRenderer = new();
        private PlayerAnimationEvents _armAnimationEvents;

        private static readonly MethodInfo VisualEffectSimulateFloatUIntMethod = typeof(VisualEffect).GetMethod(
            "Simulate",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            new[] { typeof(float), typeof(uint) },
            null);

        private static readonly MethodInfo VisualEffectSimulateFloatMethod = typeof(VisualEffect).GetMethod(
            "Simulate",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            new[] { typeof(float) },
            null);

        private readonly Collider[] _pickupHits = new Collider[10];
        private const float HopballHeldTimeProgressionChunkSeconds = 1f;

        private void Awake() {
            InitializeComponentReferences();
            InitializePlayerCollider();
            _hopballFloatPhase = Random.value * Mathf.PI * 2f;
        }

        private void InitializePlayerCollider() {
            if(PlayerCollider != null) return;

            PlayerCollider = _characterController;
        }

        private bool CacheHopballArmCustomMaterial() {
            if(playerController == null) return false;

            var playerMesh = playerController.PlayerMesh;
            if(playerMesh == null || playerMesh.materials.Length <= 1 || playerMesh.materials[1] == null) {
                return false;
            }

            var sourceMaterial = playerMesh.materials[1];
            if(_cachedHopballArmCustomMaterial != null &&
               sourceMaterial.GetEntityId() == _cachedHopballArmCustomSourceId) {
                return true;
            }

            if(_cachedHopballArmCustomMaterial != null) {
                Destroy(_cachedHopballArmCustomMaterial);
                _cachedHopballArmCustomMaterial = null;
            }

            _cachedHopballArmCustomMaterial = new Material(sourceMaterial);
            _cachedHopballArmCustomSourceId = sourceMaterial.GetEntityId();
            return true;
        }

        private void CacheHopballArmOutlineMaterials() {
            if(_fpHopballArmInstance == null) return;

            var renderers = _fpHopballArmInstance.GetComponentsInChildren<Renderer>(true);
            foreach(var r in renderers) {
                if(r == null) continue;

                var rendererId = r.GetEntityId();
                if(_cachedHopballArmOutlineByRenderer.TryGetValue(rendererId, out var cachedOutline) &&
                   cachedOutline != null) {
                    continue;
                }

                var materials = r.materials;
                Material sourceOutline = null;
                if(materials is { Length: > 0 } && materials[0] != null) {
                    sourceOutline = materials[0];
                } else if(r.sharedMaterial != null) {
                    sourceOutline = r.sharedMaterial;
                }

                if(sourceOutline == null) continue;
                _cachedHopballArmOutlineByRenderer[rendererId] = new Material(sourceOutline);
            }
        }

        private void PrewarmHopballVisualPool() {
            if(!prewarmHopballVisualsOnSpawn) return;
            if(!IsOwner) return;

            var layer = LayerMask.NameToLayer(WeaponLayerName);
            var swayHolder = FindSwayHolder();
            if(swayHolder != null) {
                if(_fpHopballVisualInstance == null && hopballVisualPrefab != null) {
                    _fpHopballVisualInstance = Instantiate(hopballVisualPrefab, swayHolder, false);
                    _fpHopballBaseLocalPosition = fpEquippedLocalPosition;
                    _fpHopballVisualInstance.transform.localPosition = _fpHopballBaseLocalPosition;
                    _fpHopballVisualInstance.transform.localRotation = Quaternion.identity;
                    SetGameObjectAndChildrenLayer(_fpHopballVisualInstance, layer);
                    SetFpVisualShadows(_fpHopballVisualInstance, false);
                    WarmupActiveHopballParticles(_fpHopballVisualInstance);
                    // Pool prewarm should prime allocations only; runtime equip still needs a fresh warmup pass.
                    _fpParticlesPrewarmed = false;
                    _fpHopballVisualInstance.SetActive(false);
                }
            }

            var bobHolder = FindBobHolder();
            if(bobHolder != null && _fpHopballArmInstance == null && hopballArmPrefab != null) {
                _fpHopballArmInstance = Instantiate(hopballArmPrefab, bobHolder, false);
                _fpHopballArmInstance.name = PooledArmVisualName;
                SetGameObjectAndChildrenLayer(_fpHopballArmInstance, layer);
                _armAnimationEvents = _fpHopballArmInstance.GetComponent<PlayerAnimationEvents>();
                if(_armAnimationEvents != null) {
                    _armAnimationEvents.OnPutAwayComplete -= OnArmPutAwayAnimationComplete;
                    _armAnimationEvents.OnPutAwayComplete += OnArmPutAwayAnimationComplete;
                }

                ApplyPlayerMaterialToArm();
                _fpHopballArmInstance.SetActive(false);
            }

            if(_worldWeaponSocket == null || hopballVisualPrefab == null) return;
            if(_worldHopballVisualInstance != null) return;

            _worldHopballVisualInstance = Instantiate(hopballVisualPrefab, _worldWeaponSocket, false);
            _worldHopballBaseLocalPosition = worldEquippedLocalPosition;
            _worldHopballVisualInstance.transform.localPosition = _worldHopballBaseLocalPosition;
            _worldHopballVisualInstance.transform.localRotation = Quaternion.identity;
            WarmupActiveHopballParticles(_worldHopballVisualInstance);
            // Pool prewarm should prime allocations only; runtime equip still needs a fresh warmup pass.
            _worldParticlesPrewarmed = false;
            _worldHopballVisualInstance.SetActive(false);
        }

        private void PrewarmHopballVisualsIfNeeded() {
            PrewarmHopballVisualPool();
        }

        private void OnArmPutAwayAnimationComplete() {
            if(_fpHopballArmInstance == null) return;
            _fpHopballArmInstance.SetActive(false);
        }

        private void OnEnable() {
            EventBus.Subscribe<PostMatchStartedEvent>(OnPostMatchStarted);
            EventBus.Subscribe<PostMatchBlackoutReadyEvent>(OnPostMatchBlackoutReady);
            EventBus.Subscribe<WeaponSwitchRequestedEvent>(OnWeaponSwitchRequested);
            EventBus.Subscribe<PlayerHopballDeathDropRequestedEvent>(OnDeathDropRequested);
            EventBus.Subscribe<PlayerHopballPickupRequestedEvent>(OnPickupRequested);
            EventBus.Subscribe<PlayerHopballManualDropRequestedEvent>(OnManualDropRequested);
            EventBus.Subscribe<HopballPickupPromptRequestEvent>(
                OnPickupPromptEvaluationRequested);
            EventBus.Subscribe<DisconnectFpVisualHideRequestedEvent>(OnPlayerDisconnectFpVisualHideRequested);
            EventBus.Subscribe<HopballVisualPrewarmRequestedEvent>(OnVisualPrewarmRequested);
            EventBus.Subscribe<HopballEquippedPresentationEvent>(OnHopballEquippedPresentation);
            EventBus.Subscribe<HopballDropPresentationEvent>(OnHopballDropPresentation);
            EventBus.Subscribe<HopballVisualCleanupRequestedEvent>(OnVisualCleanupRequested);
            EventBus.Subscribe<HopballHolderCleanupRequestedEvent>(OnHolderCleanupRequested);

            if(_armAnimationEvents == null) return;
            _armAnimationEvents.OnPutAwayComplete -= OnArmPutAwayAnimationComplete;
            _armAnimationEvents.OnPutAwayComplete += OnArmPutAwayAnimationComplete;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            CacheHopballArmCustomMaterial();
            PrewarmHopballVisualPool();
            if(HopballSpawnManager.Instance != null) {
                HopballSpawnManager.Instance.RegisterPlayerController(OwnerClientId, PlayerCollider);
            }
        }

        private void Update() {
            TrackHeldHopballProgression();
            UpdateHopballFloatMotion();
        }

        private void OnDisable() {
            FlushHeldHopballProgression();
            EventBus.Unsubscribe<PostMatchStartedEvent>(OnPostMatchStarted);
            EventBus.Unsubscribe<PostMatchBlackoutReadyEvent>(OnPostMatchBlackoutReady);
            EventBus.Unsubscribe<WeaponSwitchRequestedEvent>(OnWeaponSwitchRequested);
            EventBus.Unsubscribe<PlayerHopballDeathDropRequestedEvent>(OnDeathDropRequested);
            EventBus.Unsubscribe<PlayerHopballPickupRequestedEvent>(OnPickupRequested);
            EventBus.Unsubscribe<PlayerHopballManualDropRequestedEvent>(OnManualDropRequested);
            EventBus.Unsubscribe<HopballPickupPromptRequestEvent>(
                OnPickupPromptEvaluationRequested);
            EventBus.Unsubscribe<DisconnectFpVisualHideRequestedEvent>(OnPlayerDisconnectFpVisualHideRequested);
            EventBus.Unsubscribe<HopballVisualPrewarmRequestedEvent>(OnVisualPrewarmRequested);
            EventBus.Unsubscribe<HopballEquippedPresentationEvent>(OnHopballEquippedPresentation);
            EventBus.Unsubscribe<HopballDropPresentationEvent>(OnHopballDropPresentation);
            EventBus.Unsubscribe<HopballVisualCleanupRequestedEvent>(OnVisualCleanupRequested);
            EventBus.Unsubscribe<HopballHolderCleanupRequestedEvent>(OnHolderCleanupRequested);
            // Unsubscribe from visual state changes
            HopballController.VisualStateChanged -= OnHopballVisualStateChanged;
            if(_armAnimationEvents != null) {
                _armAnimationEvents.OnPutAwayComplete -= OnArmPutAwayAnimationComplete;
            }
        }

        private void OnPostMatchStarted(PostMatchStartedEvent _) {
            CancelPostMatchHopballTransitions();
        }

        private void OnPostMatchBlackoutReady(PostMatchBlackoutReadyEvent _) {
            if(playerController == null) return;

            ClearHopballReference();
            CleanupHopballVisuals();

            if(_playerTarget == null) {
                _playerTarget = playerController.PlayerTarget;
            }

            if(_playerTarget != null) {
                _playerTarget.enabled = false;
            }
        }

        private void OnWeaponSwitchRequested(WeaponSwitchRequestedEvent evt) {
            if(evt == null || playerController == null || playerController.NetworkObject == null) return;
            if(evt.PlayerNetworkObjectId != playerController.NetworkObjectId) return;

            if(IsHoldingHopball) {
                evt.WasHoldingHopball = true;
                DropHopball(HopballDropReason.WeaponSwitch);
            }

            if(IsRestoringAfterDissolve) {
                evt.WasRestoringAfterDissolve = true;
            }
        }

        private void OnDeathDropRequested(PlayerHopballDeathDropRequestedEvent evt) {
            if(evt == null || evt.PlayerOwnerClientId != OwnerClientId) return;
            DropHopballOnDeath();
        }

        private void OnPickupRequested(PlayerHopballPickupRequestedEvent evt) {
            if(evt == null || playerController == null || playerController.NetworkObject == null) return;
            if(evt.PlayerNetworkObjectId != playerController.NetworkObjectId) return;
            TryPickupHopball();
        }

        private void OnManualDropRequested(PlayerHopballManualDropRequestedEvent evt) {
            if(evt == null || playerController == null || playerController.NetworkObject == null) return;
            if(evt.PlayerNetworkObjectId != playerController.NetworkObjectId) return;
            DropHopball();
        }

        private void OnPickupPromptEvaluationRequested(
            HopballPickupPromptRequestEvent evt) {
            if(evt == null || playerController == null || playerController.NetworkObject == null) return;
            if(evt.PlayerNetworkObjectId != playerController.NetworkObjectId) return;
            evt.CanPickupNearbyHopball = CanPickupNearbyHopball();
        }

        private void OnPlayerDisconnectFpVisualHideRequested(DisconnectFpVisualHideRequestedEvent evt) {
            if(evt == null || playerController == null || playerController.NetworkObject == null) return;
            if(evt.PlayerNetworkObjectId != playerController.NetworkObjectId) return;
            HideFpVisualsForDisconnectTransition();
        }

        public override void OnDestroy() {
            base.OnDestroy();
            if(_armAnimationEvents != null) {
                _armAnimationEvents.OnPutAwayComplete -= OnArmPutAwayAnimationComplete;
            }

            if(_cachedHopballArmCustomMaterial != null) {
                Destroy(_cachedHopballArmCustomMaterial);
                _cachedHopballArmCustomMaterial = null;
            }

            _cachedHopballArmCustomSourceId = EntityId.None;

            foreach(var kvp in _cachedHopballArmOutlineByRenderer) {
                if(kvp.Value != null) {
                    Destroy(kvp.Value);
                }
            }

            _cachedHopballArmOutlineByRenderer.Clear();
        }

        public override void OnNetworkDespawn() {
            if(HopballSpawnManager.Instance != null) {
                HopballSpawnManager.Instance.UnregisterPlayerController(OwnerClientId);
            }

            base.OnNetworkDespawn();
            // Network is shutting down / objects are despawning. Do not try to send ServerRpcs here.
            // Just cleanup local holder visuals so we don't throw when the hopball NetworkObject is already despawned.
            if(!IsOwner) return;
            if(!IsHoldingHopball) return;

            _currentHopballController = null;
            HopballController.VisualStateChanged -= OnHopballVisualStateChanged;

            HideFpHopballVisualImmediate();
            DestroyWorldVisual();
            DestroyFpVisual();
            DestroyArmImmediate();

            PublishHopballWeaponPresentationRequest(HopballWeaponPresentationAction.RestoreAfterDrop);
            TransitionToWeaponLayers();
        }

        /// <summary>
        /// Tries to pick up a hopball within pickup range.
        /// </summary>
        private void TryPickupHopball() {
            if(!TryFindPickupCandidate(out var hopball)) return;

            var netObj = hopball.GetComponent<NetworkObject>();
            FlowLog.Emit(FlowEventIds.HopballPickupRequested,
                ("player", OwnerClientId),
                ("hopballNetId", netObj != null ? netObj.NetworkObjectId : 0UL),
                ("requestSource", "Proximity"));
            EquipHopball(hopball);
        }

        /// <summary>
        /// Returns whether this player can pick up a nearby hopball right now.
        /// Used by HUD prompt state.
        /// </summary>
        private bool CanPickupNearbyHopball() {
            return TryFindPickupCandidate(out _);
        }

        private bool TryFindPickupCandidate(out HopballController hopball) {
            hopball = null;

            if(playerController == null || IsHoldingHopball) {
                return false;
            }

            if(_hopballLayer == 0) {
                _hopballLayer = playerController.HopballLayer;
                if(_hopballLayer == 0) {
                    return false;
                }
            }

            var hitCount =
                Physics.OverlapSphereNonAlloc(playerController.Position, PickupRange, _pickupHits, _hopballLayer);
            if(hitCount == 0) {
                return false;
            }

            for(var i = 0; i < hitCount; i++) {
                var pickupHit = _pickupHits[i];
                if(pickupHit == null) continue;

                var candidate = pickupHit.GetComponent<HopballController>();
                if(candidate == null || candidate.IsEquipped || candidate.transform.parent != null ||
                   !candidate.gameObject.activeSelf) {
                    continue;
                }

                hopball = candidate;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Equips the hopball and handles local visual state.
        /// </summary>
        private void EquipHopball(HopballController hopballController) {
            if(hopballController == null || !IsOwner) return;

            _currentHopballController = hopballController;
            PublishHopballHoldStateChanged(true);
            _putAwayAnimationTriggered = false;
            if(playerController != null && playerController.PlayerInputController != null) {
                playerController.PlayerInputController.ForceDisableSniperOverlay(false);
            }

            SetupFpHopball();
            PublishHopballWeaponPresentationRequest(HopballWeaponPresentationAction.HideFirstPersonWeapon);

            HopballController.VisualStateChanged += OnHopballVisualStateChanged;
            TransitionToHopballLayers();

            if(HopballSpawnManager.Instance != null) {
                HopballSpawnManager.Instance.RequestEquipAuthority(
                    hopballController.GetComponent<NetworkObject>());
            }
        }

        /// <summary>
        /// Applies client-local presentation when a hopball equip event is received.
        /// </summary>
        private void OnVisualPrewarmRequested(HopballVisualPrewarmRequestedEvent _) {
            PrewarmHopballVisualsIfNeeded();
        }

        private void OnHopballEquippedPresentation(HopballEquippedPresentationEvent evt) {
            if(evt == null) return;
            var hopballRef = evt.HopballRef;
            if(!hopballRef.TryGet(out var networkObject) || networkObject == null) return;

            var hopball = networkObject.GetComponent<HopballController>();
            if(hopball == null) return;
            var energyRatio = hopball.VisualEnergyRatio;

            var isHolder = OwnerClientId == evt.HolderClientId && IsOwner;
            var localClientId = ulong.MaxValue;
            if(NetworkManager.Singleton != null && NetworkManager.Singleton.LocalClient != null) {
                localClientId = NetworkManager.Singleton.LocalClient.ClientId;
            }

            if(isHolder) {
                PublishHopballWeaponPresentationRequest(HopballWeaponPresentationAction.HideWorldWeapon);
                SetupWorldHopballVisual(true, energyRatio);
                PublishHopballWeaponPresentationRequest(HopballWeaponPresentationAction.ShowBothHolsters);
                if(playerController != null && playerController.PlayerShadow != null) {
                    playerController.PlayerShadow.ApplyHopballShadowState(true, true);
                }
            } else {
                SetupWorldHopballVisual(false, energyRatio);
                if(OwnerClientId != evt.HolderClientId || localClientId == evt.HolderClientId) return;
                EnablePlayerTarget();
                PublishHopballWeaponPresentationRequest(HopballWeaponPresentationAction.ShowBothHolsters);
                PublishHopballWeaponPresentationRequest(HopballWeaponPresentationAction.HideWorldWeapon);
                if(playerController != null && playerController.PlayerShadow != null) {
                    playerController.PlayerShadow.ApplyHopballShadowState(true, false);
                }
            }
        }

        private void OnHopballDropPresentation(HopballDropPresentationEvent evt) {
            if(evt == null || OwnerClientId != evt.HolderClientId) return;
            if(_playerTarget != null) {
                _playerTarget.enabled = false;
            }
        }

        private void OnVisualCleanupRequested(HopballVisualCleanupRequestedEvent _) {
            CleanupHopballVisuals();
        }

        private void OnHolderCleanupRequested(HopballHolderCleanupRequestedEvent evt) {
            if(evt == null || OwnerClientId != evt.HolderClientId) return;
            RunCleanupAndRestoreWeapons();
        }

        /// <summary>
        /// Enables the OSI Target for non-owners and sets team-based color.
        /// </summary>
        private void EnablePlayerTarget() {
            if(_playerTarget == null || IsOwner) return;

            // Get local player's team
            if(NetworkManager.Singleton == null || NetworkManager.Singleton.LocalClient == null) return;
            var localPlayer = NetworkManager.Singleton.LocalClient.PlayerObject;
            if(localPlayer == null) return;

            var localPlayerController = localPlayer.GetComponent<PlayerController>();
            if(localPlayerController == null) return;
            var localTeamMgr = localPlayerController.TeamManager;
            if(playerController == null) return;
            var holderTeamMgr = playerController.TeamManager;

            if(localTeamMgr == null || holderTeamMgr == null) {
                _playerTarget.SetTargetColor(new Color(1f, 0.392f, 0.392f)); // #FF6464 - Red (default)
            } else {
                var isTeammate = localTeamMgr.netTeam.Value == holderTeamMgr.netTeam.Value;
                _playerTarget.SetTargetColor(isTeammate
                    ? new Color(0.392f, 0.588f, 1f) // #6496FF - Blue
                    : new Color(1f, 0.392f, 0.392f)); // #FF6464 - Red
            }

            _playerTarget.enabled = true;
        }

        /// <summary>
        /// Sets up the first-person hopball visual (separate visual-only prefab that syncs with world hopball state).
        /// </summary>
        private void SetupFpHopball() {
            var swayHolder = FindSwayHolder();
            if(swayHolder == null) return;

            // Reuse pooled visual if available; instantiate once otherwise.
            if(_fpHopballVisualInstance == null) {
                _fpHopballVisualInstance = Instantiate(hopballVisualPrefab, swayHolder, false);
            } else if(_fpHopballVisualInstance.transform.parent != swayHolder) {
                _fpHopballVisualInstance.transform.SetParent(swayHolder, false);
            }

            _fpHopballBaseLocalPosition = fpEquippedLocalPosition;
            _fpHopballVisualInstance.transform.localPosition = _fpHopballBaseLocalPosition;
            _fpHopballVisualInstance.transform.localRotation = Quaternion.identity;
            _fpHopballVisualInstance.SetActive(true);
            if(!_fpParticlesPrewarmed) {
                WarmupActiveHopballParticles(_fpHopballVisualInstance, ResolveHopballVisualEnergyRatio());
                _fpParticlesPrewarmed = true;
            }

            // Set layer and shadows
            var layer = IsOwner ? LayerMask.NameToLayer(WeaponLayerName) : LayerMask.NameToLayer(MaskedLayerName);
            SetGameObjectAndChildrenLayer(_fpHopballVisualInstance, layer);
            SetFpVisualShadows(_fpHopballVisualInstance, false);

            // Reuse pooled hopball arm (parent to active BobHolder each equip).
            if(hopballArmPrefab == null) return;
            var bobHolder = FindBobHolder();
            if(bobHolder != null) {
                if(_fpHopballArmInstance == null) {
                    _fpHopballArmInstance = Instantiate(hopballArmPrefab, bobHolder, false);
                    _fpHopballArmInstance.name = PooledArmVisualName;
                    _armAnimationEvents = _fpHopballArmInstance.GetComponent<PlayerAnimationEvents>();
                    if(_armAnimationEvents != null) {
                        _armAnimationEvents.OnPutAwayComplete -= OnArmPutAwayAnimationComplete;
                        _armAnimationEvents.OnPutAwayComplete += OnArmPutAwayAnimationComplete;
                    }
                } else if(_fpHopballArmInstance.transform.parent != bobHolder) {
                    _fpHopballArmInstance.transform.SetParent(bobHolder, false);
                }

                _fpHopballArmInstance.SetActive(true);
                var armAnimator = _fpHopballArmInstance.GetComponent<Animator>();
                if(armAnimator != null) {
                    armAnimator.Rebind();
                    armAnimator.Update(0f);
                }

                SetGameObjectAndChildrenLayer(_fpHopballArmInstance, layer);
                ApplyPlayerMaterialToArm();
            } else {
                DevLog.LogError("[HopballController] BobHolder not found! Cannot instantiate hopball arm.");
            }
        }

        /// <summary>
        /// Finds the BobHolder transform for the currently active weapon.
        /// </summary>
        private Transform FindBobHolder() {
            var swayCamera = _fpCamera;
            if(swayCamera == null && playerController != null) {
                swayCamera = playerController.FpCamera;
            }

            var weaponCamera = playerController != null ? playerController.WeaponCamera : null;

            var currentFpWeapon = playerController != null ? playerController.GetCurrentFpWeaponForPresentation() : null;
            if(currentFpWeapon != null && currentFpWeapon.activeSelf) {
                var parent = currentFpWeapon.transform.parent;
                while(parent != null) {
                    if(parent.name == "BobHolder") {
                        return parent;
                    }

                    parent = parent.parent;
                }
            }

            if(swayCamera != null) {
                foreach(Transform swayHolder in swayCamera.transform) {
                    if(swayHolder.name != "SwayHolder") continue;

                    foreach(Transform bobHolder in swayHolder) {
                        if(bobHolder.name == "BobHolder" && bobHolder.gameObject.activeInHierarchy) {
                            return bobHolder;
                        }
                    }
                }
            }

            if(weaponCamera == null || (swayCamera != null && weaponCamera.transform == swayCamera.transform))
                return null;
            {
                foreach(Transform swayHolder in weaponCamera.transform) {
                    if(swayHolder.name != "SwayHolder") continue;

                    foreach(Transform bobHolder in swayHolder) {
                        if(bobHolder.name == "BobHolder" && bobHolder.gameObject.activeInHierarchy) {
                            return bobHolder;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Applies the player's chosen material to the hopball arm renderers.
        /// </summary>
        private void ApplyPlayerMaterialToArm() {
            if(_fpHopballArmInstance == null || playerController == null) return;
            if(!CacheHopballArmCustomMaterial()) return;
            CacheHopballArmOutlineMaterials();

            var renderers = _fpHopballArmInstance.GetComponentsInChildren<Renderer>(true);
            foreach(var r in renderers) {
                if(r == null) continue;
                var rendererId = r.GetEntityId();

                var materials = r.materials;
                if(materials == null || materials.Length < 2) {
                    var resizedMaterials = new Material[2];
                    if(materials is { Length: > 0 }) {
                        resizedMaterials[0] = materials[0];
                    }

                    materials = resizedMaterials;
                }

                if(_cachedHopballArmOutlineByRenderer.TryGetValue(rendererId, out var cachedOutline) &&
                   cachedOutline != null) {
                    materials[0] = cachedOutline;
                } else if(materials[0] == null && r.sharedMaterial != null) {
                    materials[0] = r.sharedMaterial;
                }

                materials[1] = _cachedHopballArmCustomMaterial;
                r.materials = materials;
            }
        }

        /// <summary>
        /// Finds the SwayHolder transform in the camera hierarchy.
        /// </summary>
        private Transform FindSwayHolder() {
            var swayCamera = _fpCamera;
            if(swayCamera == null && playerController != null) {
                swayCamera = playerController.FpCamera;
            }

            if(swayCamera != null) {
                foreach(Transform child in swayCamera.transform) {
                    if(child.name == "SwayHolder") {
                        return child;
                    }
                }
            }

            var weaponCamera = playerController != null ? playerController.WeaponCamera : null;
            if(weaponCamera != null && (swayCamera == null || weaponCamera.transform != swayCamera.transform)) {
                foreach(Transform child in weaponCamera.transform) {
                    if(child.name == "SwayHolder") {
                        return child;
                    }
                }
            }

            DevLog.LogError("[HopballController] FindSwayHolder: SwayHolder not found in camera hierarchy!");
            return null;
        }

        /// <summary>
        /// Sets shadow casting mode for all renderers in the FP visual.
        /// </summary>
        private static void SetFpVisualShadows(GameObject obj, bool castShadows) {
            var renderers = obj.GetComponentsInChildren<MeshRenderer>();
            var mode = castShadows
                ? UnityEngine.Rendering.ShadowCastingMode.On
                : UnityEngine.Rendering.ShadowCastingMode.Off;
            foreach(var mr in renderers) {
                mr.shadowCastingMode = mode;
            }
        }

        /// <summary>
        /// Sets up the visual-only world hopball model (parented to world weapon socket).
        /// The real hopball stays unparented and hidden.
        /// </summary>
        private void SetupWorldHopballVisual(bool isLocalClientHolder = false, float energyRatio = 1f) {
            if(_worldWeaponSocket == null || hopballVisualPrefab == null) {
                DevLog.LogError("[HopballController] SetupWorldHopballVisual: Missing required references");
                return;
            }

            // Reuse pooled world visual if available; instantiate once otherwise.
            if(_worldHopballVisualInstance == null) {
                _worldHopballVisualInstance = Instantiate(hopballVisualPrefab, _worldWeaponSocket, false);
            } else if(_worldHopballVisualInstance.transform.parent != _worldWeaponSocket) {
                _worldHopballVisualInstance.transform.SetParent(_worldWeaponSocket, false);
            }

            _worldHopballVisualInstance.SetActive(true);
            _worldHopballBaseLocalPosition = worldEquippedLocalPosition;
            _worldHopballVisualInstance.transform.localPosition = _worldHopballBaseLocalPosition;
            _worldHopballVisualInstance.transform.localRotation = Quaternion.identity;
            if(!_worldParticlesPrewarmed) {
                WarmupActiveHopballParticles(_worldHopballVisualInstance, energyRatio);
                _worldParticlesPrewarmed = true;
            }

            // Disable effects and light for the holder (they see FP visual instead)
            // For non-holders viewing the holder: ensure mesh renderer is enabled and visible
            var worldVisual = _worldHopballVisualInstance.GetComponent<HopballVisual>();
            if(worldVisual == null) {
                DevLog.LogError(
                    "[HopballController] SetupWorldHopballVisual: HopballVisual component not found on prefab!");
                return;
            }

            if(isLocalClientHolder) {
                worldVisual.DisableEffectsForOwner();
            } else {
                worldVisual.EnsureMeshRendererEnabled();
            }
        }

        /// <summary>
        /// Pre-warms active hopball visuals so pickup appears at the correct lifecycle immediately.
        /// </summary>
        private void WarmupActiveHopballParticles(GameObject visualRoot, float energyRatio = 1f) {
            if(visualRoot == null) return;
            if(hopballParticleWarmupSeconds <= 0f) return;

            var warmupScale = Mathf.Clamp01(energyRatio);
            var configuredWarmupTime = Mathf.Max(0f, hopballParticleWarmupSeconds * warmupScale);
            WarmupHopballVisualEffects(visualRoot, configuredWarmupTime);
        }

        /// <summary>Warmups hopball VFX on the given visual root.</summary>
        private static void WarmupHopballVisualEffects(GameObject visualRoot, float warmupTime) {
            if(visualRoot == null) return;

            var vfxComponents = visualRoot.GetComponentsInChildren<VisualEffect>(true);
            if(vfxComponents == null || vfxComponents.Length == 0) return;

            var clampedTime = Mathf.Max(0f, warmupTime);
            var stepCount = Mathf.Clamp(Mathf.CeilToInt(clampedTime / (1f / 60f)), 1, 240);
            var stepDelta = stepCount > 0 ? clampedTime / stepCount : clampedTime;
            foreach(var vfx in vfxComponents) {
                if(vfx == null || vfx.visualEffectAsset == null) continue;

                vfx.pause = false;
                vfx.Stop();
                vfx.Reinit();
                vfx.Play();

                var sampled = false;
                try {
                    if(clampedTime > 0f && VisualEffectSimulateFloatUIntMethod != null) {
                        VisualEffectSimulateFloatUIntMethod.Invoke(vfx, new object[] { stepDelta, (uint)stepCount });
                        sampled = true;
                    } else if(VisualEffectSimulateFloatMethod != null) {
                        for(var i = 0; i < stepCount; i++) {
                            VisualEffectSimulateFloatMethod.Invoke(vfx, new object[] { stepDelta });
                        }

                        sampled = true;
                    }
                } catch {
                    sampled = false;
                }

                if(sampled || clampedTime <= 0f) continue;

                for(var i = 0; i < stepCount; i++) {
                    vfx.AdvanceOneFrame();
                }
            }
        }

        /// <summary>Resolves current hopball visual energy ratio for warmup.</summary>
        private float ResolveHopballVisualEnergyRatio() {
            var hopball = _currentHopballController != null
                ? _currentHopballController
                : HopballSpawnManager.Instance != null
                    ? HopballSpawnManager.Instance.CurrentHopballController
                    : null;
            return hopball == null ? 1f : hopball.VisualEnergyRatio;
        }

        /// <summary>
        /// Applies a subtle vertical oscillation to held hopball visuals so the orb feels like floating energy.
        /// </summary>
        private void UpdateHopballFloatMotion() {
            if(!enableHopballFloatMotion) return;
            if(hopballFloatAmplitude <= 0f || hopballFloatCyclesPerSecond <= 0f) return;

            // Keep this intentionally subtle/slower so the orb feels like hovering energy, not bobbing.
            var amplitude = Mathf.Min(hopballFloatAmplitude, 0.015f);
            var cyclesPerSecond = Mathf.Min(hopballFloatCyclesPerSecond, 0.20f);
            var baseWave = Mathf.Sin((Time.time + _hopballFloatPhase) * cyclesPerSecond * Mathf.PI * 2f);

            // Blend toward a smooth-step-shaped wave to dwell a bit longer at apexes.
            var normalized = baseWave * 0.5f + 0.5f;
            var apexWave = Mathf.SmoothStep(0f, 1f, normalized) * 2f - 1f;
            var wave = Mathf.Lerp(baseWave, apexWave, hopballFloatApexDwell);
            var yOffset = wave * amplitude;

            if(_fpHopballVisualInstance != null && _fpHopballVisualInstance.activeInHierarchy) {
                var fpPos = _fpHopballBaseLocalPosition;
                fpPos.y += yOffset;
                _fpHopballVisualInstance.transform.localPosition = fpPos;
            }

            if(_worldHopballVisualInstance == null || !_worldHopballVisualInstance.activeInHierarchy) return;
            var worldPos = _worldHopballBaseLocalPosition;
            worldPos.y += yOffset;
            _worldHopballVisualInstance.transform.localPosition = worldPos;
        }

        /// <summary>
        /// Drops the hopball and restores weapons.
        /// </summary>
        private void DropHopball(HopballDropReason reason = HopballDropReason.Manual) {
            if(_currentHopballController == null || !IsOwner) return;

            var hopball = _currentHopballController;
            _currentHopballController = null;
            PublishHopballHoldStateChanged(false);

            Vector3 dropPosition;
            Quaternion dropRotation;
            if(_worldHopballVisualInstance != null) {
                dropPosition = _worldHopballVisualInstance.transform.position;
                dropRotation = _worldHopballVisualInstance.transform.rotation;
            } else {
                var hopballTransform = hopball.transform;
                dropPosition = hopballTransform.position;
                dropRotation = hopballTransform.rotation;
            }

            HopballController.VisualStateChanged -= OnHopballVisualStateChanged;

            HideFpHopballVisualImmediate();
            DestroyWorldVisual();

            if(_fpHopballArmInstance != null) {
                var activeBobHolder = FindBobHolder();
                if(activeBobHolder != null && _fpHopballArmInstance.transform.parent != activeBobHolder) {
                    _fpHopballArmInstance.transform.SetParent(activeBobHolder, false);
                }
            }

            HandleArmPutAwayAnimation();

            // Get player velocity to transfer to ball
            var playerVelocity = Vector3.zero;
            if(playerController != null) {
                playerVelocity = playerController.GetFullVelocity;
            }

            var canSendDrop = true;
            if(NetworkManager.Singleton == null) {
                canSendDrop = false;
            } else if(!NetworkManager.Singleton.IsListening) {
                // Prevent errors during shutdown/despawn (e.g. exiting match, host migration teardown).
                canSendDrop = false;
            }

            var hopballNetObj = hopball.GetComponent<NetworkObject>();
            if(hopballNetObj == null) {
                canSendDrop = false;
            } else if(!hopballNetObj.IsSpawned) {
                canSendDrop = false;
            }

            if(canSendDrop) {
                if(HopballSpawnManager.Instance != null) {
                    HopballSpawnManager.Instance.RequestDropAuthority(new HopballSpawnManager.HopballDropRequest {
                        HopballRef = hopballNetObj,
                        DropPosition = dropPosition,
                        DropRotation = dropRotation,
                        PlayerVelocity = playerVelocity,
                        DropReason = reason.ToString()
                    });
                }
            }

            if(reason == HopballDropReason.Manual) {
                PublishHopballWeaponPresentationRequest(HopballWeaponPresentationAction.RestoreAfterDrop);

                if(playerController != null && playerController.PlayerShadow != null) {
                    playerController.PlayerShadow.ApplyHopballShadowState(false, playerController.IsOwner);
                    playerController.PlayerShadow.ApplyOwnerDefaultShadowState();
                }
            }

            TransitionToWeaponLayers();
        }

        /// <summary>
        /// Drops the hopball when the player dies.
        /// </summary>
        private void DropHopballOnDeath() {
            if(HopballSpawnManager.Instance == null ||
               HopballSpawnManager.Instance.CurrentHopballController == null) return;

            var hopball = HopballSpawnManager.Instance.CurrentHopballController;

            if(!hopball.IsEquipped || hopball.HolderController == null ||
               hopball.HolderController.OwnerClientId != OwnerClientId) return;

            _currentHopballController = null;
            PublishHopballHoldStateChanged(false);

            var dropPosition = playerController.Position + Vector3.up * 1.5f;
            var dropRotation = playerController.Rotation;

            // On death, use zero velocity (player is dead, no momentum transfer)
            var deathVelocity = Vector3.zero;

            if(HopballSpawnManager.Instance != null) {
                HopballSpawnManager.Instance.RequestDropAuthority(new HopballSpawnManager.HopballDropRequest {
                    HopballRef = hopball.NetworkObject,
                    DropPosition = dropPosition,
                    DropRotation = dropRotation,
                    PlayerVelocity = deathVelocity,
                    DropReason = HopballDropReason.PlayerDeath.ToString()
                });
            }

            CleanupAndRestoreWeaponsClientRpc();
        }

        /// <summary>
        /// Client RPC to clean up visuals and restore weapons after death drop.
        /// </summary>
        [ClientRpc]
        private void CleanupAndRestoreWeaponsClientRpc() {
            // Only cleanup on the owner's client
            if(!IsOwner) return;

            // Destroy visuals
            DestroyFpVisual();
            DestroyWorldVisual();
            // Destroy arm immediately (death case, can't wait for animation)
            DestroyArmImmediate();
            // Do not restore weapon visuals here, death flow handles showing weapons when appropriate
        }

        /// <summary>
        /// Runs cleanup and restore-weapons logic locally. Used by the state-update path (DA-compatible).
        /// </summary>
        private void RunCleanupAndRestoreWeapons() {
            var postMatchTransitionActive = IsPostMatchTransitionActive();

            _currentHopballController = null;
            PublishHopballHoldStateChanged(false);
            HopballController.VisualStateChanged -= OnHopballVisualStateChanged;

            if(IsOwner) {
                EventBus.Publish(new HopballDissolvedEvent(OwnerClientId));
                DestroyFpVisual();
                DestroyWorldVisual();
                if(postMatchTransitionActive) DestroyArmImmediate();
                PublishHopballWeaponPresentationRequest(HopballWeaponPresentationAction.RestoreAfterDrop);
                TransitionToWeaponLayers();
                if(_playerTarget != null) _playerTarget.enabled = false;
            } else {
                DestroyWorldVisual();
            }

            if(postMatchTransitionActive) {
                PublishHopballWeaponPresentationRequest(
                    HopballWeaponPresentationAction.CancelPendingPullOutForPostMatch);
                return;
            }

            PublishHopballWeaponPresentationRequest(HopballWeaponPresentationAction.TriggerPullOut);
        }

        private void TrackHeldHopballProgression() {
            if(!IsOwner || !IsHoldingHopball) return;

            _heldHopballProgressionSeconds += Time.deltaTime;
            if(_heldHopballProgressionSeconds < HopballHeldTimeProgressionChunkSeconds) return;

            var wholeChunks = Mathf.Floor(_heldHopballProgressionSeconds / HopballHeldTimeProgressionChunkSeconds);
            var awardedSeconds = wholeChunks * HopballHeldTimeProgressionChunkSeconds;
            _heldHopballProgressionSeconds -= awardedSeconds;
            EventBus.Publish(new HopballHeldTimeAwardedEvent(OwnerClientId, awardedSeconds));
        }

        private void FlushHeldHopballProgression() {
            if(!IsOwner || _heldHopballProgressionSeconds <= 0f) {
                _heldHopballProgressionSeconds = 0f;
                return;
            }

            EventBus.Publish(new HopballHeldTimeAwardedEvent(OwnerClientId, _heldHopballProgressionSeconds));
            _heldHopballProgressionSeconds = 0f;
        }

        private void PublishHopballHoldStateChanged(bool isHoldingHopball) {
            if(!isHoldingHopball) {
                FlushHeldHopballProgression();
            } else {
                _heldHopballProgressionSeconds = 0f;
            }

            EventBus.Publish(new HopballHoldStateChangedEvent(OwnerClientId, isHoldingHopball));
        }

        /// <summary>
        /// Hides the FP hopball visual immediately.
        /// </summary>
        private void HideFpHopballVisualImmediate() {
            if(_fpHopballVisualInstance == null) return;
            _fpHopballVisualInstance.SetActive(false);
            _fpParticlesPrewarmed = false;
        }

        /// <summary>
        /// Triggers PutAway animation on the arm. Pooled arm is disabled when PutAwayComplete animation event fires.
        /// </summary>
        private void HandleArmPutAwayAnimation() {
            if(_fpHopballArmInstance == null) return;
            if(IsPostMatchTransitionActive()) {
                DestroyArmImmediate();
                return;
            }

            var animator = _fpHopballArmInstance.GetComponent<Animator>();
            if(animator == null) {
                DevLog.LogError("[HopballController] HandleArmPutAwayAnimation: Animator not found on arm instance");
                return;
            }

            _fpHopballArmInstance.SetActive(true);
            animator.SetTrigger(PutAwayHash);
        }

        /// <summary>
        /// Called when hopball visual state changes. Triggers PutAway animation during dissolve.
        /// </summary>
        private void OnHopballVisualStateChanged(HopballController.HopballVisualState state) {
            // Only handle on owner's client
            if(!IsOwner) return;
            if(IsPostMatchTransitionActive()) {
                _putAwayAnimationTriggered = true;
                DestroyArmImmediate();
                return;
            }

            // Trigger PutAway animation when dissolve reaches threshold
            if(_putAwayAnimationTriggered || !(state.DissolveAmount >= putAwayDissolveThreshold)) return;
            _putAwayAnimationTriggered = true;
            HandleArmPutAwayAnimation();
        }

        /// <summary>
        /// Cancels local hopball dissolve/weapon transition visuals for post-match podium flow.
        /// This should be called while fade-to-black is active.
        /// </summary>
        private void CancelPostMatchHopballTransitions() {
            _putAwayAnimationTriggered = true;
            HopballController.VisualStateChanged -= OnHopballVisualStateChanged;
            HideFpHopballVisualImmediate();
            DestroyWorldVisual();
            DestroyArmImmediate();
            PublishHopballWeaponPresentationRequest(HopballWeaponPresentationAction.CancelPendingPullOutForPostMatch);
        }

        private static bool IsPostMatchTransitionActive() {
            // Only treat transition as "safe to hard-cancel visuals" once fade-to-black should be complete.
            return PostMatchManager.IsPodiumBlackoutActiveLocal;
        }

        /// <summary>
        /// Destroys the arm instance immediately. Used for death/dissolve cleanup.
        /// Note: Normal weapon switch uses animation event which destroys the arm automatically.
        /// </summary>
        private void DestroyArmImmediate() {
            if(_fpHopballArmInstance == null) return;
            _fpHopballArmInstance.SetActive(false);
        }

        /// <summary>
        /// Hides hopball FP visuals without destroying. Used to defer visible teardown during
        /// unexpected disconnect, hide first so when NGO despawns the player, the teardown is invisible.
        /// </summary>
        private void HideFpVisualsForDisconnectTransition() {
            if(!IsOwner) return;
            HideFpHopballVisualImmediate();
            DestroyArmImmediate();
        }

        private void PublishHopballWeaponPresentationRequest(HopballWeaponPresentationAction action) {
            if(playerController == null || playerController.NetworkObject == null) return;
            EventBus.Publish(new PlayerHopballWeaponPresentationRequestedEvent(playerController.NetworkObjectId,
                action));
        }

        // ========================================================================
        // Helper Methods
        // ========================================================================

        /// <summary>
        /// Initializes component references from PlayerController.
        /// </summary>
        private void InitializeComponentReferences() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                DevLog.LogError("[HopballController] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_combatController == null) _combatController = playerController.CombatController;
            if(_fpCamera == null) _fpCamera = playerController.FpCamera;
            if(_worldWeaponSocket == null) _worldWeaponSocket = playerController.WorldWeaponSocket;
            _hopballLayer = playerController.HopballLayer;
            if(_playerTarget == null) _playerTarget = playerController.PlayerTarget;
            if(_characterController == null) _characterController = playerController.CharacterController;

            // Validate PlayerRenderer (required for material and renderer operations)
            if(_playerRenderer == null) _playerRenderer = playerController.PlayerRenderer;
            if(_playerRenderer == null) {
                DevLog.LogError("[HopballController] PlayerRenderer not found! Cannot perform renderer operations.");
                enabled = false;
                return;
            }

            // Cache animator and layer indices
            if(_playerAnimator == null && playerController != null) {
                _playerAnimator = playerController.PlayerAnimator;
            }

            CacheHopballArmCustomMaterial();

            if(_playerAnimator == null) return;
            // Use inspector values if set, otherwise auto-detect by name
            _weaponHoldLayerIndex = weaponHoldLayerIndex >= 0
                ? weaponHoldLayerIndex
                : _playerAnimator.GetLayerIndex("Weapon Hold Layer");

            _rightHandHoldLayerIndex = rightHandHoldLayerIndex >= 0
                ? rightHandHoldLayerIndex
                : _playerAnimator.GetLayerIndex("Right Hand Hold Layer");

            // Log layer indices for debugging
            if(_weaponHoldLayerIndex < 0) {
                DevLog.LogWarning("[HopballController] Weapon Hold Layer not found!");
            }

            if(_rightHandHoldLayerIndex < 0) {
                DevLog.LogWarning("[HopballController] Right Hand Hold Layer not found!");
            }
        }

        /// <summary>
        /// Transitions animation layers from weapon hold (both arms) to hopball hold (right arm only).
        /// Left arm will transition to base layer walking motion, right arm will transition to hopball hold.
        /// </summary>
        private void TransitionToHopballLayers() {
            if(_playerAnimator == null || _weaponHoldLayerIndex < 0 || _rightHandHoldLayerIndex < 0) {
                DevLog.LogWarning("[HopballController] Cannot transition layers: animator or layer indices not found");
                return;
            }

            // Stop any existing transition
            if(_layerTransitionCoroutine != null) {
                StopCoroutine(_layerTransitionCoroutine);
            }

            // TODO: Uncomment when HopballHold animation is added
            // _playerAnimator.CrossFadeInFixedTime("HopballHold", layerTransitionDuration, _rightHandHoldLayerIndex);

            // Start the weight transition coroutine
            _layerTransitionCoroutine = StartCoroutine(TransitionLayerWeights(true, layerTransitionDuration));
        }

        /// <summary>
        /// Transitions animation layers back from hopball hold (right arm only) to weapon hold (both arms).
        /// </summary>
        private void TransitionToWeaponLayers() {
            if(_playerAnimator == null || _weaponHoldLayerIndex < 0 || _rightHandHoldLayerIndex < 0) {
                DevLog.LogWarning("[HopballController] Cannot transition layers: animator or layer indices not found");
                return;
            }

            // Stop any existing transition
            if(_layerTransitionCoroutine != null) {
                StopCoroutine(_layerTransitionCoroutine);
            }

            // Start the weight transition coroutine (reverse direction)
            _layerTransitionCoroutine = StartCoroutine(TransitionLayerWeights(false, layerTransitionDuration));
        }

        /// <summary>
        /// Coroutine that smoothly transitions layer weights between weapon hold and hopball hold layers.
        /// </summary>
        private IEnumerator TransitionLayerWeights(bool toHopball, float duration) {
            if(_playerAnimator == null || _weaponHoldLayerIndex < 0 || _rightHandHoldLayerIndex < 0) {
                yield break;
            }

            var elapsed = 0f;
            var startWeaponWeight = _playerAnimator.GetLayerWeight(_weaponHoldLayerIndex);
            var startRightHandWeight = _playerAnimator.GetLayerWeight(_rightHandHoldLayerIndex);

            var targetWeaponWeight = toHopball ? 0f : 1f;
            var targetRightHandWeight = toHopball ? 1f : 0f;

            while(elapsed < duration) {
                elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(elapsed / duration);

                // Smooth interpolation (you could use AnimationCurve here for custom easing)
                var smoothT = t * t * (3f - 2f * t); // Smooth-step function

                // Interpolate layer weights
                var currentWeaponWeight = Mathf.Lerp(startWeaponWeight, targetWeaponWeight, smoothT);
                var currentRightHandWeight = Mathf.Lerp(startRightHandWeight, targetRightHandWeight, smoothT);

                _playerAnimator.SetLayerWeight(_weaponHoldLayerIndex, currentWeaponWeight);
                _playerAnimator.SetLayerWeight(_rightHandHoldLayerIndex, currentRightHandWeight);

                yield return null;
            }

            // Ensure final weights are set exactly
            _playerAnimator.SetLayerWeight(_weaponHoldLayerIndex, targetWeaponWeight);
            _playerAnimator.SetLayerWeight(_rightHandHoldLayerIndex, targetRightHandWeight);

            _layerTransitionCoroutine = null;
        }

        /// <summary>
        /// Destroys the FP visual instance if it exists.
        /// Note: Arm is destroyed separately via animation event, not here.
        /// </summary>
        private void DestroyFpVisual() {
            if(_fpHopballVisualInstance == null) return;
            _fpHopballVisualInstance.SetActive(false);
            _fpParticlesPrewarmed = false;
        }

        /// <summary>
        /// Destroys the world visual instance if it exists.
        /// </summary>
        private void DestroyWorldVisual() {
            if(_worldHopballVisualInstance == null) return;
            _worldHopballVisualInstance.SetActive(false);
            _worldParticlesPrewarmed = false;
        }

        /// <summary>
        /// Cleans up all hopball visuals. Called when ball respawns to ensure no visuals remain.
        /// Note: visuals are pooled; cleanup just disables them.
        /// </summary>
        private void CleanupHopballVisuals() {
            // Only cleanup if we're not currently holding the ball
            if(IsHoldingHopball) return;
            DestroyFpVisual();
            DestroyWorldVisual();
            // Do NOT destroy arm here - it should be destroyed via animation event when PutAway completes
            // or via explicit cleanup in death/dissolve cases
        }

        /// <summary>
        /// Recursively sets the layer of a GameObject and all its children.
        /// </summary>
        private static void SetGameObjectAndChildrenLayer(GameObject obj, int layer) {
            if(obj == null) return;
            obj.layer = layer;
            foreach(Transform child in obj.transform) {
                SetGameObjectAndChildrenLayer(child.gameObject, layer);
            }
        }
    }
}

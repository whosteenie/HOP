using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Game.Hopball;
using Game.Match;
using Game.Player.Combat;
using Game.Player.Core;
using Game.Weapons;
using Game.Weapons.Manager;
using Network.Diagnostics;
using OSI;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.VFX;

namespace Game.Player.Hopball {
    /// <summary>
    /// Handles hopball pickup, equipping, and dropping for the player.
    /// Manages weapon visibility and prevents shooting/reloading while holding the ball.
    /// </summary>
    public class PlayerHopballController : NetworkBehaviour {
        public enum HopballDropReason {
            Manual,
            WeaponSwitch,
            PlayerDeath
        }

        private static readonly List<PlayerHopballController> InstancesInternal = new();
        private static readonly int PutAwayHash = Animator.StringToHash("PutAway");
        public static IReadOnlyList<PlayerHopballController> Instances => InstancesInternal;

        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private WeaponManager _weaponManager;
        private PlayerHealthController _healthController; // For worldWeaponSocket reference
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
        [SerializeField] private float hopballFloatCyclesPerSecond = 0.06f;
        [SerializeField, Range(0f, 1f)] private float hopballFloatApexDwell = 0.35f;

        [SerializeField] private Vector3 fpEquippedLocalPosition = Vector3.zero;
        [SerializeField] private Vector3 worldEquippedLocalPosition = Vector3.zero;

        [Header("Dissolve Settings")]
        [SerializeField] private float putAwayDissolveThreshold = 0.75f;

        [Header("Animation Layer Settings")]
        [SerializeField] private float layerTransitionDuration = 0.3f;
        [Tooltip("Layer index for 'Weapon Hold Layer' (both arms). Set in inspector or will auto-detect by name.")]
        [SerializeField] private int weaponHoldLayerIndex = -1;
        [Tooltip("Layer index for 'Right Hand Hold Layer' (right arm only). Set in inspector or will auto-detect by name.")]
        [SerializeField] private int rightHandHoldLayerIndex = -1;

        // State
        public bool IsHoldingHopball => _currentHopballController != null;
        public static bool IsRestoringAfterDissolve => false; // Flag to allow weapon switch after dissolve
        private HopballController _currentHopballController;
        
        // Animation layer indices (cached for performance)
        private int _weaponHoldLayerIndex = -1;
        private int _rightHandHoldLayerIndex = -1;
        private Animator _playerAnimator;
        private Coroutine _layerTransitionCoroutine;
        private bool _putAwayAnimationTriggered;

        public PlayerController PlayerController => playerController;

        /// <summary>
        /// Clears the hopball reference. Called by Hopball when it dissolves/respawns.
        /// </summary>
        public void ClearHopballReference() {
            _currentHopballController = null;
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
        private Coroutine _restoreWeaponsCoroutine; // Track restore coroutine
        public Collider PlayerCollider { get; private set; }
        private bool _fpParticlesPrewarmed;
        private bool _worldParticlesPrewarmed;
        private Material _cachedHopballArmCustomMaterial;
        private int _cachedHopballArmCustomSourceId;
        private readonly Dictionary<int, Material> _cachedHopballArmOutlineByRenderer = new();
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
            if(_cachedHopballArmCustomMaterial != null && sourceMaterial.GetInstanceID() == _cachedHopballArmCustomSourceId) {
                return true;
            }

            if(_cachedHopballArmCustomMaterial != null) {
                Destroy(_cachedHopballArmCustomMaterial);
                _cachedHopballArmCustomMaterial = null;
            }

            _cachedHopballArmCustomMaterial = new Material(sourceMaterial);
            _cachedHopballArmCustomSourceId = sourceMaterial.GetInstanceID();
            return true;
        }

        private void CacheHopballArmOutlineMaterials() {
            if(_fpHopballArmInstance == null) return;

            var renderers = _fpHopballArmInstance.GetComponentsInChildren<Renderer>(true);
            foreach(var r in renderers) {
                if(r == null) continue;

                var rendererId = r.GetInstanceID();
                if(_cachedHopballArmOutlineByRenderer.TryGetValue(rendererId, out var cachedOutline) && cachedOutline != null) {
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

            var layer = LayerMask.NameToLayer("Weapon");
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
                _fpHopballArmInstance.name = "PooledArmVisual";
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

        public void PrewarmHopballVisualsIfNeeded() {
            PrewarmHopballVisualPool();
        }

        private void OnArmPutAwayAnimationComplete() {
            if(_fpHopballArmInstance == null) return;
            _fpHopballArmInstance.SetActive(false);
        }

        private void OnEnable() {
            if(!InstancesInternal.Contains(this)) {
                InstancesInternal.Add(this);
            }

            if(HopballController.Instance != null) {
                HopballController.Instance.OnControllerRegistered(this);
            }

            if(_armAnimationEvents == null) return;
            _armAnimationEvents.OnPutAwayComplete -= OnArmPutAwayAnimationComplete;
            _armAnimationEvents.OnPutAwayComplete += OnArmPutAwayAnimationComplete;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            CacheHopballArmCustomMaterial();
            PrewarmHopballVisualPool();
        }
        
        private void Update() {
            // Progression: Track time holding hopball
            if (IsOwner && IsHoldingHopball && Progression.ProgressionManager.Instance != null) {
                Progression.ProgressionManager.Instance.AddTimeHoldingHopball(Time.deltaTime);
            }
            UpdateHopballFloatMotion();
        }

        private void OnDisable() {
            if(HopballController.Instance != null) {
                HopballController.Instance.OnControllerUnregistered(this);
            }
            InstancesInternal.Remove(this);
            // Unsubscribe from visual state changes
            HopballController.VisualStateChanged -= OnHopballVisualStateChanged;
            if(_armAnimationEvents != null) {
                _armAnimationEvents.OnPutAwayComplete -= OnArmPutAwayAnimationComplete;
            }
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
            _cachedHopballArmCustomSourceId = 0;

            foreach(var kvp in _cachedHopballArmOutlineByRenderer) {
                if(kvp.Value != null) {
                    Destroy(kvp.Value);
                }
            }
            _cachedHopballArmOutlineByRenderer.Clear();
        }

        public override void OnNetworkDespawn() {
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

            ShowWeapons();
            TransitionToWeaponLayers();
        }

        /// <summary>
        /// Tries to pick up a hopball within pickup range.
        /// </summary>
        public void TryPickupHopball() {
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
        public bool CanPickupNearbyHopball() {
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

            var hitCount = Physics.OverlapSphereNonAlloc(playerController.Position, PickupRange, _pickupHits, _hopballLayer);
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
            _putAwayAnimationTriggered = false;
            if(playerController != null && playerController.PlayerInput != null) {
                playerController.PlayerInput.ForceDisableSniperOverlay(false);
            }

            SetupFpHopball();
            HideFpWeapons();

            HopballController.VisualStateChanged += OnHopballVisualStateChanged;
            TransitionToHopballLayers();

            if(HopballSpawnManager.Instance != null) {
                HopballSpawnManager.Instance.RequestEquipHopballAuthority(
                    hopballController.GetComponent<NetworkObject>());
            }
        }

        /// <summary>
        /// Consolidated ClientRpc that handles all client updates when hopball is equipped.
        /// Replaces multiple separate RPCs to reduce network overhead.
        /// </summary>
        [ClientRpc]
        internal void OnHopballEquippedClientRpc(NetworkObjectReference hopballRef, ulong holderClientId) {
            if(!hopballRef.TryGet(out var networkObject) || networkObject == null) return;

            var hopball = networkObject.GetComponent<HopballController>();
            if(hopball == null) return;
            var energyRatio = hopball.VisualEnergyRatio;

            var isHolder = OwnerClientId == holderClientId && IsOwner;
            var localClientId = ulong.MaxValue;
            if(NetworkManager.Singleton != null && NetworkManager.Singleton.LocalClient != null) {
                localClientId = NetworkManager.Singleton.LocalClient.ClientId;
            }

            if(isHolder) {
                HideWorldWeapon();
                SetupWorldHopballVisual(true, energyRatio);
                ShowBothHolsters();
                if(playerController != null && playerController.PlayerShadow != null) {
                    playerController.PlayerShadow.ApplyHopballShadowState(true, true);
                }
            } else {
                SetupWorldHopballVisual(false, energyRatio);
                if(OwnerClientId != holderClientId || localClientId == holderClientId) return;
                EnablePlayerTarget();
                ShowBothHolsters();
                HideWorldWeapon();
                if(playerController != null && playerController.PlayerShadow != null) {
                    playerController.PlayerShadow.ApplyHopballShadowState(true, false);
                }
            }
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
                WarmupActiveHopballParticles(_fpHopballVisualInstance, ResolveCurrentHopballVisualEnergyRatio());
                _fpParticlesPrewarmed = true;
            }

            // Set layer and shadows
            var layer = IsOwner ? LayerMask.NameToLayer("Weapon") : LayerMask.NameToLayer("Masked");
            SetGameObjectAndChildrenLayer(_fpHopballVisualInstance, layer);
            SetFpVisualShadows(_fpHopballVisualInstance, false);

            // Reuse pooled hopball arm (parent to active BobHolder each equip).
            if(hopballArmPrefab == null) return;
            var bobHolder = FindBobHolder();
            if(bobHolder != null) {
                if(_fpHopballArmInstance == null) {
                    _fpHopballArmInstance = Instantiate(hopballArmPrefab, bobHolder, false);
                    _fpHopballArmInstance.name = "PooledArmVisual";
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
                Debug.LogError("[HopballController] BobHolder not found! Cannot instantiate hopball arm.");
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

            if(_weaponManager != null) {
                var currentFpWeapon = _weaponManager.GetCurrentFpWeapon();
                if(currentFpWeapon != null && currentFpWeapon.activeSelf) {
                    var parent = currentFpWeapon.transform.parent;
                    while(parent != null) {
                        if(parent.name == "BobHolder") {
                            return parent;
                        }
                        parent = parent.parent;
                    }
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
                var rendererId = r.GetInstanceID();

                var materials = r.materials;
                if(materials == null || materials.Length < 2) {
                    var resizedMaterials = new Material[2];
                    if(materials is { Length: > 0 }) {
                        resizedMaterials[0] = materials[0];
                    }
                    materials = resizedMaterials;
                }

                if(_cachedHopballArmOutlineByRenderer.TryGetValue(rendererId, out var cachedOutline) && cachedOutline != null) {
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

            Debug.LogError("[HopballController] FindSwayHolder: SwayHolder not found in camera hierarchy!");
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
                Debug.LogError("[HopballController] SetupWorldHopballVisual: Missing required references");
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
                Debug.LogError("[HopballController] SetupWorldHopballVisual: HopballVisual component not found on prefab!");
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
            WarmupActiveHopballVisualEffects(visualRoot, configuredWarmupTime);
        }

        private static void WarmupActiveHopballVisualEffects(GameObject visualRoot, float warmupTime) {
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

        private float ResolveCurrentHopballVisualEnergyRatio() {
            var hopball = _currentHopballController != null ? _currentHopballController : HopballController.Instance;
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
        public void DropHopball(HopballDropReason reason = HopballDropReason.Manual) {
            if(_currentHopballController == null || !IsOwner) return;

            var hopball = _currentHopballController;
            _currentHopballController = null;

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
                    HopballSpawnManager.Instance.RequestDropHopballAuthority(hopballNetObj, dropPosition, dropRotation,
                        playerVelocity, reason.ToString());
                }
            }

            if(reason == HopballDropReason.Manual) {
                ShowWeapons();
                if(_weaponManager != null) {
                    _weaponManager.RefreshHolsterVisibility();
                }
                if(playerController != null && playerController.PlayerShadow != null) {
                    playerController.PlayerShadow.ApplyHopballShadowState(false, playerController.IsOwner);
                    playerController.PlayerShadow.ApplyOwnerDefaultShadowState();
                }
            }

            TransitionToWeaponLayers();
        }

        /// <summary>
        /// Server-side method to drop the hopball at a specific position.
        /// Can be called directly from server or via ServerRpc from client.
        /// </summary>
        internal static async UniTaskVoid DropHopballAtPositionAuthority(HopballController hopball, Vector3 dropPosition,
            Quaternion dropRotation, ulong requestingClientId, Vector3 playerVelocity, string dropReason) {
            if(hopball == null) return;
            if(!hopball.IsEquipped) {
                FlowLog.Emit(FlowEventIds.AnomalyHopballMismatch,
                    ("serverHolder", hopball.HolderController != null ? hopball.HolderController.OwnerClientId.ToString() : "None"),
                    ("localHolder", requestingClientId),
                    ("osiHolder", "Unknown"),
                    ("reason", "DropRejectedNotEquipped"));
                return;
            }

            hopball.PrepareDropClientRpc();
            await UniTask.WaitForEndOfFrame();

            PlayerController requestingController = null;
            if(NetworkManager.Singleton != null) {
                if(NetworkManager.Singleton.ConnectedClients.TryGetValue(requestingClientId, out var client)) {
                    var requestingPlayer = client.PlayerObject;
                    if(requestingPlayer != null) {
                        requestingController = requestingPlayer.GetComponent<PlayerController>();
                    }
                }
            }
            var serverHolderId = hopball.HolderController != null ? hopball.HolderController.OwnerClientId : ulong.MaxValue;
            if(serverHolderId != requestingClientId) {
                var serverPos = hopball.transform.position;
                var deltaFromRequest = Vector3.Distance(serverPos, dropPosition);
                FlowLog.Emit(FlowEventIds.AnomalyHopballDivergence,
                    ("serverHolder", serverHolderId == ulong.MaxValue ? "None" : serverHolderId.ToString()),
                    ("serverPos", serverPos),
                    ("clientPos", dropPosition),
                    ("delta", deltaFromRequest));
            }

            // Get hopball collider radius for accurate ground checking
            var hopballCollider = hopball.GetComponent<Collider>();
            var hopballRadius = 0.5f;
            if(hopballCollider != null) {
                var sphereCollider = hopballCollider as SphereCollider;
                if(sphereCollider != null) {
                    var lossyScale = hopball.transform.lossyScale;
                    hopballRadius = sphereCollider.radius * Mathf.Max(lossyScale.x, lossyScale.y, lossyScale.z);
                } else {
                    var capsuleCollider = hopballCollider as CapsuleCollider;
                    if(capsuleCollider != null) {
                        var lossyScale = hopball.transform.lossyScale;
                        hopballRadius = capsuleCollider.radius * Mathf.Max(lossyScale.x, lossyScale.z);
                    }
                }
            }

            // Preserve original drop position for visual fidelity (player's hand position)
            // Clamp if the hand point is pushed through world geometry (e.g. against a wall).
            // We spherecast from the player toward the intended drop point and clamp to the first world hit.
            var finalDropPosition = dropPosition;
            var worldLayer = LayerMask.GetMask("Default", "World");
            if(requestingController != null) {
                worldLayer = requestingController.WorldLayer.value;
                var origin = requestingController.Position + Vector3.up * 1.2f;
                var toDrop = finalDropPosition - origin;
                var distToDrop = toDrop.magnitude;
                if(distToDrop > 0.001f) {
                    var dirToDrop = toDrop / distToDrop;
                    const float wallMargin = 0.03f;
                    var castRadius = hopballRadius + wallMargin;
                    if(Physics.SphereCast(origin, castRadius, dirToDrop, out var wallHit, distToDrop, worldLayer)) {
                        // Place just before the wall along the player->hand line.
                        finalDropPosition = wallHit.point - dirToDrop * (castRadius + 0.01f);
                    }
                }
            }

            // Only adjust if it would fall through the floor
            const float raycastDistance = 15f;
            const float safetyMargin = 0.2f; // Safety margin above ground
            
            // Use sphere cast to check if hopball would intersect with ground at drop position
            var sphereCastRadius = hopballRadius + safetyMargin;
            var sphereCastStart = finalDropPosition + Vector3.up * sphereCastRadius;
            var sphereCastDistance = sphereCastRadius * 2f + 5f; // Check well below the drop position

            var sphereHit = Physics.SphereCast(sphereCastStart, sphereCastRadius, Vector3.down, out var hit, sphereCastDistance, worldLayer);

            if(sphereHit) {
                var groundHeight = hit.point.y + sphereCastRadius;
                // Only adjust if drop position would intersect with ground
                if(finalDropPosition.y < groundHeight) {
                    finalDropPosition = new Vector3(finalDropPosition.x, groundHeight, finalDropPosition.z);
                }
            } else {
                // Fallback: if sphere cast fails, try regular raycast
                if(Physics.Raycast(finalDropPosition + Vector3.up * 0.1f, Vector3.down, out var rayHit, raycastDistance, worldLayer)) {
                    var groundHeight = rayHit.point.y + sphereCastRadius;
                    if(finalDropPosition.y < groundHeight) {
                        finalDropPosition = new Vector3(finalDropPosition.x, groundHeight, finalDropPosition.z);
                    }
                } else {
                    // If no ground found below, check if we're already below ground level
                    if(Physics.Raycast(finalDropPosition + Vector3.down * 5f, Vector3.up, out var hitUp, 15f, worldLayer)) {
                        var groundHeight = hitUp.point.y + sphereCastRadius;
                        if(finalDropPosition.y < groundHeight) {
                            finalDropPosition = new Vector3(finalDropPosition.x, groundHeight, finalDropPosition.z);
                        }
                    }
                }
            }

            var currentScale = hopball.transform.localScale;
            var networkTransform = hopball.GetComponent<Unity.Netcode.Components.NetworkTransform>();
            var rb = hopball.Rigidbody;

            // Ensure physics body is synced to the teleport target.
            // Evidence: logs showed transform teleported while Rigidbody stayed at old position, then snapped back.
            rb.isKinematic = true;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.position = finalDropPosition;
            rb.rotation = dropRotation;

            if(networkTransform != null) {
                networkTransform.Teleport(finalDropPosition, dropRotation, currentScale);
            } else {
                var hopballTransform = hopball.transform;
                hopballTransform.position = finalDropPosition;
                hopballTransform.rotation = dropRotation;
                hopballTransform.localScale = currentScale;
            }
            
            hopball.transform.SetParent(null);

            // Re-assert RB pose after teleport and sync transforms so physics can't snap us back.
            rb.position = finalDropPosition;
            rb.rotation = dropRotation;
            Physics.SyncTransforms();

            await UniTask.WaitForFixedUpdate();

            hopball.SetDropped();
            FlowLog.Emit(FlowEventIds.HopballDropCommitted,
                ("player", requestingClientId),
                ("hopballNetId", hopball.NetworkObjectId),
                ("dropReason", string.IsNullOrEmpty(dropReason) ? "Unknown" : dropReason),
                ("position", finalDropPosition));

            hopball.Rigidbody.isKinematic = false;
            
            // Apply fraction of player velocity to ball (0.3 = 30% of player velocity)
            const float velocityTransferFactor = 0.3f;
            var ballVelocity = playerVelocity * velocityTransferFactor;
            // Ensure minimum downward velocity for natural drop
            if(ballVelocity.y > -1f) {
                ballVelocity.y = -2f;
            }
            hopball.Rigidbody.linearVelocity = ballVelocity;

            if(HopballSpawnManager.Instance != null) {
                HopballSpawnManager.Instance.OnHopballDropped();
            }

            if(requestingController == null) return;
            var controller = requestingController.PlayerHopballController;
            if(controller == null) return;
            controller.DisablePlayerTargetClientRpc();
        }

        /// <summary>
        /// Drops the hopball when the player dies.
        /// </summary>
        public void DropHopballOnDeath() {
            if(HopballSpawnManager.Instance == null || HopballSpawnManager.Instance.CurrentHopballController == null) return;

            var hopball = HopballSpawnManager.Instance.CurrentHopballController;

            if(!hopball.IsEquipped || hopball.HolderController == null ||
               hopball.HolderController.OwnerClientId != OwnerClientId) return;

            _currentHopballController = null;

            var dropPosition = playerController.Position + Vector3.up * 1.5f;
            var dropRotation = playerController.Rotation;
            
            // On death, use zero velocity (player is dead, no momentum transfer)
            var deathVelocity = Vector3.zero;

            if(HopballSpawnManager.Instance != null) {
                HopballSpawnManager.Instance.RequestDropHopballAuthority(hopball.NetworkObject, dropPosition,
                    dropRotation, deathVelocity, HopballDropReason.PlayerDeath.ToString());
            }
            CleanupVisualsAndRestoreWeaponsClientRpc();
        }

        /// <summary>
        /// Client RPC to clean up visuals and restore weapons after death drop.
        /// </summary>
        [ClientRpc]
        private void CleanupVisualsAndRestoreWeaponsClientRpc() {
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
        /// Client RPC to clean up visuals and restore weapons after dissolve.
        /// </summary>
        [ClientRpc]
        public void CleanupVisualsAndRestoreWeaponsAfterDissolveClientRpc() {
            var postMatchTransitionActive = IsPostMatchTransitionActive();

            // Clear hopball reference
            _currentHopballController = null;

            // Unsubscribe from visual state changes
            HopballController.VisualStateChanged -= OnHopballVisualStateChanged;

            if(IsOwner) {
                // Progression: Record Hopball Dissolve Challenge
                if (Progression.ProgressionManager.Instance != null) {
                    Progression.ProgressionManager.Instance.RecordHopballDissolve();
                }

                // Owner: Destroy visuals and restore weapons
                DestroyFpVisual();
                DestroyWorldVisual();
                if(postMatchTransitionActive) {
                    DestroyArmImmediate();
                }
                ShowWeapons();
                
                // Transition animation layers back to weapon hold (revert from hopball hold)
                TransitionToWeaponLayers();
            } else {
                // Non-owner: Just destroy world visual (FP visual doesn't exist for non-owners)
                DestroyWorldVisual();
            }

            if(postMatchTransitionActive) {
                if(_weaponManager != null) {
                    _weaponManager.CancelPendingPullOutForPostMatch();
                }

                return;
            }

            // Keep normal dissolve behavior outside post-match transition.
            TriggerPullOutAnimationClientRpc();
        }

        /// <summary>
        /// Client RPC to trigger pullout animation when hopball dissolves.
        /// Ensures all clients see smooth weapon restoration in normal gameplay.
        /// </summary>
        [ClientRpc]
        private void TriggerPullOutAnimationClientRpc() {
            if(_weaponManager == null) return;
            if(IsPostMatchTransitionActive()) {
                _weaponManager.CancelPendingPullOutForPostMatch();
                return;
            }

            _weaponManager.TriggerPullOutAnimation();
        }

        /// <summary>
        /// Disables the OSI Target on all clients (holder no longer holding ball).
        /// </summary>
        [ClientRpc]
        public void DisablePlayerTargetClientRpc() {
            if(_playerTarget != null) {
                _playerTarget.enabled = false;
            }
        }

        /// <summary>
        /// Client RPC called when hopball is released (e.g., on respawn).
        /// Handles delayed arm hiding after PutAway animation completes.
        /// </summary>
        [ClientRpc]
        public void OnHopballReleasedClientRpc() {
            if(IsOwner) {
                HideFpHopballVisualImmediate();
                if(IsPostMatchTransitionActive()) {
                    DestroyArmImmediate();
                    if(_weaponManager != null) {
                        _weaponManager.CancelPendingPullOutForPostMatch();
                    }
                } else {
                    HandleArmPutAwayAnimation();
                }
            } else {
                if(_weaponManager != null) {
                    _weaponManager.RefreshHolsterVisibility();
                    var currentWeapon = _weaponManager.CurrentWorldWeaponInstance;
                    if(currentWeapon != null) {
                        currentWeapon.SetActive(true);
                    }
                }

                if(playerController == null || playerController.PlayerShadow == null) return;
                playerController.PlayerShadow.ApplyHopballShadowState(false, false);
                playerController.PlayerShadow.ApplyVisibleShadowState();
            }
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
                Debug.LogError("[HopballController] HandleArmPutAwayAnimation: Animator not found on arm instance");
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
        public void CancelPostMatchHopballVisualTransitions() {
            _putAwayAnimationTriggered = true;
            HopballController.VisualStateChanged -= OnHopballVisualStateChanged;
            HideFpHopballVisualImmediate();
            DestroyWorldVisual();
            DestroyArmImmediate();
            if(_weaponManager != null) {
                _weaponManager.CancelPendingPullOutForPostMatch();
            }
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
        public void HideFpVisualsForDisconnectTransition() {
            if(!IsOwner) return;
            HideFpHopballVisualImmediate();
            DestroyArmImmediate();
        }

        /// <summary>
        /// Hides the FP weapon model (called locally by owner).
        /// </summary>
        private void HideFpWeapons() {
            if(_weaponManager == null) return;

            var currentWeapon = _weaponManager.CurrentWeapon;
            if(currentWeapon == null) return;

            var fpWeapon = currentWeapon.GetWeaponPrefab();
            if(fpWeapon == null || !fpWeapon.activeSelf) return;

            fpWeapon.SetActive(false);
        }

        /// <summary>
        /// Hides the world weapon model (called via RPC for owner).
        /// </summary>
        private void HideWorldWeapon() {
            if(_weaponManager == null) return;
            var worldWeapon = _weaponManager.CurrentWorldWeaponInstance;
            if(worldWeapon == null || !worldWeapon.activeSelf) return;
            worldWeapon.SetActive(false);
        }

        /// <summary>
        /// Shows both holstered weapon models (used when holding hopball - neither weapon is "equipped").
        /// </summary>
        private void ShowBothHolsters() {
            if(_weaponManager == null) return;
            
            var primaryHolster = _weaponManager.PrimaryHolster;
            var secondaryHolster = _weaponManager.SecondaryHolster;
            
            if(primaryHolster != null && !primaryHolster.activeSelf) {
                primaryHolster.SetActive(true);
            }
            
            if(secondaryHolster != null && !secondaryHolster.activeSelf) {
                secondaryHolster.SetActive(true);
            }
        }

        /// <summary>
        /// Shows the current FP and world weapon models.
        /// </summary>
        private void ShowWeapons() {
            if(_weaponManager == null) return;

            // Show FP weapon for current selection
            var currentFp = _weaponManager.GetCurrentFpWeapon();
            if(currentFp != null && !currentFp.activeSelf) {
                currentFp.SetActive(true);
            }

            // Show world weapon for current selection
            var worldWeapon = _weaponManager.CurrentWorldWeaponInstance;
            if(worldWeapon != null && !worldWeapon.activeSelf) {
                worldWeapon.SetActive(true);
            }

            // Clear stored references (no longer needed)
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
                Debug.LogError("[HopballController] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_weaponManager == null) _weaponManager = playerController.WeaponManager;
            if(_healthController == null) _healthController = playerController.HealthController;
            if(_fpCamera == null) _fpCamera = playerController.FpCamera;
            if(_worldWeaponSocket == null) _worldWeaponSocket = playerController.WorldWeaponSocket;
            _hopballLayer = playerController.HopballLayer;
            if(_playerTarget == null) _playerTarget = playerController.PlayerTarget;
            if(_characterController == null) _characterController = playerController.CharacterController;
            
            // Validate PlayerRenderer (required for material and renderer operations)
            if(_playerRenderer == null) _playerRenderer = playerController.PlayerRenderer;
            if(_playerRenderer == null) {
                Debug.LogError("[HopballController] PlayerRenderer not found! Cannot perform renderer operations.");
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
                Debug.LogWarning("[HopballController] Weapon Hold Layer not found!");
            }
            if(_rightHandHoldLayerIndex < 0) {
                Debug.LogWarning("[HopballController] Right Hand Hold Layer not found!");
            }
        }

        /// <summary>
        /// Transitions animation layers from weapon hold (both arms) to hopball hold (right arm only).
        /// Left arm will transition to base layer walking motion, right arm will transition to hopball hold.
        /// </summary>
        private void TransitionToHopballLayers() {
            if(_playerAnimator == null || _weaponHoldLayerIndex < 0 || _rightHandHoldLayerIndex < 0) {
                Debug.LogWarning("[HopballController] Cannot transition layers: animator or layer indices not found");
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
                Debug.LogWarning("[HopballController] Cannot transition layers: animator or layer indices not found");
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
        public void CleanupHopballVisuals() {
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

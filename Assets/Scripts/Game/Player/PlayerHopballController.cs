using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Hopball;
using Game.Weapons;
using Network.Diagnostics;
using Network.Events;
using OSI;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
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
        private Coroutine _restoreWeaponsCoroutine; // Track restore coroutine
        public Collider PlayerCollider { get; private set; }

        private readonly Collider[] _pickupHits = new Collider[10];

        private void Awake() {
            InitializeComponentReferences();
            InitializePlayerCollider();
        }

        private void InitializePlayerCollider() {
            if(PlayerCollider != null) return;

            PlayerCollider = _characterController;
        }

        private void OnEnable() {
            if(!InstancesInternal.Contains(this)) {
                InstancesInternal.Add(this);
            }

            if(HopballController.Instance != null) {
                HopballController.Instance.OnControllerRegistered(this);
            }
        }
        
        private void Update() {
            // Progression: Track time holding hopball
            if (IsOwner && IsHoldingHopball && Progression.ProgressionManager.Instance != null) {
                Progression.ProgressionManager.Instance.AddTimeHoldingHopball(Time.deltaTime);
            }
        }

        private void OnDisable() {
            if(HopballController.Instance != null) {
                HopballController.Instance.OnControllerUnregistered(this);
            }
            InstancesInternal.Remove(this);
            // Unsubscribe from visual state changes
            HopballController.VisualStateChanged -= OnHopballVisualStateChanged;
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
            if(playerController == null) {
                return;
            }
            
            if(_hopballLayer == 0) {
                _hopballLayer = playerController.HopballLayer;
                if(_hopballLayer == 0) {
                    return;
                }
            }
            
            var hitCount = Physics.OverlapSphereNonAlloc(playerController.Position, PickupRange, _pickupHits, _hopballLayer);
            if(hitCount == 0) {
                return;
            }
            
            for(var i = 0; i < hitCount; i++) {
                var hopball = _pickupHits[i].GetComponent<HopballController>();
                if(hopball == null || hopball.IsEquipped || hopball.transform.parent != null ||
                   !hopball.gameObject.activeSelf) continue;
                var netObj = hopball.GetComponent<NetworkObject>();
                FlowLog.Emit(FlowEventIds.HopballPickupRequested,
                    ("player", OwnerClientId),
                    ("hopballNetId", netObj != null ? netObj.NetworkObjectId : 0UL),
                    ("requestSource", "Proximity"));
                EquipHopball(hopball);
                break;
            }
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

            RequestEquipHopballServerRpc(hopballController.GetComponent<NetworkObject>());
        }

        /// <summary>
        /// Server RPC to request equipping the hopball (since hopball is server-authoritative).
        /// </summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestEquipHopballServerRpc(NetworkObjectReference hopballRef) {
            if(!hopballRef.TryGet(out var networkObject) || networkObject == null) return;

            var hopball = networkObject.GetComponent<HopballController>();
            if(hopball == null) return;
            if(hopball.IsEquipped) {
                FlowLog.Emit(FlowEventIds.AnomalyHopballMismatch,
                    ("serverHolder", hopball.HolderController != null ? hopball.HolderController.OwnerClientId.ToString() : "None"),
                    ("localHolder", OwnerClientId),
                    ("osiHolder", "Unknown"),
                    ("reason", "PickupRejectedAlreadyEquipped"));
                return;
            }

            // Find the requesting player's controller
            var requestingPlayer = NetworkManager.Singleton.ConnectedClients[OwnerClientId].PlayerObject;
            if(requestingPlayer == null) return;

            var requestingController = requestingPlayer.GetComponent<PlayerController>();
            if(requestingController == null) return;
            var controller = requestingController.PlayerHopballController;
            if(controller == null) return;
            FlowLog.Emit(FlowEventIds.HopballPickupCommitted,
                ("player", OwnerClientId),
                ("hopballNetId", networkObject.NetworkObjectId),
                ("serverHolder", OwnerClientId));

            // Server performs the equip (this will broadcast hopball state update to all clients)
            hopball.SetEquipped(true, controller);

            // Notify HopballSpawnManager that player picked up ball (for scoring)
            if(HopballSpawnManager.Instance != null) {
                HopballSpawnManager.Instance.OnPlayerPickedUpHopball(OwnerClientId);
            }

            // Consolidated RPC: handles all client updates in one call
            controller.OnHopballEquippedClientRpc(hopballRef, OwnerClientId);
            
            // Publish hopball picked up event
            EventBus.Publish(new HopballPickedUpEvent(OwnerClientId));
        }

        /// <summary>
        /// Consolidated ClientRpc that handles all client updates when hopball is equipped.
        /// Replaces multiple separate RPCs to reduce network overhead.
        /// </summary>
        [ClientRpc]
        private void OnHopballEquippedClientRpc(NetworkObjectReference hopballRef, ulong holderClientId) {
            if(!hopballRef.TryGet(out var networkObject) || networkObject == null) return;

            var hopball = networkObject.GetComponent<HopballController>();
            if(hopball == null) return;

            var isHolder = OwnerClientId == holderClientId && IsOwner;
            var localClientId = ulong.MaxValue;
            if(NetworkManager.Singleton != null && NetworkManager.Singleton.LocalClient != null) {
                localClientId = NetworkManager.Singleton.LocalClient.ClientId;
            }

            if(isHolder) {
                HideWorldWeapon();
                SetupWorldHopballVisual(true);
                ShowBothHolsters();
                if(playerController != null && playerController.PlayerShadow != null) {
                    playerController.PlayerShadow.ApplyHopballShadowState(true, true);
                }
            } else {
                SetupWorldHopballVisual();
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

            // Instantiate hopball visual
            _fpHopballVisualInstance = Instantiate(hopballVisualPrefab, swayHolder, false);
            _fpHopballVisualInstance.transform.localPosition = fpEquippedLocalPosition;
            _fpHopballVisualInstance.transform.localRotation = Quaternion.identity;

            // Set layer and shadows
            var layer = IsOwner ? LayerMask.NameToLayer("Weapon") : LayerMask.NameToLayer("Masked");
            SetGameObjectAndChildrenLayer(_fpHopballVisualInstance, layer);
            SetFpVisualShadows(_fpHopballVisualInstance, false);

            // Instantiate hopball arm separately - parent to BobHolder (use first one found)
            if(hopballArmPrefab != null) {
                var bobHolder = FindBobHolder();
                if(bobHolder != null) {
                    _fpHopballArmInstance = Instantiate(hopballArmPrefab, bobHolder, false);
                    SetGameObjectAndChildrenLayer(_fpHopballArmInstance, layer);
                    ApplyPlayerMaterialToArm();
                } else {
                    Debug.LogError("[HopballController] BobHolder not found! Cannot instantiate hopball arm.");
                }
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
            if(swayCamera == null) {
                return null;
            }

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

            foreach(Transform swayHolder in swayCamera.transform) {
                if(swayHolder.name != "SwayHolder") continue;
                
                foreach(Transform bobHolder in swayHolder) {
                    if(bobHolder.name == "BobHolder" && bobHolder.gameObject.activeInHierarchy) {
                        return bobHolder;
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

            var visualController = playerController.VisualController;
            if(visualController == null) return;

            // Apply material to all renderers in the arm
            var playerMesh = playerController.PlayerMesh;
            if(playerMesh == null || playerMesh.materials.Length <= 1) return;
            var material = new Material(playerMesh.materials[1]); // Index 1 is the player material (0 is outline)
            PlayerRenderer.ApplyMaterialToRenderers(_fpHopballArmInstance, material, 1);
        }

        /// <summary>
        /// Finds the SwayHolder transform in the camera hierarchy.
        /// </summary>
        private Transform FindSwayHolder() {
            var swayCamera = _fpCamera;
            if(swayCamera == null && playerController != null) {
                swayCamera = playerController.FpCamera;
            }
            if(swayCamera == null) return null;

            foreach(Transform child in swayCamera.transform) {
                if(child.name == "SwayHolder") {
                    return child;
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
        private void SetupWorldHopballVisual(bool isLocalClientHolder = false) {
            if(_worldWeaponSocket == null || hopballVisualPrefab == null) {
                Debug.LogError("[HopballController] SetupWorldHopballVisual: Missing required references");
                return;
            }

            // Create visual-only world model (not a NetworkObject, so regular parenting works)
            _worldHopballVisualInstance = Instantiate(hopballVisualPrefab, _worldWeaponSocket, false);
            _worldHopballVisualInstance.SetActive(true);
            _worldHopballVisualInstance.transform.localPosition = worldEquippedLocalPosition;
            _worldHopballVisualInstance.transform.localRotation = Quaternion.identity;

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
                RequestDropHopballServerRpc(hopballNetObj, dropPosition, dropRotation, playerVelocity,
                    reason.ToString());
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
                
                TransitionToWeaponLayers();
            } else {
                TransitionToWeaponLayers();
            }
        }

        /// <summary>
        /// Server-side method to drop the hopball at a specific position.
        /// Can be called directly from server or via ServerRpc from client.
        /// </summary>
        private static async UniTaskVoid DropHopballAtPosition(HopballController hopball, Vector3 dropPosition,
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
            var hopballRadius = 0.5f; // Default fallback
            if(hopballCollider is SphereCollider sphereCollider) {
                hopballRadius = sphereCollider.radius * Mathf.Max(hopball.transform.lossyScale.x, hopball.transform.lossyScale.y, hopball.transform.lossyScale.z);
            } else if(hopballCollider is CapsuleCollider capsuleCollider) {
                hopballRadius = capsuleCollider.radius * Mathf.Max(hopball.transform.lossyScale.x, hopball.transform.lossyScale.z);
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
                    var wallMargin = 0.03f;
                    var castRadius = hopballRadius + wallMargin;
                    if(Physics.SphereCast(origin, castRadius, dirToDrop, out var wallHit, distToDrop, worldLayer)) {
                        // Place just before the wall along the player->hand line.
                        finalDropPosition = wallHit.point - dirToDrop * (castRadius + 0.01f);
                    }
                }
            }

            // Only adjust if it would fall through the floor
            var raycastDistance = 15f;
            var safetyMargin = 0.2f; // Safety margin above ground
            
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
            var velocityTransferFactor = 0.3f;
            var ballVelocity = playerVelocity * velocityTransferFactor;
            // Ensure minimum downward velocity for natural drop
            if(ballVelocity.y > -1f) {
                ballVelocity.y = -2f;
            }
            hopball.Rigidbody.linearVelocity = ballVelocity;

            if(HopballSpawnManager.Instance != null) {
                HopballSpawnManager.Instance.OnHopballDropped();
            }

            EventBus.Publish(new HopballDroppedEvent(requestingClientId));

            if(requestingController == null) return;
            var controller = requestingController.PlayerHopballController;
            if(controller == null) return;
            controller.DisablePlayerTargetClientRpc();
        }

        /// <summary>
        /// Server RPC to request dropping the hopball at a specific position.
        /// Called from client when they drop the ball.
        /// </summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestDropHopballServerRpc(NetworkObjectReference hopballRef, Vector3 dropPosition,
            Quaternion dropRotation, Vector3 playerVelocity, string dropReason) {
            if(!hopballRef.TryGet(out var networkObject) || networkObject == null) return;

            var hopball = networkObject.GetComponent<HopballController>();
            _ = DropHopballAtPosition(hopball, dropPosition, dropRotation, OwnerClientId, playerVelocity, dropReason);
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

            _ = DropHopballAtPosition(hopball, dropPosition, dropRotation, OwnerClientId, deathVelocity,
                HopballDropReason.PlayerDeath.ToString());
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
            // Do not restore weapon visuals here—death flow handles showing weapons when appropriate
        }

        /// <summary>
        /// Client RPC to clean up visuals and restore weapons after dissolve.
        /// </summary>
        [ClientRpc]
        public void CleanupVisualsAndRestoreWeaponsAfterDissolveClientRpc() {
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
                ShowWeapons();
                
                // Transition animation layers back to weapon hold (revert from hopball hold)
                TransitionToWeaponLayers();
            } else {
                // Non-owner: Just destroy world visual (FP visual doesn't exist for non-owners)
                DestroyWorldVisual();
            }
            
            // Trigger pullout animation for all clients (so others see smooth weapon restoration)
            TriggerPullOutAnimationClientRpc();
        }

        /// <summary>
        /// Client RPC to trigger pullout animation when hopball dissolves.
        /// Ensures all clients see the weapon being pulled out smoothly.
        /// </summary>
        [ClientRpc]
        private void TriggerPullOutAnimationClientRpc() {
            if(_weaponManager == null) return;
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
                HandleArmPutAwayAnimation();
            } else {
                if(_weaponManager != null) {
                    _weaponManager.RefreshHolsterVisibility();
                    var currentWeapon = _weaponManager.CurrentWorldWeaponInstance;
                    if(currentWeapon != null) {
                        currentWeapon.SetActive(true);
                    }
                }
                if(playerController != null && playerController.PlayerShadow != null) {
                    playerController.PlayerShadow.ApplyHopballShadowState(false, false);
                    playerController.PlayerShadow.ApplyVisibleShadowState();
                }
            }
        }

        /// <summary>
        /// Hides the FP hopball visual immediately.
        /// </summary>
        private void HideFpHopballVisualImmediate() {
            if(_fpHopballVisualInstance == null) return;

            PlayerRenderer.SetHopballVisualRenderersEnabled(false, _fpHopballVisualInstance);

            var hopballVisual = _fpHopballVisualInstance.GetComponent<HopballVisual>();
            if(hopballVisual != null) {
                var visualTransform = hopballVisual.transform;
                foreach(Transform child in visualTransform) {
                    child.gameObject.SetActive(false);
                }
            }

            _fpHopballVisualInstance.SetActive(false);
            _fpHopballVisualInstance.transform.SetParent(null);
            Destroy(_fpHopballVisualInstance);
            _fpHopballVisualInstance = null;
        }

        /// <summary>
        /// Triggers PutAway animation on the arm. Arm will be destroyed automatically by PutAwayComplete animation event.
        /// If arm doesn't exist or animation can't be triggered, does nothing.
        /// </summary>
        private void HandleArmPutAwayAnimation() {
            if(_fpHopballArmInstance == null) return;

            var animator = _fpHopballArmInstance.GetComponent<Animator>();
            if(animator == null) {
                Debug.LogError("[HopballController] HandleArmPutAwayAnimation: Animator not found on arm instance");
                return;
            }

            // Trigger PutAway animation - PutAwayComplete animation event will destroy the arm automatically
            animator.SetTrigger(PutAwayHash);
        }

        /// <summary>
        /// Called when hopball visual state changes. Triggers PutAway animation during dissolve.
        /// </summary>
        private void OnHopballVisualStateChanged(HopballController.HopballVisualState state) {
            // Only handle on owner's client
            if(!IsOwner) return;

            // Trigger PutAway animation when dissolve reaches threshold
            if(_putAwayAnimationTriggered || !(state.DissolveAmount >= putAwayDissolveThreshold)) return;
            _putAwayAnimationTriggered = true;
            HandleArmPutAwayAnimation();
        }

        /// <summary>
        /// Destroys the arm instance immediately. Used for death/dissolve cleanup.
        /// Note: Normal weapon switch uses animation event which destroys the arm automatically.
        /// </summary>
        private void DestroyArmImmediate() {
            if(_fpHopballArmInstance == null) return;

            _fpHopballArmInstance.SetActive(false);
            _fpHopballArmInstance.transform.SetParent(null);
            Destroy(_fpHopballArmInstance);
            _fpHopballArmInstance = null;
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
            _fpHopballVisualInstance.transform.SetParent(null);
            Destroy(_fpHopballVisualInstance);
            _fpHopballVisualInstance = null;
        }

        /// <summary>
        /// Destroys the world visual instance if it exists.
        /// </summary>
        private void DestroyWorldVisual() {
            if(_worldHopballVisualInstance == null) return;
            Destroy(_worldHopballVisualInstance);
            _worldHopballVisualInstance = null;
        }

        /// <summary>
        /// Cleans up all hopball visuals. Called when ball respawns to ensure no visuals remain.
        /// Note: Does NOT destroy the arm - arm is destroyed separately via animation event or explicit cleanup.
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

using System.Collections.Generic;
using Game.Weapons;
using Network.Singletons;
using OSI;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Handles hopball pickup, equipping, and dropping for the player.
    /// Manages weapon visibility and prevents shooting/reloading while holding the ball.
    /// </summary>
    public class HopballController : NetworkBehaviour {
        public enum HopballDropReason {
            Manual,
            WeaponSwitch,
            PlayerDeath
        }

        private static readonly List<HopballController> InstancesInternal = new();
        public static IReadOnlyList<HopballController> Instances => InstancesInternal;

        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private WeaponManager _weaponManager;
        private PlayerHealthController _healthController; // For worldWeaponSocket reference
        private CinemachineCamera _fpCamera; // First-person camera (for FP weapon socket)
        private Transform _worldWeaponSocket;
        private Target _playerTarget; // OSI Target component on this player
        private CharacterController _characterController;

        private LayerMask _hopballLayer;
        private const float PickupRange = 2.5f;

        [Header("Hopball Settings")]
        [SerializeField] private GameObject hopballVisualPrefab; // Visual-only FP hopball prefab (no state tracking)

        [SerializeField] private Vector3 fpEquippedLocalPosition = Vector3.zero;
        [SerializeField] private Vector3 worldEquippedLocalPosition = Vector3.zero;

        // State
        public bool IsHoldingHopball => _currentHopball != null;
        public static bool IsRestoringAfterDissolve => false; // Flag to allow weapon switch after dissolve
        private Hopball _currentHopball;

        public PlayerController PlayerController => playerController;

        /// <summary>
        /// Clears the hopball reference. Called by Hopball when it dissolves/respawns.
        /// </summary>
        public void ClearHopballReference() {
            _currentHopball = null;
        }

        // Hopball model references
        private GameObject _fpHopballVisualInstance; // Visual-only FP model (no state tracking)
        private GameObject _worldHopballVisualInstance; // Visual-only world model (parented to world weapon socket)
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

            Hopball.Instance?.OnControllerRegistered(this);
        }

        private void OnDisable() {
            Hopball.Instance?.OnControllerUnregistered(this);
            InstancesInternal.Remove(this);
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            if(IsHoldingHopball) {
                DropHopball(HopballDropReason.PlayerDeath);
            }
        }

        /// <summary>
        /// Tries to pick up a hopball within pickup range.
        /// Returns true if a hopball was picked up.
        /// </summary>
        public void TryPickupHopball() {
            var hitCount = Physics.OverlapSphereNonAlloc(playerController.Position, PickupRange, _pickupHits, _hopballLayer);
            for(var i = 0; i < hitCount; i++) {
                var hopball = _pickupHits[i].GetComponent<Hopball>() ?? _pickupHits[i].GetComponentInParent<Hopball>();
                if(hopball == null || hopball.IsEquipped || hopball.transform.parent != null ||
                   !hopball.gameObject.activeSelf) continue;
                EquipHopball(hopball);
                break;
            }
        }

        /// <summary>
        /// Equips the hopball, hides FP weapons, and prevents shooting/reloading.
        /// </summary>
        private void EquipHopball(Hopball hopball) {
            if(hopball == null || !IsOwner) return;

            _currentHopball = hopball;
            playerController?.PlayerInput?.ForceDisableSniperOverlay(false);

            // Setup FP hopball visual immediately (optimistic, owner sees it right away)
            SetupFpHopball();

            // Hide FP weapons locally (owner only)
            HideFpWeapons();

            // Request server to equip the hopball (server will handle world visuals via RPC)
            RequestEquipHopballServerRpc(hopball.GetComponent<NetworkObject>());
        }

        /// <summary>
        /// Server RPC to request equipping the hopball (since hopball is server-authoritative).
        /// </summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestEquipHopballServerRpc(NetworkObjectReference hopballRef) {
            if(!hopballRef.TryGet(out var networkObject) || networkObject == null) return;

            var hopball = networkObject.GetComponent<Hopball>();
            if(hopball == null || hopball.IsEquipped) return;

            // Find the requesting player's controller
            var requestingPlayer = NetworkManager.Singleton.ConnectedClients[OwnerClientId].PlayerObject;
            if(requestingPlayer == null) return;

            var requestingController = requestingPlayer.GetComponent<PlayerController>();
            var controller = requestingController?.HopballController;
            if(controller == null) return;

            // Server performs the equip (this will broadcast hopball state update to all clients)
            hopball.SetEquipped(true, controller);

            // Notify HopballSpawnManager that player picked up ball (for scoring)
            HopballSpawnManager.Instance?.OnPlayerPickedUpHopball(OwnerClientId);

            // Consolidated RPC: handles all client updates in one call
            controller.OnHopballEquippedClientRpc(hopballRef, OwnerClientId);
        }

        /// <summary>
        /// Consolidated ClientRpc that handles all client updates when hopball is equipped.
        /// Replaces multiple separate RPCs to reduce network overhead.
        /// </summary>
        [ClientRpc]
        private void OnHopballEquippedClientRpc(NetworkObjectReference hopballRef, ulong holderClientId) {
            if(!hopballRef.TryGet(out var networkObject) || networkObject == null) return;

            var hopball = networkObject.GetComponent<Hopball>();
            if(hopball == null) return;

            var isHolder = OwnerClientId == holderClientId && IsOwner;
            var localClientId = NetworkManager.Singleton?.LocalClient?.ClientId ?? 0;

            // Owner: Hide world weapon and setup world hopball visual (for others to see)
            if(isHolder) {
                HideWorldWeapon();
                SetupWorldHopballVisual();
            }
            // Non-holders: Setup world hopball visual
            // Enable target on holder's controller only if local client is viewing it (not the holder themselves)
            else {
                SetupWorldHopballVisual();
                // This controller belongs to the holder, enable target so local client can see indicator
                // Only enable if local client is NOT the holder (holder doesn't see their own indicator)
                if(OwnerClientId == holderClientId && localClientId != holderClientId) {
                    EnablePlayerTarget();
                }
            }
        }

        /// <summary>
        /// Enables the OSI Target for non-owners and sets team-based color.
        /// </summary>
        private void EnablePlayerTarget() {
            if(_playerTarget == null || IsOwner) return;

            // Get local player's team
            var localPlayer = NetworkManager.Singleton?.LocalClient?.PlayerObject;
            if(localPlayer == null) return;

            var localPlayerController = localPlayer.GetComponent<PlayerController>();
            var localTeamMgr = localPlayerController?.TeamManager;
            var holderTeamMgr = playerController?.TeamManager;

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
            _fpHopballVisualInstance = Instantiate(hopballVisualPrefab, FindSwayHolder(), false);
            _fpHopballVisualInstance.transform.localPosition = fpEquippedLocalPosition;
            _fpHopballVisualInstance.transform.localRotation = Quaternion.identity;

            // Set layer and shadows
            var layer = IsOwner ? LayerMask.NameToLayer("Weapon") : LayerMask.NameToLayer("Masked");
            SetGameObjectAndChildrenLayer(_fpHopballVisualInstance, layer);
            SetFpVisualShadows(_fpHopballVisualInstance, false);
        }

        /// <summary>
        /// Finds the SwayHolder transform in the camera hierarchy.
        /// </summary>
        private Transform FindSwayHolder() {
            var swayCamera = _fpCamera ?? playerController?.FpCamera;
            if(swayCamera == null) return null;

            // Search for SwayHolder (structure: Camera -> BobHolder -> SwayHolder)
            foreach(Transform child in swayCamera.transform) {
                if(child.name != "BobHolder") continue;

                var swayHolder = child.Find("SwayHolder");
                if(swayHolder != null) return swayHolder;
            }

            // Fallback to camera if swayholder not found
            return swayCamera.transform;
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
        private void SetupWorldHopballVisual() {
            if(_worldWeaponSocket == null || hopballVisualPrefab == null) return;

            // Create visual-only world model (not a NetworkObject, so regular parenting works)
            _worldHopballVisualInstance = Instantiate(hopballVisualPrefab, _worldWeaponSocket, false);
            _worldHopballVisualInstance.transform.localPosition = worldEquippedLocalPosition;
            _worldHopballVisualInstance.transform.localRotation = Quaternion.identity;

            // Disable effects and light for owner (they see FP visual instead)
            var worldVisual = _worldHopballVisualInstance.GetComponent<HopballVisual>();
            if(worldVisual != null && IsOwner) {
                worldVisual.DisableEffectsForOwner();
            }
        }

        /// <summary>
        /// Drops the hopball, unparents it, enables physics, and restores weapons.
        /// </summary>
        public void DropHopball(HopballDropReason reason = HopballDropReason.Manual) {
            if(_currentHopball == null || !IsOwner) return;

            var hopball = _currentHopball;
            _currentHopball = null; // Clear reference early to prevent re-entry

            // Get drop position from world visual (where it appears on the player)
            Vector3 dropPosition;
            Quaternion dropRotation;
            if(_worldHopballVisualInstance != null) {
                dropPosition = _worldHopballVisualInstance.transform.position;
                dropRotation = _worldHopballVisualInstance.transform.rotation;
            } else {
                // Fallback to hopball's stored position if visual doesn't exist
                dropPosition = hopball.transform.position;
                dropRotation = hopball.transform.rotation;
            }

            // Destroy visual models FIRST (before notifying hopball, to prevent any race conditions)
            DestroyFpVisual();
            DestroyWorldVisual();

            // Request server to drop the hopball at the correct position (since hopball is server-authoritative)
            RequestDropHopballServerRpc(hopball.GetComponent<NetworkObject>(), dropPosition, dropRotation);

            // Restore weapons depending on reason
            if(reason == HopballDropReason.Manual) {
                ShowWeapons();
            } else {
                // Ensure stored references are cleared so future drops don't resurrect stale objects
            }
        }

        /// <summary>
        /// Server-side method to drop the hopball at a specific position.
        /// Can be called directly from server or via ServerRpc from client.
        /// </summary>
        private static void DropHopballAtPosition(Hopball hopball, Vector3 dropPosition, Quaternion dropRotation,
            ulong requestingClientId) {
            if(hopball == null || !hopball.IsEquipped) return;

            // Server sets the position authoritatively
            hopball.transform.position = dropPosition;
            hopball.transform.rotation = dropRotation;
            hopball.transform.SetParent(null); // Ensure it's unparented

            // Notify hopball that it's dropped (handles visual setup and shows components)
            hopball.SetDropped();

            // Enable physics on the main hopball object
            hopball.Rigidbody.isKinematic = false;
            hopball.Rigidbody.linearVelocity = Vector3.down * 2f;

            // Notify HopballSpawnManager that ball was dropped
            if(HopballSpawnManager.Instance != null) {
                HopballSpawnManager.Instance.OnHopballDropped();
            }

            // Notify all clients to disable the player Target (holder no longer holding ball)
            if(!NetworkManager.Singleton.ConnectedClients.TryGetValue(requestingClientId, out var client)) return;

            var requestingPlayer = client.PlayerObject;
            if(requestingPlayer == null) return;

            var requestingController = requestingPlayer.GetComponent<PlayerController>();
            var controller = requestingController?.HopballController;
            controller?.DisablePlayerTargetClientRpc();
        }

        /// <summary>
        /// Server RPC to request dropping the hopball at a specific position.
        /// Called from client when they drop the ball.
        /// </summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestDropHopballServerRpc(NetworkObjectReference hopballRef, Vector3 dropPosition,
            Quaternion dropRotation) {
            if(!hopballRef.TryGet(out var networkObject) || networkObject == null) return;

            var hopball = networkObject.GetComponent<Hopball>();
            DropHopballAtPosition(hopball, dropPosition, dropRotation, OwnerClientId);
        }

        /// <summary>
        /// Server-side method to drop the hopball when player dies.
        /// Called from PlayerHealthController on the server.
        /// </summary>
        public void DropHopballOnDeath() {
            // Get hopball from HopballSpawnManager since server doesn't have _currentHopball reference
            if(HopballSpawnManager.Instance == null || HopballSpawnManager.Instance.CurrentHopball == null) return;

            var hopball = HopballSpawnManager.Instance.CurrentHopball;

            // Verify this player is actually holding it
            if(!hopball.IsEquipped || hopball.HolderController == null ||
               hopball.HolderController.OwnerClientId != OwnerClientId) return;

            // Clear local reference if it exists (for owner client cleanup)
            _currentHopball = null;

            // Get drop position - use player position since server doesn't have world visual
            // Position slightly above player to prevent it from falling through ground
            var dropPosition = playerController.Position + Vector3.up * 1.5f;
            var dropRotation = playerController.Rotation;

            // Drop directly on server (we're already on server, so call the method directly)
            DropHopballAtPosition(hopball, dropPosition, dropRotation, OwnerClientId);

            // Notify owner client to clean up visuals and restore weapons
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
            // Do not restore weapon visuals here—death flow handles showing weapons when appropriate
        }

        /// <summary>
        /// Client RPC to clean up visuals and restore weapons after dissolve.
        /// </summary>
        [ClientRpc]
        public void CleanupVisualsAndRestoreWeaponsAfterDissolveClientRpc() {
            // Only cleanup on the owner's client
            if(!IsOwner) return;

            // Clear hopball reference
            _currentHopball = null;

            // Destroy visuals
            DestroyFpVisual();
            DestroyWorldVisual();

            // Restore weapons
            ShowWeapons();
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
        /// Hides the FP weapon model (called locally by owner).
        /// </summary>
        private void HideFpWeapons() {
            if(_weaponManager == null) return;

            var currentWeapon = _weaponManager.CurrentWeapon;

            var fpWeapon = currentWeapon?.GetWeaponPrefab();
            if(fpWeapon == null || !fpWeapon.activeSelf) return;

            fpWeapon.SetActive(false);
        }

        /// <summary>
        /// Hides the world weapon model (called via RPC for owner).
        /// </summary>
        private void HideWorldWeapon() {
            var worldWeapon = _weaponManager?.CurrentWorldWeaponInstance;
            if(worldWeapon == null || !worldWeapon.activeSelf) return;
            worldWeapon.SetActive(false);
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
        /// Initializes component references with fallbacks.
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
        }


        /// <summary>
        /// Destroys the FP visual instance if it exists.
        /// Also searches for any orphaned FP hopball visuals in the SwayHolder as a fallback.
        /// </summary>
        private void DestroyFpVisual() {
            // First, destroy via reference if we have one
            if(_fpHopballVisualInstance != null) {
                // Disable and unparent before destroying to ensure proper cleanup
                _fpHopballVisualInstance.SetActive(false);
                _fpHopballVisualInstance.transform.SetParent(null);
                Destroy(_fpHopballVisualInstance);
                _fpHopballVisualInstance = null;
            }

            // Always search for and destroy any remaining FP visuals in SwayHolder
            // This ensures cleanup even if the reference was lost or there are multiple instances
            var swayHolder = FindSwayHolder();
            if(swayHolder != null) {
                // Collect all children first (to avoid modifying collection during iteration)
                var childrenToDestroy = new List<GameObject>();

                foreach(Transform child in swayHolder) {
                    var fpVisual = child.GetComponent<HopballVisual>();
                    if(fpVisual != null) {
                        childrenToDestroy.Add(child.gameObject);
                    }
                }

                // Destroy all found FP visuals
                foreach(var visual in childrenToDestroy) {
                    if(visual == null) continue;
                    // Disable and unparent before destroying
                    visual.SetActive(false);
                    visual.transform.SetParent(null);
                    Destroy(visual);
                }
            }

            // Ensure reference is cleared
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
        /// </summary>
        public void CleanupHopballVisuals() {
            // Only cleanup if we're not currently holding the ball
            if(IsHoldingHopball) return;
            DestroyFpVisual();
            DestroyWorldVisual();
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
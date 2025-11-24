using System.Collections;
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
        private static readonly List<HopballController> InstancesInternal = new();
        public static IReadOnlyList<HopballController> Instances => InstancesInternal;

        [Header("References")]
        [SerializeField] private PlayerController playerController;

        [SerializeField] private WeaponManager weaponManager;
        [SerializeField] private PlayerHealthController healthController; // For worldWeaponSocket reference
        [SerializeField] private CinemachineCamera fpCamera; // First-person camera (for FP weapon socket)

        [SerializeField]
        private Transform worldWeaponSocket; // Third-person world weapon socket (can be found from healthController)

        [SerializeField] private LayerMask hopballLayer;
        [SerializeField] private float pickupRange = 1.5f;

        [Header("Hopball Settings")]
        [SerializeField] private GameObject hopballVisualPrefab; // Visual-only FP hopball prefab (no state tracking)

        [SerializeField] private Vector3 fpEquippedLocalPosition = Vector3.zero;
        [SerializeField] private Vector3 worldEquippedLocalPosition = Vector3.zero;

        // State
        public bool IsHoldingHopball => _currentHopball != null;
        public bool IsRestoringAfterDissolve { get; private set; } // Flag to allow weapon switch after dissolve
        private Hopball _currentHopball;
        [SerializeField] private Target playerTarget; // OSI Target component on this player
        [SerializeField] private CharacterController characterController;

        /// <summary>
        /// Clears the hopball reference. Called by Hopball when it dissolves/respawns.
        /// </summary>
        public void ClearHopballReference() {
            _currentHopball = null;
            _hopballRigidbody = null;
        }

        private GameObject _currentFpWeaponInstance;
        private GameObject _currentWorldWeaponInstance;
        private Rigidbody _hopballRigidbody;

        // Hopball model references
        private GameObject _fpHopballVisualInstance; // Visual-only FP model (no state tracking)
        private GameObject _worldHopballVisualInstance; // Visual-only world model (parented to world weapon socket)
        private Coroutine _restoreWeaponsCoroutine; // Track restore coroutine
        public Collider PlayerCollider { get; private set; }

        private void Awake() {
            InitializeComponentReferences();
            InitializeFpCamera();
            InitializeWorldWeaponSocket();
            InitializeHopballLayer();
            InitializePlayerCollider();
            InitializePlayerTarget();
        }

        private void InitializePlayerCollider() {
            if(PlayerCollider != null) return;
            if(characterController == null) {
                if(playerController != null) {
                    characterController = playerController.GetComponent<CharacterController>();
                } else {
                    characterController = GetComponent<CharacterController>();
                }
            }

            PlayerCollider = characterController;
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

        /// <summary>
        /// Initializes the OSI Target component reference.
        /// </summary>
        private void InitializePlayerTarget() {
            if(playerTarget == null) {
                playerTarget = GetComponent<Target>();
            }
        }

        private void OnDestroy() {
            // Drop hopball if holding when destroyed
            if(IsHoldingHopball) {
                DropHopball();
            }
        }

        /// <summary>
        /// Tries to pick up a hopball within pickup range.
        /// Returns true if a hopball was picked up.
        /// </summary>
        public void TryPickupHopball() {
            var hitColliders = Physics.OverlapSphere(transform.position, pickupRange, hopballLayer);

            foreach(var col in hitColliders) {
                var hopball = col.GetComponent<Hopball>() ?? col.GetComponentInParent<Hopball>();

                // Only pick up if hopball is not equipped, not dropped (active), and unparented
                if(hopball != null && !hopball.IsEquipped && hopball.transform.parent == null &&
                   hopball.gameObject.activeSelf) {
                    EquipHopball(hopball);
                }
            }
        }

        /// <summary>
        /// Equips the hopball, hides FP weapons, and prevents shooting/reloading.
        /// </summary>
        private void EquipHopball(Hopball hopball) {
            if(hopball == null || !IsOwner) return;

            _currentHopball = hopball;
            _hopballRigidbody = hopball.Rigidbody;

            // Setup FP hopball visual immediately (optimistic, owner sees it right away)
            SetupFpHopball(hopball);

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

            var controller = requestingPlayer.GetComponent<HopballController>();
            if(controller == null) return;

            // Server performs the equip (this will broadcast hopball state update to all clients)
            hopball.SetEquipped(true, controller.IsOwner, controller);

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

            bool isHolder = OwnerClientId == holderClientId && IsOwner;
            var localClientId = NetworkManager.Singleton?.LocalClient?.ClientId ?? 0;

            // Owner: Hide world weapon and setup world hopball visual (for others to see)
            if(isHolder) {
                HideWorldWeapon();
                SetupWorldHopballVisual(hopball);
            }
            // Non-holders: Setup world hopball visual
            // Enable target on holder's controller only if local client is viewing it (not the holder themselves)
            else {
                SetupWorldHopballVisual(hopball);
                // This controller belongs to the holder, enable target so local client can see indicator
                // Only enable if local client is NOT the holder (holder doesn't see their own indicator)
                if(OwnerClientId == holderClientId && localClientId != holderClientId) {
                    EnablePlayerTarget(holderClientId);
                }
            }
        }

        /// <summary>
        /// Enables the OSI Target for non-owners and sets team-based color.
        /// </summary>
        private void EnablePlayerTarget(ulong holderClientId) {
            if(playerTarget == null || IsOwner) return;

            // Get local player's team
            var localPlayer = NetworkManager.Singleton?.LocalClient?.PlayerObject;
            if(localPlayer == null) return;

            var localTeamMgr = localPlayer.GetComponent<PlayerTeamManager>();
            var holderTeamMgr = playerController?.GetComponent<PlayerTeamManager>();

            if(localTeamMgr == null || holderTeamMgr == null) {
                playerTarget.SetTargetColor(new Color(1f, 0.392f, 0.392f)); // #FF6464 - Red (default)
            } else {
                bool isTeammate = localTeamMgr.netTeam.Value == holderTeamMgr.netTeam.Value;
                playerTarget.SetTargetColor(isTeammate
                    ? new Color(0.392f, 0.588f, 1f) // #6496FF - Blue
                    : new Color(1f, 0.392f, 0.392f)); // #FF6464 - Red
            }

            playerTarget.enabled = true;
        }

        /// <summary>
        /// Sets up the first-person hopball visual (separate visual-only prefab that syncs with world hopball state).
        /// </summary>
        private void SetupFpHopball(Hopball hopball) {
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
            var swayCamera = fpCamera ?? weaponManager?.FpCamera;
            if(swayCamera == null) return null;

            // Search for SwayHolder (structure: Camera -> BobHolder -> SwayHolder)
            foreach(Transform child in swayCamera.transform) {
                if(child.name == "BobHolder") {
                    var swayHolder = child.Find("SwayHolder");
                    if(swayHolder != null) return swayHolder;
                }
            }

            // Fallback to camera if swayholder not found
            return swayCamera.transform;
        }

        /// <summary>
        /// Sets shadow casting mode for all renderers in the FP visual.
        /// </summary>
        private void SetFpVisualShadows(GameObject obj, bool castShadows) {
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
        private void SetupWorldHopballVisual(Hopball hopball) {
            if(worldWeaponSocket == null || hopballVisualPrefab == null) return;

            // Create visual-only world model (not a NetworkObject, so regular parenting works)
            _worldHopballVisualInstance = Instantiate(hopballVisualPrefab, worldWeaponSocket, false);
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
        public void DropHopball() {
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

            // Clear rigidbody reference (server will handle physics)
            _hopballRigidbody = null;

            // Restore weapons
            ShowWeapons();
        }

        /// <summary>
        /// Server-side method to drop the hopball at a specific position.
        /// Can be called directly from server or via ServerRpc from client.
        /// </summary>
        private void DropHopballAtPosition(Hopball hopball, Vector3 dropPosition, Quaternion dropRotation,
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
            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(requestingClientId, out var client)) {
                var requestingPlayer = client.PlayerObject;
                if(requestingPlayer != null) {
                    var controller = requestingPlayer.GetComponent<HopballController>();
                    if(controller != null) {
                        controller.DisablePlayerTargetClientRpc();
                    }
                }
            }
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
            Vector3 dropPosition = transform.position + Vector3.up * 1.5f;
            Quaternion dropRotation = transform.rotation;

            // Drop directly on server (we're already on server, so call the method directly)
            DropHopballAtPosition(hopball, dropPosition, dropRotation, OwnerClientId);

            // Clear rigidbody reference
            _hopballRigidbody = null;

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

            // Restore weapons
            ShowWeapons();
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
            _hopballRigidbody = null;

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
            if(playerTarget != null) {
                playerTarget.enabled = false;
            }
        }

        /// <summary>
        /// Hides the FP weapon model (called locally by owner).
        /// </summary>
        private void HideFpWeapons() {
            if(weaponManager == null) return;

            var currentWeapon = weaponManager.CurrentWeapon;
            if(currentWeapon == null) return;

            var fpWeapon = currentWeapon.GetWeaponPrefab();
            if(fpWeapon != null && fpWeapon.activeSelf) {
                _currentFpWeaponInstance = fpWeapon;
                fpWeapon.SetActive(false);
            }
        }

        /// <summary>
        /// Hides the world weapon model (called via RPC for owner).
        /// </summary>
        private void HideWorldWeapon() {
            if(worldWeaponSocket == null || weaponManager == null) return;

            var currentIndex = weaponManager.CurrentWeaponIndex;
            var weaponData = weaponManager.GetWeaponDataByIndex(currentIndex);
            if(weaponData == null || string.IsNullOrEmpty(weaponData.worldWeaponName)) return;

            var worldWeapon = worldWeaponSocket.Find(weaponData.worldWeaponName);
            if(worldWeapon != null && worldWeapon.gameObject.activeSelf) {
                _currentWorldWeaponInstance = worldWeapon.gameObject;
                worldWeapon.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Shows the current FP and world weapon models.
        /// </summary>
        private void ShowWeapons() {
            // Restore FP weapon if we have a stored reference
            if(_currentFpWeaponInstance != null) {
                _currentFpWeaponInstance.SetActive(true);
                _currentFpWeaponInstance = null;
            }

            // Restore world weapon if we have a stored reference
            if(_currentWorldWeaponInstance != null) {
                _currentWorldWeaponInstance.SetActive(true);
                _currentWorldWeaponInstance = null;
            } else {
                // Fallback: Try to find and show the current world weapon even if reference was lost
                // This ensures the world weapon is always shown when dropping the hopball
                if(worldWeaponSocket != null && weaponManager != null) {
                    var currentIndex = weaponManager.CurrentWeaponIndex;
                    var weaponData = weaponManager.GetWeaponDataByIndex(currentIndex);
                    if(weaponData != null && !string.IsNullOrEmpty(weaponData.worldWeaponName)) {
                        var worldWeapon = worldWeaponSocket.Find(weaponData.worldWeaponName);
                        if(worldWeapon != null && !worldWeapon.gameObject.activeSelf) {
                            worldWeapon.gameObject.SetActive(true);
                        }
                    }
                }
            }
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

            if(weaponManager == null) {
                weaponManager = GetComponent<WeaponManager>();
            }

            if(healthController == null) {
                healthController = GetComponent<PlayerHealthController>();
            }
        }

        /// <summary>
        /// Initializes FP camera reference with fallbacks.
        /// </summary>
        private void InitializeFpCamera() {
            if(fpCamera != null) return;

            if(weaponManager != null) {
                fpCamera = weaponManager.FpCamera;
                if(fpCamera == null && weaponManager.WeaponCameraController != null) {
                    fpCamera = weaponManager.WeaponCameraController.FpCamera;
                }
            }
        }

        /// <summary>
        /// Initializes world weapon socket reference with fallbacks.
        /// </summary>
        private void InitializeWorldWeaponSocket() {
            if(worldWeaponSocket != null) return;

            if(weaponManager != null) {
                worldWeaponSocket = weaponManager.WorldWeaponSocket;
            }

            if(worldWeaponSocket == null) {
                worldWeaponSocket = transform.Find("WorldModelRoot/WeaponSocket")
                                    ?? transform.Find("WorldModelRoot")?.Find("WeaponSocket");
            }
        }

        /// <summary>
        /// Initializes hopball layer mask if not assigned.
        /// </summary>
        private void InitializeHopballLayer() {
            if(hopballLayer.value != 0) return;

            int hopballLayerIndex = LayerMask.NameToLayer("Hopball");
            if(hopballLayerIndex != -1) {
                hopballLayer = 1 << hopballLayerIndex;
            } else {
                Debug.LogWarning("[HopballController] Hopball layer not found. Please ensure 'Hopball' layer exists.");
            }
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
                    if(visual != null) {
                        // Disable and unparent before destroying
                        visual.SetActive(false);
                        visual.transform.SetParent(null);
                        Destroy(visual);
                    }
                }
            }

            // Ensure reference is cleared
            _fpHopballVisualInstance = null;
        }

        /// <summary>
        /// Destroys the world visual instance if it exists.
        /// </summary>
        private void DestroyWorldVisual() {
            if(_worldHopballVisualInstance != null) {
                Destroy(_worldHopballVisualInstance);
                _worldHopballVisualInstance = null;
            }
        }

        /// <summary>
        /// Cleans up all hopball visuals. Called when ball respawns to ensure no visuals remain.
        /// </summary>
        public void CleanupHopballVisuals() {
            // Only cleanup if we're not currently holding the ball
            if(!IsHoldingHopball) {
                DestroyFpVisual();
                DestroyWorldVisual();
            }
        }

        /// <summary>
        /// Recursively sets the layer of a GameObject and all its children.
        /// </summary>
        private void SetGameObjectAndChildrenLayer(GameObject obj, int layer) {
            if(obj == null) return;
            obj.layer = layer;
            foreach(Transform child in obj.transform) {
                SetGameObjectAndChildrenLayer(child.gameObject, layer);
            }
        }

        /// <summary>
        /// Called when player dies. Drops the hopball if holding it.
        /// </summary>
        public void OnPlayerDeath() {
            if(IsHoldingHopball) {
                DropHopball();
            } else {
                // Even if not holding, ensure Target is disabled on death
                if(playerTarget != null) {
                    playerTarget.enabled = false;
                }
            }
        }

        /// <summary>
        /// Restores weapons after hopball dissolves. Called by Hopball when energy reaches 0 and dissolve completes.
        /// DropHopball() already handles showing weapons, so we just need to reset flags.
        /// </summary>
        public void RestoreWeaponsAfterDissolve() {
            if(!IsOwner) return;

            // Prevent multiple calls
            if(_restoreWeaponsCoroutine != null) return;

            // Clear hopball state (DropHopball already does this, but ensure it's cleared)
            _currentHopball = null;
            _hopballRigidbody = null;

            // Ensure visuals are destroyed (may have already been destroyed by DropHopball)
            DestroyFpVisual();
            DestroyWorldVisual();

            // Disable OSI Target script (ball dissolved, player no longer holding)
            if(playerTarget != null) {
                playerTarget.enabled = false;
            }

            // Wait a moment for dissolve effect to finish, then clear the flag to allow shooting/reloading
            _restoreWeaponsCoroutine = StartCoroutine(RestoreWeaponsAfterDelay());
        }

        private IEnumerator RestoreWeaponsAfterDelay() {
            yield return new WaitForSeconds(0.3f);

            // Ensure FP visual is destroyed (double-check)
            DestroyFpVisual();

            // Clear the flag - weapons are already shown by DropHopball(), shooting/reloading now enabled
            IsRestoringAfterDissolve = false;
            _restoreWeaponsCoroutine = null;
        }
    }
}
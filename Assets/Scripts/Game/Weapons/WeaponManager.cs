using System;
using System.Collections.Generic;
using Game.Player;
using Game.Audio;
using Network.AntiCheat;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Weapons {
    public class WeaponManager : NetworkBehaviour {
        [SerializeField] private PlayerController playerController;
        private CinemachineCamera _fpCamera;
        private Transform _worldWeaponSocket;
        private Animator _playerAnimator;
        private WeaponCameraController _weaponCameraController;
        private PlayerRenderer _playerRenderer;

        [Header("Loadout Weapon Pools")]
        [SerializeField] private List<WeaponData> primaryWeaponOptions = new();

        [SerializeField] private List<WeaponData> secondaryWeaponOptions = new();

        [Header("Weapon System")]
        [SerializeField, HideInInspector] private List<WeaponData> weaponDataList = new();
        [Header("Holstered Weapon Models")]
        [Tooltip("Explicit holstered primary weapon objects. Required for primary holster display.")]
        [SerializeField] private List<GameObject> primaryHolsteredWeapons = new();
        [Tooltip("Explicit holstered secondary weapon objects. Required for secondary holster display.")]
        [SerializeField] private List<GameObject> secondaryHolsteredWeapons = new();

        private readonly List<GameObject> _fpWeaponInstances = new();
        private readonly Dictionary<int, int> _weaponAmmo = new();
        private GameObject _pendingTpWeapon; // Track pending TP weapon to show via animation event
        private class ServerWeaponState {
            public float LastShotTime;
            public int ServerAmmo;
            public ulong LastShotId;
        }

        private readonly Dictionary<int, ServerWeaponState> _serverWeaponStates = new();

        public Weapon CurrentWeapon { get; private set; }
        public GameObject CurrentWorldWeaponInstance { get; private set; }

        public int CurrentWeaponIndex { get; private set; } = -1;

        public int WeaponCount => weaponDataList.Count;
        public bool IsPullingOut { get; private set; }

        private static readonly int PullOutHash = Animator.StringToHash("PullOut");
        private static readonly int weaponIndexHash = Animator.StringToHash("WeaponIndex");
        private readonly Dictionary<string, GameObject> _primaryHolsterLookup = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GameObject> _secondaryHolsterLookup = new(StringComparer.OrdinalIgnoreCase);
        private GameObject _selectedPrimaryHolster;
        private GameObject _selectedSecondaryHolster;
        public GameObject PrimaryHolster => _selectedPrimaryHolster;
        public GameObject SecondaryHolster => _selectedSecondaryHolster;
        private int _pendingHolsterHideSlot = -1;

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[WeaponManager] PlayerController not found!");
                enabled = false;
                return;
            }

            if(CurrentWeapon == null) CurrentWeapon = playerController.WeaponComponent;
            if(_fpCamera == null) _fpCamera = playerController.FpCamera;
            if(_worldWeaponSocket == null) _worldWeaponSocket = playerController.WorldWeaponSocket;
            if(_playerAnimator == null) _playerAnimator = playerController.PlayerAnimator;
            if(_weaponCameraController == null) _weaponCameraController = playerController.WeaponCameraController;
            
            // Validate PlayerRenderer (required for renderer operations)
            if(_playerRenderer == null) _playerRenderer = playerController.PlayerRenderer;
            if(_playerRenderer != null) return;
            Debug.LogError("[WeaponManager] PlayerRenderer not found! Cannot perform renderer operations.");
            enabled = false;
        }

        public void InitializeWeapons() {
            if(CurrentWeapon == null) {
                Debug.LogError("[WeaponManager] Weapon component not assigned!");
                return;
            }

            // Subscribe to weapon index changes to rebuild weapon list when they sync
            if(playerController != null) {
                playerController.primaryWeaponIndex.OnValueChanged -= OnWeaponIndexChanged;
                playerController.primaryWeaponIndex.OnValueChanged += OnWeaponIndexChanged;
                playerController.secondaryWeaponIndex.OnValueChanged -= OnWeaponIndexChanged;
                playerController.secondaryWeaponIndex.OnValueChanged += OnWeaponIndexChanged;
            }

            BuildEquippedWeaponList();
            
            // For non-owners, check if NetworkVariables are still at default (might not be synced yet)
            if(!IsOwner && playerController != null) {
                var primaryIndex = playerController.primaryWeaponIndex.Value;
                var secondaryIndex = playerController.secondaryWeaponIndex.Value;
                
                // If both are 0, and we have weapon options, might be unsynced - wait for sync
                if(primaryIndex == 0 && secondaryIndex == 0 && 
                   primaryWeaponOptions != null && primaryWeaponOptions.Count > 0) {
                    // Don't initialize yet - wait for NetworkVariables to sync
                    // OnWeaponIndexChanged will handle initialization when values arrive
                    return;
                }
            }
            
            SetupHolsteredWeaponModels();
            DisableUnequippedWorldWeapons();

            if(weaponDataList == null || weaponDataList.Count == 0) {
                Debug.LogError("[WeaponManager] weaponDataList is empty!");
                return;
            }

            // Hide all 3P weapons initially
            if(_worldWeaponSocket != null) {
                foreach(Transform child in _worldWeaponSocket) {
                    child.gameObject.SetActive(false);
                }
            }

            // Instantiate FP weapon viewmodels only
            for(var i = 0; i < weaponDataList.Count; i++) {
                var data = weaponDataList[i];

                if(data == null || data.weaponPrefab == null) {
                    Debug.LogError($"[WeaponManager] Invalid weapon data at index {i}");
                    continue;
                }

                var swayHolder = new GameObject("SwayHolder");
                swayHolder.AddComponent<WeaponSway>();
                swayHolder.transform.SetParent(_fpCamera.transform, false);
                swayHolder.transform.localPosition = Vector3.zero;
                swayHolder.transform.localEulerAngles = Vector3.zero;
                var bobHolder = new GameObject("BobHolder");
                bobHolder.AddComponent<WeaponBob>();
                bobHolder.transform.SetParent(swayHolder.transform, false);
                bobHolder.transform.localPosition = Vector3.zero;
                bobHolder.transform.localEulerAngles = Vector3.zero;

                var fpWeaponInstance = Instantiate(data.weaponPrefab, bobHolder.transform, false);
                fpWeaponInstance.transform.localPosition = data.spawnPosition;
                fpWeaponInstance.transform.localEulerAngles = data.spawnRotation;

                // Add WeaponAnimationEvents component for animation event handling
                if(fpWeaponInstance.GetComponent<WeaponAnimationEvents>() == null) {
                    fpWeaponInstance.AddComponent<WeaponAnimationEvents>();
                }

                var meshRenderers = fpWeaponInstance.GetComponentsInChildren<MeshRenderer>();
                foreach(var meshRenderer in meshRenderers) {
                    meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
                }

                // Setup SkinnedMeshRenderers (for FP arm models)
                SetupFpWeaponSkinnedMeshRenderers(fpWeaponInstance);

                if(IsOwner) {
                    // Owner: Use Weapon layer (rendered by separate weapon camera above all geometry)
                    var weaponLayer = LayerMask.NameToLayer("Weapon");
                    SetGameObjectAndChildrenLayer(fpWeaponInstance, weaponLayer);
                } else {
                    // Non-owner: Use Masked layer (hidden from owner's view)
                    fpWeaponInstance.layer = LayerMask.NameToLayer("Masked");
                }

                fpWeaponInstance.SetActive(false);
                _fpWeaponInstances.Add(fpWeaponInstance);

                // Initialize ammo
                _weaponAmmo[i] = data.magSize;
            }

            // Switch to first weapon
            if(_fpWeaponInstances.Count > 0) {
                EquipInitialWeapon(0);
            } else {
                Debug.LogError("[WeaponManager] No weapons instantiated!");
            }

            UpdateHolsterVisibility();
        }


        public void SwitchWeapon(int newIndex) {
            if(newIndex < 0 || newIndex >= weaponDataList.Count)
                return;

            // Check if holding hopball - if so, allow switching even to same weapon
            // Also check if restoring after dissolve to allow switch
            var isHoldingHopball = false;
            var isRestoringAfterDissolve = false;
            if(IsOwner) {
                if(playerController == null) return;
                var hopballController = playerController.PlayerHopballController;
                if(hopballController != null) {
                    if(hopballController.IsHoldingHopball) {
                        isHoldingHopball = true;
                        // Drop hopball when switching weapons (let weapon switch visuals handle showing)
                        hopballController.DropHopball(PlayerHopballController.HopballDropReason.WeaponSwitch);
                    }

                    // Check if restoring after dissolve
                    if(PlayerHopballController.IsRestoringAfterDissolve) {
                        isRestoringAfterDissolve = true;
                    }
                }
            }

            // Block switching to same weapon unless holding hopball or restoring after dissolve
            if(newIndex == CurrentWeaponIndex && !isHoldingHopball && !isRestoringAfterDissolve)
                return;

            if(IsOwner && SoundFXManager.Instance != null) {
                SoundFXManager.Instance.PlayUISound(SfxKey.WeaponSwitch);
            }

            // Cache ammo from current weapon before switching away
            if(CurrentWeapon != null && CurrentWeaponIndex >= 0) {
                _weaponAmmo[CurrentWeaponIndex] = CurrentWeapon.currentAmmo;
            }

            // Immediately hide current weapon (no sheath delay)
            if(CurrentWeaponIndex >= 0) {
                // Hide FP weapon
                var oldFp = _fpWeaponInstances[CurrentWeaponIndex];
                if(oldFp != null) {
                    oldFp.SetActive(false);
                }

                // Hide 3P weapon - use CurrentWorldWeaponInstance first (most reliable)
                if(CurrentWorldWeaponInstance != null) {
                    CurrentWorldWeaponInstance.SetActive(false);
                } else {
                    // Fallback: try to find by name if CurrentWorldWeaponInstance wasn't set
                    var oldName = weaponDataList[CurrentWeaponIndex].worldWeaponName;
                    if(_worldWeaponSocket != null && !string.IsNullOrEmpty(oldName)) {
                        var oldObj = _worldWeaponSocket.Find(oldName);
                        if(oldObj != null) {
                            oldObj.gameObject.SetActive(false);
                        }
                    }
                }
                CurrentWorldWeaponInstance = null; // Clear reference after hiding
            }

            // Commit to new weapon index immediately
            CurrentWeaponIndex = newIndex;
            var data = weaponDataList[CurrentWeaponIndex];
            _pendingHolsterHideSlot = GetSlotForIndex(CurrentWeaponIndex);

            // Prepare and show new FP weapon
            var fp = _fpWeaponInstances[CurrentWeaponIndex];
            if(fp != null) {
                fp.transform.localPosition = data.spawnPosition;
                fp.transform.localEulerAngles = data.spawnRotation;

                // Activate weapon GameObject
                fp.SetActive(true);

                // Setup SkinnedMeshRenderers (for FP arm models)
                SetupFpWeaponSkinnedMeshRenderers(fp);

                // Rebind animator to reset state (will enter PullOut state if configured)
                var anim = fp.GetComponent<Animator>();
                if(anim != null && anim.enabled) {
                    anim.Rebind();
                    anim.Update(0f);

                    // Trigger pull out animation
                    // If animation doesn't exist yet, the trigger will be ignored gracefully
                    anim.SetTrigger(PullOutHash);
                }
            }

            // Prepare new 3P weapon but DON'T show it yet - wait for animation event
            if(_worldWeaponSocket != null && !string.IsNullOrEmpty(data.worldWeaponName)) {
                var worldObj = _worldWeaponSocket.Find(data.worldWeaponName);
                if(worldObj) {
                    var worldWeaponInstance = worldObj.gameObject;
                    // Store reference but don't activate yet - will be activated by animation event
                    _pendingTpWeapon = worldWeaponInstance;
                    worldWeaponInstance.SetActive(false); // Ensure it's hidden
                    CurrentWorldWeaponInstance = null;
                }
            }

            // Restore ammo (fallback to mag size if somehow missing)
            var restoredAmmo = data.magSize;
            if(_weaponAmmo.TryGetValue(CurrentWeaponIndex, out var storedAmmo)) {
                restoredAmmo = storedAmmo;
            }

            // Update weapon data immediately (no waiting for animations)
            // Pass null for worldWeaponInstance since it's not shown yet - will be set when TP weapon is shown
            CurrentWeapon.SwitchToWeapon(data, fp, null, restoredAmmo);
            ReportAmmoSync(CurrentWeaponIndex, restoredAmmo);

            // Set pulling out state
            // The pull-out animation will call HandlePullOutCompleted() when done
            IsPullingOut = true;

            // Update player animator weapon index for 3P animations
            if(_playerAnimator == null) return;
            _playerAnimator.SetInteger(weaponIndexHash, newIndex);
            // Trigger TP pull out animation
            _playerAnimator.SetTrigger(PullOutHash);

            if(IsOwner) {
                if(IsServer) {
                    if(TryConsumeWeaponSwitchQuota()) {
                        BroadcastWeaponSwitchClientRpc(newIndex);
                    }
                } else {
                    RequestWeaponSwitchBroadcastServerRpc(newIndex);
                }
            }

            UpdateHolsterVisibility();
            
            // Update holster shadow state for owners after weapon switch
            if(IsOwner && playerController != null && playerController.PlayerShadow != null) {
                playerController.PlayerShadow.UpdateHolsterShadowStateForOwner();
            }
        }

        /// <summary>
        /// Called from player animation event to show the TP weapon during pull out animation.
        /// </summary>
        public void ShowTpWeapon() {
            if(_pendingTpWeapon == null) return;
            _pendingTpWeapon.SetActive(true);

            // Update weapon data with the now-active TP weapon
            if(CurrentWeapon != null && CurrentWeaponIndex >= 0) {
                var data = weaponDataList[CurrentWeaponIndex];
                var fpWeapon = _fpWeaponInstances[CurrentWeaponIndex];
                var restoredAmmo = _weaponAmmo.TryGetValue(CurrentWeaponIndex, out var ammo) ? ammo : data.magSize;

                CurrentWeapon.SwitchToWeapon(
                    data,
                    fpWeapon,
                    _pendingTpWeapon,
                    restoredAmmo
                );
            }

            CurrentWorldWeaponInstance = _pendingTpWeapon;
            _pendingTpWeapon = null;

            EnsureWorldWeaponShadowState();
            EnsureWeaponHierarchyActive();

             _pendingHolsterHideSlot = -1;
             UpdateHolsterVisibility();
             
             // Update holster shadow state for owners after TP weapon is shown
             if(IsOwner && playerController != null && playerController.PlayerShadow != null) {
                 playerController.PlayerShadow.UpdateHolsterShadowStateForOwner();
             }
        }

        /// <summary>
        /// Called when the pull-out animation completes (via animation event).
        /// Allows shooting and reloading again.
        /// </summary>
        public void HandlePullOutCompleted() {
            IsPullingOut = false;
        }

        /// <summary>
        /// Triggers the pullout animation. Used when hopball dissolves to restore weapon visibility.
        /// </summary>
        public void TriggerPullOutAnimation() {
            if(_playerAnimator == null) return;
            
            // If we're not switching weapons (e.g., after hopball dissolve), we need to set up _pendingTpWeapon
            // so the animation event can show it. The weapon might already be inactive from HideWorldWeapon().
            if(_pendingTpWeapon == null && CurrentWeaponIndex >= 0 && CurrentWeaponIndex < weaponDataList.Count) {
                var data = weaponDataList[CurrentWeaponIndex];
                
                // Try to find the world weapon - it might already exist but be inactive
                if(_worldWeaponSocket != null && !string.IsNullOrEmpty(data.worldWeaponName)) {
                    var worldObj = _worldWeaponSocket.Find(data.worldWeaponName);
                    if(worldObj) {
                        _pendingTpWeapon = worldObj.gameObject;
                        // Ensure it's hidden initially (will be shown by animation event)
                        _pendingTpWeapon.SetActive(false);
                    }
                }
                
                // Set holster slot to hide the correct holster during pullout
                _pendingHolsterHideSlot = GetSlotForIndex(CurrentWeaponIndex);
                UpdateHolsterVisibility();
            }
            
            // Set weapon index for 3P animations
            _playerAnimator.SetInteger(weaponIndexHash, CurrentWeaponIndex);
            // Trigger TP pull out animation
            _playerAnimator.SetTrigger(PullOutHash);
            
            // Mark as pulling out
            IsPullingOut = true;
        }

        private void EnsureWorldWeaponShadowState() {
            if(CurrentWorldWeaponInstance == null) return;

            if(!CurrentWorldWeaponInstance.activeSelf) {
                CurrentWorldWeaponInstance.SetActive(true);
            }

            var targetMode = playerController != null && playerController.IsOwner
                ? ShadowCastingMode.ShadowsOnly
                : ShadowCastingMode.On;

            var playerShadow = playerController != null ? playerController.PlayerShadow : null;
            if(playerShadow != null) {
                playerShadow.SetWorldWeaponRenderersShadowMode(targetMode);
                return;
            }

            var renderers = CurrentWorldWeaponInstance.GetComponentsInChildren<MeshRenderer>(true);
            foreach(var mr in renderers) {
                if(mr == null) continue;
                mr.enabled = true;
                mr.shadowCastingMode = targetMode;
            }
        }

        private void EnsureWeaponHierarchyActive() {
            if(CurrentWorldWeaponInstance == null) return;
            var parent = CurrentWorldWeaponInstance.transform;
            while(parent != null) {
                if(!parent.gameObject.activeSelf) {
                    parent.gameObject.SetActive(true);
                }

                parent = parent.parent;
            }
        }

        [Rpc(SendTo.Server)]
        private void RequestWeaponSwitchBroadcastServerRpc(int newIndex) {
            if(!TryConsumeWeaponSwitchQuota()) return;
            BroadcastWeaponSwitchClientRpc(newIndex);
        }

        [Rpc(SendTo.Everyone, Delivery = RpcDelivery.Unreliable)]
        private void BroadcastWeaponSwitchClientRpc(int newIndex) {
            if(IsOwner) return;
            ApplyRemoteWeaponSwitch(newIndex);
        }

        private void ApplyRemoteWeaponSwitch(int newIndex) {
            if(newIndex < 0 || newIndex >= weaponDataList.Count) return;

            // Hide current 3P weapon - use CurrentWorldWeaponInstance first (most reliable)
            if(CurrentWorldWeaponInstance != null) {
                CurrentWorldWeaponInstance.SetActive(false);
            } else if(CurrentWeaponIndex >= 0 && CurrentWeaponIndex < weaponDataList.Count) {
                // Fallback: try to find by name if CurrentWorldWeaponInstance wasn't set
                var previousName = weaponDataList[CurrentWeaponIndex].worldWeaponName;
                if(_worldWeaponSocket != null && !string.IsNullOrEmpty(previousName)) {
                    var previousObj = _worldWeaponSocket.Find(previousName);
                    if(previousObj != null) {
                        previousObj.gameObject.SetActive(false);
                    }
                }
            }
            CurrentWorldWeaponInstance = null; // Clear reference after hiding

            CurrentWeaponIndex = newIndex;
            var data = weaponDataList[newIndex];
            _pendingHolsterHideSlot = GetSlotForIndex(CurrentWeaponIndex);

            if(_worldWeaponSocket != null && !string.IsNullOrEmpty(data.worldWeaponName)) {
                var worldObj = _worldWeaponSocket.Find(data.worldWeaponName);
                if(worldObj) {
                    _pendingTpWeapon = worldObj.gameObject;
                    worldObj.gameObject.SetActive(false);
                    CurrentWorldWeaponInstance = null;
                }
            }

            if(_playerAnimator == null) return;
            _playerAnimator.SetInteger(weaponIndexHash, newIndex);
            _playerAnimator.SetTrigger(PullOutHash);

            UpdateHolsterVisibility();
        }

        public void ResetAllWeaponAmmo() {
            _weaponAmmo.Clear();
            for(var i = 0; i < weaponDataList.Count; i++) {
                var data = weaponDataList[i];
                if(data != null) {
                    _weaponAmmo[i] = Mathf.Clamp(data.magSize, 0, int.MaxValue);
                }
            }
        }

        public void ApplyTpWeaponStateOnRespawn() {
            if(_playerAnimator == null) return;
            var slot = Mathf.Clamp(GetSlotForIndex(CurrentWeaponIndex), 0, 1);
            _playerAnimator.SetInteger(weaponIndexHash, slot);
            _playerAnimator.Rebind();
            _playerAnimator.Update(0f);
            UpdateHolsterVisibility();
        }

        public GameObject GetCurrentFpWeapon() {
            if(CurrentWeaponIndex < 0 || CurrentWeaponIndex >= _fpWeaponInstances.Count) return null;
            return _fpWeaponInstances[CurrentWeaponIndex];
        }

        public void SetCurrentFpWeaponVisible(bool visible) {
            var fpWeapon = GetCurrentFpWeapon();
            if(fpWeapon == null) return;

            _playerRenderer.SetFpWeaponRenderersEnabled(visible, fpWeapon);
        }

        public void OffsetCurrentFpWeapon(Vector3 localPosition, Vector3 localEulerAngles) {
            var fpWeapon = GetCurrentFpWeapon();
            if(fpWeapon == null) return;
            fpWeapon.transform.localPosition = localPosition;
            fpWeapon.transform.localEulerAngles = localEulerAngles;
        }

        public WeaponData GetWeaponDataByIndex(int index) {
            if(index < 0 || index >= weaponDataList.Count) return null;
            return weaponDataList[index];
        }

        private void EquipInitialWeapon(int index) {
            if(index < 0 || index >= weaponDataList.Count) {
                Debug.LogError($"[WeaponManager] EquipInitialWeapon: invalid index {index}");
                return;
            }

            CurrentWeaponIndex = index;
            IsPullingOut = false;

            var data = weaponDataList[index];

            // ---- FP WEAPON ----
            var fp = _fpWeaponInstances[index];
            if(fp != null) {
                fp.transform.localPosition = data.spawnPosition;
                fp.transform.localEulerAngles = data.spawnRotation;

                // Activate GameObject first so Animator.Update() can be called safely
                fp.SetActive(true);

                // Setup SkinnedMeshRenderers (for FP arm models)
                SetupFpWeaponSkinnedMeshRenderers(fp);

                var anim = fp.GetComponent<Animator>();
                if(anim != null && anim.enabled) {
                    anim.Rebind();
                    anim.Update(0f);
                }
            }

            // ---- 3P WORLD WEAPON ----
            GameObject worldWeaponInstance = null;
            if(!string.IsNullOrEmpty(data.worldWeaponName) && _worldWeaponSocket != null) {
                var worldObj = _worldWeaponSocket.Find(data.worldWeaponName);
                if(worldObj != null) {
                    worldWeaponInstance = worldObj.gameObject;
                    worldWeaponInstance.SetActive(true);
                    CurrentWorldWeaponInstance = worldWeaponInstance;
                }
            }

            // ---- AMMO ----
            var restoredAmmo = data.magSize;
            if(_weaponAmmo.TryGetValue(index, out var storedAmmo)) {
                restoredAmmo = storedAmmo;
            } else {
                _weaponAmmo[index] = restoredAmmo; // ensure dictionary has an entry
            }

            // This sets weapon data, ammo, HUD, muzzle lights, etc.
            CurrentWeapon.SwitchToWeapon(
                data,
                fp,
                worldWeaponInstance,
                restoredAmmo
            );

            ReportAmmoSync(CurrentWeaponIndex, restoredAmmo);

            _pendingHolsterHideSlot = -1;
            UpdateHolsterVisibility();
            
            // Update holster shadow state for owners after initial weapon equip
            if(IsOwner && playerController != null && playerController.PlayerShadow != null) {
                playerController.PlayerShadow.UpdateHolsterShadowStateForOwner();
            }
        }

        /// <summary>
        /// Recursively sets the layer of a GameObject and all its children
        /// </summary>
        private static void SetGameObjectAndChildrenLayer(GameObject obj, int layer) {
            if(obj == null) return;

            obj.layer = layer;
            foreach(Transform child in obj.transform) {
                SetGameObjectAndChildrenLayer(child.gameObject, layer);
            }
        }

        /// <summary>
        /// Enables and configures SkinnedMeshRenderers for FP weapon models (e.g., arm models).
        /// Sets shadow casting to Off and ensures they are enabled.
        /// Also applies player material customization from PlayerPrefs (owner only).
        /// </summary>
        private void SetupFpWeaponSkinnedMeshRenderers(GameObject fpWeaponInstance) {
            if(fpWeaponInstance == null) return;

            // Use PlayerRenderer for enabled state
            _playerRenderer.SetFpWeaponSkinnedRenderersEnabled(true, fpWeaponInstance);

            var skinnedRenderers = fpWeaponInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach(var skinnedRenderer in skinnedRenderers) {
                if(skinnedRenderer == null) continue;
                // Shadow mode is handled by PlayerShadow, but we set it here for initial setup
                skinnedRenderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            // Apply player material customization (owner only, local rendering)
            // Use same approach as hopball arms - apply to all renderers
            if(IsOwner) {
                ApplyPlayerMaterialToFpWeapon(fpWeaponInstance);
            }
        }

        /// <summary>
        /// Applies player material customization from PlayerPrefs to FP weapon arms only.
        /// Only called for owners since FP weapon rendering is fully local.
        /// Uses same approach as hopball arms for consistency.
        /// </summary>
        private void ApplyPlayerMaterialToFpWeapon(GameObject fpWeaponInstance) {
            if(fpWeaponInstance == null || playerController == null) return;

            // Find the arm GameObject (tagged with "Arm")
            GameObject armObject = null;
            foreach(Transform child in fpWeaponInstance.GetComponentsInChildren<Transform>(true)) {
                if(child.CompareTag("Arm")) {
                    armObject = child.gameObject;
                    break;
                }
            }

            if(armObject == null) {
                Debug.LogWarning("[WeaponManager] Could not find Arm tagged object in FP weapon instance.");
                return;
            }

            // Get material from player mesh (same as hopball approach)
            var playerMesh = playerController.PlayerMesh;
            if(playerMesh == null || playerMesh.materials.Length <= 1) return;
            var material = new Material(playerMesh.materials[1]); // Index 1 is the player material (0 is outline)
            
            // Apply to all renderers in the arm object only (not weapons)
            PlayerRenderer.ApplyMaterialToRenderers(armObject, material);
        }

        #region Holstered Weapons

        private void SetupHolsteredWeaponModels() {
            _primaryHolsterLookup.Clear();
            _secondaryHolsterLookup.Clear();

            BuildHolsterLookup(primaryHolsteredWeapons, _primaryHolsterLookup);
            BuildHolsterLookup(secondaryHolsteredWeapons, _secondaryHolsterLookup);

            _selectedPrimaryHolster = ResolveHolsterForSlot(0, _primaryHolsterLookup);
            _selectedSecondaryHolster = ResolveHolsterForSlot(1, _secondaryHolsterLookup);

            DisableHolster(_selectedPrimaryHolster);
            DisableHolster(_selectedSecondaryHolster);
        }

        private void BuildHolsterLookup(IEnumerable<GameObject> overrides, Dictionary<string, GameObject> lookup) {
            if(overrides == null) return;

            foreach(var go in overrides) {
                RegisterHolsterObject(lookup, go);
            }
        }

        private static void RegisterHolsterObject(IDictionary<string, GameObject> lookup, GameObject go) {
            if(go == null) return;
            var key = NormalizeHolsterKey(go.name);
            if(string.IsNullOrEmpty(key)) return;

            if(go.activeSelf) {
                go.SetActive(false);
            }

            lookup[key] = go;
        }

        private GameObject ResolveHolsterForSlot(int slot, Dictionary<string, GameObject> lookup) {
            var weaponData = GetWeaponDataForSlot(slot);
            return ResolveHolsterObject(weaponData, lookup);
        }

        private WeaponData GetWeaponDataForSlot(int slot) {
            if(weaponDataList == null || weaponDataList.Count == 0) return null;
            for(var i = 0; i < weaponDataList.Count; i++) {
                var data = weaponDataList[i];
                if(data == null) continue;
                var weaponSlot = ResolveWeaponSlot(data, i);
                if(weaponSlot == slot) {
                    return data;
                }
            }

            if(slot == 0 && weaponDataList.Count > 0) return weaponDataList[0];
            if(slot == 1 && weaponDataList.Count > 1) return weaponDataList[1];
            return null;
        }

        private static int ResolveWeaponSlot(WeaponData data, int fallback) {
            if(data == null) return fallback;
            return data.weaponSlot >= 0 ? data.weaponSlot : fallback;
        }

        private GameObject ResolveHolsterObject(WeaponData data, Dictionary<string, GameObject> lookup) {
            if(data == null || lookup == null || lookup.Count == 0) return null;

            var names = new List<string>(3);
            if(!string.IsNullOrEmpty(data.worldWeaponName)) names.Add(data.worldWeaponName);
            if(!string.IsNullOrEmpty(data.weaponName)) names.Add(data.weaponName);

            foreach(var candidate in names) {
                var key = NormalizeHolsterKey(candidate);
                if(string.IsNullOrEmpty(key)) continue;
                if(lookup.TryGetValue(key, out var go)) {
                    return go;
                }
            }

            return null;
        }

        private static string NormalizeHolsterKey(string value) {
            if(string.IsNullOrEmpty(value)) return null;
            return value.Replace("(Clone)", "").Trim().ToLowerInvariant();
        }

        private static void DisableHolster(GameObject holster) {
            if(holster == null) return;
            if(holster.activeSelf) {
                holster.SetActive(false);
            }
        }

        private void UpdateHolsterVisibility() {
            var currentSlot = GetSlotForIndex(CurrentWeaponIndex);

            if(_selectedPrimaryHolster != null) {
                var showPrimary = currentSlot != 0 || _pendingHolsterHideSlot == 0;
                if(_selectedPrimaryHolster.activeSelf != showPrimary) {
                    _selectedPrimaryHolster.SetActive(showPrimary);
                }
            }

            if(_selectedSecondaryHolster != null) {
                var showSecondary = currentSlot != 1 || _pendingHolsterHideSlot == 1;
                if(_selectedSecondaryHolster.activeSelf != showSecondary) {
                    _selectedSecondaryHolster.SetActive(showSecondary);
                }
            }
        }
        
        #endregion

        private int GetSlotForIndex(int index) {
            var data = GetWeaponDataByIndex(index);
            if(data == null) return -1;
            return ResolveWeaponSlot(data, index);
        }
        public int GetCurrentHolsterSlot() => GetSlotForIndex(CurrentWeaponIndex);
        public void RefreshHolsterVisibility() => UpdateHolsterVisibility();

        /// <summary>
        /// Called when weapon index NetworkVariables change. Rebuilds weapon list for non-owners when values sync.
        /// </summary>
        private void OnWeaponIndexChanged(int oldValue, int newValue) {
            // Only rebuild if we're not the owner (owner sets values, non-owners receive them)
            if(IsOwner) return;
            // Rebuild weapon list with synced values
            var oldListCount = weaponDataList != null ? weaponDataList.Count : 0;
            BuildEquippedWeaponList();
                
            // If weapons haven't been initialized yet (waited for NetworkVariable sync), do full initialization now
            if(_fpWeaponInstances == null || _fpWeaponInstances.Count == 0) {
                // Do full weapon instantiation (copy from InitializeWeapons)
                SetupHolsteredWeaponModels();
                DisableUnequippedWorldWeapons();
                    
                if(weaponDataList == null || weaponDataList.Count == 0) {
                    Debug.LogError("[WeaponManager] weaponDataList is empty after sync!");
                    return;
                }
                    
                // Hide all 3P weapons initially
                if(_worldWeaponSocket != null) {
                    foreach(Transform child in _worldWeaponSocket) {
                        child.gameObject.SetActive(false);
                    }
                }
                    
                // Instantiate FP weapon viewmodels
                for(var i = 0; i < weaponDataList.Count; i++) {
                    var data = weaponDataList[i];
                    if(data == null || data.weaponPrefab == null) {
                        Debug.LogError($"[WeaponManager] Invalid weapon data at index {i}");
                        continue;
                    }
                        
                    var swayHolder = new GameObject("SwayHolder");
                    swayHolder.AddComponent<WeaponSway>();
                    swayHolder.transform.SetParent(_fpCamera.transform, false);
                    swayHolder.transform.localPosition = Vector3.zero;
                    swayHolder.transform.localEulerAngles = Vector3.zero;
                    var bobHolder = new GameObject("BobHolder");
                    bobHolder.AddComponent<WeaponBob>();
                    bobHolder.transform.SetParent(swayHolder.transform, false);
                    bobHolder.transform.localPosition = Vector3.zero;
                    bobHolder.transform.localEulerAngles = Vector3.zero;
                        
                    var fpWeaponInstance = Instantiate(data.weaponPrefab, bobHolder.transform, false);
                    fpWeaponInstance.transform.localPosition = data.spawnPosition;
                    fpWeaponInstance.transform.localEulerAngles = data.spawnRotation;
                        
                    if(fpWeaponInstance.GetComponent<WeaponAnimationEvents>() == null) {
                        fpWeaponInstance.AddComponent<WeaponAnimationEvents>();
                    }
                        
                    var meshRenderers = fpWeaponInstance.GetComponentsInChildren<MeshRenderer>();
                    foreach(var meshRenderer in meshRenderers) {
                        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
                    }
                        
                    SetupFpWeaponSkinnedMeshRenderers(fpWeaponInstance);
                    fpWeaponInstance.layer = LayerMask.NameToLayer("Masked");
                    fpWeaponInstance.SetActive(false);
                    _fpWeaponInstances.Add(fpWeaponInstance);
                    _weaponAmmo[i] = data.magSize;
                }
                    
                // Equip first weapon now that we have the correct list
                if(_fpWeaponInstances.Count > 0) {
                    EquipInitialWeapon(0);
                }
                    
                UpdateHolsterVisibility();
                return;
            }
                
            // If weapon list changed (different count or different weapons), re-instantiate and re-equip
            if(weaponDataList.Count != oldListCount) {
                // Destroy old FP weapon instances
                foreach(var fpWeapon in _fpWeaponInstances) {
                    if(fpWeapon != null) Destroy(fpWeapon.transform.root.gameObject);
                }
                _fpWeaponInstances.Clear();
                    
                // Re-instantiate with new weapon list
                SetupHolsteredWeaponModels();
                DisableUnequippedWorldWeapons();
                    
                if(weaponDataList == null || weaponDataList.Count == 0) {
                    Debug.LogError("[WeaponManager] weaponDataList is empty after rebuild!");
                    return;
                }
                    
                // Hide all 3P weapons initially
                if(_worldWeaponSocket != null) {
                    foreach(Transform child in _worldWeaponSocket) {
                        child.gameObject.SetActive(false);
                    }
                }
                    
                // Instantiate FP weapon viewmodels
                for(var i = 0; i < weaponDataList.Count; i++) {
                    var data = weaponDataList[i];
                    if(data == null || data.weaponPrefab == null) {
                        Debug.LogError($"[WeaponManager] Invalid weapon data at index {i}");
                        continue;
                    }
                        
                    var swayHolder = new GameObject("SwayHolder");
                    swayHolder.AddComponent<WeaponSway>();
                    swayHolder.transform.SetParent(_fpCamera.transform, false);
                    swayHolder.transform.localPosition = Vector3.zero;
                    swayHolder.transform.localEulerAngles = Vector3.zero;
                    var bobHolder = new GameObject("BobHolder");
                    bobHolder.AddComponent<WeaponBob>();
                    bobHolder.transform.SetParent(swayHolder.transform, false);
                    bobHolder.transform.localPosition = Vector3.zero;
                    bobHolder.transform.localEulerAngles = Vector3.zero;
                        
                    var fpWeaponInstance = Instantiate(data.weaponPrefab, bobHolder.transform, false);
                    fpWeaponInstance.transform.localPosition = data.spawnPosition;
                    fpWeaponInstance.transform.localEulerAngles = data.spawnRotation;
                        
                    if(fpWeaponInstance.GetComponent<WeaponAnimationEvents>() == null) {
                        fpWeaponInstance.AddComponent<WeaponAnimationEvents>();
                    }
                        
                    var meshRenderers = fpWeaponInstance.GetComponentsInChildren<MeshRenderer>();
                    foreach(var meshRenderer in meshRenderers) {
                        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
                    }
                        
                    SetupFpWeaponSkinnedMeshRenderers(fpWeaponInstance);
                    fpWeaponInstance.layer = LayerMask.NameToLayer("Masked");
                    fpWeaponInstance.SetActive(false);
                    _fpWeaponInstances.Add(fpWeaponInstance);
                    _weaponAmmo[i] = data.magSize;
                }
                    
                // Re-equip current weapon (or equip first if current is invalid)
                if(CurrentWeaponIndex >= 0 && CurrentWeaponIndex < weaponDataList.Count) {
                    EquipInitialWeapon(CurrentWeaponIndex);
                } else if(weaponDataList.Count > 0) {
                    EquipInitialWeapon(0);
                }
            } else {
                // Just update holsters if list didn't change
                SetupHolsteredWeaponModels();
                DisableUnequippedWorldWeapons();
            }
                
            // Re-equip current weapon if it's still valid
            if(CurrentWeaponIndex >= 0 && CurrentWeaponIndex < weaponDataList.Count) {
                UpdateHolsterVisibility();
            }
        }

        /// <summary>
        /// Disables all world weapons that aren't in the player's equipped weapon list.
        /// Ensures only selected weapons are visible on the player model.
        /// </summary>
        private void DisableUnequippedWorldWeapons() {
            if(_worldWeaponSocket == null) return;
            
            // Collect all world weapon names from equipped weapons
            var equippedWorldWeaponNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if(weaponDataList != null) {
                foreach(var weaponData in weaponDataList) {
                    if(weaponData != null && !string.IsNullOrEmpty(weaponData.worldWeaponName)) {
                        equippedWorldWeaponNames.Add(weaponData.worldWeaponName);
                    }
                }
            }
            
            // Also include holstered weapon names
            if(_selectedPrimaryHolster != null) {
                equippedWorldWeaponNames.Add(_selectedPrimaryHolster.name);
            }
            if(_selectedSecondaryHolster != null) {
                equippedWorldWeaponNames.Add(_selectedSecondaryHolster.name);
            }
            
            // Disable all world weapons that aren't in the equipped list
            foreach(Transform child in _worldWeaponSocket) {
                if(child == null) continue;
                
                var weaponName = child.name;
                var normalizedName = NormalizeHolsterKey(weaponName);
                
                // Check if this weapon is in the equipped list
                var isEquipped = equippedWorldWeaponNames.Contains(weaponName) || 
                                 (!string.IsNullOrEmpty(normalizedName) && equippedWorldWeaponNames.Contains(normalizedName));
                
                // Also check if it's the current world weapon (should be active)
                var isCurrentWeapon = CurrentWorldWeaponInstance != null && 
                                      CurrentWorldWeaponInstance == child.gameObject;
                
                // Disable if not equipped and not current weapon
                if(!isEquipped && !isCurrentWeapon) {
                    child.gameObject.SetActive(false);
                }
            }
        }

        private void BuildEquippedWeaponList() {
            weaponDataList = new List<WeaponData>();
            weaponDataList.Clear();

            // Get weapon indices from NetworkVariables (synced across all clients)
            // For owner, these are set from PlayerPrefs in OnNetworkSpawn
            // For non-owners, these come from the NetworkVariables
            int primaryIndex;
            int secondaryIndex;
            
            if(playerController != null) {
                primaryIndex = playerController.primaryWeaponIndex.Value;
                secondaryIndex = playerController.secondaryWeaponIndex.Value;
            } else {
                // Fallback to PlayerPrefs if PlayerController not available (shouldn't happen)
                primaryIndex = PlayerPrefs.GetInt("PrimaryWeaponIndex", 0);
                secondaryIndex = PlayerPrefs.GetInt("SecondaryWeaponIndex", 0);
            }

            var primary = GetWeaponFromOptions(primaryWeaponOptions, primaryIndex, "Primary");
            if(primary != null) {
                weaponDataList.Add(primary);
            }

            var secondary = GetWeaponFromOptions(secondaryWeaponOptions, secondaryIndex, "Secondary");
            if(secondary != null) {
                weaponDataList.Add(secondary);
            }
        }

        private static WeaponData GetWeaponFromOptions(List<WeaponData> options, int storedIndex, string slotLabel) {
            if(options == null || options.Count == 0) {
                Debug.LogWarning($"[WeaponManager] No {slotLabel} weapon options assigned.");
                return null;
            }

            var clampedIndex = Mathf.Clamp(storedIndex, 0, options.Count - 1);
            if(clampedIndex != storedIndex) {
                Debug.LogWarning(
                    $"[WeaponManager] {slotLabel} weapon index {storedIndex} out of range. Using {clampedIndex} instead.");
                PlayerPrefs.SetInt($"{slotLabel}WeaponIndex", clampedIndex);
            }

            var weaponData = options[clampedIndex];
            if(weaponData == null) {
                Debug.LogWarning($"[WeaponManager] {slotLabel} weapon at index {clampedIndex} == null.");
            }

            return weaponData;
        }

        private bool TryConsumeWeaponSwitchQuota() {
            var config = AntiCheatConfig.Instance;
            if(config == null) return true;
            if(RpcRateLimiter.TryConsume(OwnerClientId, RpcRateLimiter.Keys.WeaponSwitch, config.weaponSwitchLimit,
                    config.rpcWindowSeconds)) {
                return true;
            }

            AntiCheatLogger.LogRateLimit(OwnerClientId, RpcRateLimiter.Keys.WeaponSwitch);
            return false;
        }

        public bool ValidateDamageRange(int weaponIndex, Vector3 hitPoint, out string reason) {
            reason = null;
            var data = GetWeaponDataByIndex(weaponIndex);
            if(data == null) {
                reason = "invalid weapon data";
                return false;
            }

            if(data.maxServerRange <= 0f) return true;

            var shooterPos = playerController != null
                ? playerController.PlayerTransform.position
                : transform.position;

            var distance = Vector3.Distance(shooterPos, hitPoint);
            if(distance <= data.maxServerRange + 1f) return true;

            reason = $"range {distance:F1}m exceeds limit {data.maxServerRange:F1}m";
            return false;
        }

        private ServerWeaponState GetOrCreateServerState(int weaponIndex) {
            if(_serverWeaponStates.TryGetValue(weaponIndex, out var state)) return state;
            state = new ServerWeaponState();
            var data = GetWeaponDataByIndex(weaponIndex);
            state.ServerAmmo = data != null ? data.magSize : 0;
            _serverWeaponStates[weaponIndex] = state;
            return state;
        }

        public bool ValidateServerShot(int weaponIndex, ulong shotId, out string reason) {
            reason = null;
            if(!IsServer) return true;
            var data = GetWeaponDataByIndex(weaponIndex);
            if(data == null) {
                reason = "unknown weapon";
                return false;
            }

            var state = GetOrCreateServerState(weaponIndex);
            if(shotId == state.LastShotId) {
                return true;
            }

            if(shotId < state.LastShotId) {
                reason = "shot id rewind";
                return false;
            }

            var config = AntiCheatConfig.Instance;
            var now = Time.time;
            var grace = config != null ? config.fireRateGraceSeconds : 0f;
            if(state.LastShotTime > 0f) {
                var minInterval = Mathf.Max(0.01f, data.fireRate - grace);
                if(now - state.LastShotTime < minInterval) {
                    reason = "firing too fast";
                    return false;
                }
            }

            if(state.ServerAmmo <= 0) {
                reason = "no ammo";
                return false;
            }

            state.ServerAmmo = Mathf.Max(0, state.ServerAmmo - 1);
            state.LastShotTime = now;
            state.LastShotId = shotId;
            return true;
        }

        public void ReportAmmoSync(int weaponIndex, int newAmmo) {
            if(!IsServer) {
                ReportAmmoSyncServerRpc(weaponIndex, newAmmo);
                return;
            }

            UpdateServerAmmo(weaponIndex, newAmmo);
        }

        [Rpc(SendTo.Server)]
        private void ReportAmmoSyncServerRpc(int weaponIndex, int newAmmo) {
            UpdateServerAmmo(weaponIndex, newAmmo);
        }

        private void UpdateServerAmmo(int weaponIndex, int ammo) {
            if(!IsServer) return;
            var data = GetWeaponDataByIndex(weaponIndex);
            if(data == null) return;
            var clamped = Mathf.Clamp(ammo, 0, data.magSize);
            var state = GetOrCreateServerState(weaponIndex);
            state.ServerAmmo = clamped;
        }
    }
}
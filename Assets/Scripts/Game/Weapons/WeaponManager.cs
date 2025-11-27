using System;
using System.Collections;
using System.Collections.Generic;
using Game.Player;
using Network.AntiCheat;
using Network.Singletons;
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
        private Coroutine _pullOutTimeoutCoroutine;
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
        }

        public void InitializeWeapons() {
            if(CurrentWeapon == null) {
                Debug.LogError("[WeaponManager] Weapon component not assigned!");
                return;
            }

            BuildEquippedWeaponList();
            SetupHolsteredWeaponModels();

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

                var bobHolder = new GameObject("BobHolder");
                bobHolder.AddComponent<WeaponBob>();
                var swayHolder = new GameObject("SwayHolder");
                swayHolder.AddComponent<WeaponSway>();
                swayHolder.transform.SetParent(bobHolder.transform, false);
                swayHolder.transform.localPosition = Vector3.zero;
                swayHolder.transform.localEulerAngles = Vector3.zero;
                bobHolder.transform.SetParent(_fpCamera.transform, false);
                bobHolder.transform.localPosition = Vector3.zero;
                bobHolder.transform.localEulerAngles = Vector3.zero;

                var fpWeaponInstance = Instantiate(data.weaponPrefab, swayHolder.transform, false);
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
                var hopballController = playerController?.HopballController;
                if(hopballController != null) {
                    if(hopballController.IsHoldingHopball) {
                        isHoldingHopball = true;
                        // Drop hopball when switching weapons (let weapon switch visuals handle showing)
                        hopballController.DropHopball(HopballController.HopballDropReason.WeaponSwitch);
                    }

                    // Check if restoring after dissolve
                    if(HopballController.IsRestoringAfterDissolve) {
                        isRestoringAfterDissolve = true;
                    }
                }
            }

            // Block switching to same weapon unless holding hopball or restoring after dissolve
            if(newIndex == CurrentWeaponIndex && !isHoldingHopball && !isRestoringAfterDissolve)
                return;

            if(IsOwner) {
                SoundFXManager.Instance?.PlayUISound(SfxKey.WeaponSwitch);
            }

            // Cache ammo from current weapon before switching away
            if(CurrentWeapon != null && CurrentWeaponIndex >= 0) {
                _weaponAmmo[CurrentWeaponIndex] = CurrentWeapon.currentAmmo;
            }

            // Immediately hide current weapon (no sheath delay)
            if(CurrentWeaponIndex >= 0) {
                // Hide FP weapon
                var oldFp = _fpWeaponInstances[CurrentWeaponIndex];
                oldFp?.SetActive(false);

                // Hide 3P weapon
                var oldName = weaponDataList[CurrentWeaponIndex].worldWeaponName;
                if(_worldWeaponSocket != null && !string.IsNullOrEmpty(oldName)) {
                    var oldObj = _worldWeaponSocket.Find(oldName);
                    if(oldObj)
                        oldObj.gameObject.SetActive(false);
                }
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
            // Add a timeout fallback in case animation doesn't exist or event doesn't fire
            IsPullingOut = true;

            // Stop any existing timeout coroutine
            if(_pullOutTimeoutCoroutine != null) {
                StopCoroutine(_pullOutTimeoutCoroutine);
            }

            // Start timeout coroutine (fallback if animation event doesn't fire)
            // Typical pull out animations are 0.3-0.5 seconds, so 1 second is a safe timeout
            _pullOutTimeoutCoroutine = StartCoroutine(PullOutTimeoutCoroutine(1f));

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
        }

        /// <summary>
        /// Called when the pull-out animation completes (via animation event).
        /// Allows shooting and reloading again.
        /// </summary>
        public void HandlePullOutCompleted() {
            if(_pullOutTimeoutCoroutine != null) {
                StopCoroutine(_pullOutTimeoutCoroutine);
                _pullOutTimeoutCoroutine = null;
            }

            IsPullingOut = false;
        }

        /// <summary>
        /// Fallback timeout coroutine in case pull out animation doesn't exist or event doesn't fire.
        /// </summary>
        private IEnumerator PullOutTimeoutCoroutine(float timeout) {
            yield return new WaitForSeconds(timeout);
            if(!IsPullingOut) yield break;
            // Timeout reached, clear pull out state
            IsPullingOut = false;
            _pullOutTimeoutCoroutine = null;
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
                playerShadow.SetWorldWeaponRenderersShadowMode(targetMode, true);
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

            if(CurrentWeaponIndex >= 0 && CurrentWeaponIndex < weaponDataList.Count) {
                var previousName = weaponDataList[CurrentWeaponIndex].worldWeaponName;
                if(_worldWeaponSocket != null && !string.IsNullOrEmpty(previousName)) {
                    var previousObj = _worldWeaponSocket.Find(previousName);
                    if(previousObj) {
                        previousObj.gameObject.SetActive(false);
                    }
                }
            }

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

        public Weapon GetWeaponByIndex(int index) {
            if(index == CurrentWeaponIndex) return CurrentWeapon;
            return null;
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

            var renderers = fpWeapon.GetComponentsInChildren<Renderer>(true);
            foreach(var r in renderers) {
                r.enabled = visible;
            }
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

        private int GetSlotForIndex(int index) {
            var data = GetWeaponDataByIndex(index);
            if(data == null) return -1;
            return ResolveWeaponSlot(data, index);
        }

        public void SetHolsterShadowMode(int slot, ShadowCastingMode mode) {
            switch(slot) {
                case 0:
                    SetHolsterShadowModeForSlot(_selectedSecondaryHolster, mode);
                    break;
                case 1:
                    SetHolsterShadowModeForSlot(_selectedPrimaryHolster, mode);
                    break;
                default:
                    SetHolsterShadowModeForSlot(_selectedPrimaryHolster, mode);
                    SetHolsterShadowModeForSlot(_selectedSecondaryHolster, mode);
                    break;
            }
        }

        private static void SetHolsterShadowModeForSlot(GameObject holster, ShadowCastingMode mode) {
            if(holster == null) return;
            var renderers = holster.GetComponentsInChildren<MeshRenderer>(true);
            foreach(var renderer in renderers) {
                if(renderer == null) continue;
                renderer.shadowCastingMode = mode;
            }
        }

        #endregion

        private void BuildEquippedWeaponList() {
            weaponDataList = new List<WeaponData>();
            weaponDataList.Clear();

            var primary = GetWeaponFromOptions(primaryWeaponOptions, PlayerPrefs.GetInt("PrimaryWeaponIndex", 0),
                "Primary");
            if(primary != null) {
                weaponDataList.Add(primary);
            }

            var secondary = GetWeaponFromOptions(secondaryWeaponOptions, PlayerPrefs.GetInt("SecondaryWeaponIndex", 0),
                "Secondary");
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
            var grace = config?.fireRateGraceSeconds ?? 0f;
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
using System;
using System.Collections;
using System.Collections.Generic;
using Game.Player;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

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

        private readonly List<GameObject> _fpWeaponInstances = new();
        private readonly Dictionary<int, int> _weaponAmmo = new();
        private Coroutine _pullOutTimeoutCoroutine;
        private GameObject _pendingTpWeapon; // Track pending TP weapon to show via animation event

        public Weapon CurrentWeapon { get; private set; }

        public int CurrentWeaponIndex { get; private set; } = -1;

        public int WeaponCount => weaponDataList.Count;
        public bool IsPullingOut { get; private set; }

        private static readonly int PullOutHash = Animator.StringToHash("PullOut");
        private static readonly int weaponIndex = Animator.StringToHash("WeaponIndex");

        private void Awake() {
            playerController ??= GetComponent<PlayerController>();

            CurrentWeapon ??= playerController.WeaponComponent;
            
            _fpCamera ??= playerController.FpCamera;
            
            _worldWeaponSocket ??= playerController.WorldWeaponSocket;
            
            _playerAnimator ??= playerController.PlayerAnimator;
            
            _weaponCameraController ??= playerController.WeaponCameraController;
            
        }

        public void InitializeWeapons() {
            if(CurrentWeapon == null) {
                Debug.LogError("[WeaponManager] Weapon component not assigned!");
                return;
            }

            BuildEquippedWeaponList();

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
                    meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }

                if(IsOwner) {
                    // Owner: Use Weapon layer (rendered by separate weapon camera above all geometry)
                    var weaponLayer = LayerMask.NameToLayer("Weapon");
                    SetGameObjectAndChildrenLayer(fpWeaponInstance, weaponLayer);
                    Debug.LogWarning($"[WeaponManager] InitializeWeapons - Set FP weapon {i} ({data.weaponName}) to Weapon layer ({weaponLayer}). Instance active: {fpWeaponInstance.activeSelf}, Layer: {fpWeaponInstance.layer}");
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
        }


        public void SwitchWeapon(int newIndex) {
            if(newIndex < 0 || newIndex >= weaponDataList.Count)
                return;

            // Check if holding hopball - if so, allow switching even to same weapon
            // Also check if restoring after dissolve to allow switch
            var isHoldingHopball = false;
            var isRestoringAfterDissolve = false;
            if(IsOwner) {
                var playerController = GetComponent<PlayerController>();
                if(playerController != null) {
                    var hopballController = playerController.HopballController;
                    if(hopballController != null) {
                        if(hopballController.IsHoldingHopball) {
                            isHoldingHopball = true;
                            // Drop hopball when switching weapons (let weapon switch visuals handle showing)
                            hopballController.DropHopball(HopballController.HopballDropReason.WeaponSwitch);
                        }
                        // Check if restoring after dissolve
                        if(hopballController.IsRestoringAfterDissolve) {
                            isRestoringAfterDissolve = true;
                        }
                    }
                }
            }

            // Block switching to same weapon unless holding hopball or restoring after dissolve
            if(newIndex == CurrentWeaponIndex && !isHoldingHopball && !isRestoringAfterDissolve)
                return;

            // Cache ammo from current weapon before switching away
            if(CurrentWeaponIndex >= 0 && CurrentWeapon != null) {
                _weaponAmmo[CurrentWeaponIndex] = CurrentWeapon.currentAmmo;
            }

            // Immediately hide current weapon (no sheath delay)
            if(CurrentWeaponIndex >= 0) {
                // Hide FP weapon
                var oldFp = _fpWeaponInstances[CurrentWeaponIndex];
                if(oldFp != null)
                    oldFp.SetActive(false);

                // Hide 3P weapon
                var oldName = weaponDataList[CurrentWeaponIndex].worldWeaponName;
                if(!string.IsNullOrEmpty(oldName) && _worldWeaponSocket != null) {
                    var oldObj = _worldWeaponSocket.Find(oldName);
                    if(oldObj)
                        oldObj.gameObject.SetActive(false);
                }
            }

            // Commit to new weapon index immediately
            CurrentWeaponIndex = newIndex;
            var data = weaponDataList[CurrentWeaponIndex];

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
            GameObject worldWeaponInstance = null;
            if(!string.IsNullOrEmpty(data.worldWeaponName) && _worldWeaponSocket != null) {
                var worldObj = _worldWeaponSocket.Find(data.worldWeaponName);
                if(worldObj) {
                    worldWeaponInstance = worldObj.gameObject;
                    // Store reference but don't activate yet - will be activated by animation event
                    _pendingTpWeapon = worldWeaponInstance;
                    worldWeaponInstance.SetActive(false); // Ensure it's hidden
                }
            }

            // Restore ammo (fallback to mag size if somehow missing)
            var restoredAmmo = data.magSize;
            if(_weaponAmmo.TryGetValue(CurrentWeaponIndex, out var storedAmmo)) {
                restoredAmmo = storedAmmo;
            }

            // Update weapon data immediately (no waiting for animations)
            // Pass null for worldWeaponInstance since it's not shown yet - will be set when TP weapon is shown
            CurrentWeapon.SwitchToWeapon(
                data,
                fp,
                null, // Will be set when TP weapon is shown via animation event
                restoredAmmo
            );

            // Set pulling out state
            // The pull out animation will call HandlePullOutCompleted() when done
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
            if(_playerAnimator != null) {
                _playerAnimator.SetInteger(weaponIndex, newIndex);
                // Trigger TP pull out animation
                _playerAnimator.SetTrigger(PullOutHash);
            }
        }

        /// <summary>
        /// Called from player animation event to show the TP weapon during pull out animation.
        /// </summary>
        public void ShowTpWeapon() {
            if(_pendingTpWeapon != null) {
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
                
                _pendingTpWeapon = null;
            }
        }

        /// <summary>
        /// Called when the pull out animation completes (via animation event).
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
            if(IsPullingOut) {
                // Timeout reached, clear pull out state
                IsPullingOut = false;
                _pullOutTimeoutCoroutine = null;
            }
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
            return CurrentWeapon;
        }

        public GameObject GetCurrentFpWeapon() {
            if(CurrentWeaponIndex < 0 || CurrentWeaponIndex >= _fpWeaponInstances.Count) return null;
            return _fpWeaponInstances[CurrentWeaponIndex];
        }

        public void SetCurrentFpWeaponVisible(bool visible) {
            var fpWeapon = GetCurrentFpWeapon();
            if(fpWeapon == null) return;

            var renderers = fpWeapon.GetComponentsInChildren<Renderer>(true);
            foreach(var renderer in renderers) {
                renderer.enabled = visible;
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
                Debug.LogWarning($"[WeaponManager] EquipInitialWeapon - Activated FP weapon {index} ({data.weaponName}). Active: {fp.activeSelf}, Layer: {fp.layer}, ActiveInHierarchy: {fp.activeInHierarchy}");

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
        private void BuildEquippedWeaponList() {
            weaponDataList ??= new List<WeaponData>();
            weaponDataList.Clear();

            var primary = GetWeaponFromOptions(primaryWeaponOptions, PlayerPrefs.GetInt("PrimaryWeaponIndex", 0), "Primary");
            if(primary != null) {
                weaponDataList.Add(primary);
            }

            var secondary = GetWeaponFromOptions(secondaryWeaponOptions, PlayerPrefs.GetInt("SecondaryWeaponIndex", 0), "Secondary");
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
                Debug.LogWarning($"[WeaponManager] {slotLabel} weapon index {storedIndex} out of range. Using {clampedIndex} instead.");
                PlayerPrefs.SetInt($"{slotLabel}WeaponIndex", clampedIndex);
            }

            var weaponData = options[clampedIndex];
            if(weaponData == null) {
                Debug.LogWarning($"[WeaponManager] {slotLabel} weapon at index {clampedIndex} is null.");
            }

            return weaponData;
        }
    }
}
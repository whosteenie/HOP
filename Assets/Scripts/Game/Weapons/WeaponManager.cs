using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapons {
    public class WeaponManager : NetworkBehaviour {
        [Header("Weapon System")] [SerializeField]
        private List<WeaponData> weaponDataList;

        [SerializeField] private Weapon weaponComponent;
        [SerializeField] private CinemachineCamera fpCamera;
        [SerializeField] private Transform worldWeaponSocket;
        [SerializeField] private Animator playerAnimator;

        private int currentWeaponIndex = -1;
        private int _pendingWeaponIndex = -1;
        private bool _isSwitchingWeapon = false;

        private List<GameObject> _fpWeaponInstances = new();
        private Dictionary<int, int> _weaponAmmo = new();

        public Weapon CurrentWeapon => weaponComponent;
        public int CurrentWeaponIndex => currentWeaponIndex;
        public bool IsSwitchingWeapon => _isSwitchingWeapon;
        
        private static readonly int SheatheHash = Animator.StringToHash("Sheathe");
        
        public void InitializeWeapons() {
            if(weaponComponent == null) {
                Debug.LogError("[WeaponManager] Weapon component not assigned!");
                return;
            }

            if(weaponDataList == null || weaponDataList.Count == 0) {
                Debug.LogError("[WeaponManager] weaponDataList is empty!");
                return;
            }

            // Hide all 3P weapons initially
            if(worldWeaponSocket != null) {
                foreach(Transform child in worldWeaponSocket) {
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
                bobHolder.transform.SetParent(fpCamera.transform, false);
                bobHolder.transform.localPosition = Vector3.zero;
                bobHolder.transform.localEulerAngles = Vector3.zero;

                var fpWeaponInstance = Instantiate(data.weaponPrefab, swayHolder.transform, false);
                fpWeaponInstance.transform.localPosition = data.spawnPosition;
                fpWeaponInstance.transform.localEulerAngles = data.spawnRotation;

                var meshRenderers = fpWeaponInstance.GetComponentsInChildren<MeshRenderer>();
                foreach(var meshRenderer in meshRenderers) {
                    meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }

                if(!IsOwner) {
                    fpWeaponInstance.layer = LayerMask.NameToLayer("Masked");
                }

                fpWeaponInstance.SetActive(false);
                _fpWeaponInstances.Add(fpWeaponInstance);

                // Initialize ammo
                _weaponAmmo[i] = data.magSize;
            }

            Debug.Log(
                $"[WeaponManager] Initialized ammo: {string.Join(", ", _weaponAmmo.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");

            // Switch to first weapon
            if(_fpWeaponInstances.Count > 0) {
                EquipInitialWeapon(0);
            } else {
                Debug.LogError("[WeaponManager] No weapons instantiated!");
            }
        }

        public void SwitchWeapon(int newIndex) {
            if(_isSwitchingWeapon || newIndex == currentWeaponIndex)
                return;

            if(newIndex < 0 || newIndex >= weaponDataList.Count)
                return;

            // Cache ammo from current weapon before switching away
            if(currentWeaponIndex >= 0 && weaponComponent != null) {
                _weaponAmmo[currentWeaponIndex] = weaponComponent.currentAmmo;
            }

            _isSwitchingWeapon = true;
            _pendingWeaponIndex = newIndex;

            // **FIX: Send sheathe trigger to EVERYONE immediately**
            if(IsOwner && IsSpawned)
                TriggerSheatheAnimationServerRpc(newIndex);
        }

        [Rpc(SendTo.Server)]
        private void TriggerSheatheAnimationServerRpc(int newIndex) {
            // Broadcast sheathe to ALL clients (including owner)
            TriggerSheatheAnimationClientRpc(newIndex);
            
            // Handle switch logic on server
            SwitchWeaponClientRpc(newIndex);
        }

        [Rpc(SendTo.Everyone)]
        private void TriggerSheatheAnimationClientRpc(int newIndex) {
            // **ALL clients play sheathe animation simultaneously**
            _pendingWeaponIndex = newIndex;
            playerAnimator.SetTrigger(SheatheHash);
        }

        [Rpc(SendTo.NotOwner)]
        private void SwitchWeaponClientRpc(int newIndex) {
            _pendingWeaponIndex = newIndex;
            // Animation already triggered by TriggerSheatheAnimationClientRpc
        }

        public void HandleSheatheCompleted() {
            // 1) Hide old FP + 3P weapon
            if(currentWeaponIndex >= 0) {
                // FP
                var oldFp = _fpWeaponInstances[currentWeaponIndex];
                if(oldFp != null)
                    oldFp.SetActive(false);

                // 3P
                var oldName = weaponDataList[currentWeaponIndex].worldWeaponName;
                if(!string.IsNullOrEmpty(oldName) && worldWeaponSocket != null) {
                    var oldObj = worldWeaponSocket.Find(oldName);
                    if(oldObj)
                        oldObj.gameObject.SetActive(false);
                }
            }

            // 2) Safety check
            if(_pendingWeaponIndex < 0 || _pendingWeaponIndex >= weaponDataList.Count) {
                Debug.LogWarning("[WeaponManager] HandleSheatheCompleted but pending index invalid");
                _isSwitchingWeapon = false;
                _pendingWeaponIndex = -1;
                return;
            }

            // 3) Commit the new current index
            currentWeaponIndex = _pendingWeaponIndex;
            var data = weaponDataList[currentWeaponIndex];

            // 4) Prepare FP instance (but don't show it yet)
            var fp = _fpWeaponInstances[currentWeaponIndex];
            if(fp != null) {
                fp.transform.localPosition = data.spawnPosition;
                fp.transform.localEulerAngles = data.spawnRotation;

                var anim = fp.GetComponent<Animator>();
                if(anim.enabled) {
                    anim.Rebind();
                    anim.Update(0f);
                }
            }

            // 5) Prepare world weapon reference (can be inactive, that's fine)
            GameObject worldWeaponInstance = null;
            if(!string.IsNullOrEmpty(data.worldWeaponName) && worldWeaponSocket != null) {
                var worldObj = worldWeaponSocket.Find(data.worldWeaponName);
                if(worldObj)
                    worldWeaponInstance = worldObj.gameObject;
            }

            // 6) Restore ammo (fallback to mag size if somehow missing)
            int restoredAmmo = data.magSize;
            if(_weaponAmmo.TryGetValue(currentWeaponIndex, out var storedAmmo)) {
                restoredAmmo = storedAmmo;
            }

            // *** This updates weapon data + ammo + HUD right after sheath ***
            weaponComponent.SwitchToWeapon(
                data,
                fp,
                worldWeaponInstance,
                restoredAmmo
            );
        }
        
        public void HandleUnsheatheShowModel() {
            if (currentWeaponIndex < 0 || currentWeaponIndex >= weaponDataList.Count)
                return;

            var data = weaponDataList[currentWeaponIndex];

            // Show 3P model
            if (!string.IsNullOrEmpty(data.worldWeaponName) && worldWeaponSocket != null) {
                var worldObj = worldWeaponSocket.Find(data.worldWeaponName);
                if (worldObj)
                    worldObj.gameObject.SetActive(true);
            }

            // Show FP model
            var fp = _fpWeaponInstances[currentWeaponIndex];
            if (fp != null)
                fp.SetActive(true);
        }

        // Called at first frame of idle
        public void HandleUnsheatheCompleted() {
            if(_isSwitchingWeapon) {
                _isSwitchingWeapon = false;
                _pendingWeaponIndex = -1;
            }
        }

        public void ResetAllWeaponAmmo() {
            _weaponAmmo.Clear();
            for(var i = 0; i < weaponDataList.Count; i++) {
                _weaponAmmo[i] = weaponDataList[i].magSize;
            }
        }

        public Weapon GetWeaponByIndex(int index) {
            return weaponComponent;
        }

        public WeaponData GetWeaponDataByIndex(int index) {
            if(index < 0 || index >= weaponDataList.Count) return null;
            return weaponDataList[index];
        }
        
        public void EquipInitialWeapon(int index) {
            if (index < 0 || index >= weaponDataList.Count) {
                Debug.LogError($"[WeaponManager] EquipInitialWeapon: invalid index {index}");
                return;
            }

            currentWeaponIndex = index;
            _pendingWeaponIndex = -1;
            _isSwitchingWeapon = false;

            var data = weaponDataList[index];

            // ---- FP WEAPON ----
            var fp = _fpWeaponInstances[index];
            if (fp != null) {
                fp.transform.localPosition = data.spawnPosition;
                fp.transform.localEulerAngles = data.spawnRotation;

                var anim = fp.GetComponent<Animator>();
                if (anim) {
                    anim.Rebind();
                    anim.Update(0f);
                }

                fp.SetActive(true);
            }

            // ---- 3P WORLD WEAPON ----
            GameObject worldWeaponInstance = null;
            if (!string.IsNullOrEmpty(data.worldWeaponName) && worldWeaponSocket != null) {
                var worldObj = worldWeaponSocket.Find(data.worldWeaponName);
                if (worldObj != null) {
                    worldWeaponInstance = worldObj.gameObject;
                    worldWeaponInstance.SetActive(true);
                }
            }

            // ---- AMMO ----
            int restoredAmmo = data.magSize;
            if (_weaponAmmo.TryGetValue(index, out var storedAmmo)) {
                restoredAmmo = storedAmmo;
            } else {
                _weaponAmmo[index] = restoredAmmo; // ensure dictionary has an entry
            }

            // This sets weapon data, ammo, HUD, muzzle lights, etc.
            weaponComponent.SwitchToWeapon(
                data,
                fp,
                worldWeaponInstance,
                restoredAmmo
            );
        }
    }
}
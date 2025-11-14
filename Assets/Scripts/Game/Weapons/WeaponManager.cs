using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapons {
    public class WeaponManager : NetworkBehaviour {
        [Header("Weapon System")]
        [SerializeField] private List<WeaponData> weaponDataList;
        [SerializeField] private Weapon weaponComponent;
        [SerializeField] private CinemachineCamera fpCamera;
        [SerializeField] private Transform worldWeaponSocket;
        
        private int currentWeaponIndex = -1;
        
        private List<GameObject> _fpWeaponInstances = new();
        private Dictionary<int, int> _weaponAmmo = new();
        private bool _isSwitchingWeapon = false;

        public Weapon CurrentWeapon => weaponComponent;
        public int CurrentWeaponIndex => currentWeaponIndex;
        public bool IsSwitchingWeapon => _isSwitchingWeapon;
        
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
            
            Debug.Log($"[WeaponManager] Initialized ammo: {string.Join(", ", _weaponAmmo.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
    
            // Switch to first weapon
            if(_fpWeaponInstances.Count > 0) {
                SwitchWeapon(0);
            } else {
                Debug.LogError("[WeaponManager] No weapons instantiated!");
            }
        }

        public void SwitchWeapon(int newIndex) {
            Debug.Log($"[WeaponManager] SwitchWeapon({newIndex}) called. currentIndex={currentWeaponIndex}, isSwitching={_isSwitchingWeapon}");
            if(newIndex < 0 || newIndex >= weaponDataList.Count) {
                Debug.LogError($"[WeaponManager] Invalid weapon index: {newIndex}");
                return;
            }

            if(newIndex >= _fpWeaponInstances.Count) {
                Debug.LogError($"[WeaponManager] FP weapon instance not found for index: {newIndex}");
                return;
            }
            
            if(_isSwitchingWeapon) {
                Debug.LogWarning($"[WeaponManager] Already switching weapons, ignoring request to switch to {newIndex}");
                return;
            }

            // Start the switch coroutine
            StartCoroutine(SwitchWeaponCoroutine(newIndex));
            
            // Network sync the weapon switch for other clients
            if(IsOwner && currentWeaponIndex >= 0 && IsSpawned) {
                SwitchWeaponServerRpc(newIndex);
            }
        }

        [Rpc(SendTo.Server)]
        private void SwitchWeaponServerRpc(int newIndex) {
            // Broadcast to all clients except owner
            SwitchWeaponClientRpc(newIndex);
        }

        [Rpc(SendTo.NotOwner)]
        private void SwitchWeaponClientRpc(int newIndex) {
            // Non-owners just need to show/hide 3P weapons
            if(currentWeaponIndex >= 0 && currentWeaponIndex < weaponDataList.Count) {
                var oldWorldWeaponName = weaponDataList[currentWeaponIndex].worldWeaponName;
                if(!string.IsNullOrEmpty(oldWorldWeaponName) && worldWeaponSocket) {
                    var oldWorldWeapon = worldWeaponSocket.Find(oldWorldWeaponName);
                    if(oldWorldWeapon) {
                        oldWorldWeapon.gameObject.SetActive(false);
                    }
                }
            }

            currentWeaponIndex = newIndex;

            var newWorldWeaponName = weaponDataList[newIndex].worldWeaponName;
            if(!string.IsNullOrEmpty(newWorldWeaponName) && worldWeaponSocket) {
                var worldWeapon = worldWeaponSocket.Find(newWorldWeaponName);
                if(worldWeapon) {
                    worldWeapon.gameObject.SetActive(true);
                }
            }
        }

        private IEnumerator SwitchWeaponCoroutine(int newIndex) {
            _isSwitchingWeapon = true;

            // Hide old weapon
            if(currentWeaponIndex >= 0 && currentWeaponIndex < weaponDataList.Count) {
                var sheathTime = weaponDataList[currentWeaponIndex].sheathTime;
                
                // Save ammo from weapon component (it's valid at this point)
                if(weaponComponent != null && weaponComponent.currentAmmo >= 0) {
                    _weaponAmmo[currentWeaponIndex] = weaponComponent.currentAmmo;
                    Debug.Log($"[WeaponManager] Saved weapon {currentWeaponIndex} ammo: {weaponComponent.currentAmmo}");
                }
                
                // Hide old FP weapon
                if(_fpWeaponInstances[currentWeaponIndex]) {
                    _fpWeaponInstances[currentWeaponIndex].SetActive(false);
                }
                
                // Hide old 3P weapon
                var oldWorldWeaponName = weaponDataList[currentWeaponIndex].worldWeaponName;
                if(!string.IsNullOrEmpty(oldWorldWeaponName) && worldWeaponSocket) {
                    var oldWorldWeapon = worldWeaponSocket.Find(oldWorldWeaponName);
                    if(oldWorldWeapon) {
                        oldWorldWeapon.gameObject.SetActive(false);
                    }
                }

                // Wait for sheath animation
                yield return new WaitForSeconds(sheathTime);
            }

            currentWeaponIndex = newIndex;
            float drawTime = weaponDataList[newIndex].drawTime;
            
            // Get weapon data
            var newWeaponData = weaponDataList[newIndex];
            
            // RESET FP weapon position/rotation BEFORE showing it
            if(_fpWeaponInstances[newIndex]) {
                _fpWeaponInstances[newIndex].transform.localPosition = newWeaponData.spawnPosition;
                _fpWeaponInstances[newIndex].transform.localEulerAngles = newWeaponData.spawnRotation;
                
                // Reset animator state
                var weaponAnimator = _fpWeaponInstances[newIndex].GetComponent<Animator>();
                if(weaponAnimator && weaponAnimator.enabled) {
                    weaponAnimator.Rebind();
                    weaponAnimator.Update(0f);
                }
            }
            
            // Show new 3P weapon
            GameObject worldWeaponInstance = null;
            var newWorldWeaponName = newWeaponData.worldWeaponName;
            if(!string.IsNullOrEmpty(newWorldWeaponName) && worldWeaponSocket) {
                var worldWeapon = worldWeaponSocket.Find(newWorldWeaponName);
                if(worldWeapon) {
                    worldWeapon.gameObject.SetActive(true);
                    worldWeaponInstance = worldWeapon.gameObject;
                }
            }
            
            // Restore ammo (use dictionary value, it's already initialized with magSize)
            var restoredAmmo = _weaponAmmo[newIndex]; // Don't use TryGetValue, we know it exists
            
            Debug.Log($"[WeaponManager] Switching to weapon {newIndex}, restoredAmmo = {restoredAmmo}, dictionary value = {_weaponAmmo[newIndex]}");
            // Switch weapon component BEFORE showing the model (so it has correct data)
            weaponComponent.SwitchToWeapon(
                newWeaponData,
                _fpWeaponInstances[newIndex],
                worldWeaponInstance,
                restoredAmmo
            );
            
            // NOW show the FP weapon (after position reset and data load)
            if(_fpWeaponInstances[newIndex]) {
                _fpWeaponInstances[newIndex].SetActive(true);
            }

            // Wait for draw animation
            yield return new WaitForSeconds(drawTime);

            _isSwitchingWeapon = false;
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
    }
}
using System.Collections.Generic;
using Game.Player;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapons {
    public class WeaponManager : NetworkBehaviour {
        public int currentWeaponIndex;
        private List<Weapon> _equippedWeapons;
        [SerializeField] private List<WeaponData> weaponDataList;

        public Weapon CurrentWeapon {
            get {
                if(_equippedWeapons == null || _equippedWeapons.Count == 0) {
                    Debug.LogWarning("No equipped weapons found.");
                    return null;
                }

                if(currentWeaponIndex < 0 || currentWeaponIndex >= _equippedWeapons.Count) {
                    Debug.LogWarning("Current weapon index is out of range.");
                    return null;
                }

                return _equippedWeapons[currentWeaponIndex];
            }
        }    
    
        private void Awake() {
            _equippedWeapons = new List<Weapon>();
        }

        public void InitializeWeapons(CinemachineCamera cam, PlayerController controller) {
            _equippedWeapons ??= new List<Weapon>();
        
            _equippedWeapons.Clear();
        
            foreach(var data in weaponDataList) {
                var weapon = gameObject.AddComponent<Weapon>();
            
                var weaponInstance = Instantiate(data.weaponPrefab, cam.transform, false);
                weaponInstance.transform.localPosition = data.spawnPosition;
                weaponInstance.transform.localEulerAngles = data.spawnRotation;

                var muzzleInstance = Instantiate(data.muzzlePrefab, weaponInstance.transform, false);
                muzzleInstance.transform.localPosition = data.positionMuzzle;

                weapon.Initialize(data);

                weapon.weaponPrefab = weaponInstance;
                weapon.weaponMuzzle = muzzleInstance;
            
                var meshRenderers = weaponInstance.GetComponentsInChildren<MeshRenderer>();
                foreach(var meshRenderer in meshRenderers) {
                    meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
            
                if(data.weaponSlot != 0 || !IsOwner) {
                    weaponInstance.gameObject.SetActive(false);
                }

                if(!IsOwner) {
                    weapon.weaponPrefab.layer = LayerMask.NameToLayer("Masked");
                }
            
                weapon.BindAndResolve(cam, controller);
                _equippedWeapons.Add(weapon);
            }
        }
    }
}

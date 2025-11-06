using System.Collections.Generic;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

public class WeaponManager : NetworkBehaviour {
    public int currentWeaponIndex;
    private List<Weapon> _equippedWeapons;
    [SerializeField] private List<WeaponData> weaponDataList;

    public Weapon CurrentWeapon {
        get {
            if(_equippedWeapons == null || _equippedWeapons.Count == 0)
                return null;

            if(currentWeaponIndex < 0 || currentWeaponIndex >= _equippedWeapons.Count)
                return null;

            return _equippedWeapons[currentWeaponIndex];
        }
    }    
    
    private void Awake() {
        _equippedWeapons = new List<Weapon>();
    }

    public void InitializeWeapons(CinemachineCamera cam, PlayerController controller, HUDManager hud) {
        _equippedWeapons ??= new List<Weapon>();
        
        _equippedWeapons.Clear();
        
        foreach(var data in weaponDataList) {
            var weapon = gameObject.AddComponent<Weapon>();
            
            var weaponInstance = Instantiate(data.weaponPrefab, cam.transform, false);
            weaponInstance.transform.localPosition = data.positionSpawn;
            weaponInstance.transform.localEulerAngles = data.rotationSpawn;

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
            
            weapon.BindAndResolve(cam, controller, this, hud);
            _equippedWeapons.Add(weapon);
        }
    }
}

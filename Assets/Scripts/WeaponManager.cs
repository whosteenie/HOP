using System.Collections.Generic;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

public class WeaponManager : NetworkBehaviour {
    public int currentWeaponIndex;
    private List<Weapon> _equippedWeapons;
    [SerializeField] private List<WeaponData> weaponDataList;

    public Weapon CurrentWeapon => _equippedWeapons[currentWeaponIndex];

    private void Awake() {
        _equippedWeapons = new List<Weapon>();
    }

    public void InitializeWeapons(CinemachineCamera cam, FpController controller, HUDManager hud) {
        _equippedWeapons.Clear();
        
        foreach(var data in weaponDataList) {
            var weapon = gameObject.AddComponent<Weapon>();
            weapon.Initialize(data);
            
            // add prefab to camera as child
            var weaponPrefab = Instantiate(data.weaponPrefab).transform;
            weaponPrefab.SetParent(cam.transform);
            weaponPrefab.localPosition = data.positionSpawn;
            weaponPrefab.localEulerAngles = data.rotationSpawn;

            var muzzlePrefab = Instantiate(data.muzzlePrefab).transform;
            muzzlePrefab.SetParent(weaponPrefab.transform);
            muzzlePrefab.localPosition = data.positionMuzzle;

            if(data.weaponSlot != 0) {
                weaponPrefab.gameObject.SetActive(false);
            }
            
            weapon.BindAndResolve(cam, controller, this, hud);
            _equippedWeapons.Add(weapon);
        }
    }
}

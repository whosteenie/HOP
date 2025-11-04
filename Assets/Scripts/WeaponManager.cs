using System.Collections.Generic;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Serialization;

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
            weapon.BindAndResolve(cam, controller, this, hud);
            _equippedWeapons.Add(weapon);
        }
    }
}

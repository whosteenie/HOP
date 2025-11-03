using System.Collections.Generic;
using UnityEngine;

public class WeaponManager : MonoBehaviour {
    public int currentWeapon;
    public List<Weapon> equippedWeapons;
    [SerializeField] private List<WeaponData> weaponDataList;
    
    private void Awake() {
        equippedWeapons = new List<Weapon>();

        foreach(var data in weaponDataList) {
            var weapon = gameObject.AddComponent<Weapon>();
            weapon.Initialize(data);
            equippedWeapons.Add(weapon);
        }
    }
}

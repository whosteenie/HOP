using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

public class WeaponManager : MonoBehaviour {
    public int currentWeaponIndex;
    public List<Weapon> equippedWeapons;
    [SerializeField] private List<WeaponData> weaponDataList;
    
    public Weapon CurrentWeapon => equippedWeapons[currentWeaponIndex];

    private void Awake() {
        equippedWeapons = new List<Weapon>();

        foreach(var data in weaponDataList) {
            var weapon = gameObject.AddComponent<Weapon>();
            weapon.Initialize(data);
            equippedWeapons.Add(weapon);
        }
    }
}

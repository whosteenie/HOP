using UnityEngine;

namespace Game.Weapons {
    [DisallowMultipleComponent]
    public class WorldWeaponBinding : MonoBehaviour {
        [SerializeField] private WeaponData weaponData;

        public WeaponData WeaponData => weaponData;
    }
}

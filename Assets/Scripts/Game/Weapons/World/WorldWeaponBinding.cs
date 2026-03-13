using UnityEngine;

namespace Game.Weapons.World {
    [DisallowMultipleComponent]
    public class WorldWeaponBinding : MonoBehaviour {
        [SerializeField] private WeaponData weaponData;
        [Header("Runtime References")]
        [Tooltip("Required. Explicit muzzle transform used for strict world muzzle sampling.")]
        [SerializeField] private Transform muzzleTransform;
        [Tooltip("Optional. Muzzle light object toggled during firing.")]
        [SerializeField] private GameObject muzzleLightObject;

        public WeaponData WeaponData => weaponData;
        public Transform MuzzleTransform => muzzleTransform;
        public GameObject MuzzleLightObject => muzzleLightObject;

        public bool TryGetRuntimeReferences(out Transform muzzle, out GameObject muzzleLight) {
            muzzle = muzzleTransform;
            muzzleLight = muzzleLightObject;
            return muzzle != null;
        }
    }
}

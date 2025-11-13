using UnityEngine;

namespace Game.Weapons {
    [CreateAssetMenu(fileName = "New Weapon", menuName = "Weapon Data")]
    public class WeaponData : ScriptableObject {
        [Header("Identity")]
        public GameObject weaponPrefab; // FP weapon model
        public int weaponSlot;
        
        [Header("FP Weapon Transform")]
        public Vector3 spawnPosition; // FP weapon position relative to camera
        public Vector3 spawnRotation; // FP weapon rotation
        public Vector3 fpMuzzleLocalPosition; // Muzzle position relative to FP weapon
        
        [Header("3P Weapon (Pre-placed on character)")]
        public string worldWeaponName; // Name of the 3P weapon GameObject on character
        public Vector3 worldMuzzleLocalPosition; // Muzzle position relative to 3P weapon
        
        [Header("Muzzle Lights (Optional)")]
        public string fpMuzzleLightChildName = "MuzzleLight";
        public string worldMuzzleLightChildName = "MuzzleLight";
    
        [Header("Ammo")]
        public int magSize;
        public int currentAmmo;
    
        [Header("Damage")]
        public int baseDamage;
        public float maxDamageMultiplier;
        public float damageCap;

        [Header("Fire Properties")]
        public float fireRate;
        public string fireMode;
        public float bulletSpread;
    
        [Header("Reload")]
        public float reloadTime;
        
        [Header("Switching")]
        public float sheathTime;
        public float drawTime;
        
        [Header("Visuals")]
        public TrailRenderer bulletTrail;
        public ParticleSystem bulletImpact;
        public GameObject muzzleFlashPrefab;
    }
}
using UnityEngine;

namespace Game.Weapons {
    [CreateAssetMenu(fileName = "New Weapon", menuName = "Weapon Data")]
    public class WeaponData : ScriptableObject {
        [Header("Identity")]
        public string weaponName;
        public GameObject weaponPrefab; // FP weapon model

        public int weaponSlot;

        [Header("FP Weapon Transform")]
        public Vector3 spawnPosition; // FP weapon position relative to camera

        public Vector3 spawnRotation; // FP weapon rotation
        public Vector3 muzzleLocalOffset; // Muzzle position offset (shared for FP & world models)

        [Header("3P Weapon (Pre-placed on character)")]
        public string worldWeaponName; // Name of the 3P weapon GameObject on character
        [Tooltip("Optional holstered primary model reference (if different from worldWeaponName).")]
        public GameObject holsteredPrimaryModel;
        [Tooltip("Optional holstered secondary model reference (if different from worldWeaponName).")]
        public GameObject holsteredSecondaryModel;

        [Header("Muzzle Lights (Optional)")]
        public string fpMuzzleLightChildName = "MuzzleLight";

        public string worldMuzzleLightChildName = "MuzzleLight";

        [Header("Ammo")]
        public int magSize;

        [Header("Damage")]
        public float baseDamage;

        public float maxDamageMultiplier;
        public float damageCap;

        [Header("Damage Falloff")]
        public bool useDamageFalloff;
        [Tooltip("Distance (meters) at which damage begins to fall off. Full damage at or below this range.")]
        public float maxDamageRange = 15f;
        [Tooltip("Distance (meters) at which damage reaches minimum value.")]
        public float minDamageRange = 60f;
        [Tooltip("Minimum damage value applied beyond minDamageRange.")]
        public float minDamage = 5f;

        [Header("Shotgun Settings")]
        public bool usePelletSpread;
        [Tooltip("Number of pellets to fire per shot when using pellet spread.")]
        public int pelletCount = 8;
        [Tooltip("Scales base damage per pellet (e.g., 0.2 means each pellet does 20% of base damage).")]
        public float pelletDamageMultiplier = 0.15f;

        [Header("Fire Properties")]
        public float fireRate;

        public string fireMode;
        public float bulletSpread;

        [Header("Aiming")]
        [Tooltip("If true, shows the sniper overlay when using Zoom input.")]
        public bool useSniperOverlay;

        [Header("Reload")]
        public float reloadTime;
        [Tooltip("If true, reloading fills the entire magazine at once. If false, ammo is refilled one round at a time.")]
        public bool useMagReload = true;
        [Tooltip("Time between loading individual rounds when not using a full-mag reload.")]
        public float perRoundReloadTime = 0.5f;

        [Header("Visuals")]
        public TrailRenderer bulletTrail;
        public ParticleSystem bulletImpact;
        public GameObject muzzleFlashPrefab;

        [Header("Audio")]
        public SfxKey shootSfx = SfxKey.Shoot;
        public SfxKey reloadSfx = SfxKey.Reload;

        [Header("Server Validation")]
        [Tooltip("Maximum range (meters) used when validating hits server-side.")]
        public float maxServerRange = 150f;
    }
}
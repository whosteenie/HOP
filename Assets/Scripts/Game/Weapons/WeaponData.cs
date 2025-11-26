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

        [Header("Muzzle Lights (Optional)")]
        public string fpMuzzleLightChildName = "MuzzleLight";

        public string worldMuzzleLightChildName = "MuzzleLight";

        [Header("Ammo")]
        public int magSize;

        public int currentAmmo;

        [Header("Damage")]
        public float baseDamage;

        public float maxDamageMultiplier;
        public float damageCap;

        [Header("Damage Falloff")]
        public bool useDamageFalloff = false;
        [Tooltip("Distance (meters) at which damage begins to fall off. Full damage at or below this range.")]
        public float maxDamageRange = 15f;
        [Tooltip("Distance (meters) at which damage reaches minimum value.")]
        public float minDamageRange = 60f;
        [Tooltip("Minimum damage value applied beyond minDamageRange.")]
        public float minDamage = 5f;

        [Header("Shotgun Settings")]
        public bool usePelletSpread = false;
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
        public bool useSniperOverlay = false;

        [Header("Reload")]
        public float reloadTime;
        [Tooltip("If true, reloading fills the entire magazine at once. If false, ammo is refilled one round at a time.")]
        public bool useMagReload = true;
        [Tooltip("Time between loading individual rounds when not using a full-mag reload.")]
        public float perRoundReloadTime = 0.5f;

        [Header("Switching")]
        public float sheathTime;

        public float drawTime;

        [Header("Visuals")]
        public TrailRenderer bulletTrail;

        public ParticleSystem bulletImpact;
        public GameObject muzzleFlashPrefab;

        [Header("Audio")]
        public SfxKey shootSfx = SfxKey.Shoot;
        public SfxKey reloadSfx = SfxKey.Reload;
    }
}
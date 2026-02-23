using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Weapons {
    [CreateAssetMenu(fileName = "New Weapon", menuName = "Weapon Data")]
    public class WeaponData : ScriptableObject {
        public enum WeaponSlotType {
            Primary = 0,
            Secondary = 1
        }

        public enum FireModeType {
            Semi = 0,
            Full = 1
        }

        [Header("Core")]
        public string weaponName;
        public WeaponSlotType weaponSlot = WeaponSlotType.Primary;
        public Sprite loadoutIcon;
        public GameObject weaponPrefab; // FP weapon model

        [Header("Firing")]
        [InspectorName("Fire Mode")] public FireModeType fireModeType = FireModeType.Semi;
        public float fireRate;
        public float bulletSpread;
        [Tooltip("If true, shows the sniper overlay when using Zoom input.")]
        public bool useSniperOverlay;

        [Header("Damage")]
        public float baseDamage;
        public float damageCap;
        public bool useDamageFalloff;
        [Tooltip("Distance (meters) at which damage begins to fall off. Full damage at or below this range.")]
        public float maxDamageRange = 15f;
        [Tooltip("Distance (meters) at which damage reaches minimum value.")]
        public float minDamageRange = 60f;
        [Tooltip("Minimum damage value applied beyond minDamageRange.")]
        public float minDamage = 5f;
        public bool usePelletSpread;
        [Tooltip("Number of pellets to fire per shot when using pellet spread.")]
        public int pelletCount = 8;
        [Tooltip("Scales base damage per pellet (e.g., 0.2 means each pellet does 20% of base damage).")]
        public float pelletDamageMultiplier = 0.15f;

        [Header("Reload")]
        public int magSize;
        public float reloadTime;
        [Tooltip("If true, reloading fills the entire magazine at once. If false, ammo is refilled one round at a time.")]
        public bool useMagReload = true;
        [Tooltip("Time between loading individual rounds when not using a full-mag reload.")]
        public float perRoundReloadTime = 0.5f;

        [Header("Hit Registration")]
        [Tooltip("If true, uses a hybrid ray+sphere cast for more forgiving hit detection.")]
        public bool useSphereCast;
        [Tooltip("Base radius of the hit sphere at muzzle (meters).")]
        public float sphereCastRadius = 0.05f;
        [Tooltip("Distance (meters) at which the sphere starts growing from base radius.")]
        public float sphereCastGrowthStartDist;
        [Tooltip("Maximum radius of the hit sphere at growth end distance.")]
        public float sphereCastMaxRadius = 0.3f;

        [Header("Presentation & FX")]
        public Vector3 spawnPosition; // FP weapon position relative to camera
        public Vector3 spawnRotation; // FP weapon rotation
        public TrailRenderer bulletTrail;
        public ParticleSystem bulletImpact;
        public GameObject muzzleFlashPrefab;

        [Header("Refactor Candidates")]
        [Tooltip("SoundCatalog id used when firing this weapon (e.g. 'weapons.pistol.shoot').")]
        public string shootSoundId = "";
        [Tooltip("SoundCatalog id used when reloading this weapon (e.g. 'weapons.pistol.reload').")]
        public string reloadSoundId = "";

        [SerializeField, HideInInspector, FormerlySerializedAs("fireMode")]
        private string legacyFireMode = "";

        public int WeaponSlotIndex => (int)weaponSlot;

        private void OnValidate() {
            MigrateLegacyFireMode();

            if(!System.Enum.IsDefined(typeof(WeaponSlotType), weaponSlot)) {
                weaponSlot = WeaponSlotType.Primary;
            }

            if(!System.Enum.IsDefined(typeof(FireModeType), fireModeType)) {
                fireModeType = FireModeType.Semi;
            }

            magSize = Mathf.Max(1, magSize);

            baseDamage = Mathf.Max(0f, baseDamage);
            damageCap = Mathf.Max(0f, damageCap);

            maxDamageRange = Mathf.Max(0f, maxDamageRange);
            minDamageRange = Mathf.Max(maxDamageRange, minDamageRange);
            minDamage = Mathf.Max(0f, minDamage);

            pelletCount = Mathf.Max(1, pelletCount);
            pelletDamageMultiplier = Mathf.Max(0f, pelletDamageMultiplier);

            fireRate = Mathf.Max(0.01f, fireRate);
            bulletSpread = Mathf.Max(0f, bulletSpread);

            reloadTime = Mathf.Max(0f, reloadTime);
            perRoundReloadTime = Mathf.Max(0.05f, perRoundReloadTime);

            sphereCastRadius = Mathf.Max(0f, sphereCastRadius);
            sphereCastGrowthStartDist = Mathf.Max(0f, sphereCastGrowthStartDist);
            sphereCastMaxRadius = Mathf.Max(sphereCastRadius, sphereCastMaxRadius);
        }

        private void MigrateLegacyFireMode() {
            if(string.IsNullOrWhiteSpace(legacyFireMode)) {
                return;
            }

            var trimmed = legacyFireMode.Trim();
            if(System.Enum.TryParse(trimmed, true, out FireModeType parsed)) {
                fireModeType = parsed;
            } else if(trimmed.Equals("FullAuto", System.StringComparison.OrdinalIgnoreCase) ||
                      trimmed.Equals("Auto", System.StringComparison.OrdinalIgnoreCase) ||
                      trimmed.Equals("Automatic", System.StringComparison.OrdinalIgnoreCase)) {
                fireModeType = FireModeType.Full;
            } else {
                fireModeType = FireModeType.Semi;
            }

            legacyFireMode = string.Empty;
        }
    }
}

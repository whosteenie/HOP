using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Weapon.Core {
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

        public enum KinemationSpecialHandling {
            Null = 0,
            None = 1,
            DrakeShell = 2,
            KarLoopBullet = 3
        }

        // Matches KinemationFpWeaponDriver GrappleWeaponIndex animation mapping.
        public enum KinemationGrappleWeaponIndex {
            Null = -1,
            Ak = 0,
            M1911 = 1,
            Pdw = 2,
            Kar = 3,
            Drake = 4,
            Dgl = 5
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
        [Tooltip("If true, reloading fills the entire magazine at once. If false, ammo is refilled one round at a time.")]
        public bool useMagReload = true;

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
        public TrailRenderer bulletTrail;
        public ParticleSystem bulletImpact;
        public GameObject muzzleFlashPrefab;

        [Header("Refactor Candidates")]
        [Tooltip("SoundCatalog id used when firing this weapon (e.g. 'weapons.pistol.shoot').")]
        public string shootSoundId = "";
        [Tooltip("SoundCatalog id used when reloading this weapon (e.g. 'weapons.pistol.reload').")]
        public string reloadSoundId = "";

        [Header("KINEMATION")]
        [Tooltip("Required for KINEMATION behavior routing. Use None when no special handling is needed.")]
        public KinemationSpecialHandling kinemationSpecialHandling = KinemationSpecialHandling.Null;
        [Tooltip("Required for grapple animation bucket mapping.")]
        public KinemationGrappleWeaponIndex kinemationGrappleWeaponIndex = KinemationGrappleWeaponIndex.Null;
        [Tooltip("WeaponEventSounds clip indices used by reload actions. Used to stop reload SFX when reload is canceled/interrupted.")]
        public int[] kinemationReloadEventSoundIndices = System.Array.Empty<int>();

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

            baseDamage = Mathf.Max(0f, baseDamage);
            damageCap = Mathf.Max(0f, damageCap);

            maxDamageRange = Mathf.Max(0f, maxDamageRange);
            minDamageRange = Mathf.Max(maxDamageRange, minDamageRange);
            minDamage = Mathf.Max(0f, minDamage);

            pelletCount = Mathf.Max(1, pelletCount);
            pelletDamageMultiplier = Mathf.Max(0f, pelletDamageMultiplier);

            fireRate = Mathf.Max(0.01f, fireRate);
            bulletSpread = Mathf.Max(0f, bulletSpread);

            sphereCastRadius = Mathf.Max(0f, sphereCastRadius);
            sphereCastGrowthStartDist = Mathf.Max(0f, sphereCastGrowthStartDist);
            sphereCastMaxRadius = Mathf.Max(sphereCastRadius, sphereCastMaxRadius);
            NormalizeKinemationReloadSoundIndices();
        }

        private void NormalizeKinemationReloadSoundIndices() {
            if(kinemationReloadEventSoundIndices == null || kinemationReloadEventSoundIndices.Length == 0) {
                kinemationReloadEventSoundIndices = System.Array.Empty<int>();
                return;
            }

            var uniqueNonNegative = new HashSet<int>();
            foreach(var clipIndex in kinemationReloadEventSoundIndices) {
                if(clipIndex < 0) continue;
                uniqueNonNegative.Add(clipIndex);
            }

            if(uniqueNonNegative.Count == 0) {
                kinemationReloadEventSoundIndices = System.Array.Empty<int>();
                return;
            }

            var normalized = new int[uniqueNonNegative.Count];
            uniqueNonNegative.CopyTo(normalized);
            System.Array.Sort(normalized);
            kinemationReloadEventSoundIndices = normalized;
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

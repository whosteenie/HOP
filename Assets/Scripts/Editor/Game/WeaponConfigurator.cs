using Game.Weapons.Core;
using UnityEditor;
using UnityEngine;

namespace Editor.Game {
    public class WeaponConfigurator : EditorWindow {

        [MenuItem("Tools/Apply Weapon HitReg Defaults")]
        public static void ApplyDefaults() {
            var guids = AssetDatabase.FindAssets("t:WeaponData");
            if (guids.Length == 0) {
                Debug.LogWarning("No WeaponData assets found!");
                return;
            }

            foreach (var guid in guids) {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var weapon = AssetDatabase.LoadAssetAtPath<WeaponData>(path);
                if (weapon == null) continue;

                // Simple name match: weaponName or file name
                var name = weapon.name.ToLower();

                if (name.Contains("shotgun")) {
                    ConfigureWeapon(weapon, 
                        radius: 0.05f, 
                        start: 0f, 
                        max: 0.1f, 
                        reason: "Shotgun: Tight spread, little forgiveness.");
                } else if (name.Contains("sniper") || name.Contains("m107")) {
                    ConfigureWeapon(weapon, 
                        radius: 0.02f, 
                        start: 20f, 
                        max: 0.5f, 
                        reason: "Sniper: Pinpoint close, generous at range.");
                } else if (name.Contains("smg") || name.Contains("uzi")) {
                    ConfigureWeapon(weapon, 
                        radius: 0.1f, 
                        start: 5f, 
                        max: 0.25f, 
                        reason: "SMG: Forgiving, cap to avoid crazy shots.");
                } else if (name.Contains("rifle") || name.Contains("ak74")) {
                    ConfigureWeapon(weapon, 
                        radius: 0.1f, 
                        start: 0f, 
                        max: 0.4f, 
                        reason: "Rifle: Consistent tracking forgiveness.");
                } else if (name.Contains("pistol") || name.Contains("m1911")) {
                    ConfigureWeapon(weapon, 
                        radius: 0.1f, 
                        start: 0f, 
                        max: 0.35f, 
                        reason: "Pistol: Good forgiveness for close encounters.");
                } else if (name.Contains("deagle")) {
                    ConfigureWeapon(weapon, 
                        radius: 0.08f, 
                        start: 0f, 
                        max: 0.45f, 
                        reason: "Deagle: High skill, needs forgiveness against erratic movement.");
                }

                EditorUtility.SetDirty(weapon);
            }
            
            AssetDatabase.SaveAssets();
            Debug.Log("Applied Hit Registration Defaults to all WeaponData assets.");
        }

        private static void ConfigureWeapon(WeaponData weapon, float radius, float start, float max, string reason = "") {
            weapon.useSphereCast = true;
            weapon.sphereCastRadius = radius;
            weapon.sphereCastGrowthStartDist = start;
            weapon.sphereCastMaxRadius = max;
            Debug.Log($"Configured {weapon.name}: {reason}");
        }
    }
}

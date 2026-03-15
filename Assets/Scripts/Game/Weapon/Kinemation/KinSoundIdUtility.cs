using System.Text;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;

namespace Game.Weapons.Kinemation {
    public static class KinSoundIdUtility {
        private const string FireSuffix = ".fire";
        private const string EventPrefix = ".event.";

        public static string BuildWeaponSoundKey(FPSWeaponSettings settings, string fallbackName = null) {
            var source = settings != null ? settings.name : fallbackName;
            if(string.IsNullOrWhiteSpace(source)) {
                source = "unknown";
            }

            source = source.Replace("_Settings", string.Empty);
            source = source.Replace(" Settings", string.Empty);
            source = source.Replace("settings", string.Empty);

            var normalized = NormalizeToken(source);
            return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
        }

        public static string BuildFireSoundId(string weaponSoundKey) {
            return $"weapons.kin.{NormalizeToken(weaponSoundKey)}{FireSuffix}";
        }

        public static string BuildEventSoundId(string weaponSoundKey, int clipIndex) {
            return $"weapons.kin.{NormalizeToken(weaponSoundKey)}{EventPrefix}{clipIndex}";
        }

        private static string NormalizeToken(string raw) {
            if(string.IsNullOrWhiteSpace(raw)) {
                return "unknown";
            }

            var sb = new StringBuilder(raw.Length);
            var wroteSeparator = false;
            foreach(var ch in raw) {
                if(char.IsLetterOrDigit(ch)) {
                    sb.Append(char.ToLowerInvariant(ch));
                    wroteSeparator = false;
                    continue;
                }

                if(wroteSeparator) continue;
                sb.Append('.');
                wroteSeparator = true;
            }

            var normalized = sb.ToString().Trim('.');
            return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
        }
    }
}

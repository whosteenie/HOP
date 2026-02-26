using UnityEngine;

namespace Game.Weapons {
    [DisallowMultipleComponent]
    public sealed class KinemationReloadEventRelay : MonoBehaviour {
        [SerializeField] private KinemationFpWeaponDriver driver;

        public void Bind(KinemationFpWeaponDriver value) {
            driver = value;
        }

        private KinemationFpWeaponDriver ResolveDriver() {
            if(driver != null) return driver;
            driver = GetComponentInParent<KinemationFpWeaponDriver>();
            return driver;
        }

        // Animation Event hook
        public void ReloadSingle() {
            var resolved = ResolveDriver();
            if(resolved == null) return;
            resolved.NotifyReloadSingleEvent();
        }

        // Animation Event hook
        public void AmmoEject() {
            var resolved = ResolveDriver();
            if(resolved == null) return;
            resolved.NotifyAmmoEjectEvent();
        }

        // Animation Event hook
        public void ShellShow() {
            var resolved = ResolveDriver();
            if(resolved == null) return;
            resolved.NotifyShellShowEvent();
        }

        // Animation Event hook
        public void ReloadComplete() {
            var resolved = ResolveDriver();
            if(resolved == null) return;
            resolved.NotifyReloadCompleteEvent();
        }

        // Animation Event hook
        public void EquipComplete() {
            var resolved = ResolveDriver();
            if(resolved == null) return;
            resolved.NotifyEquipCompleteEvent();
        }

        // KIN animation-event hook (fire clip timing).
        public void PlayFireSound() {
            var resolved = ResolveDriver();
            if(resolved == null) return;
            resolved.NotifyWeaponFireSoundEvent();
        }

        // KIN animation-event hook (indexed weapon event timing).
        public void PlayWeaponSound(int clipIndex) {
            var resolved = ResolveDriver();
            if(resolved == null) return;
            resolved.NotifyWeaponEventSoundEvent(clipIndex);
        }

        // Aliases for common naming styles.
        public void OnReloadSingle() => ReloadSingle();
        public void OnReloadComplete() => ReloadComplete();
        public void OnEquipComplete() => EquipComplete();
        public void OnPlayFireSound() => PlayFireSound();
        public void OnPlayWeaponSound(int clipIndex) => PlayWeaponSound(clipIndex);
    }
}

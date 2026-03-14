using UnityEngine;

namespace Game.Weapons.Kinemation {
    [DisallowMultipleComponent]
    public sealed class KinReloadEventRelay : MonoBehaviour {
        [SerializeField] private KinFpWeaponDriver driver;

        public void Bind(KinFpWeaponDriver value) {
            driver = value;
        }

        private KinFpWeaponDriver ResolveDriver() {
            if(driver != null) return driver;
            driver = GetComponentInParent<KinFpWeaponDriver>();
            return driver;
        }

        private string BuildSourceTag(string eventName) {
            return $"{eventName}@{name}#{GetInstanceID()}";
        }

        // Animation Event hook
        public void ReloadSingle() {
            var resolved = ResolveDriver();
            if(resolved == null) return;
            resolved.NotifyReloadSingleEvent(BuildSourceTag(nameof(ReloadSingle)));
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

        // Animation Event hook alias used by some KIN clips (e.g. Kar98K).
        public void ShowShell() => ShellShow();

        // Animation Event hook
        public void ReloadComplete() {
            var resolved = ResolveDriver();
            if(resolved == null) return;
            resolved.NotifyReloadCompleteEvent(BuildSourceTag(nameof(ReloadComplete)));
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
        public void OnShellShow() => ShellShow();
        public void OnShowShell() => ShowShell();
        public void OnReloadComplete() => ReloadComplete();
        public void OnEquipComplete() => EquipComplete();
        public void OnPlayFireSound() => PlayFireSound();
        public void OnPlayWeaponSound(int clipIndex) => PlayWeaponSound(clipIndex);
    }
}

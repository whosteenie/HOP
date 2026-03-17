using UnityEngine;

namespace Game.Weapon.Kinemation {
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

        public void ReloadSingle() {
            var resolved = ResolveDriver();
            if(resolved == null) return;
            resolved.NotifyReloadSingleEvent();
        }

        public void AmmoEject() {
            var resolved = ResolveDriver();
            if(resolved == null) return;
            resolved.NotifyAmmoEjectEvent();
        }

        private void ShellShow() {
            var resolved = ResolveDriver();
            if(resolved == null) return;
            resolved.NotifyShellShowEvent();
        }

        // Animation Event hook alias used by some KIN clips (e.g. Kar98K).
        public void ShowShell() => ShellShow();

        public void ReloadComplete() {
            var resolved = ResolveDriver();
            if(resolved == null) return;
            resolved.NotifyReloadCompleteEvent();
        }

        public void EquipComplete() {
            var resolved = ResolveDriver();
            if(resolved == null) return;
            resolved.NotifyEquipCompleteEvent();
        }

        // KIN animation-event hook (indexed weapon event timing).
        public void PlayWeaponSound(int clipIndex) {
            var resolved = ResolveDriver();
            if(resolved == null) return;
            resolved.NotifyWeaponEventSoundEvent(clipIndex);
        }
    }
}

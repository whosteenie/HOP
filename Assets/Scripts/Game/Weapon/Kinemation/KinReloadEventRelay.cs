using System;
using UnityEngine;
// ReSharper disable UnusedMember.Global

namespace Game.Weapon.Kinemation {
    [DisallowMultipleComponent]
    public sealed class KinReloadEventRelay : MonoBehaviour {
        private Action _onReloadSingle;
        private Action _onAmmoEject;
        private Action _onShellShow;
        private Action _onReloadComplete;
        private Action _onEquipComplete;
        private Action<int> _onPlayWeaponSound;

        public void Bind(
            Action onReloadSingle,
            Action onAmmoEject,
            Action onShellShow,
            Action onReloadComplete,
            Action onEquipComplete,
            Action<int> onPlayWeaponSound) {
            _onReloadSingle = onReloadSingle;
            _onAmmoEject = onAmmoEject;
            _onShellShow = onShellShow;
            _onReloadComplete = onReloadComplete;
            _onEquipComplete = onEquipComplete;
            _onPlayWeaponSound = onPlayWeaponSound;
        }

        public void ReloadSingle() {
            _onReloadSingle?.Invoke();
        }

        public void AmmoEject() {
            _onAmmoEject?.Invoke();
        }

        private void ShellShow() {
            _onShellShow?.Invoke();
        }

        // Animation Event hook alias used by some KIN clips (e.g. Kar98K).
        public void ShowShell() => ShellShow();

        public void ReloadComplete() {
            _onReloadComplete?.Invoke();
        }

        public void EquipComplete() {
            _onEquipComplete?.Invoke();
        }

        // KIN animation-event hook (indexed weapon event timing).
        public void PlayWeaponSound(int clipIndex) {
            _onPlayWeaponSound?.Invoke(clipIndex);
        }
    }
}

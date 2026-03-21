using System;
using KINEMATION.FPSAnimationPack.Scripts.Sounds;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Game.Weapon.Kinemation {
    internal static class KinViewmodelUtility {
        public static void SetLayerRecursive(GameObject root, int layer) {
            if(root == null) return;
            root.layer = layer;
            foreach(Transform child in root.transform) {
                SetLayerRecursive(child.gameObject, layer);
            }
        }

        public static void DisableViewmodelShadows(GameObject root) {
            if(root == null) return;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach(var renderer in renderers) {
                if(renderer == null) continue;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        public static void AttachReloadEventRelays(GameObject viewmodelRoot,
            Action onReloadSingle,
            Action onAmmoEject,
            Action onShellShow,
            Action onReloadComplete,
            Action onEquipComplete,
            Action<int> onPlayWeaponSound,
            bool weaponSoundPlaybackDisabled,
            bool disablePlayerSounds) {
            if(viewmodelRoot == null) return;

            var animators = viewmodelRoot.GetComponentsInChildren<Animator>(true);
            foreach(var animator in animators) {
                if(animator == null) continue;
                var relay = animator.GetComponent<KinReloadEventRelay>();
                if(relay == null) relay = animator.gameObject.AddComponent<KinReloadEventRelay>();
                relay.Bind(onReloadSingle, onAmmoEject, onShellShow, onReloadComplete, onEquipComplete,
                    onPlayWeaponSound);
            }

            var weaponSounds = viewmodelRoot.GetComponentsInChildren<FPSWeaponSound>(true);
            foreach(var weaponSound in weaponSounds) {
                if(weaponSound == null) continue;
                var relay = weaponSound.GetComponent<KinReloadEventRelay>();
                if(relay == null) relay = weaponSound.gameObject.AddComponent<KinReloadEventRelay>();
                relay.Bind(onReloadSingle, onAmmoEject, onShellShow, onReloadComplete, onEquipComplete,
                    onPlayWeaponSound);
                if(weaponSoundPlaybackDisabled) Object.Destroy(weaponSound);
            }

            if(!disablePlayerSounds) return;
            var playerSounds = viewmodelRoot.GetComponentsInChildren<FPSPlayerSound>(true);
            foreach(var playerSound in playerSounds) {
                if(playerSound == null) continue;
                if(playerSound.GetComponent<KinPlayerSoundEventRelay>() == null)
                    playerSound.gameObject.AddComponent<KinPlayerSoundEventRelay>();
                Object.Destroy(playerSound);
            }
        }

        public static void EnsureHierarchyActive(GameObject instanceRoot) {
            if(instanceRoot == null) return;
            var parent = instanceRoot.transform;
            while(parent != null) {
                if(!parent.gameObject.activeSelf) {
                    parent.gameObject.SetActive(true);
                }

                parent = parent.parent;
            }
        }
    }
}

using System.Reflection;
using Diagnostics;
using KINEMATION.FPSAnimationPack.Scripts.Camera;
using KINEMATION.FPSAnimationPack.Scripts.Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Weapon.Kinemation {
    /// <summary>Viewmodel lifecycle: create player instance, build runtime settings, disable unneeded components, apply layer/shadows/relays.</summary>
    internal sealed class KinDriverBootstrap {
        private static readonly MethodInfo FpsPlayerSetMovementEnabledMethod =
            typeof(FPSPlayer).GetMethod("SetCharacterControllerMovementEnabled",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsPlayerAllowControllerMovementField =
            typeof(FPSPlayer).GetField("allowCharacterControllerMovement", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly KinFpWeaponDriver _driver;
        private readonly KinDriverAudio _audio;

        public KinDriverBootstrap(KinFpWeaponDriver driver, KinDriverAudio audio) {
            _driver = driver;
            _audio = audio;
        }

        private FPSPlayerSettings RuntimePlayerSettings { get; set; }

        public void InitializeIfNeeded(int renderLayer, GameObject fpsPlayerPrefab, GameObject weaponPrefab,
            bool weaponSoundPlaybackDisabled, bool disableKinemationPlayerSounds,
            System.Action<GameObject, FPSPlayer, Animator> setPlayerInstance) {
            if(_driver.PlayerInstance != null) {
                KinViewmodelUtility.SetLayerRecursive(_driver.PlayerInstance, renderLayer);
                return;
            }

            if(fpsPlayerPrefab == null || weaponPrefab == null) {
                DevLog.LogError("[KinFpWeaponDriver] Missing prefabs. Cannot initialize KINEMATION viewmodel.", _driver);
                return;
            }

            var playerInstance = Object.Instantiate(fpsPlayerPrefab, _driver.transform, false);
            playerInstance.name = "KinemationViewmodel";
            playerInstance.SetActive(false);

            var fpsPlayer = playerInstance.GetComponentInChildren<FPSPlayer>(true);
            if(fpsPlayer == null) {
                DevLog.LogError("[KinFpWeaponDriver] FPSPlayer component missing on KINEMATION player prefab hierarchy.", _driver);
                Object.Destroy(playerInstance);
                return;
            }

            var fpsAnimator = fpsPlayer.GetComponent<Animator>();
            DisableFpsPlayerMovementControl(fpsPlayer);

            BuildRuntimeSettings(fpsPlayer, weaponPrefab);
            setPlayerInstance(playerInstance, fpsPlayer, fpsAnimator);
            _audio.EnsureWeaponAudioSource();

            DisableUnneededComponents(playerInstance);
            KinViewmodelUtility.SetLayerRecursive(playerInstance, renderLayer);
            KinViewmodelUtility.DisableViewmodelShadows(playerInstance);
            KinViewmodelUtility.AttachReloadEventRelays(playerInstance,
                _driver.NotifyReloadSingleEvent,
                _driver.NotifyAmmoEjectEvent,
                _driver.NotifyShellShowEvent,
                _driver.NotifyReloadCompleteEvent,
                _driver.NotifyEquipCompleteEvent,
                _driver.NotifyWeaponEventSoundEvent,
                weaponSoundPlaybackDisabled,
                disableKinemationPlayerSounds);

            if(disableKinemationPlayerSounds && weaponSoundPlaybackDisabled) {
                var sources = playerInstance.GetComponentsInChildren<AudioSource>(true);
                foreach(var s in sources) { if(s != null) s.enabled = false; }
            }

            playerInstance.SetActive(true);
        }

        public void CleanupRuntimeSettings() {
            if(RuntimePlayerSettings == null) return;
            Object.Destroy(RuntimePlayerSettings);
            RuntimePlayerSettings = null;
        }

        private static void DisableFpsPlayerMovementControl(FPSPlayer fpsPlayer) {
            if(fpsPlayer == null) return;
            if(FpsPlayerSetMovementEnabledMethod != null) {
                FpsPlayerSetMovementEnabledMethod.Invoke(fpsPlayer, new object[] { false });
                return;
            }
            FpsPlayerAllowControllerMovementField?.SetValue(fpsPlayer, false);
        }

        private void BuildRuntimeSettings(FPSPlayer fpsPlayer, GameObject weaponPrefab) {
            var sourceSettings = fpsPlayer.playerSettings;
            RuntimePlayerSettings = sourceSettings != null ? Object.Instantiate(sourceSettings) : ScriptableObject.CreateInstance<FPSPlayerSettings>();
            RuntimePlayerSettings.weaponPrefabs = new System.Collections.Generic.List<GameObject> { weaponPrefab };
            fpsPlayer.playerSettings = RuntimePlayerSettings;
        }

        private static void DisableUnneededComponents(GameObject playerInstance) {
            foreach(var c in playerInstance.GetComponentsInChildren<PlayerInput>(true))
                if(c != null) c.enabled = false;
            foreach(var c in playerInstance.GetComponentsInChildren<CharacterController>(true))
                if(c != null) c.enabled = false;
            var cameraAnim = playerInstance.GetComponentInChildren<FPSCameraAnimator>(true);
            if(cameraAnim != null) cameraAnim.enabled = false;
            var cam = playerInstance.GetComponentInChildren<Camera>(true);
            if(cam != null) cam.enabled = false;
            var listener = playerInstance.GetComponentInChildren<AudioListener>(true);
            if(listener != null) listener.enabled = false;
        }
    }
}

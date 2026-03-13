using System.Reflection;
using Game.Weapons.Manager;
using KINEMATION.FPSAnimationPack.Scripts.Camera;
using KINEMATION.FPSAnimationPack.Scripts.Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Weapons.Kinemation {
    /// <summary>Viewmodel lifecycle: create player instance, build runtime settings, disable unneeded components, apply layer/shadows/relays.</summary>
    internal sealed class KinemationDriverBootstrap {
        private static readonly MethodInfo FpsPlayerSetMovementEnabledMethod =
            typeof(FPSPlayer).GetMethod("SetCharacterControllerMovementEnabled",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsPlayerAllowControllerMovementField =
            typeof(FPSPlayer).GetField("allowCharacterControllerMovement", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly KinemationFpWeaponDriver _driver;
        private readonly KinemationDriverAudio _audio;
        private FPSPlayerSettings _runtimePlayerSettings;

        public KinemationDriverBootstrap(KinemationFpWeaponDriver driver, KinemationDriverAudio audio) {
            _driver = driver;
            _audio = audio;
        }

        public FPSPlayerSettings RuntimePlayerSettings => _runtimePlayerSettings;

        public bool InitializeIfNeeded(int renderLayer, GameObject fpsPlayerPrefab, GameObject weaponPrefab,
            bool weaponSoundPlaybackDisabled, bool disableKinemationPlayerSounds,
            System.Action<GameObject, FPSPlayer, Animator> setPlayerInstance) {
            if(_driver.PlayerInstance != null) {
                WeaponFpPresentation.SetLayerRecursive(_driver.PlayerInstance, renderLayer);
                return true;
            }

            if(fpsPlayerPrefab == null || weaponPrefab == null) {
                Debug.LogError("[KinemationFpWeaponDriver] Missing prefabs. Cannot initialize KINEMATION viewmodel.", _driver);
                return false;
            }

            var playerInstance = Object.Instantiate(fpsPlayerPrefab, _driver.transform, false);
            playerInstance.name = "KinemationViewmodel";
            playerInstance.SetActive(false);

            var fpsPlayer = playerInstance.GetComponentInChildren<FPSPlayer>(true);
            if(fpsPlayer == null) {
                Debug.LogError("[KinemationFpWeaponDriver] FPSPlayer component missing on KINEMATION player prefab hierarchy.", _driver);
                Object.Destroy(playerInstance);
                return false;
            }

            var fpsAnimator = fpsPlayer.GetComponent<Animator>();
            DisableFpsPlayerMovementControl(fpsPlayer);

            BuildRuntimeSettings(fpsPlayer, weaponPrefab);
            setPlayerInstance(playerInstance, fpsPlayer, fpsAnimator);
            _audio.EnsureDedicatedWeaponAudioSource();

            DisableUnneededComponents(playerInstance);
            WeaponFpPresentation.SetLayerRecursive(playerInstance, renderLayer);
            WeaponFpPresentation.DisableViewmodelShadows(playerInstance);
            WeaponFpPresentation.AttachReloadEventRelays(playerInstance, _driver, weaponSoundPlaybackDisabled, disableKinemationPlayerSounds);

            if(disableKinemationPlayerSounds && weaponSoundPlaybackDisabled) {
                var sources = playerInstance.GetComponentsInChildren<AudioSource>(true);
                foreach(var s in sources) { if(s != null) s.enabled = false; }
            }

            playerInstance.SetActive(true);
            return true;
        }

        public void CleanupRuntimeSettings() {
            if(_runtimePlayerSettings != null) {
                Object.Destroy(_runtimePlayerSettings);
                _runtimePlayerSettings = null;
            }
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
            _runtimePlayerSettings = sourceSettings != null ? Object.Instantiate(sourceSettings) : ScriptableObject.CreateInstance<FPSPlayerSettings>();
            _runtimePlayerSettings.weaponPrefabs = new System.Collections.Generic.List<GameObject> { weaponPrefab };
            fpsPlayer.playerSettings = _runtimePlayerSettings;
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

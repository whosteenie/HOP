using System.Collections;
using System.Collections.Generic;
using Game.Player;
using Game.Weapons;
using Unity.Netcode;
using UnityEngine;

namespace Game.Systems {
    /// <summary>
    /// Instantiates heavy assets (Player, Weapons) off-screen during the Main Menu
    /// to force shader compilation and texture uploading, reducing lag on first spawn.
    /// </summary>
    public class SilentAssetLoader : MonoBehaviour {
        private const float HiddenY = -5000f;
        private readonly List<GameObject> _tempObjects = new();

        public void StartLoading(NetworkObject playerPrefab, List<GameObject> additionalAssets = null) {
            StartCoroutine(LoadAssetsRoutine(playerPrefab, additionalAssets));
        }

        private IEnumerator LoadAssetsRoutine(NetworkObject playerPrefab, List<GameObject> additionalAssets) {
            if(playerPrefab == null) yield break;

            Debug.Log("[SilentAssetLoader] Starting background asset pre-load...");

            // 0. Instantiate Additional Assets (e.g. Hopball)
            if(additionalAssets != null) {
                foreach(var asset in additionalAssets) {
                    if(asset == null) continue;
                    var instance = Instantiate(asset, new Vector3(0, HiddenY, 0), Quaternion.identity);
                    _tempObjects.Add(instance);
                    yield return null;
                }
            }

            // 1. Instantiate Player Prefab
            var playerInstance = Instantiate(playerPrefab.gameObject, new Vector3(0, HiddenY, 0), Quaternion.identity);
            _tempObjects.Add(playerInstance);

            // Disable components that might interfere
            var audioListeners = playerInstance.GetComponentsInChildren<AudioListener>();
            foreach(var listener in audioListeners) listener.enabled = false;

            var cameras = playerInstance.GetComponentsInChildren<Camera>();
            foreach(var cam in cameras) cam.enabled = false;
            
            // Disable CharacterController to prevent falling/physics cost
            var cc = playerInstance.GetComponent<CharacterController>();
            if(cc) cc.enabled = false;

            // Wait a frame for player to initialize (awake/start)
            yield return null;

            // 2. Get WeaponManager to find weapons
            var playerController = playerInstance.GetComponent<PlayerController>();
            WeaponManager weaponManager = null;
            if(playerController != null) {
                // We might need to access private field via reflection if it's not public, 
                // but PlayerController has a public WeaponManager property or we can GetComponent
                weaponManager = playerInstance.GetComponent<WeaponManager>();
                if(weaponManager == null) {
                    weaponManager = playerInstance.GetComponentInChildren<WeaponManager>(true);
                }
            }

            if(weaponManager != null) {
                // Access private weapon lists via reflection if needed, or if valid serialization, 
                // we can assume they are populated on the prefab.
                // Since WeaponManager uses [SerializeField] private List<WeaponData>, we need to read them.
                // However, deserialization happens on Instantiate.
                
                // Reflection to get the lists
                var type = typeof(WeaponManager);
                var primaryField = type.GetField("primaryWeaponOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var secondaryField = type.GetField("secondaryWeaponOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                var primaryWeapons = primaryField?.GetValue(weaponManager) as List<WeaponData>;
                var secondaryWeapons = secondaryField?.GetValue(weaponManager) as List<WeaponData>;

                var allWeapons = new List<WeaponData>();
                if(primaryWeapons != null) allWeapons.AddRange(primaryWeapons);
                if(secondaryWeapons != null) allWeapons.AddRange(secondaryWeapons);

                // 3. Instantiate Weapons one by one
                foreach(var data in allWeapons) {
                    if(data == null || data.weaponPrefab == null) continue;

                    // Instantiate FP model
                    var weaponInstance = Instantiate(data.weaponPrefab, new Vector3(0, HiddenY, 0), Quaternion.identity);
                    _tempObjects.Add(weaponInstance);
                    
                    // Also instantiate World Model if it exists
                    // Note: World models are usually on the player prefab already, so they might be covered,
                    // but if they are separate prefabs referenced by name (old system) or data, we might miss them.
                    // The current system implies they are children of the player.
                    
                    // Stagger: wait one frame per weapon
                    yield return null;
                }
            }

            // Wait one more frame to ensure everything rendered at least once (even if off-screen/culled, 
            // Unity might still upload textures/shaders if we force active).
            // To truly force shader compilation, objects often need to be in view of a camera, 
            // but just instantiating them forces Awake/OnEnable and texture/mesh upload.
            yield return null;
            yield return null;

            Debug.Log($"[SilentAssetLoader] Pre-load complete. Cleanup {_tempObjects.Count} objects.");

            // 4. Cleanup
            foreach(var obj in _tempObjects) {
                if(obj != null) Destroy(obj);
            }
            _tempObjects.Clear();
            
            // Destroy self
            Destroy(this);
        }
    }
}

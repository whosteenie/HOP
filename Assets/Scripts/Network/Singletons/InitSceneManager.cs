using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network.Singletons {
    /// <summary>
    /// Manages the initialization scene that contains all DDOL singletons.
    /// This scene loads first, initializes all persistent managers, then transitions to MainMenu.
    /// </summary>
    public class InitSceneManager : MonoBehaviour {
        [Header("Scene Settings")]
        [SerializeField] private string mainMenuSceneName = "MainMenu";

        [SerializeField] private float initializationDelay = 0.1f;

        [Header("Required Singletons (for validation)")]
        [SerializeField] private SessionManager sessionManager;

        [SerializeField] private SceneTransitionManager sceneTransitionManager;
        [SerializeField] private SoundFXManager soundFXManager;

        [Header("Additional DDOL Managers")]
        [SerializeField] private MatchSettingsManager matchSettingsManager;

        [SerializeField] private NetworkManager networkManager; // Unity's NetworkManager (not a singleton pattern)
        [SerializeField] private SessionNetworkBridge sessionNetworkBridge;

        private static bool hasInitialized;

        private async void Start() {
            try {
                // Prevent multiple initializations if scene is reloaded somehow
                if(hasInitialized) {
                    Debug.LogWarning("[InitSceneManager] Already initialized, skipping");
                    return;
                }

                // Wait a frame to ensure all Awake/OnEnable calls complete
                await UniTask.DelayFrame(1);

                // Small delay to let all singletons fully initialize
                await UniTask.Delay(TimeSpan.FromSeconds(initializationDelay));

                // Validate critical singletons are present
                if(!ValidateSingletons()) {
                    Debug.LogError("[InitSceneManager] Critical singletons missing! Cannot proceed.");
                    return;
                }

                hasInitialized = true;

                // Load main menu scene
                await LoadMainMenuAsync();
            } catch(Exception e) {
                Debug.LogException(e);
            }
        }

        private static bool ValidateSingletons() {
            var allValid = true;

            // Critical singletons (required)
            if(SessionManager.Instance == null) {
                Debug.LogError("[InitSceneManager] SessionManager.Instance == null!");
                allValid = false;
            }

            // Recommended singletons
            if(SceneTransitionManager.Instance == null) {
                Debug.LogWarning(
                    "[InitSceneManager] SceneTransitionManager.Instance == null (optional but recommended)");
            }

            if(SoundFXManager.Instance == null) {
                Debug.LogWarning("[InitSceneManager] SoundFXManager.Instance == null (optional)");
            }

            // Additional DDOL managers
            if(MatchSettingsManager.Instance == null) {
                Debug.LogWarning("[InitSceneManager] MatchSettings.Instance == null (optional)");
            }

            if(NetworkManager.Singleton == null) {
                Debug.LogWarning(
                    "[InitSceneManager] NetworkManager.Singleton == null (optional, but required for networking)");
            }

            if(SessionNetworkBridge.Instance == null) {
                Debug.LogWarning(
                    "[InitSceneManager] SessionNetworkBridge.Instance == null (optional, spawns at runtime)");
            }

            return allValid;
        }

        private async UniTask LoadMainMenuAsync() {
            // Check if main menu is already loaded
            if(SceneManager.GetSceneByName(mainMenuSceneName).isLoaded) {
                SceneManager.SetActiveScene(SceneManager.GetSceneByName(mainMenuSceneName));
                return;
            }

            // Load main menu scene additively (so init scene persists)
            var loadOp = SceneManager.LoadSceneAsync(mainMenuSceneName, LoadSceneMode.Additive);

            if(loadOp == null) {
                Debug.LogError($"[InitSceneManager] Failed to load scene: {mainMenuSceneName}");
                return;
            }

            // Wait for scene to load
            while(!loadOp.isDone) {
                await UniTask.Yield();
            }

            // Set main menu as active scene
            var mainMenuScene = SceneManager.GetSceneByName(mainMenuSceneName);
            if(mainMenuScene.IsValid()) {
                SceneManager.SetActiveScene(mainMenuScene);
            } else {
                Debug.LogError("[InitSceneManager] MainMenu scene is not valid after loading");
                return;
            }

            // Fade transition removed for now - splash screen will handle its own fade into main menu
            // This prevents black flash on application start
        }

        /// <summary>
        /// Called when returning to main menu from game - ensures main menu is loaded
        /// </summary>
        public static async UniTask EnsureMainMenuLoadedAsync(string mainMenuSceneName = "MainMenu") {
            if(SceneManager.GetSceneByName(mainMenuSceneName).isLoaded) {
                return; // Already loaded
            }

            var loadOp = SceneManager.LoadSceneAsync(mainMenuSceneName, LoadSceneMode.Additive);
            if(loadOp != null) {
                while(!loadOp.isDone) {
                    await UniTask.Yield();
                }

                var scene = SceneManager.GetSceneByName(mainMenuSceneName);
                if(scene.IsValid()) {
                    SceneManager.SetActiveScene(scene);
                }
            }
        }
    }
}
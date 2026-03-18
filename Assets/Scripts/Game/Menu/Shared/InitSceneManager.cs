using System;
using Cysharp.Threading.Tasks;
using Diagnostics;
using Game.Match;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Menu.Shared {
    /// <summary>
    /// Manages the initialization scene that contains all DDOL singletons.
    /// This scene loads first, initializes all persistent managers, then transitions to MainMenu.
    /// </summary>
    public class InitSceneManager : MonoBehaviour {
        [Header("Scene Settings")]
        [SerializeField] private string mainMenuSceneName = "MainMenu";

        [SerializeField] private float initializationDelay = 0.1f;

        [Header("Required Singletons (for validation)")]
        [SerializeField] private Network.Session.SessionManager sessionManager;

        [SerializeField] private SceneTransitionManager sceneTransitionManager;

        [Header("Additional DDOL Managers")]
        [SerializeField] private MatchSettingsManager matchSettingsManager;

        [SerializeField] private NetworkManager networkManager; // Unity's NetworkManager (not a singleton pattern)

        private static bool hasInitialized;

        private async void Start() {
            try {
                // Prevent multiple initializations if scene is reloaded somehow
                if(hasInitialized) {
                    DevLog.LogWarning("[InitSceneManager] Already initialized, skipping");
                    return;
                }

                // Wait a frame to ensure all Awake/OnEnable calls complete
                await UniTask.DelayFrame(1);

                // Small delay to let all singletons fully initialize
                await UniTask.Delay(TimeSpan.FromSeconds(initializationDelay));

                // Validate critical singletons are present
                if(!ValidateSingletons()) {
                    DevLog.LogError("[InitSceneManager] Critical singletons missing! Cannot proceed.");
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
            if(Network.Session.SessionManager.Instance == null) {
                DevLog.LogError("[InitSceneManager] SessionManager.Instance == null!");
                allValid = false;
            }

            // Recommended singletons
            if(SceneTransitionManager.Instance == null) {
                DevLog.LogWarning(
                    "[InitSceneManager] SceneTransitionManager.Instance == null (optional but recommended)");
            }

            // Additional DDOL managers
            if(MatchSettingsManager.Instance == null) {
                DevLog.LogWarning("[InitSceneManager] MatchSettings.Instance == null (optional)");
            }

            if(NetworkManager.Singleton == null) {
                DevLog.LogWarning(
                    "[InitSceneManager] NetworkManager.Singleton == null (optional, but required for networking)");
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
                DevLog.LogError($"[InitSceneManager] Failed to load scene: {mainMenuSceneName}");
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
                DevLog.LogError("[InitSceneManager] MainMenu scene is not valid after loading");
            }

            // Fade transition removed for now - splash screen will handle its own fade into main menu
            // This prevents black flash on application start
        }
    }
}


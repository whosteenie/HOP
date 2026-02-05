using System;
using Steamworks;
using UnityEngine;
using UnityUtils;
using Cysharp.Threading.Tasks;

namespace Network.Steam {
    /// <summary>
    /// Handles Steamworks initialization and lifecycle.
    /// Acts as a central access point for Steam features.
    /// </summary>
    public class SteamManager : Singleton<SteamManager> {
        [Header("Configuration")]
        [Tooltip("Your Steam App ID. Use 480 for testing (Spacewar).")]
        [SerializeField] private uint appId = 480;

        private bool IsInitialized { get; set; }
        private static SteamId LocalSteamId => SteamClient.SteamId;
        private static string LocalPlayerName => SteamClient.Name;

        // Event for when Steam is ready
        public event Action OnSteamInitialized;

        protected override void Awake() {
            base.Awake();
            if (Instance != this) return;

            DontDestroyOnLoad(gameObject);
            
            try {
                // Initialize Steam Client
                SteamClient.Init(appId);
                IsInitialized = true;
                Debug.Log($"[SteamManager] Initialized Steamworks (AppID: {appId}). Logged in as: {LocalPlayerName} ({LocalSteamId})");
                
                if(OnSteamInitialized != null) {
                    OnSteamInitialized.Invoke();
                }
            }
            catch (Exception e) {
                Debug.LogError($"[SteamManager] Failed to initialize Steamworks: {e.Message}");
                // In development, maybe we want to continue offline?
                // For now, let's just log it.
            }
        }

        private void Update() {
            if (!IsInitialized) return;

            // Run Steam callbacks every frame
            SteamClient.RunCallbacks();
        }

        private void OnApplicationQuit() {
            if (IsInitialized) {
                SteamClient.Shutdown();
                IsInitialized = false;
                Debug.Log("[SteamManager] Steamworks shutdown.");
            }
        }
        
        /// <summary>
        /// Opens the Steam Overlay to the Invite Friends dialog.
        /// </summary>
        /// <param name="lobbyId">The lobby ID to invite friends to.</param>
        public void OpenInviteOverlay(ulong lobbyId) {
            if (!IsInitialized) return;
            SteamFriends.OpenGameInviteOverlay(lobbyId); 
        }

        /// <summary>
        /// Fetches the large avatar for a user and converts it to a Texture2D.
        /// </summary>
        public async UniTask<Texture2D> GetAvatarAsync(SteamId id) {
            if (!IsInitialized) return null;

            try {
                var image = await SteamFriends.GetLargeAvatarAsync(id);
                if (!image.HasValue) return null;

                var img = image.Value;
                Texture2D texture = new Texture2D((int)img.Width, (int)img.Height, TextureFormat.RGBA32, false);
                
                // Copy data
                texture.LoadRawTextureData(img.Data);
                texture.Apply();
                
                return texture;
            }
            catch (Exception e) {
                Debug.LogError($"[SteamManager] Failed to fetch avatar for {id}: {e.Message}");
                return null;
            }
        }
    }
}

using System;
using System.Collections.Generic;
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
        [Header("Avatar Cache")]
        [Tooltip("Maximum number of Steam avatars kept in memory at once.")]
        [SerializeField] private int avatarCacheMaxEntries = 128;
        [Tooltip("Cooldown before retrying avatar fetch after a failed request.")]
        [SerializeField] private float avatarFetchFailureCooldownSeconds = 15f;

        private bool IsInitialized { get; set; }
        private static SteamId LocalSteamId => SteamClient.SteamId;
        private static string LocalPlayerName => SteamClient.Name;
        private readonly Dictionary<ulong, Texture2D> _avatarCache = new();
        private readonly Dictionary<ulong, LinkedListNode<ulong>> _avatarLruNodes = new();
        private readonly LinkedList<ulong> _avatarLruOrder = new();
        private readonly Dictionary<ulong, UniTask<Texture2D>> _avatarInFlight = new();
        private readonly Dictionary<ulong, float> _avatarFailureCooldownUntil = new();
        private uint _avatarCacheGeneration;

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
                IsInitialized = false;
                Debug.LogWarning($"[SteamManager] Steam is unavailable/offline. Online features disabled. ({e.Message})");
            }
        }

        private void Update() {
            if (!IsInitialized) return;

            // Run Steam callbacks every frame
            SteamClient.RunCallbacks();
        }

        private void OnApplicationQuit() {
            ClearAvatarCache();
            if(!IsInitialized) return;
            SteamClient.Shutdown();
            IsInitialized = false;
            Debug.Log("[SteamManager] Steamworks shutdown.");
        }

        private void OnDestroy() {
            if(Instance != this) return;
            ClearAvatarCache();
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
            if(!IsInitialized || id.Value == 0) return null;

            var steamId = id.Value;
            if(TryGetCachedAvatar(steamId, out var cachedTexture)) {
                return cachedTexture;
            }

            if(IsAvatarFetchCoolingDown(steamId)) {
                return null;
            }

            if(_avatarInFlight.TryGetValue(steamId, out var inFlightTask)) {
                return await inFlightTask;
            }

            var generationAtRequestStart = _avatarCacheGeneration;
            var fetchTask = FetchAndCacheAvatarAsync(id, generationAtRequestStart).Preserve();
            _avatarInFlight[steamId] = fetchTask;

            try {
                return await fetchTask;
            } finally {
                _avatarInFlight.Remove(steamId);
            }
        }

        public void ClearAvatarCache() {
            _avatarCacheGeneration++;
            foreach(var texture in _avatarCache.Values) {
                if(texture != null) {
                    Destroy(texture);
                }
            }

            _avatarCache.Clear();
            _avatarLruNodes.Clear();
            _avatarLruOrder.Clear();
            _avatarInFlight.Clear();
            _avatarFailureCooldownUntil.Clear();
        }

        private async UniTask<Texture2D> FetchAndCacheAvatarAsync(SteamId id, uint generationAtRequestStart) {
            var steamId = id.Value;
            if(IsAvatarFetchCoolingDown(steamId)) {
                return null;
            }

            try {
                var image = await SteamFriends.GetLargeAvatarAsync(id);
                if(!image.HasValue) {
                    MarkAvatarFetchFailure(steamId);
                    return null;
                }

                var texture = CreateTextureFromSteamImage(image.Value);
                if(texture == null) {
                    MarkAvatarFetchFailure(steamId);
                    return null;
                }

                if(generationAtRequestStart != _avatarCacheGeneration) {
                    Destroy(texture);
                    return null;
                }

                CacheAvatarTexture(steamId, texture);
                _avatarFailureCooldownUntil.Remove(steamId);
                return texture;
            } catch(Exception e) {
                MarkAvatarFetchFailure(steamId);
                Debug.LogWarning($"[SteamManager] Failed to fetch avatar for {id}: {e.Message}");
                return null;
            }
        }

        private bool TryGetCachedAvatar(ulong steamId, out Texture2D texture) {
            if(_avatarCache.TryGetValue(steamId, out texture) == false || texture == null) {
                RemoveAvatarCacheEntry(steamId, destroyTexture: false);
                texture = null;
                return false;
            }

            TouchAvatarCacheEntry(steamId);
            return true;
        }

        private void CacheAvatarTexture(ulong steamId, Texture2D texture) {
            if(texture == null) return;

            if(_avatarCache.TryGetValue(steamId, out var existingTexture)) {
                if(existingTexture != null && existingTexture != texture) {
                    Destroy(existingTexture);
                }

                _avatarCache[steamId] = texture;
                TouchAvatarCacheEntry(steamId);
                return;
            }

            _avatarCache[steamId] = texture;
            _avatarLruNodes[steamId] = _avatarLruOrder.AddFirst(steamId);
            TrimAvatarCacheToLimit();
        }

        private void TouchAvatarCacheEntry(ulong steamId) {
            if(_avatarLruNodes.TryGetValue(steamId, out var existingNode) == false || existingNode == null) {
                _avatarLruNodes[steamId] = _avatarLruOrder.AddFirst(steamId);
                return;
            }

            if(existingNode.List != _avatarLruOrder || existingNode == _avatarLruOrder.First) return;
            _avatarLruOrder.Remove(existingNode);
            _avatarLruOrder.AddFirst(existingNode);
        }

        private void TrimAvatarCacheToLimit() {
            var maxEntries = Mathf.Max(8, avatarCacheMaxEntries);
            while(_avatarCache.Count > maxEntries) {
                var leastRecentNode = _avatarLruOrder.Last;
                if(leastRecentNode == null) {
                    break;
                }

                RemoveAvatarCacheEntry(leastRecentNode.Value, destroyTexture: true);
            }
        }

        private void RemoveAvatarCacheEntry(ulong steamId, bool destroyTexture) {
            if(_avatarCache.Remove(steamId, out var texture)) {
                if(destroyTexture && texture != null) {
                    Destroy(texture);
                }
            }

            if(_avatarLruNodes.Remove(steamId, out var node)) {
                if(node is { List: not null }) {
                    node.List.Remove(node);
                }
            }

            _avatarFailureCooldownUntil.Remove(steamId);
        }

        private bool IsAvatarFetchCoolingDown(ulong steamId) {
            if(_avatarFailureCooldownUntil.TryGetValue(steamId, out var cooldownUntil) == false) {
                return false;
            }

            if(!(Time.unscaledTime >= cooldownUntil)) return true;
            _avatarFailureCooldownUntil.Remove(steamId);
            return false;
        }

        private void MarkAvatarFetchFailure(ulong steamId) {
            var cooldownDuration = Mathf.Max(0.1f, avatarFetchFailureCooldownSeconds);
            _avatarFailureCooldownUntil[steamId] = Time.unscaledTime + cooldownDuration;
        }

        private static Texture2D CreateTextureFromSteamImage(Steamworks.Data.Image image) {
            var width = (int)image.Width;
            var height = (int)image.Height;
            var data = image.Data;
            if(data == null || data.Length == 0 || width <= 0 || height <= 0) {
                return null;
            }

            // Flip image vertically so UITK background rendering matches expected orientation.
            var flippedData = new byte[data.Length];
            var stride = width * 4; // RGBA32
            for(var y = 0; y < height; y++) {
                Array.Copy(data, y * stride, flippedData, (height - 1 - y) * stride, stride);
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.LoadRawTextureData(flippedData);
            texture.Apply();
            return texture;
        }
    }
}

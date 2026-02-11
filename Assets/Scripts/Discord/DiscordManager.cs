using System;
using Discord.Sdk;
using UnityEngine;

namespace Discord {
    public class DiscordManager : MonoBehaviour {
        public static DiscordManager Instance { get; private set; }

        private Client _discord;
        private const long AppId = 1467433546963619916;

        private void Awake() {
            if (Instance != null) {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        
            InitializeDiscord();
        }

        private void InitializeDiscord() {
            try {
                // Partner SDK uses a parameterless constructor for Client
                _discord = new Client();

                // Set Application ID (required for SDK usage without Connect)
                _discord.SetApplicationId(AppId);
                
                // Also register launch command to ensure Discord knows how to launch us?
                // This links the current specific executable (or Unity Editor) to the App ID, 
                // allowing it to appear in the "Game Activity" / "Go Live" section.
                _discord.RegisterLaunchCommand(AppId, ""); 
            
                Debug.Log("[DiscordManager] Discord SDK initialized (Partner SDK).");
            } catch (Exception e) {
                Debug.LogWarning($"[DiscordManager] Failed to initialize Discord SDK: {e.Message}");
                _discord = null;
            }
        }

        public void SetStatus(string details, string state, long startTimestamp = 0) {
            if (_discord == null) return;

            try {
                var activity = new Activity();
                activity.SetDetails(details);
                activity.SetState(state);
                activity.SetApplicationId(AppId);

                // Assets
                var assets = new ActivityAssets();
                assets.SetLargeImage("hop_logo");
                assets.SetLargeText("HOP");
                activity.SetAssets(assets);

                // Timestamps
                if (startTimestamp > 0) {
                    var timestamps = new ActivityTimestamps();
                    timestamps.SetStart((ulong)startTimestamp);
                    activity.SetTimestamps(timestamps);
                }

                _discord.UpdateRichPresence(activity, res => {
                    if (!res.Successful()) {
                        Debug.LogWarning($"[DiscordManager] Failed to update activity: {res}");
                    }
                });
            } catch (Exception e) {
                Debug.LogError($"[DiscordManager] Error updating status: {e.Message}");
            }
        }

        public void ClearStatus() {
            if (_discord == null) return;
            try {
                // Updating with empty activity clears it? 
                // Or use a method to clear? UpdateRichPresence with empty activity is standard.
                var activity = new Activity();
                _discord.UpdateRichPresence(activity, res => {
                    if (!res.Successful()) {
                        Debug.LogWarning($"[DiscordManager] Failed to clear activity: {res}");
                    }
                });
            } catch (Exception e) {
                Debug.LogError($"[DiscordManager] Error clearing status: {e.Message}");
            }
        }

        private void OnApplicationQuit() {
            if(_discord == null) return;
            _discord.Dispose();
            _discord = null;
        }
    }
}

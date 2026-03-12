using System;
using Discord.Sdk;
using UnityEngine;

namespace Discord {
    public class DiscordManager : MonoBehaviour {
        public static DiscordManager Instance { get; private set; }

        private Client _discord;
        private const long AppId = 1467433546963619916;
        private bool _isReady;
        private bool _hasPendingPresence;
        private string _pendingDetails = string.Empty;
        private string _pendingState = string.Empty;
        private long _pendingStartTimestamp;

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
                _discord = new Client();
                _discord.AddLogCallback(OnDiscordLog, LoggingSeverity.Warning);
                _discord.SetStatusChangedCallback(OnDiscordStatusChanged);
                _discord.SetApplicationId(AppId);

                bool registeredLaunchCommand = _discord.RegisterLaunchCommand(AppId, string.Empty);
                _discord.Connect();

                var initialStatus = _discord.GetStatus();
                _isReady = initialStatus == Client.Status.Ready;

                Debug.Log(
                    $"[DiscordManager] Discord SDK initialized. " +
                    $"Status={Client.StatusToString(initialStatus)} " +
                    $"LaunchRegistered={registeredLaunchCommand}");

                if (_isReady) {
                    FlushPendingPresence();
                }
            } catch (Exception e) {
                Debug.LogWarning($"[DiscordManager] Failed to initialize Discord SDK: {e.Message}");
                _discord = null;
                _isReady = false;
            }
        }

        public void SetStatus(string details, string state, long startTimestamp = 0) {
            if (_discord == null) return;

            try {
                _pendingDetails = details ?? string.Empty;
                _pendingState = state ?? string.Empty;
                _pendingStartTimestamp = startTimestamp;
                _hasPendingPresence = true;
                FlushPendingPresence();
            } catch (Exception e) {
                Debug.LogError($"[DiscordManager] Error updating status: {e.Message}");
            }
        }

        public void ClearStatus() {
            if (_discord == null) return;
            try {
                _hasPendingPresence = false;
                _pendingDetails = string.Empty;
                _pendingState = string.Empty;
                _pendingStartTimestamp = 0;

                if (!_isReady) {
                    return;
                }

                _discord.ClearRichPresence();
            } catch (Exception e) {
                Debug.LogError($"[DiscordManager] Error clearing status: {e.Message}");
            }
        }

        private void FlushPendingPresence() {
            if (_discord == null || !_isReady || !_hasPendingPresence) {
                return;
            }

            var activity = new Activity();
            activity.SetDetails(_pendingDetails);
            activity.SetState(_pendingState);
            activity.SetApplicationId(AppId);

            var assets = new ActivityAssets();
            assets.SetLargeImage("hop_logo");
            assets.SetLargeText("HOP");
            activity.SetAssets(assets);

            if (_pendingStartTimestamp > 0) {
                var timestamps = new ActivityTimestamps();
                timestamps.SetStart((ulong)_pendingStartTimestamp);
                activity.SetTimestamps(timestamps);
            }

            _discord.UpdateRichPresence(activity, OnRichPresenceUpdated);
        }

        private void OnDiscordLog(string message, LoggingSeverity severity) {
            if (severity >= LoggingSeverity.Error) {
                Debug.LogWarning($"[DiscordManager] SDK {severity}: {message}");
            }
        }

        private void OnDiscordStatusChanged(Client.Status status, Client.Error error, int errorDetail) {
            _isReady = status == Client.Status.Ready;

            if (status == Client.Status.Ready) {
                Debug.Log("[DiscordManager] Discord client is ready.");
                FlushPendingPresence();
                return;
            }

            if (error != Client.Error.None) {
                Debug.LogWarning(
                    $"[DiscordManager] Discord status changed: " +
                    $"Status={Client.StatusToString(status)} " +
                    $"Error={Client.ErrorToString(error)} " +
                    $"Detail={errorDetail}");
            }
        }

        private void OnRichPresenceUpdated(ClientResult res) {
            if (res.Successful()) {
                return;
            }

            Debug.LogWarning(
                $"[DiscordManager] Failed to update activity: " +
                $"Type={res.Type()} Error={res.Error()} Retryable={res.Retryable()} " +
                $"RetryAfter={res.RetryAfter()} Response={res.ResponseBody()}");
        }

        private void OnApplicationQuit() {
            if(_discord == null) return;
            _discord.Dispose();
            _discord = null;
            _isReady = false;
        }
    }
}

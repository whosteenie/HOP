using System;
using Cysharp.Threading.Tasks;
using Discord.Sdk;
using Steamworks;
using UnityEngine;

namespace Discord {
    public class DiscordManager : MonoBehaviour {
        public static DiscordManager Instance { get; private set; }

        private Client _discord;
        private const long AppId = 1467433546963619916;
        private const string DiscordSteamWebIdentity = "discord";
        private bool _isReady;
        private bool _hasPendingPresence;
        private string _pendingDetails = string.Empty;
        private string _pendingState = string.Empty;
        private long _pendingStartTimestamp;
        private bool _isConnecting;

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

                var registeredLaunchCommand = _discord.RegisterLaunchCommand(AppId, string.Empty);
                var registeredSteamLaunch = false;
                if (SteamClient.IsValid && SteamClient.IsLoggedOn) {
                    registeredSteamLaunch = _discord.RegisterLaunchSteamApplication(AppId, SteamClient.AppId);
                }

                var initialStatus = _discord.GetStatus();
                _isReady = false;

                Debug.Log(
                    "[DiscordManager] Discord SDK initialized. " +
                    $"Status={Client.StatusToString(initialStatus)} " +
                    $"LaunchRegistered={registeredLaunchCommand} " +
                    $"SteamLaunchRegistered={registeredSteamLaunch}");

                BeginAuthenticationAndConnect().Forget();
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

        private static void OnDiscordLog(string message, LoggingSeverity severity) {
            if (severity >= LoggingSeverity.Error) {
                Debug.LogWarning($"[DiscordManager] SDK {severity}: {message}");
            }
        }

        private async UniTaskVoid BeginAuthenticationAndConnect() {
            if (_discord == null || _isConnecting) {
                return;
            }

            _isConnecting = true;

            try {
                if (!SteamClient.IsValid || !SteamClient.IsLoggedOn) {
                    Debug.LogWarning("[DiscordManager] Steam is not available; Discord Social SDK auth requires a token before Connect().");
                    return;
                }

                var steamTicket = await SteamUser.GetAuthTicketForWebApiAsync(DiscordSteamWebIdentity);
                if (steamTicket?.Data == null || steamTicket.Data.Length == 0) {
                    Debug.LogWarning("[DiscordManager] Failed to get Steam web auth ticket for Discord auth.");
                    return;
                }

                var externalAuthToken = ToHexString(steamTicket.Data);
                steamTicket.Cancel();

                if (string.IsNullOrEmpty(externalAuthToken)) {
                    Debug.LogWarning("[DiscordManager] Steam web auth ticket for Discord auth was empty.");
                    return;
                }

                _discord.GetProvisionalToken(
                    AppId,
                    AuthenticationExternalAuthType.SteamSessionTicket,
                    externalAuthToken,
                    OnProvisionalTokenReceived);
            } catch (Exception e) {
                Debug.LogWarning($"[DiscordManager] Failed to begin Discord authentication: {e.Message}");
                _isConnecting = false;
            }
        }

        private void OnProvisionalTokenReceived(
            ClientResult result,
            string accessToken,
            string refreshToken,
            AuthorizationTokenType tokenType,
            int expiresIn,
            string scopes) {
            if (!result.Successful()) {
                _isConnecting = false;
                Debug.LogWarning(
                    "[DiscordManager] Failed to acquire Discord token: " +
                    $"Type={result.Type()} Error={result.Error()} Retryable={result.Retryable()} " +
                    $"RetryAfter={result.RetryAfter()} Response={result.ResponseBody()}");
                return;
            }

            _discord.UpdateToken(tokenType, accessToken, OnDiscordTokenUpdated);
        }

        private void OnDiscordTokenUpdated(ClientResult result) {
            if (!result.Successful()) {
                _isConnecting = false;
                Debug.LogWarning(
                    "[DiscordManager] Failed to update Discord token: " +
                    $"Type={result.Type()} Error={result.Error()} Retryable={result.Retryable()} " +
                    $"RetryAfter={result.RetryAfter()} Response={result.ResponseBody()}");
                return;
            }

            try {
                _discord.Connect();
            } catch (Exception e) {
                _isConnecting = false;
                Debug.LogWarning($"[DiscordManager] Failed to connect Discord SDK after token update: {e.Message}");
            }
        }

        private void OnDiscordStatusChanged(Client.Status status, Client.Error error, int errorDetail) {
            _isReady = status == Client.Status.Ready;

            switch(status) {
                case Client.Status.Ready: {
                    _isConnecting = false;
                    Debug.Log("[DiscordManager] Discord client is ready.");
                    if (SteamClient.IsValid && SteamClient.IsLoggedOn && !string.IsNullOrWhiteSpace(SteamClient.Name)) {
                        _discord.UpdateProvisionalAccountDisplayName(SteamClient.Name, OnProvisionalDisplayNameUpdated);
                    }
                    FlushPendingPresence();
                    return;
                }
                case Client.Status.Disconnected:
                    _isConnecting = false;
                    break;
            }

            if (error != Client.Error.None) {
                Debug.LogWarning(
                    "[DiscordManager] Discord status changed: " +
                    $"Status={Client.StatusToString(status)} " +
                    $"Error={Client.ErrorToString(error)} " +
                    $"Detail={errorDetail}");
            }
        }

        private static void OnRichPresenceUpdated(ClientResult res) {
            if (res.Successful()) {
                return;
            }

            Debug.LogWarning(
                "[DiscordManager] Failed to update activity: " +
                $"Type={res.Type()} Error={res.Error()} Retryable={res.Retryable()} " +
                $"RetryAfter={res.RetryAfter()} Response={res.ResponseBody()}");
        }

        private static void OnProvisionalDisplayNameUpdated(ClientResult result) {
            if (result.Successful()) {
                return;
            }

            Debug.LogWarning(
                "[DiscordManager] Failed to update provisional display name: " +
                $"Type={result.Type()} Error={result.Error()} Retryable={result.Retryable()} " +
                $"RetryAfter={result.RetryAfter()} Response={result.ResponseBody()}");
        }

        private static string ToHexString(byte[] bytes) {
            if (bytes == null || bytes.Length == 0) {
                return string.Empty;
            }

            var chars = new char[bytes.Length * 2];
            const string hex = "0123456789ABCDEF";

            for (var i = 0; i < bytes.Length; i++) {
                var value = bytes[i];
                chars[i * 2] = hex[value >> 4];
                chars[i * 2 + 1] = hex[value & 0x0F];
            }

            return new string(chars);
        }

        private void OnApplicationQuit() {
            if(_discord == null) return;
            _discord.Dispose();
            _discord = null;
            _isReady = false;
        }
    }
}

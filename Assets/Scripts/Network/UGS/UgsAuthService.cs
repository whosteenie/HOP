using System.Text;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Steamworks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;

namespace Network.UGS {
    /// <summary>
    /// Centralized UGS initialization + authentication.
    /// Prefers Steam sign-in when Steam is available, falls back to anonymous.
    /// </summary>
    public static class UgsAuthService {
        private const string SteamIdentity = "unityauthenticationservice";

        private static readonly object SignInGate = new object();
        private static Task _inFlightSignInTask;

        public static async UniTask InitializeAndSignInAsync() {
            if(UnityServices.State != ServicesInitializationState.Initialized) {
                var options = new InitializationOptions();
                
                // Check if we are running as a clone (ParrelSync / MPPM)
                // MPPM usually adds arguments or we can check project path hash
#if UNITY_EDITOR
                if (IsClone()) {
                    string cloneName = GetCloneName();
                    options.SetProfile(cloneName);
                    Debug.Log($"[UGS Auth] Initializing as clone profile: {cloneName}");
                }
#endif
                await UnityServices.InitializeAsync(options);
            }

            if(AuthenticationService.Instance.IsSignedIn) return;

            Task inFlight;
            lock(SignInGate) {
                if(_inFlightSignInTask == null) {
                    _inFlightSignInTask = InitializeAndSignInInternalAsync();
                }
                inFlight = _inFlightSignInTask;
            }

            await inFlight;

            if(Debug.isDebugBuild) {
                var signedIn = AuthenticationService.Instance.IsSignedIn;
                var authorized = AuthenticationService.Instance.IsAuthorized;
                var pid = AuthenticationService.Instance.PlayerId;
                var profile = AuthenticationService.Instance.Profile;
                var steamLoggedOn = SteamClient.IsValid && SteamClient.IsLoggedOn;
                Debug.Log(
                    $"[UGS Auth] signedIn={signedIn} authorized={authorized} playerId='{pid}' profile='{profile}' " +
                    $"steamLoggedOn={steamLoggedOn} steamAppId={SteamClient.AppId}"
                );
            }
        }

        private static async Task InitializeAndSignInInternalAsync() {
            try {
                if(AuthenticationService.Instance.IsSignedIn) return;

                // For MPPM clones, skip Steam auth entirely to get unique PlayerIDs
#if UNITY_EDITOR
                if(IsClone()) {
                    Debug.Log("[UgsAuthService] Clone detected - using anonymous auth for unique PlayerID");
                    try {
                        // Clear any cached session to force a fresh unique PlayerID
                        AuthenticationService.Instance.ClearSessionToken();
                        await AuthenticationService.Instance.SignInAnonymouslyAsync();
                    } catch(System.Exception e) {
                        Debug.LogWarning($"[UgsAuthService] Anonymous sign-in failed for clone. Exception: {e.Message}");
                    }
                    return;
                }
#endif

                if(SteamClient.IsValid && SteamClient.IsLoggedOn) {
                    var ok = await TrySteamSignInAsync();
                    if(ok) return;
                }

                try {
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                } catch(System.Exception e) {
                    Debug.LogWarning($"[UgsAuthService] Anonymous sign-in failed. Exception: {e.Message}");
                }
            } finally {
                lock(SignInGate) {
                    _inFlightSignInTask = null;
                }
            }
        }

        private static async Task<bool> TrySteamSignInAsync() {
            try {
                var ticket = await SteamUser.GetAuthTicketForWebApiAsync(SteamIdentity, 10.0);
                if(ticket == null) {
                    Debug.LogWarning("[UgsAuthService] Steam auth ticket request timed out or failed.");
                    return false;
                }

                var data = ticket.Data;
                ticket.Cancel();

                if(data == null || data.Length == 0) {
                    Debug.LogWarning("[UgsAuthService] Steam auth ticket was empty.");
                    return false;
                }

                var hex = ToHexString(data);
                if(string.IsNullOrEmpty(hex)) {
                    Debug.LogWarning("[UgsAuthService] Failed to encode Steam auth ticket.");
                    return false;
                }

                var options = new SignInOptions();
                options.CreateAccount = true;

                var appId = SteamClient.AppId.ToString();
                if(string.IsNullOrEmpty(appId)) {
                    Debug.LogWarning("[UgsAuthService] Steam AppId was empty; cannot sign in with Steam.");
                    return false;
                }

                await AuthenticationService.Instance.SignInWithSteamAsync(hex, SteamIdentity, appId, options);
                return AuthenticationService.Instance.IsSignedIn;
            } catch(System.Exception e) {
                Debug.LogWarning(
                    $"[UgsAuthService] Steam sign-in failed (AppID: {SteamClient.AppId}). " +
                    $"Falling back to anonymous. Exception: {e.Message}"
                );
                return false;
            }
        }

        private static string ToHexString(byte[] bytes) {
            if(bytes == null) return "";
            if(bytes.Length == 0) return "";

            var sb = new StringBuilder(bytes.Length * 2);
            for(var i = 0; i < bytes.Length; i++) {
                sb.Append(bytes[i].ToString("X2"));
            }
            return sb.ToString();
        }
    private static bool IsClone() {
            var clonePath = Application.dataPath;
            // ParrelSync uses "clone", Unity MPPM uses "Library/VP/mppm..."
            return clonePath.Contains("clone") || clonePath.Contains("ParrelSync") || clonePath.Contains("mppm"); 
        }

        private static string GetCloneName() {
            var clonePath = Application.dataPath;
            // Simple hash of the path to keep it short and unique per clone folder
            return "Clone_" + clonePath.GetHashCode().ToString("X");
        }
    }
}


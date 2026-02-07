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
                await UnityServices.InitializeAsync();
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
                var steamLoggedOn = SteamClient.IsValid && SteamClient.IsLoggedOn;
                Debug.Log(
                    $"[UGS Auth] signedIn={signedIn} authorized={authorized} playerId='{pid}' " +
                    $"steamLoggedOn={steamLoggedOn} steamAppId={SteamClient.AppId}"
                );
            }
        }

        private static async Task InitializeAndSignInInternalAsync() {
            try {
                if(AuthenticationService.Instance.IsSignedIn) return;

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
    }
}


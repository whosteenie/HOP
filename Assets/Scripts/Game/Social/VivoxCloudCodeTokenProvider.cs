using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Unity.Services.CloudCode;
using Unity.Services.Vivox;
using UnityEngine;

namespace Game.Social {
    public sealed class VivoxCloudCodeTokenProvider : IVivoxTokenProvider {
        private const string EndpointName = "VivoxToken";
        private const string JoinActionPrefix = "join~";

        private static string Base64UrlEncode(string value) {
            if(string.IsNullOrEmpty(value)) return string.Empty;
            var bytes = Encoding.UTF8.GetBytes(value);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public async Task<string> GetTokenAsync(string issuer = null, TimeSpan? expiration = null, string targetUserUri = null,
            string action = null, string channelUri = null, string fromUserUri = null, string realm = null) {
            if(string.IsNullOrEmpty(action)) {
                throw new ArgumentException("Vivox token request missing action.", nameof(action));
            }

            if(string.IsNullOrEmpty(fromUserUri)) {
                throw new ArgumentException("Vivox token request missing fromUserUri.", nameof(fromUserUri));
            }

            var actionForCloudCode = action;
            if(string.Equals(action, "join", StringComparison.Ordinal) && string.IsNullOrEmpty(channelUri) == false) {
                // Work around environments that strip channelUri before script receives params.
                actionForCloudCode = JoinActionPrefix + Base64UrlEncode(channelUri);
            }

            var args = new Dictionary<string, object> {
                ["action"] = actionForCloudCode,
                ["fromUserUri"] = fromUserUri
            };

            if(expiration.HasValue) {
                args["expiration"] = (int)expiration.Value.TotalSeconds;
            }

            if(string.IsNullOrEmpty(channelUri) == false) {
                args["channelUri"] = channelUri;
            }

            if(string.IsNullOrEmpty(targetUserUri) == false) {
                args["targetUserUri"] = targetUserUri;
            }

            if(string.IsNullOrEmpty(realm) == false) {
                args["realm"] = realm;
            }

            if(Debug.isDebugBuild) {
                var expSeconds = expiration.HasValue ? (int)expiration.Value.TotalSeconds : -1;
                Debug.Log(
                    "[VivoxToken] Requesting VAT via Cloud Code. " +
                    $"action='{action}' fromUserUri='{fromUserUri}' channelUri='{channelUri}' targetUserUri='{targetUserUri}' " +
                    $"expSeconds={expSeconds} realm='{realm}'"
                );
            }

            try {
                var token = await CloudCodeService.Instance.CallEndpointAsync<string>(EndpointName, args);
                if(string.IsNullOrEmpty(token)) {
                    throw new Exception("Cloud Code returned an empty Vivox access token.");
                }

                return token;
            } catch(Exception e) {
                Debug.LogError($"[VivoxCloudCodeTokenProvider] Failed to fetch token (action='{action}'). Exception: {e.Message}");
                throw;
            }
        }
    }
}


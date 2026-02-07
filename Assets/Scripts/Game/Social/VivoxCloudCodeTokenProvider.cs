using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.CloudCode;
using Unity.Services.Vivox;
using UnityEngine;

namespace Game.Social {
    /// <summary>
    /// Vivox token provider that requests one-time-use VATs from UGS Cloud Code.
    /// This allows running with Vivox Test Mode disabled and without shipping a Vivox signing key in the client.
    /// </summary>
    public sealed class VivoxCloudCodeTokenProvider : IVivoxTokenProvider {
        // Cloud Code Script endpoint name (must match the script name you deploy).
        private const string EndpointName = "VivoxToken";

        public async Task<string> GetTokenAsync(string issuer = null, TimeSpan? expiration = null, string targetUserUri = null,
            string action = null, string channelUri = null, string fromUserUri = null, string realm = null) {
            if(string.IsNullOrEmpty(action)) {
                throw new ArgumentException("Vivox token request missing action.", nameof(action));
            }

            if(string.IsNullOrEmpty(fromUserUri)) {
                throw new ArgumentException("Vivox token request missing fromUserUri.", nameof(fromUserUri));
            }

            var args = new Dictionary<string, object>();
            if(!string.IsNullOrEmpty(issuer)) {
                args["issuer"] = issuer;
            }

            if(expiration.HasValue) {
                // Vivox passes expiration as "seconds since Unix epoch" in its token provider calls.
                args["expiration"] = (int)expiration.Value.TotalSeconds;
            }

            if(!string.IsNullOrEmpty(targetUserUri)) {
                args["targetUserUri"] = targetUserUri;
            }

            args["action"] = action;

            if(!string.IsNullOrEmpty(channelUri)) {
                args["channelUri"] = channelUri;
            }

            args["fromUserUri"] = fromUserUri;

            if(!string.IsNullOrEmpty(realm)) {
                args["realm"] = realm;
            }

            if(Debug.isDebugBuild) {
                var expSeconds = expiration.HasValue ? (int)expiration.Value.TotalSeconds : -1;
                Debug.Log(
                    "[VivoxToken] Requesting VAT via Cloud Code. " +
                    $"action='{action}' fromUserUri='{fromUserUri}' channelUri='{channelUri}' targetUserUri='{targetUserUri}' " +
                    $"issuerProvided={(string.IsNullOrEmpty(issuer) ? "0" : "1")} expSeconds={expSeconds} realm='{realm}'"
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


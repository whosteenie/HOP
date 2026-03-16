using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Diagnostics {
    /// <summary>
    /// Build-safe structured flow logger for runtime diagnostics.
    /// </summary>
    public static class FlowLog {
        private const string Prefix = "[HOPFLOW]";
        private static readonly HashSet<string> OnceKeys = new();
        private static readonly Dictionary<string, float> RateLimitedKeys = new();
        private static readonly string RunId = Guid.NewGuid().ToString("N")[..8];
        private static readonly object Gate = new();

        // ReSharper disable once ConvertToConstant.Local
        private static readonly bool Enabled = true;
        private const bool EmitInEditor = false;

        /// <summary>
        /// Optional provider for a human-readable session identifier (e.g. lobby or match ID).
        /// Registered by higher-level systems (such as Network.Session) to keep Diagnostics
        /// independent of concrete session implementations.
        /// </summary>
        private static Func<string> sessionIdProvider;

        /// <summary>
        /// Registers a callback that returns the current session identifier for flow logging.
        /// Passing null clears the provider and causes logs to use \"none\" for the session id.
        /// </summary>
        public static void SetSessionIdProvider(Func<string> provider) {
            sessionIdProvider = provider;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void LogBoot() {
            Emit(FlowEventIds.BootStart,
                ("build", Application.version),
                ("unity", Application.unityVersion),
                ("platform", Application.platform.ToString()));
        }

        public static void Emit(string eventId, params (string Key, object Value)[] fields) {
            if(ShouldEmit() == false) return;
            if(string.IsNullOrWhiteSpace(eventId)) return;
            Debug.Log(BuildLine(eventId, fields));
        }

        public static void Once(string eventId, string dedupeKey, params (string Key, object Value)[] fields) {
            if(ShouldEmit() == false) return;
            if(string.IsNullOrWhiteSpace(eventId)) return;
            if(string.IsNullOrWhiteSpace(dedupeKey)) {
                Emit(eventId, fields);
                return;
            }

            var key = eventId + "|" + dedupeKey;
            lock(Gate) {
                if(OnceKeys.Add(key) == false) return;
            }

            Debug.Log(BuildLine(eventId, fields));
        }

        public static void RateLimited(string eventId, string rateKey, float intervalSeconds,
            params (string Key, object Value)[] fields) {
            if(ShouldEmit() == false) return;
            if(string.IsNullOrWhiteSpace(eventId)) return;
            if(intervalSeconds <= 0f) {
                Emit(eventId, fields);
                return;
            }

            if(string.IsNullOrWhiteSpace(rateKey)) {
                rateKey = eventId;
            }

            var now = Time.unscaledTime;
            lock(Gate) {
                if(RateLimitedKeys.TryGetValue(rateKey, out var last) && now - last < intervalSeconds) {
                    return;
                }
                RateLimitedKeys[rateKey] = now;
            }

            Debug.Log(BuildLine(eventId, fields));
        }

        private static bool ShouldEmit() {
            if(Enabled == false) return false;
            return !Application.isEditor || EmitInEditor;
        }

        private static string BuildLine(string eventId, (string Key, object Value)[] fields) {
            var sb = new StringBuilder(256);
            sb.Append(Prefix);
            sb.Append(" id=").Append(eventId);
            sb.Append(" run=").Append(RunId);
            sb.Append(" session=").Append(Sanitize(GetSessionId()));
            sb.Append(" role=").Append(Sanitize(GetRole()));
            sb.Append(" scene=").Append(Sanitize(GetSceneName()));

            if(fields == null) return sb.ToString();
            foreach(var (key, value) in fields) {
                if(string.IsNullOrWhiteSpace(key)) continue;
                sb.Append(' ');
                sb.Append(key);
                sb.Append('=');
                sb.Append(Sanitize(value));
            }

            return sb.ToString();
        }

        private static string GetRole() {
            var nm = NetworkManager.Singleton;
            if(nm == null) return "None";
            if(nm.IsServer) return "Host";
            return nm.IsClient ? "Client" : "Offline";
        }

        private static string GetSceneName() {
            var activeScene = SceneManager.GetActiveScene();
            return activeScene.IsValid() ? activeScene.name : "Unknown";
        }

        private static string GetSessionId() {
            if(sessionIdProvider == null) return "none";

            try {
                var id = sessionIdProvider();
                return string.IsNullOrEmpty(id) ? "none" : id;
            } catch {
                // Diagnostics should never throw because of provider failures.
                return "none";
            }
        }

        private static string Sanitize(object value) {
            if(value == null) return "null";
            var raw = value switch {
                float f => f.ToString("0.###", CultureInfo.InvariantCulture),
                double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                _ => value.ToString()
            };

            if(string.IsNullOrEmpty(raw)) return "\"\"";

            var needsQuotes = false;
            foreach(var t in raw) {
                if(!char.IsWhiteSpace(t) && t != '=') continue;
                needsQuotes = true;
                break;
            }

            if(needsQuotes == false) return raw;
            return "'" + raw.Replace("'", "''") + "'";
        }
    }
}

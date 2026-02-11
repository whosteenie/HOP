using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network.Diagnostics {
    /// <summary>
    /// Build-safe structured flow logger for runtime diagnostics.
    /// </summary>
    public static class FlowLog {
        private const string Prefix = "[HOPFLOW]";
        private static readonly HashSet<string> OnceKeys = new();
        private static readonly Dictionary<string, float> RateLimitedKeys = new();
        private static readonly string RunId = Guid.NewGuid().ToString("N")[..8];
        private static readonly object Gate = new();

        private static readonly bool Enabled = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void LogBoot() {
            Emit(FlowEventIds.BootStart,
                ("build", Application.version),
                ("unity", Application.unityVersion),
                ("platform", Application.platform.ToString()));
        }

        public static void Emit(string eventId, params (string Key, object Value)[] fields) {
            if(Enabled == false) return;
            if(string.IsNullOrWhiteSpace(eventId)) return;
            Debug.Log(BuildLine(eventId, fields));
        }

        public static void Once(string eventId, string dedupeKey, params (string Key, object Value)[] fields) {
            if(Enabled == false) return;
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
            if(Enabled == false) return;
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
            if(SessionManager.HasInstance == false) return "none";
            var sessionManager = SessionManager.Instance;
            if(sessionManager == null) return "none";

            return string.IsNullOrEmpty(sessionManager.FlowSessionId) == false ? sessionManager.FlowSessionId : "none";
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

using UnityEngine;

namespace Network.AntiCheat {
    public static class AntiCheatLogger {
        public static void LogRateLimit(ulong clientId, string key) {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.LogWarning($"[AntiCheat][RateLimit] Client {clientId} exceeded limit for {key}.");
#endif
            // In production builds, you could send to server telemetry instead
            // ServerTelemetry.LogAntiCheatEvent(clientId, "RateLimit", key);
        }

        public static void LogInvalidDamage(ulong clientId, string reason) {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.LogWarning($"[AntiCheat][Damage] Client {clientId} sent invalid damage RPC: {reason}");
#endif
        }

        public static void LogMovementViolation(ulong clientId, string details) {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.LogWarning($"[AntiCheat][Movement] Client {clientId} violation: {details}");
#endif
        }

        public static void LogMovementEnforcement(ulong clientId, string details) {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.LogError($"[AntiCheat][Movement] Client {clientId} ENFORCED: {details}");
#endif
        }

        public static void LogAuthorityViolation(string context, ulong clientId = ulong.MaxValue) {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.LogWarning(clientId == ulong.MaxValue
                ? $"[AntiCheat][Authority] {context} invoked without server authority."
                : $"[AntiCheat][Authority] Client {clientId} invoked {context} without server authority.");
#endif
        }
    }
}
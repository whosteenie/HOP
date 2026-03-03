using UnityEngine;

namespace Network.Session {
    internal static class MatchmakerPollingPolicy {
        // Matchmaker ticket status requests are rate-limited per player; keep a conservative baseline.
        public const int TicketPollBaseIntervalMs = 1200;
        public const int TicketPollFailureBackoffStepMs = 1000;
        public const int TicketPollMaxIntervalMs = 6000;

        public const int MatchLobbyDiscoveryMaxAttempts = 30;
        public const int MatchLobbyDiscoveryIntervalMs = 1000;

        public static int ResolveTicketPollDelayMs(int consecutiveFailures) {
            var safeFailures = Mathf.Max(0, consecutiveFailures);
            var delayMs = TicketPollBaseIntervalMs + safeFailures * TicketPollFailureBackoffStepMs;
            return Mathf.Clamp(delayMs, TicketPollBaseIntervalMs, TicketPollMaxIntervalMs);
        }

        public static int ResolveMatchLobbyDiscoveryDelayMs(int attemptIndex) {
            _ = attemptIndex;
            return MatchLobbyDiscoveryIntervalMs;
        }
    }
}

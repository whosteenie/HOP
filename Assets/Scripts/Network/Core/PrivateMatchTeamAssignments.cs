using System.Collections.Generic;

namespace Network.Core {
    /// <summary>
    /// Holds draft team assignments (SteamId -> team index 0 or 1) from the private match setup panel.
    /// Set by SessionManager when starting a private match; read and cleared by CustomNetworkManager when assigning teams.
    /// </summary>
    public static class PrivateMatchTeamAssignments {
        private static Dictionary<ulong, int> bySteamId;
        private static readonly object Lock = new();

        public static bool HasAssignments {
            get {
                lock(Lock) {
                    return bySteamId is { Count: > 0 };
                }
            }
        }

        /// <summary>
        /// Sets the draft team map. Team index 0 = TeamA, 1 = TeamB.
        /// </summary>
        public static void Set(IReadOnlyDictionary<ulong, int> steamIdToTeamIndex) {
            lock(Lock) {
                bySteamId = steamIdToTeamIndex is { Count: > 0 }
                    ? new Dictionary<ulong, int>(steamIdToTeamIndex)
                    : null;
            }
        }

        /// <summary>
        /// Gets team index (0 or 1) for a Steam ID. Returns -1 if not set.
        /// </summary>
        public static int GetTeamIndexForSteamId(ulong steamId) {
            lock(Lock) {
                if(bySteamId == null) return -1;
                return bySteamId.GetValueOrDefault(steamId, -1);
            }
        }

        /// <summary>
        /// Clears assignments after use (e.g. after spawn batch). Call from CustomNetworkManager.
        /// </summary>
        public static void Clear() {
            lock(Lock) {
                bySteamId = null;
            }
        }
    }
}

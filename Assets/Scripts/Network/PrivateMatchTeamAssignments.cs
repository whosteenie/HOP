using System.Collections.Generic;

namespace Network {
    /// <summary>
    /// Holds draft team assignments (SteamId -> team index 0 or 1) from the private match setup panel.
    /// Set by SessionManager when starting a private match; read and cleared by CustomNetworkManager when assigning teams.
    /// </summary>
    public static class PrivateMatchTeamAssignments {
        private static Dictionary<ulong, int> _bySteamId;
        private static readonly object Lock = new();

        public static bool HasAssignments {
            get {
                lock(Lock) {
                    return _bySteamId != null && _bySteamId.Count > 0;
                }
            }
        }

        /// <summary>
        /// Sets the draft team map. Team index 0 = TeamA, 1 = TeamB.
        /// </summary>
        public static void Set(IReadOnlyDictionary<ulong, int> steamIdToTeamIndex) {
            lock(Lock) {
                _bySteamId = steamIdToTeamIndex != null && steamIdToTeamIndex.Count > 0
                    ? new Dictionary<ulong, int>(steamIdToTeamIndex)
                    : null;
            }
        }

        /// <summary>
        /// Gets team index (0 or 1) for a Steam ID. Returns -1 if not set.
        /// </summary>
        public static int GetTeamIndexForSteamId(ulong steamId) {
            lock(Lock) {
                if(_bySteamId == null) return -1;
                return _bySteamId.TryGetValue(steamId, out var t) ? t : -1;
            }
        }

        /// <summary>
        /// Clears assignments after use (e.g. after spawn batch). Call from CustomNetworkManager.
        /// </summary>
        public static void Clear() {
            lock(Lock) {
                _bySteamId = null;
            }
        }
    }
}

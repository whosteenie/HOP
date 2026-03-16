using System;
using Game.Match;
using Network.Session;
using UnityEngine;

namespace Game.Adapters {
    /// <summary>
    /// Wires game-specific matchmaker behavior (max players per mode, default public match settings)
    /// into Network.Session.SessionMatchmaker via hooks so the network layer does not depend
    /// directly on Game.Match.MatchSettingsManager.
    /// </summary>
    public sealed class SessionMatchmakerGameAdapter : MonoBehaviour {
        private void Awake() {
            SessionMatchmaker.SetMatchmakerGameHooks(
                ResolveMaxPlayersForModeGame,
                ResetPublicRuntimeMatchSettingsGame);
        }

        private static int ResolveMaxPlayersForModeGame(string mode) {
            var def = MatchSettingsManager.Instance != null
                ? MatchSettingsManager.Instance.GetGamemodeDef(mode)
                : default;

            var maxPlayers = 10;
            if (def.maxPlayers > 0) maxPlayers = def.maxPlayers;
            return maxPlayers;
        }

        private static void ResetPublicRuntimeMatchSettingsGame(string mode) {
            var matchSettings = MatchSettingsManager.Instance;
            if (matchSettings == null) return;

            var defaultDuration = matchSettings.defaultMatchDurationSeconds > 0
                ? matchSettings.defaultMatchDurationSeconds
                : 600;

            matchSettings.matchDurationSeconds       = defaultDuration;
            matchSettings.preMatchCountdownEnabled   = true;
            matchSettings.swapWeaponsOnDeath         = true;
            matchSettings.scoreToWin                 = ResolveDefaultPublicScoreToWinGame(mode);
            matchSettings.kothHillSpeed              = 1;
            matchSettings.taggedPlayers              = 1;
        }

        private static int ResolveDefaultPublicScoreToWinGame(string mode) {
            if (string.Equals(mode, "Hopball", StringComparison.OrdinalIgnoreCase)) return 60;
            return string.Equals(mode, "KOTH", StringComparison.OrdinalIgnoreCase) ? 200 : 50;
        }
    }
}


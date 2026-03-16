using Game.Match;
using Network.Session;
using UnityEngine;

namespace Game.Match {
    /// <summary>
    /// Wires game-specific match settings behavior (mode selection, per-match options reset)
    /// into Network.Session.SessionManager via hooks so that the network stack does not
    /// depend directly on Game.Match.MatchSettingsManager.
    /// </summary>
    public sealed class SessionManagerGameAdapter : MonoBehaviour {
        private void Awake() {
            SessionManager.SetMatchSettingsHooks(
                ResetMatchSettingsForNewMatch,
                SetSelectedGameModeId);

            // Provide gameplay scene predicate based on MatchMapService.
            SessionManager.IsGameplayScenePredicate = MatchMapService.IsGameplayScene;
        }

        private static void ResetMatchSettingsForNewMatch() {
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null) return;

            matchSettings.preMatchCountdownEnabled = true;
            matchSettings.swapWeaponsOnDeath = true;
        }

        private static void SetSelectedGameModeId(string modeId) {
            if(string.IsNullOrWhiteSpace(modeId)) return;
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null) return;
            if(matchSettings.selectedGameModeId == modeId) return;
            matchSettings.selectedGameModeId = modeId;
        }
    }
}


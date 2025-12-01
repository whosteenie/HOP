using UnityEngine;

namespace Game.Match {
    public class MatchSettingsManager : MonoBehaviour {
        public static MatchSettingsManager Instance { get; private set; }

        [Header("Defaults")]
        [Tooltip("Fallback duration if nothing else is set (seconds).")]
        public int defaultMatchDurationSeconds = 600; // 10 minutes

        [Tooltip("Pre-match countdown duration in seconds. Players cannot move/shoot/grapple during this time.")]
        public int preMatchCountdownSeconds = 5;

        [Header("Runtime")]
        [Tooltip("Set by main menu / gamemode selection before loading the Game scene.")]
        public int matchDurationSeconds;

        [Tooltip("Optional identifier for current gamemode, e.g. 'Deathmatch'.")]
        public string selectedGameModeId;

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            if(matchDurationSeconds <= 0) {
                matchDurationSeconds = defaultMatchDurationSeconds;
            }

            // Initialize gamemode if not set
            if(string.IsNullOrEmpty(selectedGameModeId)) {
                selectedGameModeId = "Deathmatch";
            }
        }

        public int GetMatchDurationSeconds() {
            return matchDurationSeconds > 0 ? matchDurationSeconds : defaultMatchDurationSeconds;
        }

        public int GetPreMatchCountdownSeconds() {
            return preMatchCountdownSeconds > 0 ? preMatchCountdownSeconds : 5;
        }

        /// <summary>
        /// Check if a game mode is team-based.
        /// </summary>
        /// <param name="modeId">The game mode identifier (e.g., "Team Deathmatch", "Deathmatch")</param>
        /// <returns>True if the mode is team-based, false otherwise</returns>
        public static bool IsTeamBasedMode(string modeId) => modeId switch {
            "Team Deathmatch" => true,
            "Hopball" => true,
            "CTF" => true,
            "Oddball" => true,
            "KOTH" => true,
            // Add more team modes here
            _ => false // Deathmatch, Private Match, Gun Tag, etc. are FFA
        };

        /// <summary>
        /// Check if the current game mode (from this instance) is team-based.
        /// </summary>
        /// <returns>True if the current mode is team-based, false otherwise</returns>
        public bool IsCurrentModeTeamBased() {
            return IsTeamBasedMode(selectedGameModeId);
        }
    }
}
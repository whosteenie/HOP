using UnityEngine;
using Network;
using Network.Steam;
using Steamworks;
using Steamworks.Data;

namespace Game.Match {
    /// <summary>
    /// Manages match settings (Duration, Gamemode).
    /// Syncs with Steam Lobby Data ("GameMode").
    /// </summary>
    public class MatchSettingsManager : MonoBehaviour {
        public static MatchSettingsManager Instance { get; private set; }

        [Header("Defaults")]
        [Tooltip("Fallback duration if nothing else is set (seconds).")]
        public int defaultMatchDurationSeconds = 600; // 10 minutes

        [Tooltip("Pre-match countdown duration in seconds.")]
        public int preMatchCountdownSeconds = 5;

        [Header("Runtime")]
        public int matchDurationSeconds;
        public string selectedGameModeId;

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            if(matchDurationSeconds <= 0) matchDurationSeconds = defaultMatchDurationSeconds;
            if(string.IsNullOrEmpty(selectedGameModeId)) selectedGameModeId = "Deathmatch";
        }

        private void Start() {
            SyncGamemodeFromSession();
        }

        private void OnEnable() {
            SteamMatchmaking.OnLobbyDataChanged += OnLobbyDataChanged;
            SyncGamemodeFromSession();
        }

        private void OnDisable() {
            SteamMatchmaking.OnLobbyDataChanged -= OnLobbyDataChanged;
        }

        private void OnLobbyDataChanged(Lobby lobby) {
            SyncGamemodeFromSession();
        }

        private void SyncGamemodeFromSession() {
            var sessionManager = SessionManager.Instance;
            if (sessionManager == null || !sessionManager.CurrentLobby.HasValue) return;
            
            string newGamemode = sessionManager.CurrentLobby.Value.GetData("GameMode");
            
            if (!string.IsNullOrEmpty(newGamemode) && selectedGameModeId != newGamemode) {
                selectedGameModeId = newGamemode;
                Debug.Log($"[MatchSettingsManager] Synced gamemode from Steam Lobby: {selectedGameModeId}");
                
                // Force refresh scoreboard
                if(Game.UI.ScoreboardManager.Instance != null) {
                    Game.UI.ScoreboardManager.Instance.RefreshGamemode();
                }
            }
        }

        public int GetMatchDurationSeconds() => matchDurationSeconds > 0 ? matchDurationSeconds : defaultMatchDurationSeconds;
        public int GetPreMatchCountdownSeconds() => preMatchCountdownSeconds > 0 ? preMatchCountdownSeconds : 5;

        public static bool IsTeamBasedMode(string modeId) => modeId switch {
            "Team Deathmatch" => true,
            "Hopball" => true,
            "CTF" => true,
            "Oddball" => true,
            "KOTH" => true,
            _ => false
        };

        public bool IsCurrentModeTeamBased() => IsTeamBasedMode(selectedGameModeId);
    }
}
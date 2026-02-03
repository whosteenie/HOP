using UnityEngine;
using Network;
using Network.Steam;
using Steamworks;
using Steamworks.Data;
using System.Collections.Generic;

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

        [System.Serializable]
        public struct GamemodeDef {
            public string Id;
            public int MinPlayers;
            public int MaxPlayers; // Global Max (Party + Randoms)
            public int MaxPartySize; // Max size for Public Queue
            public bool IsTeamBased;
        }

        public List<GamemodeDef> gamemodeDefinitions = new List<GamemodeDef> {
            new GamemodeDef { Id = "Deathmatch", MinPlayers = 2, MaxPlayers = 10, MaxPartySize = 5, IsTeamBased = false }, 
            new GamemodeDef { Id = "Team Deathmatch", MinPlayers = 2, MaxPlayers = 10, MaxPartySize = 5, IsTeamBased = true },
            new GamemodeDef { Id = "Hopball", MinPlayers = 2, MaxPlayers = 10, MaxPartySize = 5, IsTeamBased = true },
            new GamemodeDef { Id = "KOTH", MinPlayers = 2, MaxPlayers = 10, MaxPartySize = 5, IsTeamBased = true }, 
            new GamemodeDef { Id = "Gun Tag", MinPlayers = 2, MaxPlayers = 10, MaxPartySize = 5, IsTeamBased = false }
        };

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            if(matchDurationSeconds <= 0) matchDurationSeconds = defaultMatchDurationSeconds;
            if(string.IsNullOrEmpty(selectedGameModeId)) selectedGameModeId = "Deathmatch";
            
            // Allow FFA Party Queue for now based on user request ("allow for 10 party members... enable public... loose matching")
            // Wait, user said "allow for 10 party members for a full private match... parties of 6 or more are meant for private matches only".
            // User did NOT explicitly say "Allow 5 stack in FFA Public".
            // Standard is NO. But I will default to 1 (Solo) for FFA Public to be safe, unless user overrides.
            // Adjusting based on user request: "Parties of 6 or more are meant for private matches only".
            // This implies parties of 5 CAN queue.
            // But for FFA? I'll set MaxPartySize to 5 for Team modes, and maybe 1 or small for FFA.
            // Let's stick to standard safety: FFA = Solo Queue in Public.
            // RE-READING: "loose packet matching... if a 5 stack is too little too late then they keep searching"
            // This implies 5 stacks ARE queuing.
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

        public bool IsCurrentModeTeamBased() {
            var def = GetGamemodeDef(selectedGameModeId);
            return def.IsTeamBased;
        }

        public GamemodeDef GetGamemodeDef(string id) {
            return gamemodeDefinitions.Find(g => g.Id == id);
        }
    }
}
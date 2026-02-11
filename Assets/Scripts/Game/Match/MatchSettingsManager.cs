using UnityEngine;
using Network;
using Steamworks;
using Steamworks.Data;
using System.Collections.Generic;

namespace Game.Match {
    /// <summary>
    /// Manages match settings (Duration, Gamemode).
    /// Syncs with Steam Lobby Data ("TargetMode").
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
            public string id;
            public int minPlayers;
            public int maxPlayers; // Global Max (Party + Randoms)
            public int maxPartySize; // Max size for Public Queue
            public bool isTeamBased;
        }

        public List<GamemodeDef> gamemodeDefinitions = new() {
            new GamemodeDef { id = "Deathmatch", minPlayers = 2, maxPlayers = 10, maxPartySize = 5, isTeamBased = false }, 
            new GamemodeDef { id = "Team Deathmatch", minPlayers = 2, maxPlayers = 10, maxPartySize = 5, isTeamBased = true },
            new GamemodeDef { id = "Hopball", minPlayers = 2, maxPlayers = 10, maxPartySize = 5, isTeamBased = true },
            new GamemodeDef { id = "KOTH", minPlayers = 2, maxPlayers = 10, maxPartySize = 5, isTeamBased = true }, 
            new GamemodeDef { id = "Gun Tag", minPlayers = 2, maxPlayers = 10, maxPartySize = 5, isTeamBased = false }
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
            
            var newGamemode = sessionManager.CurrentLobby.Value.GetData("TargetMode");

            if(string.IsNullOrEmpty(newGamemode) || selectedGameModeId == newGamemode) return;
            selectedGameModeId = newGamemode;
            Debug.Log($"[MatchSettingsManager] Synced gamemode from Steam Lobby: {selectedGameModeId}");
                
            // Force refresh scoreboard
            if(UI.ScoreboardManager.Instance != null) {
                UI.ScoreboardManager.Instance.RefreshGamemode();
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
            return def.isTeamBased;
        }

        public GamemodeDef GetGamemodeDef(string id) {
            return gamemodeDefinitions.Find(g => g.id == id);
        }
    }
}
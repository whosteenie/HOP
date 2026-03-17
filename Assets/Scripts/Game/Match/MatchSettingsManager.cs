using UnityEngine;
using Steamworks;
using Steamworks.Data;
using System.Collections.Generic;
using Events;
using SessionManager = Network.Session.SessionManager;

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
        [HideInInspector] public bool preMatchCountdownEnabled = true;
        [HideInInspector] public bool swapWeaponsOnDeath = true;
        /// <summary> Score limit to win (e.g. Hopball, KOTH). Set from private match draft. </summary>
        public int scoreToWin = 50;
        /// <summary> KOTH hill movement speed multiplier stored as a percentage (100 = 1.00x). </summary>
        public int kothHillSpeed = 100;
        /// <summary> Number of tagged players (Gun Tag only). Set from private match draft. </summary>
        public int taggedPlayers = 1;

        [System.Serializable]
        public struct GamemodeDef {
            public string id;
            public int maxPlayers; // Global Max (Party + Randoms)
            public bool isTeamBased;
        }

        public List<GamemodeDef> gamemodeDefinitions = new() {
            new GamemodeDef { id = "Deathmatch", maxPlayers = 10, isTeamBased = false }, 
            new GamemodeDef { id = "Team Deathmatch", maxPlayers = 10, isTeamBased = true },
            new GamemodeDef { id = "Hopball", maxPlayers = 10, isTeamBased = true },
            new GamemodeDef { id = "KOTH", maxPlayers = 10, isTeamBased = true }, 
            new GamemodeDef { id = "Gun Tag", maxPlayers = 10, isTeamBased = false }
        };

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            if(matchDurationSeconds < 0) matchDurationSeconds = defaultMatchDurationSeconds;
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

            EventBus.Publish(new ScoreboardGamemodeChangedEvent());
        }

        public int GetMatchDurationSeconds() => matchDurationSeconds >= 0 ? matchDurationSeconds : defaultMatchDurationSeconds;
        public int GetPreMatchCountdownSeconds() => preMatchCountdownSeconds > 0 ? preMatchCountdownSeconds : 5;
        public bool IsPreMatchCountdownEnabled() => preMatchCountdownEnabled;
        public bool ShouldSwapWeaponsOnDeath() => swapWeaponsOnDeath;
        public int GetScoreToWin() => scoreToWin >= 0 ? scoreToWin : 50;
        public int GetKothHillSpeedPercent() {
            return kothHillSpeed switch {
                <= 0 => 100,
                // Backward compatibility: older runtime/session values used small integers like 1, 2, 3
                // to mean "normal / faster / fastest", not literal percentages.
                <= 10 => kothHillSpeed * 100,
                _ => kothHillSpeed
            };
        }
        public float GetKothHillSpeedMultiplier() => Mathf.Max(0.01f, GetKothHillSpeedPercent() / 100f);
        public int GetTaggedPlayers() => taggedPlayers > 0 ? taggedPlayers : 1;
        public bool IsInfiniteMatchTimer() => GetMatchDurationSeconds() == 0;
        public bool IsInfiniteScoreLimit() => GetScoreToWin() == 0;

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

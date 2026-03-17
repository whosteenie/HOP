using System;
using Game.Hopball;
using Game.Match;
using Game.Player.Core;
using Network.Contracts;
using Network.Session;
using UnityEngine;

namespace Game.Adapters {
    /// <summary>
    /// Provides game-specific backfill eligibility logic for public matches and
    /// plugs it into Network.Session.SessionMatchLobby via the BackfillEligibilityProvider hook.
    /// </summary>
    public sealed class SessionMatchLobbyGameAdapter : MonoBehaviour {
        private void Awake() {
            SessionMatchLobby.BackfillEligibilityProvider = EvaluateBackfillEligibilityGame;
        }

        /// <summary>
        /// Game rules: whether this public match is still eligible for backfill join-in-progress.
        /// Mirrors the original logic that previously lived in SessionMatchLobby.
        /// </summary>
        private static (bool allowed, string reason) EvaluateBackfillEligibilityGame(ISessionContext ctx) {
            var matchSettings = MatchSettingsManager.Instance;
            var mode = matchSettings != null && !string.IsNullOrWhiteSpace(matchSettings.selectedGameModeId)
                ? matchSettings.selectedGameModeId
                : ctx.SelectedGameMode;

            if(string.IsNullOrWhiteSpace(mode)) return (true, "UnknownMode");

            if(PostMatchManager.Instance != null && PostMatchManager.Instance.PostMatchFlowStarted)
                return (false, "PostMatch");

            var timer = MatchTimerManager.Instance;
            if(timer != null) {
                if(timer.IsWaitingForPlayers || timer.IsPreMatch) return (true, "PreMatch");
                var duration = matchSettings != null ? matchSettings.GetMatchDurationSeconds() : 0;
                if(duration > 0 && timer.TimeRemainingSeconds >= 0) {
                    var remainingFraction = timer.TimeRemainingSeconds / (float)Mathf.Max(1, duration);
                    var minRemaining = ResolveBackfillTimeRemainingThreshold(mode);
                    if(remainingFraction <= minRemaining) return (false, $"LateTime:{remainingFraction:0.00}");
                }
            }

            var scoreToWin = matchSettings != null ? matchSettings.GetScoreToWin() : 0;
            if(scoreToWin <= 0) return (true, "Eligible");

            var scoreProgress = ResolveBackfillScoreProgress(mode);
            if(scoreProgress <= 0f) return (true, "Eligible");
            var progressThreshold = ResolveBackfillScoreThreshold(mode);
            if(progressThreshold <= 0f) return (true, "Eligible");
            return scoreProgress >= progressThreshold ? (false, $"LateScore:{scoreProgress:0.00}") : (true, "Eligible");
        }

        private static float ResolveBackfillTimeRemainingThreshold(string mode) => mode switch {
            "Hopball" => 0.20f, "KOTH" => 0.20f, "Team Deathmatch" => 0.20f, "Deathmatch" => 0.20f, "Gun Tag" => 0.20f,
            _ => 0.15f
        };

        private static float ResolveBackfillScoreThreshold(string mode) => mode switch {
            "Hopball" => 0.70f, "KOTH" => 0.80f, "Team Deathmatch" => 0.75f, "Deathmatch" => 0.80f, "Gun Tag" => 0f,
            _ => 0.80f
        };

        private static float ResolveBackfillScoreProgress(string mode) => mode switch {
            "Hopball" => ResolveHopballBackfillScoreProgress(),
            "KOTH" => ResolveKothBackfillScoreProgress(),
            "Team Deathmatch" => ResolveLeadingTeamObjectiveOrKillProgress(teamMode: true),
            "Deathmatch" => ResolveLeadingTeamObjectiveOrKillProgress(teamMode: false),
            "Gun Tag" => 0f,
            _ => 0f
        };

        private static float ResolveHopballBackfillScoreProgress() {
            var hopballManager = HopballSpawnManager.Instance;
            return hopballManager == null
                ? 0f
                : ResolveLeadingTeamObjectiveProgress(hopballManager.GetTeamAScore(), hopballManager.GetTeamBScore());
        }

        private static float ResolveKothBackfillScoreProgress() {
            var kothManager = KingOfTheHillManager.Instance;
            return kothManager == null
                ? 0f
                : ResolveLeadingTeamObjectiveProgress(kothManager.GetTeamAScore(), kothManager.GetTeamBScore());
        }

        private static float ResolveLeadingTeamObjectiveProgress(int teamAScore, int teamBScore) {
            var scoreToWin = MatchSettingsManager.Instance != null ? MatchSettingsManager.Instance.GetScoreToWin() : 0;
            if(scoreToWin <= 0) return 0f;
            var leadingScore = Mathf.Max(teamAScore, teamBScore);
            return leadingScore / (float)Mathf.Max(1, scoreToWin);
        }

        private static float ResolveLeadingTeamObjectiveOrKillProgress(bool teamMode) {
            var scoreToWin = MatchSettingsManager.Instance != null ? MatchSettingsManager.Instance.GetScoreToWin() : 0;
            if(scoreToWin <= 0) return 0f;

            if(!teamMode) {
                var leadingKills = 0;
                foreach(var player in PlayerController.SpawnedPlayers) {
                    if(player == null || player.NetworkObject == null || !player.NetworkObject.IsSpawned) continue;
                    leadingKills = Mathf.Max(leadingKills, player.Kills.Value);
                }
                return leadingKills / (float)Mathf.Max(1, scoreToWin);
            }

            var teamAKills = 0;
            var teamBKills = 0;
            foreach(var player in PlayerController.SpawnedPlayers) {
                if(player == null || player.NetworkObject == null || !player.NetworkObject.IsSpawned) continue;
                var teamManager = player.TeamManager;
                if(teamManager == null) continue;
                switch(teamManager.netTeam.Value) {
                    case SpawnPoint.Team.TeamA: teamAKills += player.Kills.Value; break;
                    case SpawnPoint.Team.TeamB: teamBKills += player.Kills.Value; break;
                    case SpawnPoint.Team.None: break;
                    default: throw new ArgumentOutOfRangeException();
                }
            }
            return Mathf.Max(teamAKills, teamBKills) / (float)Mathf.Max(1, scoreToWin);
        }
    }
}


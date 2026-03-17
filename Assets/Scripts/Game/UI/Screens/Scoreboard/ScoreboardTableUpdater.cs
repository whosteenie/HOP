using System.Collections.Generic;
using Game.Hopball;
using Game.Match;
using Game.Player.Core;
using Game.Spawning;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Screens.Scoreboard {
    /// <summary>Updates FFA and TDM scoreboard tables (player rows, velocity cache, team scores).</summary>
    internal sealed class ScoreboardTableUpdater {
        private readonly HashSet<ulong> _previousPlayerIds = new();
        private readonly Dictionary<ulong, int> _previousSortValues = new();
        private readonly Dictionary<ulong, Label> _cachedVelocityLabels = new();
        private readonly Dictionary<ulong, float> _previousVelocityValues = new();

        public void ClearCaches() {
            _previousPlayerIds.Clear();
            _previousSortValues.Clear();
            _cachedVelocityLabels.Clear();
            _previousVelocityValues.Clear();
        }

        public void UpdateFfa(IReadOnlyCollection<PlayerController> allControllers, VisualElement playerRows,
            VisualElement scoreboardContainer, VisualElement tdmScoreboardContainer, bool isTagMode,
            ScoreboardRowFactory rowFactory, ScoreboardPlayerData playerData, VisualElement root, Object logContext) {
            if(scoreboardContainer == null || tdmScoreboardContainer == null || playerRows == null) {
                if(root != null)
                    Debug.LogWarning("[ScoreboardManager] FFA scoreboard UI elements not initialized", logContext);
                return;
            }

            if(!rowFactory.EnsureTemplateAssigned()) return;
            scoreboardContainer.RemoveFromClassList("hidden");
            tdmScoreboardContainer.AddToClassList("hidden");

            var currentPlayerIds = new HashSet<ulong>();
            var currentSortValues = new Dictionary<ulong, int>();
            foreach(var player in allControllers) {
                if(player == null || !player.IsSpawned) continue;
                currentPlayerIds.Add(player.OwnerClientId);
                currentSortValues[player.OwnerClientId] = playerData.GetPlayerScore(player, isTagMode);
            }

            var needsRebuild = !currentPlayerIds.SetEquals(_previousPlayerIds);
            if(!needsRebuild) {
                foreach(var kvp in currentSortValues) {
                    if(_previousSortValues.TryGetValue(kvp.Key, out var old) && old == kvp.Value) continue;
                    needsRebuild = true;
                    break;
                }
            }

            if(needsRebuild) {
                playerRows.Clear();
                _cachedVelocityLabels.Clear();
                _previousVelocityValues.Clear();
                var sortedPlayers = playerData.BuildSortedPlayerList(allControllers, isTagMode);
                var rowCount = 0;
                foreach(var player in sortedPlayers) {
                    if(player == null || !player.IsSpawned) continue;
                    var row = rowFactory.CreatePlayerRow(player, playerRows, isTagMode);
                    if(row == null) continue;
                    if(rowCount % 2 == 1) row.AddToClassList("player-row-alt");
                    rowCount++;
                    var labels = row.Query<Label>().ToList();
                    if(labels.Count > 0) _cachedVelocityLabels[player.OwnerClientId] = labels[^1];
                }

                while(rowCount < 10) {
                    var row = rowFactory.CreateEmptyRow(playerRows, isTagMode);
                    if(row == null) break;
                    if(rowCount % 2 == 1) row.AddToClassList("player-row-alt");
                    rowCount++;
                }

                _previousPlayerIds.Clear();
                foreach(var id in currentPlayerIds) _previousPlayerIds.Add(id);
                _previousSortValues.Clear();
                foreach(var kvp in currentSortValues) _previousSortValues[kvp.Key] = kvp.Value;
            } else {
                foreach(var player in allControllers) {
                    if(player == null || !player.IsSpawned) continue;
                    if(!_cachedVelocityLabels.TryGetValue(player.OwnerClientId, out var velocityLabel)) continue;
                    var statsCtrl = playerData.GetStatsController(player);
                    if(statsCtrl == null || velocityLabel == null) continue;
                    var avgVelocity = statsCtrl.AverageVelocity.Value;
                    if(_previousVelocityValues.TryGetValue(player.OwnerClientId, out var prev) &&
                       Mathf.Abs(prev - avgVelocity) <= 0.05f) continue;
                    velocityLabel.text = $"{avgVelocity:F1} u/s";
                    _previousVelocityValues[player.OwnerClientId] = avgVelocity;
                }
            }
        }

        /// <returns>True if TDM was updated; false if UI refs missing (caller should fall back to FFA).</returns>
        public static bool UpdateTdm(IReadOnlyCollection<PlayerController> allControllers, VisualElement enemyTeamRows,
            VisualElement yourTeamRows,
            VisualElement scoreboardContainer, VisualElement tdmScoreboardContainer, Label enemyScoreValue,
            Label yourScoreValue,
            MatchSettingsManager matchSettings, ScoreboardRowFactory rowFactory,
            VisualElement root, Object logContext) {
            if(scoreboardContainer == null || tdmScoreboardContainer == null || enemyTeamRows == null ||
               yourTeamRows == null) {
                if(root != null)
                    Debug.LogWarning(
                        "[ScoreboardManager] TDM scoreboard UI elements not initialized, falling back to FFA",
                        logContext);
                return false;
            }

            if(!rowFactory.EnsureTemplateAssigned()) return false;
            scoreboardContainer.AddToClassList("hidden");
            tdmScoreboardContainer.RemoveFromClassList("hidden");
            enemyTeamRows.Clear();
            yourTeamRows.Clear();

            var networkManager = NetworkManager.Singleton;
            if(networkManager == null || networkManager.LocalClient == null) return false;
            var localPlayer = networkManager.LocalClient.PlayerObject;
            if(localPlayer == null) return false;
            var localController = localPlayer.GetComponent<PlayerController>();
            var localTeamMgr = localController != null ? localController.TeamManager : null;

            if(localTeamMgr == null) return false;
            var localTeam = localTeamMgr.netTeam.Value;

            var enemyPlayers = new List<PlayerController>();
            var yourTeamPlayers = new List<PlayerController>();
            foreach(var player in allControllers) {
                if(player == null || !player.IsSpawned) continue;
                var tm = player.TeamManager;
                if(tm == null) continue;
                if(tm.netTeam.Value == localTeam) yourTeamPlayers.Add(player);
                else enemyPlayers.Add(player);
            }

            enemyPlayers.Sort((a, b) => b.Kills.Value.CompareTo(a.Kills.Value));
            yourTeamPlayers.Sort((a, b) => b.Kills.Value.CompareTo(a.Kills.Value));

            var enemyCount = 0;
            foreach(var player in enemyPlayers) {
                var row = rowFactory.CreatePlayerRow(player, enemyTeamRows, true, false);
                if(row == null) continue;
                if(enemyCount % 2 == 1) row.AddToClassList("player-row-alt");
                enemyCount++;
            }

            while(enemyCount < 5) {
                var row = rowFactory.CreateEmptyRow(enemyTeamRows, false);
                if(row == null) break;
                if(enemyCount % 2 == 1) row.AddToClassList("player-row-alt");
                enemyCount++;
            }

            var yourCount = 0;
            foreach(var player in yourTeamPlayers) {
                var row = rowFactory.CreatePlayerRow(player, yourTeamRows, true, true);
                if(row == null) continue;
                if(yourCount % 2 == 1) row.AddToClassList("player-row-alt");
                yourCount++;
            }

            while(yourCount < 5) {
                var row = rowFactory.CreateEmptyRow(yourTeamRows, false);
                if(row == null) break;
                if(yourCount % 2 == 1) row.AddToClassList("player-row-alt");
                yourCount++;
            }

            if(matchSettings != null && matchSettings.selectedGameModeId == "Hopball" &&
               HopballSpawnManager.Instance != null) {
                var teamA = HopballSpawnManager.Instance.GetTeamAScore();
                var teamB = HopballSpawnManager.Instance.GetTeamBScore();
                if(localTeam == SpawnPoint.Team.TeamA) {
                    if(yourScoreValue != null) yourScoreValue.text = teamA.ToString();
                    if(enemyScoreValue != null) enemyScoreValue.text = teamB.ToString();
                } else {
                    if(yourScoreValue != null) yourScoreValue.text = teamB.ToString();
                    if(enemyScoreValue != null) enemyScoreValue.text = teamA.ToString();
                }
            } else if(matchSettings != null && matchSettings.selectedGameModeId == "KOTH" &&
                      KingOfTheHillManager.Instance != null) {
                var teamA = KingOfTheHillManager.Instance.GetTeamAScore();
                var teamB = KingOfTheHillManager.Instance.GetTeamBScore();
                if(localTeam == SpawnPoint.Team.TeamA) {
                    if(yourScoreValue != null) yourScoreValue.text = teamA.ToString();
                    if(enemyScoreValue != null) enemyScoreValue.text = teamB.ToString();
                } else {
                    if(yourScoreValue != null) yourScoreValue.text = teamB.ToString();
                    if(enemyScoreValue != null) enemyScoreValue.text = teamA.ToString();
                }
            } else {
                var (yourScore, enemyScore) = ScoreboardPlayerData.CalculateTeamKillScores(allControllers, localTeam);
                if(enemyScoreValue != null) enemyScoreValue.text = enemyScore.ToString();
                if(yourScoreValue != null) yourScoreValue.text = yourScore.ToString();
            }

            return true;
        }
    }
}